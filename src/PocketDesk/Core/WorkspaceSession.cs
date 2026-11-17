using System.Diagnostics;
using System.Reflection;
using PocketDesk.Native;

namespace PocketDesk.Core;

public sealed class WorkspaceSession
{
    private readonly RecoveryStore store;
    private RecoveryState state;
    private bool guardStarted;
    public WindowService Windows { get; } = new();
    public DisplayService Displays { get; } = new();
    public WindowSnapshot? Active { get; private set; }
    public bool HasRecovery => state.Windows.Count > 0 || state.Display is not null;
    public bool HasDisplayChange => state.Display is not null;
    public bool HasUncertainZoom => state.Windows.Any(w => w.ZoomUncertain);
    public bool RequiresAutomaticRecoveryEvidence => state.PhoneDisplayOwner?.SelectedFallbackSizes is { Count: > 0 };
    public AutomaticRecoveryDecision AutomaticRecoveryDecision(UuControllerSnapshot? snapshot, DateTimeOffset? now = null) =>
        AutomaticRecoveryEvidencePolicy.Decide(state, snapshot, now);
    public int AdjustedRestoreCount { get; private set; }
    public WorkspaceSession(RecoveryStore store) { this.store = store; state = store.Load(); }

    public PixelRect Take(WindowEntry entry, PixelRect area)
    {
        if (Active?.Handle != entry.Handle && Active is not null) RestoreActive();
        var captured = Windows.Capture(entry.Handle);
        if (captured.ProcessId != entry.ProcessId) throw new InvalidOperationException("窗口已变化，请刷新应用列表后重试。");
        var original = state.Windows.FirstOrDefault(w => w.Handle == entry.Handle && Windows.IsSameWindow(w));
        if (original is null)
        {
            state.Windows.RemoveAll(w => w.Handle == entry.Handle && !Windows.IsSameWindow(w));
            original = captured;
            state.Windows.Add(original);
        }
        store.Save(state);
        StartGuard();
        // Assign before native mutation so failures still leave a restorable active window.
        Active = original;
        return Windows.Place(entry.Handle, area);
    }

    public void RestoreActive()
    {
        if (Active is null) return;
        RestoreOne(Active);
        if (!HasDisplayChange) state.Windows.RemoveAll(w => w.Handle == Active.Handle);
        else Replace(Active with { ZoomSteps = 0 });
        Active = null;
        store.Save(state);
    }

    public void AdjustZoom(int delta)
    {
        if (Active is null || !Windows.IsSameWindow(Active)) throw new InvalidOperationException("窗口已关闭，请返回应用列表。");
        if (Active.ZoomUncertain) throw new InvalidOperationException("上次字号发送结果不确定，请先在应用中手动恢复字号。");
        if (!Windows.CanZoom(Active)) throw new InvalidOperationException("此应用尚未适配字号快捷键，请在应用内调整。");
        if (Math.Abs(Active.ZoomSteps + delta) > 8) throw new InvalidOperationException("本次字号调整最多 8 级。");
        if (!Windows.Focus(Active.Handle)) throw new InvalidOperationException("无法激活目标窗口，未发送字号快捷键。");
        var before = Active;
        Active = before with { ZoomSteps = before.ZoomSteps + delta, ZoomUncertain = true };
        Replace(Active);
        try { store.Save(state); }
        catch { Active = before; Replace(before); throw; }
        if (!Windows.Zoom(Active, delta))
        {
            if (!Windows.LastZoomMayHaveChanged) { Active = before; Replace(before); store.Save(state); }
            throw new InvalidOperationException(Windows.LastError ?? "字号快捷键发送结果不确定，请手动检查字号。");
        }
        Active = Active with { ZoomUncertain = false };
        Replace(Active);
        store.Save(state);
    }

    public void ChangeDisplay(string deviceName, DisplayMode mode, bool automaticPhoneMode = false)
    {
        if (HasDisplayChange) throw new InvalidOperationException("本次已调整过显示模式，请先结束工作台并还原，再尝试另一种模式。");
        ArgumentNullException.ThrowIfNull(mode);
        var observed = Displays.Capture(deviceName);
        Displays.ValidateModeAvailable(deviceName, mode);
        var prepared = state with
        {
            Display = observed, Windows = state.Windows.ToList(),
            PendingDisplayMode = mode, AppliedDisplayMode = null,
            AutomaticPhoneMode = automaticPhoneMode ? mode : null, PhoneDisplayOwner = null
        };
        try
        {
            ApplyPreparedDisplay(prepared, observed, mode, captureWindows: true);
        }
        catch { RestoreAll(); throw; }
    }

    /// <summary>
    /// Applies a known current controller's profile, or an explicit user choice,
    /// including when UU initially reports a generic desktop mode. The caller
    /// supplies that authorization; a UU adapter alone is insufficient evidence.
    /// A previous session must be recovered before adopting a new device profile.
    /// </summary>
    public void ApplyDeviceProfile(string deviceName, DisplayMode mode, PhoneProfile profile,
        ViewportPreferences? preferences = null, DisplayMode? selectedFallback = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(profile);
        var display = Displays.ListDisplays().FirstOrDefault(d =>
            string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("UU 显示器当前未连接，未应用设备规格。");
        var observed = Displays.Capture(deviceName);
        var prepared = PrepareDeviceApply(state, display, observed, mode, profile, preferences, selectedFallback);
        ValidateEnumeratedFallback(deviceName, selectedFallback);
        Displays.ValidateModeAvailable(deviceName, mode);
        ApplyPreparedDisplay(prepared, observed, mode, captureWindows: true);
    }

    private static RecoveryState PrepareDeviceApply(
        RecoveryState recovery, DisplayInfo display, DisplaySnapshot current,
        DisplayMode requested, PhoneProfile profile, ViewportPreferences? preferences = null,
        DisplayMode? selectedFallback = null)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(profile);
        if (recovery.Display is not null || recovery.Windows.Count > 0 ||
            OwnershipModes(recovery).Any() || recovery.PhoneDisplayOwner is not null)
            throw new InvalidOperationException("请先结束并还原原工作台，再应用另一台设备的规格。");
        bool fallback = IsRequestedFallback(display, requested, profile, preferences, selectedFallback);
        if (!PhoneSessionPolicy.CanAutoEnter(display, requested, profile, preferences) && !fallback)
            throw new InvalidOperationException("目标模式必须是所选设备在 UU 虚拟屏上的原生或安全画面尺寸。");
        if (!string.Equals(display.DeviceName, current.DeviceName, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(current.Identity))
            throw new InvalidOperationException("无法确认当前 UU 显示器身份，未应用设备规格。");
        var phoneOwner = preferences is null ? null : PhoneDisplayOwnership.Create(profile, preferences);
        if (fallback) phoneOwner = phoneOwner!.WithFallback(requested);
        return recovery with
        {
            Display = current, Windows = recovery.Windows.ToList(),
            AutomaticPhoneMode = requested, PendingDisplayMode = requested, AppliedDisplayMode = null,
            PhoneDisplayOwner = phoneOwner
        };
    }

    /// <summary>
    /// Repeated user-requested portrait/landscape changes share the first recovery
    /// snapshot. Failure keeps the current active window and the durable journal;
    /// callers may retry or explicitly end the workspace after reporting it.
    /// </summary>
    public void RotatePhoneDisplay(string deviceName, DisplayMode mode, PhoneProfile profile,
        ViewportPreferences? preferences = null, DisplayMode? selectedFallback = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(profile);
        var display = Displays.ListDisplays().FirstOrDefault(d =>
            string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("手机显示器当前未连接，未切换方向。");
        var observed = Displays.Capture(deviceName);
        var prepared = PreparePhoneRotation(state, display, observed, mode, profile, preferences, selectedFallback);
        ValidateEnumeratedFallback(deviceName, selectedFallback);

        // Driver rejection must happen before adding recovery records or touching
        // any windows. Apply performs its own test and verified mode read-back.
        Displays.ValidateModeAvailable(deviceName, mode);
        ApplyPreparedDisplay(prepared, observed, mode, captureWindows: state.Display is null);
    }

    private void ValidateEnumeratedFallback(string deviceName, DisplayMode? fallback)
    {
        if (fallback is not null && !Displays.ListModes(deviceName).Contains(fallback))
            throw new InvalidOperationException("备用画面不再属于驱动当前提供的模式，未修改显示设置。");
    }

    private static bool IsRequestedFallback(DisplayInfo display, DisplayMode requested, PhoneProfile profile,
        ViewportPreferences? preferences, DisplayMode? selectedFallback)
    {
        if (selectedFallback is null) return false;
        if (!display.IsUuVirtual || preferences is null || selectedFallback != requested ||
            !PhoneScreenGeometry.IsSafeSupportedFrame(requested, profile, preferences))
            throw new InvalidOperationException("备用画面必须是当前明确选中的安全驱动模式。");
        return true;
    }

    public bool OwnsPhoneMode(DisplayInfo display, DisplayMode current, PhoneProfile profile,
        ViewportPreferences preferences)
    {
        try
        {
            if (state.Display is null || state.PhoneDisplayOwner is null || !display.IsUuVirtual) return false;
            var observed = Displays.Capture(display.DeviceName);
            return observed.Mode == current && OwnsPhoneModeSnapshot(state, display, observed, profile, preferences);
        }
        catch { return false; }
    }

    private static bool OwnsPhoneModeSnapshot(RecoveryState recovery, DisplayInfo display, DisplaySnapshot current,
        PhoneProfile profile, ViewportPreferences preferences)
    {
        try
        {
            if (!display.IsUuVirtual || recovery.Display is not { } original ||
                recovery.PhoneDisplayOwner is not { } owner ||
                !string.Equals(display.DeviceName, current.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(original.DeviceName, current.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(original.Identity) ||
                !string.Equals(original.Identity, current.Identity, StringComparison.OrdinalIgnoreCase)) return false;
            owner.Validate();
            var markers = OwnershipModes(recovery).ToArray();
            return owner.Matches(profile, preferences) && owner.Contains(current.Mode) &&
                markers.Length > 0 && markers.All(owner.Contains);
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.InvalidDataException) { return false; }
    }

    private void ApplyPreparedDisplay(
        RecoveryState prepared, DisplaySnapshot observed, DisplayMode mode, bool captureWindows)
    {
        if (captureWindows)
        {
            foreach (var entry in Windows.ListWindows())
            {
                if (prepared.Windows.Any(w => w.Handle == entry.Handle)) continue;
                try { prepared.Windows.Add(Windows.Capture(entry.Handle)); } catch { }
            }
        }
        store.Save(prepared);
        state = prepared;
        StartGuard();

        // A switch to another remote session while capturing windows must not be
        // overwritten. Recheck the actual display identity and mode immediately
        // before mutation, leaving the saved recovery state intact on failure.
        var latest = Displays.Capture(observed.DeviceName);
        if (!string.Equals(latest.Identity, observed.Identity, StringComparison.OrdinalIgnoreCase) ||
            latest.Mode != observed.Mode)
            throw new InvalidOperationException("UU 显示模式在切换前已变化，请确认当前连接后重试。恢复记录已保留。");

        // Deliberately no RestoreAll catch here: it would clear Active, move user
        // windows and discard the first snapshot during a recoverable rotation.
        Displays.Apply(observed.DeviceName, mode);
        var confirmed = state with { AppliedDisplayMode = mode, PendingDisplayMode = null };
        // Assign only after durable save: a failed confirmation retains pending
        // ownership in memory and on disk, including for the recovery helper.
        store.Save(confirmed);
        state = confirmed;
    }

    private static RecoveryState PreparePhoneRotation(
        RecoveryState recovery, DisplayInfo display, DisplaySnapshot current,
        DisplayMode requested, PhoneProfile profile, ViewportPreferences? preferences = null,
        DisplayMode? selectedFallback = null)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(profile);
        bool fallback = IsRequestedFallback(display, requested, profile, preferences, selectedFallback);
        bool ownsCurrent = preferences is not null && OwnsPhoneModeSnapshot(recovery, display, current, profile, preferences);
        if ((!PhoneSessionPolicy.CanAutoEnter(display, current.Mode, profile, preferences) && !ownsCurrent) ||
            (!PhoneSessionPolicy.CanAutoEnter(display, requested, profile, preferences) && !fallback))
            throw new InvalidOperationException("当前与目标模式必须属于已配置设备的画面尺寸，未修改显示模式。");
        bool targetPortrait = current.Mode.Width > current.Mode.Height;
        var target = PhoneScreenGeometry.Calculate(profile, targetPortrait,
            preferences ?? new(false));
        // A caller may explicitly retain the target orientation's native pixels
        // when the driver has no safe frame. Native sizes already belong to the
        // profile's finite family; this does not authorize unrelated resolutions.
        bool nativeTarget = requested.Width == (targetPortrait ? profile.PortraitWidth : profile.PortraitHeight) &&
            requested.Height == (targetPortrait ? profile.PortraitHeight : profile.PortraitWidth);
        if (fallback ? (requested.Height > requested.Width) == (current.Mode.Height > current.Mode.Width)
            : !nativeTarget && (requested.Width != target.Width || requested.Height != target.Height))
            throw new InvalidOperationException("目标模式不是该设备另一方向的画面尺寸，未修改显示模式。");
        if (!string.Equals(display.DeviceName, current.DeviceName, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(current.Identity))
            throw new InvalidOperationException("无法确认当前手机显示器身份，未切换方向。");
        if (recovery.Display is { } original &&
            (!string.Equals(original.DeviceName, current.DeviceName, StringComparison.OrdinalIgnoreCase) ||
             string.IsNullOrWhiteSpace(original.Identity) ||
             !string.Equals(original.Identity, current.Identity, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("本次恢复记录属于其他显示器，请先结束并还原原工作台。");
        var preparedOwner = recovery.PhoneDisplayOwner ?? (preferences is null
            ? null : PhoneDisplayOwnership.Create(profile, preferences));
        if (preparedOwner is not null && !preparedOwner.Matches(profile, preferences ?? new(false)))
            throw new InvalidOperationException("设备或留白配置与原工作台归属不同，请先结束旧工作台。");
        if (fallback) preparedOwner = preparedOwner!.WithFallback(requested);
        if (recovery.Display is not null)
        {
            var owners = OwnershipModes(recovery).ToArray();
            if (recovery.PhoneDisplayOwner is { } phoneOwner)
            {
                phoneOwner.Validate();
                if (!phoneOwner.Matches(profile, preferences ?? new(false)) ||
                    !phoneOwner.Contains(current.Mode) || !preparedOwner!.Contains(requested) ||
                    owners.Length == 0 || owners.Any(owner => !phoneOwner.Contains(owner)))
                    throw new InvalidOperationException("设备或留白配置与原工作台归属不同，请先结束旧工作台。");
            }
            else if (owners.Length == 0 || owners.Any(owner => preferences is null
                ? !SamePixelDimensions(owner, current.Mode)
                : !PhoneScreenGeometry.MatchesNativeOrFitted(owner, profile, preferences)))
                throw new InvalidOperationException("当前设备尺寸与原工作台归属不同，请先结束旧工作台再切换方向。");
        }

        // Each orientation has its own safe frame. Persist the finite family
        // before Apply, retaining both the pending target and last applied mode.
        // Existing window records and the first original display stay unchanged.
        return recovery with
        {
            Windows = recovery.Windows.ToList(),
            Display = recovery.Display ?? current,
            AutomaticPhoneMode = requested,
            PendingDisplayMode = requested,
            PhoneDisplayOwner = preparedOwner
        };
    }

    public IReadOnlyList<string> RestoreAll(bool preserveCurrentDisplay = false)
    {
        AdjustedRestoreCount = 0;
        var errors = new List<string>();
        if (state.Display is { } display)
        {
            try
            {
                if (!preserveCurrentDisplay)
                {
                    var current = Displays.Capture(display.DeviceName);
                    if (ShouldRestoreDisplay(state, current))
                    {
                        // Recheck immediately before entering native restore. The
                        // OS offers no atomic mode compare-and-swap; this narrows
                        // the window in which another controller can take over.
                        current = Displays.Capture(display.DeviceName);
                        if (ShouldRestoreDisplay(state, current)) Displays.Restore(display);
                    }
                    else RecoveryStore.WriteLog($"未写入旧显示模式：当前为 {current.Mode.Width}×{current.Mode.Height}，已还原或属于新的外部模式。");
                }
                else RecoveryStore.WriteLog("控制端已交接，保留当前显示模式并释放旧显示恢复目标。");
                ClearDisplayRecovery();
            }
            catch (Exception ex) { errors.Add("显示模式：" + ex.Message); }
        }
        foreach (var snapshot in state.Windows.ToArray())
        {
            try
            {
                RestoreOne(snapshot);
                if (state.Display is null) state.Windows.RemoveAll(w => w.Handle == snapshot.Handle);
                store.Save(state);
            }
            catch (Exception ex) { errors.Add("窗口：" + ex.Message); }
        }
        Active = null;
        store.Save(state);
        return errors;
    }

    private static bool SamePixelDimensions(DisplayMode a, DisplayMode b) =>
        Math.Min(a.Width, a.Height) == Math.Min(b.Width, b.Height) &&
        Math.Max(a.Width, a.Height) == Math.Max(b.Width, b.Height);

    private static IEnumerable<DisplayMode> OwnershipModes(RecoveryState recovery) =>
        new[] { recovery.PendingDisplayMode, recovery.AppliedDisplayMode, recovery.AutomaticPhoneMode }
            .OfType<DisplayMode>();

    private static bool ShouldRestoreDisplay(RecoveryState recovery, DisplaySnapshot current)
    {
        var original = recovery.Display;
        if (original is null) return false;
        if (!string.Equals(original.DeviceName, current.DeviceName, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(original.Identity) ||
            !string.Equals(current.Identity, original.Identity, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("显示器身份已变化，恢复记录已保留，未写入旧显示模式。");

        // The native write may never have happened, or a previous recovery may
        // already have completed. No ownership inference or native write needed.
        if (current.Mode == original.Mode && current.PositionX == original.PositionX && current.PositionY == original.PositionY)
            return false;
        var owners = OwnershipModes(recovery).ToArray();
        if (owners.Length == 0)
            throw new InvalidOperationException("旧恢复记录缺少显示模式归属信息，已保留记录；确认控制端后再释放旧显示目标。");
        if (recovery.PhoneDisplayOwner is { } phoneOwner)
        {
            phoneOwner.Validate();
            if (owners.Any(owner => !phoneOwner.Contains(owner)))
                throw new InvalidOperationException("恢复记录混合了其他设备的画面，已保留记录。");
            return phoneOwner.Contains(current.Mode);
        }
        // A phone/tablet rotation retains its pixel pair; a later device/desktop
        // using a different pair must keep its mode, even if apply had failed.
        return owners.Any(owner => SamePixelDimensions(current.Mode, owner));
    }

    private void ClearDisplayRecovery()
    {
        var display = state.Display;
        var automatic = state.AutomaticPhoneMode;
        var pending = state.PendingDisplayMode;
        var applied = state.AppliedDisplayMode;
        var phoneOwner = state.PhoneDisplayOwner;
        state.Display = null;
        state.AutomaticPhoneMode = null;
        state.PendingDisplayMode = null;
        state.AppliedDisplayMode = null;
        state.PhoneDisplayOwner = null;
        try { store.Save(state); }
        catch
        {
            // A failed journal write is not a completed recovery. In particular,
            // do not delete related window records after failing to persist it.
            state.Display = display;
            state.AutomaticPhoneMode = automatic;
            state.PendingDisplayMode = pending;
            state.AppliedDisplayMode = applied;
            state.PhoneDisplayOwner = phoneOwner;
            throw;
        }
    }

    private void RestoreOne(WindowSnapshot snapshot)
    {
        if (!Windows.IsSameWindow(snapshot)) return; // never address a reused HWND / PID
        if (snapshot.ZoomUncertain)
        {
            Windows.Restore(snapshot);
            throw new InvalidOperationException("字号发送结果不确定。请在应用中恢复字号后，点击“已手动恢复字号”。");
        }
        if (snapshot.ZoomSteps != 0)
        {
            if (!Windows.Focus(snapshot.Handle))
                throw new InvalidOperationException("字号未能还原，记录已保留，请关闭菜单并松开修饰键后重试。");
            var pending = snapshot with { ZoomUncertain = true };
            Replace(pending);
            try { store.Save(state); } catch { Replace(snapshot); throw; }
            if (!Windows.Zoom(snapshot, -snapshot.ZoomSteps))
            {
                if (!Windows.LastZoomMayHaveChanged) { Replace(snapshot); store.Save(state); }
                else if (Active?.Handle == snapshot.Handle) Active = pending;
                throw new InvalidOperationException(Windows.LastError ?? "字号未能还原，恢复记录已保留。");
            }
            // Persist the completed zoom reversal before attempting window placement.
            var cleared = snapshot with { ZoomSteps = 0 };
            Replace(cleared);
            if (Active?.Handle == snapshot.Handle) Active = cleared;
            store.Save(state);
        }
        if (!Windows.Restore(snapshot)) throw new InvalidOperationException(Windows.LastError ?? "窗口位置未能还原，记录已保留。");
        if (Windows.LastRestoreAdjusted) AdjustedRestoreCount++;
    }

    public void AcknowledgeManualZoomRecovery()
    {
        foreach (var item in state.Windows.Where(w => w.ZoomUncertain).ToArray())
        {
            var cleared = item with { ZoomSteps = 0, ZoomUncertain = false };
            Replace(cleared);
            if (Active?.Handle == item.Handle) Active = cleared;
        }
        store.Save(state);
    }

    private void Replace(WindowSnapshot snapshot)
    {
        int index = state.Windows.FindIndex(w => w.Handle == snapshot.Handle);
        if (index >= 0) state.Windows[index] = snapshot;
    }

    private void StartGuard()
    {
        if (guardStarted) return;
        using var current = Process.GetCurrentProcess();
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add("--recover-after");
        info.ArgumentList.Add(current.Id.ToString());
        info.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString());
        try { Process.Start(info)?.Dispose(); guardStarted = true; }
        catch (Exception ex) { throw new InvalidOperationException("无法启动恢复助手，已停止修改：" + ex.Message, ex); }
    }
}
