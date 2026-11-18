using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PocketDesk.Native;

/// <summary>Window operations for the current interactive user session. No background hooks or automatic input.</summary>
public sealed class WindowService
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x80;
    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010,
        SwpNoOwnerZOrder = 0x0200, SwpAsyncWindowPos = 0x4000;
    private const uint MonitorDefaultToPrimary = 1, MonitorDefaultToNearest = 2;
    private const int SwShowNormal = 1, SwShowMinimized = 2, SwShowMaximized = 3, SwRestore = 9;
    private const uint WpfAsyncWindowPlacement = 4, WpfRestoreToMaximized = 2;
    private const ushort VkControl = 0x11, VkShift = 0x10, VkMenu = 0x12,
        VkLWin = 0x5B, VkRWin = 0x5C, VkOemPlus = 0xBB, VkOemMinus = 0xBD,
        VkAdd = 0x6B, VkSubtract = 0x6D;
    private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "UURemote", "UURemoteServer", "UURemoteService", "UUYC", "GameViewer",
        "GameViewerServer", "GameViewerService", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchHost", "SearchApp", "TextInputHost", "LockApp"
    };

    public string? LastError { get; private set; }
    /// <summary>True when Restore must make an off-screen saved rectangle visible in the current work area.</summary>
    public bool LastRestoreAdjusted { get; private set; }
    /// <summary>After a failed Zoom, true means some input was inserted and automatic rollback is ambiguous.</summary>
    public bool LastZoomMayHaveChanged { get; private set; }

    public IReadOnlyList<WindowEntry> ListWindows()
    {
        using var dpi = new DpiScope();
        var result = new List<WindowEntry>();
        Native.EnumWindows((window, _) =>
        {
            try
            {
                if (!Native.IsWindowVisible(window) || window == Native.GetShellWindow() || window == Native.GetDesktopWindow())
                    return true;
                if ((GetExStyle(window) & WsExToolWindow) != 0 || IsCloaked(window)) return true;
                var windowClass = GetClass(window);
                if (windowClass is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
                Native.GetWindowThreadProcessId(window, out var pid);
                if (pid == 0 || pid == Environment.ProcessId) return true;
                var title = GetTitle(window);
                if (string.IsNullOrWhiteSpace(title)) return true;
                using var process = Process.GetProcessById((int)pid);
                var name = process.ProcessName;
                if (ExcludedProcesses.Contains(name) || name.StartsWith("UURemote", StringComparison.OrdinalIgnoreCase)) return true;
                result.Add(new WindowEntry(window.ToInt64(), title, name, (int)pid));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // Windows and processes may disappear during enumeration.
            }
            return true;
        }, IntPtr.Zero);
        return result.OrderBy(x => x.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    /// <summary>Observe an ordinary foreground app on this virtual monitor. Does not
    /// activate it or enlist it in workspace restoration. Popups and shell surfaces
    /// remain entirely under the application's/Windows' own input handling.</summary>
    public WindowSnapshot? ForegroundOn(DisplayInfo display, WindowSnapshot? cached)
    {
        if (!display.IsUuVirtual) return null;
        using var dpi = new DpiScope();
        try
        {
            var h = Native.GetForegroundWindow();
            if (h == 0 || !Native.IsWindowVisible(h) || Native.IsIconic(h) || Native.GetWindow(h, 4) != 0 ||
                h == Native.GetDesktopWindow() || h == Native.GetShellWindow() || IsCloaked(h) ||
                (GetExStyle(h) & WsExToolWindow) != 0) return null;
            var monitor = Native.MonitorFromWindow(h, 0);
            if (monitor == 0 || ToPixelRect(ReadMonitor(monitor).Monitor) != display.Bounds) return null;
            Native.GetWindowThreadProcessId(h, out var pid);
            if (pid == 0 || pid == Environment.ProcessId) return null;
            if (cached?.Handle == h.ToInt64() && IsSameWindow(cached)) return cached;
            if (Native.GetWindowTextLength(h) <= 0 || GetClass(h) is "#32768" or "#32770" or "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                return null;
            var target = Capture(h.ToInt64());
            return !ExcludedProcesses.Contains(target.ProcessName) && !target.ProcessName.StartsWith("UURemote", StringComparison.OrdinalIgnoreCase)
                && Native.GetForegroundWindow() == h ? target : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    }

    public WindowSnapshot Capture(long handle)
    {
        LastError = null;
        using var dpi = new DpiScope();
        var window = new IntPtr(handle);
        if (!Native.IsWindow(window)) throw new InvalidOperationException("窗口已关闭。");
        var threadId = Native.GetWindowThreadProcessId(window, out var pid);
        using var process = Process.GetProcessById(checked((int)pid));
        var startTicks = process.StartTime.ToUniversalTime().Ticks;
        var placement = ReadPlacement(window);
        var workspace = (GetExStyle(window) & WsExToolWindow) == 0;
        var normal = ToPixelRect(placement.NormalPosition);
        var monitor = ReadMonitor(Native.MonitorFromWindow(window, MonitorDefaultToNearest));
        if (workspace) normal = Offset(normal, monitor.Work.Left - monitor.Monitor.Left, monitor.Work.Top - monitor.Monitor.Top);
        var snapshot = new WindowSnapshot
        {
            Handle = handle,
            ProcessId = (int)pid,
            ProcessStartTicks = startTicks,
            ProcessName = process.ProcessName,
            ProcessPath = ReadProcessPath(process),
            WindowClass = GetClass(window),
            WindowThreadId = threadId,
            NormalBounds = normal,
            CurrentBounds = ReadRect(window),
            ShowCommand = (int)placement.ShowCommand,
            PlacementFlags = (int)placement.Flags,
            MinPositionX = placement.MinPosition.X,
            MinPositionY = placement.MinPosition.Y,
            MaxPositionX = placement.MaxPosition.X,
            MaxPositionY = placement.MaxPosition.Y,
            PlacementUsesWorkspaceCoordinates = workspace
        };
        if (!IsSameWindow(snapshot)) throw new InvalidOperationException("捕获期间窗口发生变化，请重新选择。");
        return snapshot;
    }

    public bool IsSameWindow(WindowSnapshot snapshot)
    {
        try
        {
            var window = new IntPtr(snapshot.Handle);
            if (!Native.IsWindow(window) || snapshot.ProcessStartTicks <= 0 || string.IsNullOrEmpty(snapshot.WindowClass)) return false;
            var thread = Native.GetWindowThreadProcessId(window, out var pid);
            if (pid != snapshot.ProcessId || thread != snapshot.WindowThreadId || GetClass(window) != snapshot.WindowClass) return false;
            using var process = Process.GetProcessById((int)pid);
            if (process.StartTime.ToUniversalTime().Ticks != snapshot.ProcessStartTicks) return false;
            var path = ReadProcessPath(process);
            return string.IsNullOrEmpty(snapshot.ProcessPath) || string.Equals(path, snapshot.ProcessPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Requests the size and position, returning the actual result (an app may enforce a minimum size).</summary>
    public PixelRect Place(long handle, PixelRect bounds)
    {
        LastError = null;
        ValidateBounds(bounds);
        using var dpi = new DpiScope();
        var window = new IntPtr(handle);
        EnsureResponsive(window);
        if (Native.IsIconic(window))
        {
            // A window minimized from maximized restores to maximized first.
            if (!Native.ShowWindowAsync(window, SwRestore)) throw NativeError("无法还原窗口");
            if (!WaitUntil(() => !Native.IsIconic(window), 900))
                throw new InvalidOperationException("窗口未能退出最小化状态。");
        }
        if (Native.IsZoomed(window))
        {
            if (!Native.ShowWindowAsync(window, SwRestore)) throw NativeError("无法还原窗口");
            if (!WaitUntil(() => !Native.IsIconic(window) && !Native.IsZoomed(window), 900))
                throw new InvalidOperationException("窗口未能退出最小化或最大化状态。");
        }
        // Crossing monitors can deliver WM_DPICHANGED; reapply after it settles.
        for (var pass = 0; pass < 2; pass++)
        {
            if (!Native.SetWindowPos(window, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                    SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpAsyncWindowPos))
                throw NativeError("无法移动窗口");
            WaitForStableRect(window, bounds);
        }
        return ReadRect(window);
    }

    public bool Restore(WindowSnapshot snapshot)
    {
        LastError = null;
        LastRestoreAdjusted = false;
        try
        {
            using var dpi = new DpiScope();
            if (!IsSameWindow(snapshot)) return Fail("原窗口已关闭或身份不匹配，已跳过还原。");
            if (snapshot.ShowCommand is not (SwShowNormal or SwShowMinimized or SwShowMaximized))
                return Fail("窗口还原记录包含无效的显示状态，已停止还原。");
            var window = new IntPtr(snapshot.Handle);
            EnsureResponsive(window);
            ValidateBounds(snapshot.NormalBounds);
            var normal = snapshot.NormalBounds;
            var normalNative = ToRect(normal);
            var monitorHandle = Native.MonitorFromRect(ref normalNative, 0);
            var fallback = monitorHandle == IntPtr.Zero;
            if (fallback)
            {
                var origin = new Point();
                monitorHandle = Native.MonitorFromPoint(origin, MonitorDefaultToPrimary);
            }
            var monitor = ReadMonitor(monitorHandle);
            var workArea = ToPixelRect(monitor.Work);
            // Resolution/topology changes can leave even a minimized window's normal
            // rectangle partially outside the work area. SetWindowPlacement may then
            // silently reposition or constrain it. Choose a deterministic visible
            // rectangle ourselves, rather than compare against an impossible target.
            normal = NormalizeBoundsToWorkArea(normal, workArea);
            var minimum = ReadMinimumTrackingSize(window);
            if (minimum is { } min)
            {
                // Constraints may change even when the saved rectangle still
                // fits the monitor. Only measured minimums may enlarge it.
                var width = Math.Max(normal.Width, min.X);
                var height = Math.Max(normal.Height, min.Y);
                normal = new PixelRect(
                    width >= workArea.Width ? workArea.X : Math.Clamp(normal.X, workArea.X, checked(workArea.X + workArea.Width - width)),
                    height >= workArea.Height ? workArea.Y : Math.Clamp(normal.Y, workArea.Y, checked(workArea.Y + workArea.Height - height)),
                    width, height);
                ValidateBounds(normal);
            }
            LastRestoreAdjusted = normal != snapshot.NormalBounds;
            if (snapshot.ShowCommand == SwShowNormal)
            {
                // A placement request can be rescaled or repositioned by the
                // target app after a display/DPI transition. Restore normal
                // windows in physical screen pixels, as Place already does,
                // instead of queueing a competing asynchronous placement first.
                bool MatchesNormal() => IsSameWindow(snapshot) && !Native.IsIconic(window) &&
                    !Native.IsZoomed(window) && RectNear(ReadRect(window), normal, 4);
                if (MatchesNormal()) return true;
                Place(snapshot.Handle, normal);
                return WaitForStableMatch(MatchesNormal, 900)
                    || Fail("窗口未确认恢复到记录的位置和状态；还原记录应保留以便重试。");
            }
            var desiredNormalScreen = normal;
            var workspace = (GetExStyle(window) & WsExToolWindow) == 0;
            if (workspace) normal = Offset(normal, monitor.Monitor.Left - monitor.Work.Left, monitor.Monitor.Top - monitor.Work.Top);
            var placement = new WindowPlacement
            {
                Length = (uint)Marshal.SizeOf<WindowPlacement>(),
                Flags = ((uint)snapshot.PlacementFlags & 3) | WpfAsyncWindowPlacement,
                ShowCommand = (uint)snapshot.ShowCommand,
                MinPosition = LastRestoreAdjusted ? new Point(-1, -1) : new Point(snapshot.MinPositionX, snapshot.MinPositionY),
                MaxPosition = LastRestoreAdjusted ? new Point(-1, -1) : new Point(snapshot.MaxPositionX, snapshot.MaxPositionY),
                NormalPosition = ToRect(normal)
            };
            if (LastRestoreAdjusted) placement.Flags &= ~1u;
            bool MatchesPlacement()
            {
                if (!IsSameWindow(snapshot)) return false;
                var current = ReadPlacement(window);
                var expectedMinimized = snapshot.ShowCommand is 2 or 6 or 7 or 11;
                if (Native.IsIconic(window) != expectedMinimized) return false;
                if (!expectedMinimized && Native.IsZoomed(window) != (snapshot.ShowCommand == SwShowMaximized)) return false;
                var actual = current.NormalPosition;
                var desired = placement.NormalPosition;
                // A few pixels of nonclient/DPI rounding are acceptable; large mismatches retain recovery state.
                bool exact = RectNear(ToPixelRect(actual), ToPixelRect(desired), 4);
                if (!exact && snapshot.ShowCommand == SwShowMaximized && LastRestoreAdjusted &&
                    Native.DwmGetWindowAttributeRect(window, 9, out var visible, Marshal.SizeOf<Rect>()) == 0)
                {
                    var actualScreen = ToPixelRect(actual);
                    if (workspace) actualScreen = Offset(actualScreen,
                        monitor.Work.Left - monitor.Monitor.Left, monitor.Work.Top - monitor.Monitor.Top);
                    var windowDpi = Native.GetDpiForWindow(window);
                    if (windowDpi == 0) windowDpi = 96;
                    int frameX = Native.GetSystemMetricsForDpi(32, windowDpi) + Native.GetSystemMetricsForDpi(92, windowDpi);
                    int frameY = Native.GetSystemMetricsForDpi(33, windowDpi) + Native.GetSystemMetricsForDpi(92, windowDpi);
                    exact = IsBoundedMaximizedAdjustment(actualScreen, desiredNormalScreen,
                        ToPixelRect(visible), workArea, frameX, frameY);
                }
                return exact && (!expectedMinimized ||
                    (current.Flags & WpfRestoreToMaximized) == (placement.Flags & WpfRestoreToMaximized));
            }
            // Do not enqueue an asynchronous restore when the target already
            // matches. A redundant queued request could otherwise run after the
            // caller's next operation and undo a subsequent maximize or move.
            if (MatchesPlacement()) return true;
            if (!Native.SetWindowPlacement(window, ref placement)) return Fail(NativeError("无法恢复窗口位置").Message);
            var ok = WaitUntil(MatchesPlacement, 1200);
            return ok || Fail("窗口未确认恢复到记录的位置和状态；还原记录应保留以便重试。");
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or OverflowException)
        {
            return Fail(e.Message);
        }
    }

    private static bool RectNear(PixelRect actual, PixelRect expected, int tolerance) =>
        Math.Abs((long)actual.X - expected.X) <= tolerance && Math.Abs((long)actual.Y - expected.Y) <= tolerance &&
        Math.Abs((long)actual.X + actual.Width - expected.X - expected.Width) <= tolerance &&
        Math.Abs((long)actual.Y + actual.Height - expected.Y - expected.Height) <= tolerance;

    private static bool IsBoundedMaximizedAdjustment(PixelRect actualNormal, PixelRect desiredNormal,
        PixelRect maximizedVisible, PixelRect workArea, int frameX, int frameY) =>
        frameX is >= 0 and <= 128 && frameY is >= 0 and <= 128 &&
        Math.Abs((long)actualNormal.Width - desiredNormal.Width) <= 4 &&
        Math.Abs((long)actualNormal.Height - desiredNormal.Height) <= 4 &&
        Math.Abs((long)actualNormal.X - desiredNormal.X) <= frameX &&
        Math.Abs((long)actualNormal.Y - desiredNormal.Y) <= frameY &&
        RectNear(maximizedVisible, workArea, 4);

    private static bool WaitForStableMatch(Func<bool> condition, int timeout)
    {
        var watch = Stopwatch.StartNew();
        int consecutive = 0;
        while (watch.ElapsedMilliseconds < timeout)
        {
            consecutive = condition() ? consecutive + 1 : 0;
            if (consecutive >= 6) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    /// <summary>
    /// Keeps a valid screen-pixel rectangle inside the current monitor work area.
    /// Contained rectangles are unchanged; oversized rectangles shrink before their
    /// origin is clamped. App-specific minimum dimensions are handled by Restore.
    /// </summary>
    public static PixelRect NormalizeBoundsToWorkArea(PixelRect bounds, PixelRect workArea)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        ArgumentNullException.ThrowIfNull(workArea);
        ValidateBounds(bounds);
        ValidateBounds(workArea);
        var width = Math.Min(bounds.Width, workArea.Width);
        var height = Math.Min(bounds.Height, workArea.Height);
        return new PixelRect(
            Math.Clamp(bounds.X, workArea.X, checked(workArea.X + workArea.Width - width)),
            Math.Clamp(bounds.Y, workArea.Y, checked(workArea.Y + workArea.Height - height)),
            width, height);
    }

    public bool Focus(long handle)
    {
        LastError = null;
        var window = new IntPtr(handle);
        if (!Native.IsWindow(window)) return Fail("窗口已关闭。");
        if (Native.IsIconic(window))
        {
            Native.ShowWindowAsync(window, SwRestore);
            if (!WaitUntil(() => !Native.IsIconic(window), 600)) return Fail("窗口未能恢复显示。");
        }
        Native.SetForegroundWindow(window);
        return WaitUntil(() => Native.GetForegroundWindow() == window, 350)
            || Fail("Windows 未允许切换前台窗口，请先点选目标窗口。");
    }

    public bool IsForeground(WindowSnapshot snapshot) =>
        Native.GetForegroundWindow() == (nint)snapshot.Handle && IsSameWindow(snapshot);

    public bool CanZoom(WindowSnapshot snapshot)
    {
        try
        {
            if (!IsSameWindow(snapshot)) return false;
            // Recheck the executable instead of trusting user-editable persisted ProcessName.
            using var process = Process.GetProcessById(snapshot.ProcessId);
            var name = process.ProcessName;
            return name.Equals("Code", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Cursor", StringComparison.OrdinalIgnoreCase)
                || name.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase)
                || ChatGptPackage.SupportsZoom(process);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>True means all keystrokes were inserted, not that custom application key bindings applied a zoom.</summary>
    public bool Zoom(WindowSnapshot snapshot, int delta)
    {
        LastError = null;
        LastZoomMayHaveChanged = false;
        if (delta == 0) return true;
        if (delta is < -12 or > 12) return Fail("单次字号调整最多 12 级。");
        try
        {
            if (!CanZoom(snapshot)) return Fail("此窗口不在字号适配列表中，或已关闭。");
            var window = new IntPtr(snapshot.Handle);
            if (Native.GetForegroundWindow() != window) return Fail("目标窗口不在前台，未发送字号快捷键。");
            if (ModifiersPressed()) return Fail("请松开 Ctrl、Shift、Alt 和 Windows 键后再调节字号。");
            using var process = Process.GetProcessById(snapshot.ProcessId);
            var terminal = process.ProcessName.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase);
            // Terminal documents Ctrl+numpad_plus/minus. These avoid OEM keyboard-layout ambiguity.
            var key = terminal ? (delta > 0 ? VkAdd : VkSubtract) : (delta > 0 ? VkOemPlus : VkOemMinus);
            var inputs = new List<Input>();
            for (var step = 0; step < Math.Abs(delta); step++)
            {
                inputs.Add(Key(VkControl));
                inputs.Add(Key(key));
                inputs.Add(Key(key, up: true));
                inputs.Add(Key(VkControl, up: true));
            }
            if (!IsSameWindow(snapshot) || Native.GetForegroundWindow() != window || ModifiersPressed())
                return Fail("发送前窗口焦点或按键状态变化，未发送快捷键。");
            var array = inputs.ToArray();
            var inserted = Native.SendInput((uint)array.Length, array, Marshal.SizeOf<Input>());
            LastZoomMayHaveChanged = inserted > 0;
            if (inserted == array.Length) return true;
            // A partial send must not leave our synthetic modifiers down.
            if (inserted > 0)
            {
                var releases = new[] { Key(key, true), Key(VkControl, true) };
                Native.SendInput((uint)releases.Length, releases, Marshal.SizeOf<Input>());
            }
            return Fail($"Windows 仅接受了 {inserted}/{array.Length} 个输入事件。权限级别（UIPI）或安全桌面可能阻止输入；不能确认字号变更。");
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return Fail(e.Message);
        }
    }

    private bool Fail(string message) { LastError = message; return false; }
    private static bool ModifiersPressed() => new[] { VkControl, VkShift, VkMenu, VkLWin, VkRWin }
        .Any(key => (Native.GetAsyncKeyState(key) & 0x8000) != 0);
    private static Input Key(ushort key, bool up = false) => new()
    {
        Type = 1,
        Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? 2u : 0u, ExtraInfo = new UIntPtr(0x504F434B) } }
    };
    private static void ValidateBounds(PixelRect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > 65535 || bounds.Height > 65535)
            throw new ArgumentOutOfRangeException(nameof(bounds), "窗口尺寸必须大于零且不超过 65535 像素。");
        _ = checked(bounds.X + bounds.Width);
        _ = checked(bounds.Y + bounds.Height);
    }
    private static void EnsureResponsive(IntPtr window)
    {
        if (!Native.IsWindow(window)) throw new InvalidOperationException("窗口已关闭。");
        if (Native.IsHungAppWindow(window) || Native.SendMessageTimeout(window, 0, UIntPtr.Zero, IntPtr.Zero, 2, 300, out _) == IntPtr.Zero)
            throw new InvalidOperationException("窗口无响应或权限不足，未继续移动。");
    }
    private static Point? ReadMinimumTrackingSize(IntPtr window)
    {
        var dpi = Native.GetDpiForWindow(window);
        if (dpi == 0) dpi = 96;
        var limits = new MinMaxInfo
        {
            MinTrackSize = new Point(Native.GetSystemMetricsForDpi(34, dpi), Native.GetSystemMetricsForDpi(35, dpi)),
            MaxTrackSize = new Point(Native.GetSystemMetricsForDpi(59, dpi), Native.GetSystemMetricsForDpi(60, dpi))
        };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<MinMaxInfo>());
        try
        {
            Marshal.StructureToPtr(limits, memory, false);
            // WM_GETMINMAXINFO is a system message; Windows marshals its structure
            // across processes. This reads constraints without showing the window.
            if (Native.SendMessageTimeout(window, 0x0024, UIntPtr.Zero, memory, 2, 300, out _) == IntPtr.Zero)
                return null;
            var measured = Marshal.PtrToStructure<MinMaxInfo>(memory).MinTrackSize;
            return measured.X is > 0 and <= 65535 && measured.Y is > 0 and <= 65535 ? measured : null;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    private static bool WaitUntil(Func<bool> condition, int timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        do { if (condition()) return true; Thread.Sleep(20); } while (stopwatch.ElapsedMilliseconds < timeout);
        return condition();
    }
    private static void WaitForStableRect(IntPtr window, PixelRect requested)
    {
        var watch = Stopwatch.StartNew();
        var previous = ReadRect(window);
        var unchanged = 0;
        while (watch.ElapsedMilliseconds < 650)
        {
            Thread.Sleep(25);
            var actual = ReadRect(window);
            unchanged = actual == previous ? unchanged + 1 : 0;
            if (actual == requested || (watch.ElapsedMilliseconds >= 150 && unchanged >= 4)) return;
            previous = actual;
        }
    }
    private static WindowPlacement ReadPlacement(IntPtr window)
    {
        var value = new WindowPlacement { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
        if (!Native.GetWindowPlacement(window, ref value)) throw NativeError("无法读取窗口位置");
        return value;
    }
    private static PixelRect ReadRect(IntPtr window)
    {
        if (!Native.GetWindowRect(window, out var rect)) throw NativeError("无法读取窗口边界");
        return ToPixelRect(rect);
    }
    private static MonitorInfo ReadMonitor(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!Native.GetMonitorInfo(monitor, ref info)) throw NativeError("无法读取显示器工作区");
        return info;
    }
    private static string ReadProcessPath(Process process)
    {
        try
        {
            var size = 32768;
            var builder = new StringBuilder(size);
            return Native.QueryFullProcessImageName(process.Handle, 0, builder, ref size) ? builder.ToString() : "";
        }
        catch (Win32Exception) { return ""; }
    }
    private static string GetClass(IntPtr window)
    {
        var text = new StringBuilder(256);
        return Native.GetClassName(window, text, text.Capacity) > 0 ? text.ToString() : "";
    }
    private static string GetTitle(IntPtr window)
    {
        var text = new StringBuilder(Math.Min(32768, Native.GetWindowTextLength(window) + 1));
        Native.GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }
    private static long GetExStyle(IntPtr window) => IntPtr.Size == 8
        ? Native.GetWindowLongPtr(window, GwlExStyle).ToInt64() : Native.GetWindowLong(window, GwlExStyle);
    private static bool IsCloaked(IntPtr window) => Native.DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
    private static PixelRect ToPixelRect(Rect r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    private static Rect ToRect(PixelRect r) => new() { Left = r.X, Top = r.Y, Right = checked(r.X + r.Width), Bottom = checked(r.Y + r.Height) };
    private static PixelRect Offset(PixelRect r, int x, int y) => r with { X = checked(r.X + x), Y = checked(r.Y + y) };
    private static Win32Exception NativeError(string operation) => new(Marshal.GetLastWin32Error(), operation);

    private sealed class DpiScope : IDisposable
    {
        private readonly IntPtr previous = Native.SetThreadDpiAwarenessContext(new IntPtr(-4));
        public void Dispose() { if (previous != IntPtr.Zero) Native.SetThreadDpiAwarenessContext(previous); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPlacement
    {
        public uint Length, Flags, ShowCommand;
        public Point MinPosition, MaxPosition;
        public Rect NormalPosition;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo
    {
        public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct HardwareInput { public uint Message; public ushort Low, High; }

    private static class Native
    {
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr window, uint which);
        internal delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsHungAppWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] internal static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr window, StringBuilder text, int length);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] internal static extern int DwmGetWindowAttributeRect(IntPtr window, int attribute, out Rect value, int size);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowPlacement(IntPtr window, ref WindowPlacement placement);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPlacement(IntPtr window, ref WindowPlacement placement);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromRect(ref Rect rectangle, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(Point point, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern int GetSystemMetricsForDpi(int index, uint dpi);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    }
}
