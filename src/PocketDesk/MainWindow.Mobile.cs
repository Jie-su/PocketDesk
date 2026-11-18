using System.Diagnostics;
using System.Windows;
using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk;

public partial class MainWindow
{
    private MobileGestureLayer? gestureLayer;
    private WindowSnapshot? gestureTarget;
    private WindowSnapshot? panelReturnTarget;
    private ToolbarPlacement? gesturePlacement;
    private double gestureDensity;
    private long launchGeneration;
    private void InvalidatePoolLaunch() => launchGeneration++;
    private void ReturnAfterAppClosed()
    {
        gestureLayer?.Suspend();
        session.RestoreActive();
        if (working && automaticWorkspace && !IsVisible)
        {
            ShowDock();
            toolbar?.SetNotice("窗口已关闭，可在应用池中再选一个。");
        }
        else ShowLibrary();
    }
    private FrameworkElement CreatePoolPanel() => new AppPoolPanel(() => toolbar, launch =>
    {
        toolbar?.Collapse();
        _ = FollowPoolLaunchAsync(launch.ExecutablePath, launch.ProcessId);
    }, listWindows: session.Windows.ListWindows, switchWindow: entry => Run(() =>
    {
        toolbar?.Collapse();
        OpenEntry(entry);
    }));

    private void ShowMobileWindows()
    {
        if (!working || previewTimer.IsEnabled || changingDisplay) { ShowLibrary(); return; }
        ShowDock();
        toolbar?.ShowRunningWindows();
    }

    private void UpdateMobileControls()
    {
        var mobile = working && automaticWorkspace && Target is { IsUuVirtual: true };
        toolbar?.SetGestureState(devicePreferences.MobileGestures, mobile);
        toolbar?.SetScrollButtonsState(devicePreferences.ScrollButtons, mobile && devicePreferences.MobileGestures);
        toolbar?.SetAutoZoomState(devicePreferences.AutoZoom, mobile);
    }

    private void ApplyMobileReadingSize()
    {
        if (!working || !automaticWorkspace || Target is not { IsUuVirtual: true }) return;
        try
        {
            int delta = session.ApplyMobileAutoZoom(devicePreferences.AutoZoom);
            if (delta != 0) toolbar?.SetNotice(delta > 0 ? "已为当前窗口放大一级" : "已撤销自动字号调整");
        }
        catch (Exception ex)
        {
            RecoveryStore.WriteLog("自动字号：" + ex.Message);
            toolbar?.SetNotice(ex.Message);
        }
    }

    private ToolbarPlacement DockPlacement(DisplayInfo display) => ToolbarPlacement.Measure(
        DeviceViewport(display), display.Scale * ContentScale(display), ScreenPreferences.Landscape);

    private void ShowDock()
    {
        if (Target is not { } display) throw new InvalidOperationException("工作屏已断开。");
        if (toolbar is null)
        {
            toolbar = new ToolbarWindow(() => Run(() =>
            {
                SettingsExpander.IsExpanded = true;
                ShowLibrary();
            }), delta => Run(() =>
            {
                session.AdjustZoom(delta);
                toolbar?.SetNotice($"本次字号调整 {session.Active?.ZoomSteps:+0;-0;0} 级");
            }), () => Run(FinishWorkspace), () => Run(RotatePhoneScreen));
            toolbar.SetPoolContent(CreatePoolPanel());
            toolbar.ExpansionChanged += expanded =>
            {
                if (expanded)
                {
                    panelReturnTarget = gestureTarget ?? session.Active;
                    InvalidatePoolLaunch();
                    if (devicePreferences.MobileGestures)
                        toolbar.SetNotice("上次手势：" + (gestureLayer?.LastStatus ?? "尚未收到应用内输入"));
                    gestureLayer?.Suspend();
                }
                else if (!closed && !closingInProgress)
                    Dispatcher.BeginInvoke(() => Run(() =>
                    {
                        if (!working || IsVisible || !toolbar.IsVisible || toolbar.IsExpanded) return;
                        var active = panelReturnTarget;
                        panelReturnTarget = null;
                        if (toolbar.IsActive && active is not null && session.Windows.IsSameWindow(active))
                            session.Windows.Focus(active.Handle);
                        SyncGestureLayer();
                    }));
            };
            toolbar.ToggleGesturesRequested += () => Run(() =>
            {
                devicePreferences.SaveMobileGestures(!devicePreferences.MobileGestures);
                UpdateMobileControls();
                SyncGestureLayer();
            });
            toolbar.ToggleScrollButtonsRequested += () => Run(() =>
            {
                devicePreferences.SaveScrollButtons(!devicePreferences.ScrollButtons);
                UpdateMobileControls();
            });
            toolbar.ToggleAutoZoomRequested += () => Run(() =>
            {
                devicePreferences.SaveAutoZoom(!devicePreferences.AutoZoom);
                UpdateMobileControls();
                if (session.Active is not { } active || !session.Windows.IsSameWindow(active) || !session.Windows.CanZoom(active)) return;
                toolbar.Collapse();
                if (session.Windows.Focus(active.Handle)) ApplyMobileReadingSize();
            });
            toolbar.DpiChanged += (_, _) => ScheduleDisplayRefresh();
        }
        ConfigureDockBounds(display);
        toolbar.SetZoomEnabled(session.Active is { } active && session.Windows.CanZoom(active));
        UpdateMobileControls();
        toolbar.SetRotationEnabled(!previewTimer.IsEnabled && CanUsePhoneFrame(display, session.Displays.Capture(display.DeviceName).Mode));
        if (IsVisible || !toolbar.IsVisible)
        {
            toolbar.Collapse();
            toolbar.SetNotice(session.Active?.ProcessName ?? "启动常用应用，或切换正在运行的窗口");
        }
        toolbar.Show();
        if (IsVisible) Hide();
    }

    private void ConfigureDockBounds(DisplayInfo display)
    {
        var contentScale = ContentScale(display);
        gestureDensity = display.Scale * contentScale;
        gesturePlacement = ToolbarPlacement.Measure(DeviceViewport(display), gestureDensity, ScreenPreferences.Landscape);
        toolbar?.ConfigurePlacement(gesturePlacement, contentScale);
    }

    private void SyncGestureLayer()
    {
        if (!working || !automaticWorkspace || closed || closingInProgress || changingDisplay ||
            IsVisible || toolbar is not { IsVisible: true } || toolbar.IsExpanded ||
            !devicePreferences.MobileGestures || Target is not { IsUuVirtual: true } display)
        {
            gestureLayer?.Suspend();
            gestureTarget = null;
            return;
        }
        // A user may open or switch an app with Explorer or the taskbar. Follow that
        // foreground window for scrolling only; never take, zoom or reposition it.
        var active = session.Windows.ForegroundOn(display, gestureTarget ?? session.Active);
        if (active is null)
        {
            gestureTarget = null;
            gestureLayer?.Suspend();
            return;
        }
        gestureTarget = active;
        if (gesturePlacement is { } placement)
            (gestureLayer ??= new MobileGestureLayer()).Sync(active, placement.App, gestureDensity, devicePreferences.ScrollButtons);
    }

    // Startups can hand off to an existing process (e.g. a browser) or create
    // several windows. Never choose between unrelated windows by display title.
    private async Task FollowPoolLaunchAsync(string? exePath, int? pid)
    {
        var generation = ++launchGeneration;
        var device = Target?.DeviceName;
        bool StillCurrent() => generation == launchGeneration && working && !closed && !closingInProgress &&
            !changingDisplay && Target?.DeviceName == device && !IsVisible;
        try
        {
            // Let the clicked popup close before checking the newly started app.
            for (int i = 0; i < 16 && exePath is not null; i++)
            {
                await Task.Delay(250);
                if (!StillCurrent()) return;
                var candidates = session.Windows.ListWindows().Where(w =>
                    string.Equals(w.ProcessName, System.IO.Path.GetFileNameWithoutExtension(exePath), StringComparison.OrdinalIgnoreCase) &&
                    ProcessMatches(w.ProcessId, exePath)).ToList();
                var launched = candidates.Where(w => w.ProcessId == pid).ToList();
                // Allow the new process time to open its window before falling back
                // to a single existing instance of the same executable.
                if (launched.Count == 0 && i < 15) continue;
                var matches = launched.Count != 0 ? launched : candidates;
                if (matches.Count > 1) break;
                if (matches.Count == 1)
                {
                    OpenEntry(matches[0]);
                    return;
                }
            }
            if (!StillCurrent()) return;
            ShowMobileWindows();
            toolbar?.SetNotice("应用已启动，请在这里选择窗口。");
        }
        catch (Exception ex)
        {
            if (!closed && !closingInProgress && working)
            {
                ShowMobileWindows();
                toolbar?.SetNotice("请重新选择窗口：" + ex.Message);
            }
        }
    }

    private static bool ProcessMatches(int processId, string exePath)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return string.Equals(process.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException)
        { return false; }
    }
}
