using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk;

public partial class MainWindow : Window
{
    private readonly WorkspaceSession session;
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer displayRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer autoFollowTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer controllerTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly UuControllerService controllerService = new();
    private readonly ControllerDisconnectPolicy controllerDisconnect = new();
    private long observedReconnectionGeneration;
    private readonly DevicePreferenceStore devicePreferences = new();
    private readonly ViewportPreferenceStore viewportPreferences = new();
    private bool updatingViewportControls;
    private readonly AutoWorkspacePolicy autoPolicy = new();
    private readonly AutoPortraitAttemptPolicy portraitAttempt = new();
    private readonly PhoneProfileStore profileStore = new();
    private readonly ThemeService themeService;
    private PhoneProfile phoneProfile;
    private readonly PhoneProfile legacyProfile;
    private bool readingController;
    private long recoveryRetryAt;
    private string? eligibleProfileKey;
    private string? controllerDiagnostic;
    private string? recoveryRetryDevice;
    private string? portraitFailure;
    private List<WindowEntry> entries = [];
    private ToolbarWindow? toolbar;
    private bool updating;
    private bool working;
    private bool automaticWorkspace;
    private volatile bool changingDisplay;
    private long displayGeneration;
    private bool closed;
    private bool closingInProgress;
    private long? targetMissingSince;
    private string? lastModeDevice;
    private DisplayMode? lastObservedMode;
    private int previewSeconds;
    private string listSignature = "";
    private DisplayInfo? Target => DisplayPicker.SelectedItem as DisplayInfo;

    public MainWindow()
    {
        session = new WorkspaceSession(new RecoveryStore());
        legacyProfile = profileStore.Load();
        phoneProfile = devicePreferences.Apply(legacyProfile);
        InitializeComponent();
        AutoFollowBox.IsChecked = devicePreferences.AutoFollow;
        themeService = new ThemeService(Application.Current);
        themeService.ThemeChanged += (_, _) => RefreshThemeButtons();
        RefreshThemeButtons();
        PhoneSurface.SizeChanged += (_, _) => UpdateResponsiveLayout();
        WindowList.SizeChanged += (_, e) => { if (e.WidthChanged) UpdateResponsiveLayout(); };
        PhoneProfileText.Text = phoneProfile.Label;
        PortraitOnConnectBox.IsChecked = phoneProfile.PortraitOnConnect;
        RefreshViewportControls();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(HandleMessage);
            if (!OwnWindow.RegisterHotKey(handle, 1, 0x4003, 0x7B)) SetStatus("返回快捷键被其他应用占用；请用底部控制条返回。", true);
            if (!OwnWindow.RegisterHotKey(handle, 2, 0x4003, 0x7A)) SetStatus("旋转快捷键被其他应用占用；请用旋转按钮。", true);
        };
        Loaded += async (_, _) =>
        {
            await RefreshControllerAsync(checkConnection: false);
            if (closed || closingInProgress) return;
            if (session.HasRecovery)
            {
                var initial = ReadDeviceDecision();
                bool handover = initial.IsControllerIdentified && initial.Profile is { } detected && !SameProfile(detected, phoneProfile);
                var recoveryDecision = session.AutomaticRecoveryDecision(controllerService.Latest);
                if (recoveryDecision == AutomaticRecoveryDecision.Defer)
                    SetStatus("上次手机画面的控制者尚未确认，恢复记录已保留。可打开 UU 主窗口确认，或手动点“还原”。", true);
                else
                {
                    bool preserve = recoveryDecision == AutomaticRecoveryDecision.Preserve ||
                        (!session.RequiresAutomaticRecoveryEvidence && handover);
                    var errors = session.RestoreAll(preserveCurrentDisplay: preserve);
                    SetStatus(errors.Count == 0 ? "上次的工作区已恢复。" : string.Join("；", errors), errors.Count > 0);
                }
            }
            RefreshDisplays();
            RefreshWindows();
            MaxHeight = SystemParameters.WorkArea.Height;
            refreshTimer.Start();
            autoFollowTimer.Start();
            controllerTimer.Start();
            Run(CheckAutoConnection);
        };
        refreshTimer.Tick += (_, _) =>
        {
            if (IsVisible) RefreshWindows();
            if (session.Active is { } active && !session.Windows.IsSameWindow(active))
            {
                Run(() => { session.RestoreActive(); ShowLibrary(); SetStatus("窗口已关闭。窗口关闭后保存到应用内的字号，请在该应用中自行检查。"); });
            }
        };
        previewTimer.Tick += (_, _) =>
        {
            previewSeconds--;
            PreviewText.Text = $"画面和点击正常吗？{previewSeconds} 秒后自动回退";
            if (previewSeconds <= 0) RevertPreview();
        };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        autoFollowTimer.Tick += (_, _) => Run(CheckAutoConnection);
        controllerTimer.Tick += async (_, _) => await RefreshControllerAsync();
        DpiChanged += (_, _) => { UpdateResponsiveLayout(); ScheduleDisplayRefresh(); };
        displayRefreshTimer.Tick += (_, _) => { displayRefreshTimer.Stop(); Run(RefreshDisplayLayout); };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            refreshTimer.Stop();
            previewTimer.Stop();
            displayRefreshTimer.Stop();
            autoFollowTimer.Stop();
            controllerTimer.Stop();
            controllerService.Dispose();
            closed = true;
            themeService.Dispose();
            OwnWindow.UnregisterHotKey(new WindowInteropHelper(this).Handle, 1);
            OwnWindow.UnregisterHotKey(new WindowInteropHelper(this).Handle, 2);
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            toolbar?.CloseForExit();
        };
    }

    private nint HandleMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam == 1) { Run(ShowLibrary); handled = true; }
        if (msg == 0x0312 && wParam == 2) { Run(RotatePhoneScreen); handled = true; }
        return 0;
    }
    private void RefreshDisplays()
    {
        string? selected = Target?.DeviceName;
        updating = true;
        try
        {
            var displays = session.Displays.ListDisplays();
            DisplayPicker.ItemsSource = displays;
            DisplayPicker.SelectedItem = displays.FirstOrDefault(d => d.DeviceName == selected) ?? displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
        }
        finally { updating = false; }
        UpdateDisplayDetails();
    }
    private void UpdateDisplayDetails()
    {
        RotateButton.IsEnabled = false;
        toolbar?.SetRotationEnabled(false);
        if (Target is not { } display) return;
        DisplayDetail.Text = $"{display.Bounds.Width} × {display.Bounds.Height} 像素 · 缩放 {display.Scale:P0}";
        var current = session.Displays.Capture(display.DeviceName).Mode;
        var canRotate = !previewTimer.IsEnabled && CanUsePhoneFrame(display, current);
        RotateButton.IsEnabled = canRotate;
        toolbar?.SetRotationEnabled(canRotate);
        var selected = ModePicker.SelectedItem as DisplayMode;
        bool unchanged = lastModeDevice == display.DeviceName && lastObservedMode == current;
        var modes = session.Displays.ListModes(display.DeviceName);
        ModePicker.ItemsSource = modes.OrderBy(m => m.Width >= m.Height).ThenBy(m => m.Width).ThenBy(m => m.Height).ThenByDescending(m => m.Frequency).ToList();
        ModePicker.SelectedItem = unchanged && selected is not null && modes.Contains(selected) ? selected : modes.FirstOrDefault(m => m.Width == current.Width && m.Height == current.Height && m.Frequency == current.Frequency && m.Orientation == current.Orientation) ?? modes.FirstOrDefault();
        lastModeDevice = display.DeviceName;
        lastObservedMode = current;
        ApplyModeButton.IsEnabled = modes.Count > 0 && !session.HasDisplayChange;
        DisplayPicker.IsEnabled = !session.HasDisplayChange;
        ModePicker.IsEnabled = !session.HasDisplayChange;
    }
    private void DisplayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating || !IsLoaded) return;
        // An explicit screen choice takes precedence over automatic selection.
        AutoFollowBox.IsChecked = false;
        devicePreferences.SaveAutoFollow(false);
        automaticWorkspace = false;
        Run(() => { session.RestoreActive(); UpdateDisplayDetails(); if (working) FitLibrary(); });
    }
    private void RefreshWindows()
    {
        entries = session.Windows.ListWindows().ToList();
        ApplySearch();
    }
    private void ApplySearch()
    {
        if (WindowList is null) return;
        string query = SearchBox.Text.Trim();
        var visible = entries.Where(w => w.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || w.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        string signature = string.Join("|", visible.Select(w => $"{w.Handle}:{w.Title}"));
        if (signature != listSignature) { WindowList.ItemsSource = AppCatalog.GroupWindows(visible); listSignature = signature; }
        WindowCount.Text = $"{visible.Count} 个窗口";
        EmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) => ApplySearch();
    private void RefreshClicked(object sender, RoutedEventArgs e) => Run(() => { RefreshDisplays(); RefreshWindows(); SetStatus("已刷新显示器和窗口。"); });

    private void EnterClicked(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (previewTimer.IsEnabled) throw new InvalidOperationException("请先保留或回退分辨率。");
        if (session.Active is not null) { ShowActive(); return; }
        if (working) { FinishWorkspace(); return; }
        EnterWorkspace();
    });
    private void EnterWorkspace(bool automatically = false)
    {
        if (Target is null) throw new InvalidOperationException("没有可用的显示器。");
        working = true;
        automaticWorkspace = automatically;
        SettingsExpander.IsExpanded = false;
        FitLibrary();
        WorkspaceButton.Content = "返回桌面";
        SetStatus("点击一个窗口即可铺满；底部控制条可随时返回。");
    }
    private void FitLibrary()
    {
        if (Target is not { } display) return;
        var contentScale = ContentScale(display);
        UpdateResponsiveLayout();
        PhoneSurface.LayoutTransform = new System.Windows.Media.ScaleTransform(contentScale, contentScale);
        PhoneSurface.Margin = new Thickness(0);
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        MaxHeight = double.PositiveInfinity;
        OwnWindow.SetBounds(this, DeviceViewport(display));
    }
    private void UpdateResponsiveLayout()
    {
        if (PhoneSurface is not null && WindowList is not null)
            WorkspaceLayout.Apply(this, PhoneSurface.ActualWidth);
    }
    private void ClearSearchClicked(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }
    private void ThemeClicked(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (sender is Button { Tag: string preference } && Enum.TryParse<ThemePreference>(preference, out var value))
            themeService.SetPreference(value);
    });
    private void RefreshThemeButtons()
    {
        foreach (var (button, preference) in new[] { (SystemThemeButton, ThemePreference.System), (LightThemeButton, ThemePreference.Light), (DarkThemeButton, ThemePreference.Dark) })
        {
            bool selected = themeService.Preference == preference;
            button.SetResourceReference(Control.BackgroundProperty, selected ? "SoftBlue" : "Card");
            button.SetResourceReference(Control.ForegroundProperty, selected ? "Accent" : "PrimaryText");
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
        ThemeHint.Text = themeService.Preference == ThemePreference.System
            ? $"跟随 Windows · 当前{(themeService.IsDark ? "深色" : "浅色")}" : "已保存，下次打开继续使用";
    }
    private PixelRect AppArea(DisplayInfo display)
    {
        var area = DeviceViewport(display);
        double scale = display.Scale * ContentScale(display);
        var layout = ToolbarLayout.Measure(area.Width / scale, area.Height / scale);
        int height = Math.Clamp((int)Math.Round(layout.Height * scale), 1, Math.Max(1, area.Height - 100));
        return new(area.X, area.Y, area.Width, Math.Max(100, area.Height - height));
    }
    // UU overlays must sit outside the streamed frame. Reserve their space in
    // the display mode itself; a window inset cannot move Explorer's taskbar.
    private PixelRect DeviceViewport(DisplayInfo display) => display.WorkArea;
    private void RefreshViewportControls()
    {
        updatingViewportControls = true;
        try
        {
            var preference = viewportPreferences.Load(phoneProfile);
            ReserveUuToolbarBox.IsChecked = preference.Enabled;
            LandscapeToolbarPicker.SelectedIndex = (int)preference.Landscape;
        }
        finally { updatingViewportControls = false; }
    }
    private void ViewportPreferenceChanged(object sender, RoutedEventArgs e) => SaveViewportPreferences();
    private void LandscapeToolbarChanged(object sender, SelectionChangedEventArgs e) => SaveViewportPreferences();
    private void SaveViewportPreferences()
    {
        if (!IsLoaded || updatingViewportControls || LandscapeToolbarPicker.SelectedIndex < 0) return;
        Run(() =>
        {
            if (working || session.HasDisplayChange)
                FinishWorkspace(suppressAutoReconnect: false);
            viewportPreferences.Save(phoneProfile, new(ReserveUuToolbarBox.IsChecked == true,
                (LandscapeToolbarPlacement)LandscapeToolbarPicker.SelectedIndex));
            portraitAttempt.ExplicitRetry();
            recoveryRetryDevice = null;
            portraitFailure = null;
            SetStatus("已更新画面比例。若 UU 点按位置没有同步，请重新选择一次超级屏。");
            CheckAutoConnection();
        });
    }
    private ViewportPreferences ScreenPreferences => viewportPreferences.Load(phoneProfile);
    private bool CanUsePhoneFrame(DisplayInfo display, DisplayMode mode) =>
        PhoneSessionPolicy.CanAutoEnter(display, mode, phoneProfile, ScreenPreferences) ||
        session.OwnsPhoneMode(display, mode, phoneProfile, ScreenPreferences);
    private double ContentScale(DisplayInfo display)
    {
        if (!display.IsUuVirtual) return 1;
        var isDeviceFrame = phoneProfile.HasDimensions && CanUsePhoneFrame(display, session.Displays.Capture(display.DeviceName).Mode);
        // Cropping the streamed frame must not make every button and font smaller.
        double density = Math.Min(display.Bounds.Width, display.Bounds.Height) / (double)phoneProfile.LogicalWidth;
        if (isDeviceFrame)
        {
            bool portrait = display.Bounds.Height > display.Bounds.Width;
            double nativeWidth = portrait ? phoneProfile.PortraitWidth : phoneProfile.PortraitHeight;
            double nativeHeight = portrait ? phoneProfile.PortraitHeight : phoneProfile.PortraitWidth;
            density = phoneProfile.PortraitWidth / (double)phoneProfile.LogicalWidth *
                Math.Max(display.Bounds.Width / nativeWidth, display.Bounds.Height / nativeHeight);
        }
        return Math.Clamp(density / display.Scale, 0.75, 3);
    }
    private void OpenWindowClicked(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (previewTimer.IsEnabled) throw new InvalidOperationException("请先保留或回退分辨率。");
        if ((sender as Button)?.Tag is not WindowEntry entry || Target is not { } display) return;
        if (!working) EnterWorkspace();
        var area = AppArea(display);
        var actual = session.Take(entry, area);
        bool fits = actual.Width <= area.Width + 3 && actual.Height <= area.Height + 3;
        ShowActive();
        toolbar?.SetNotice(fits ? entry.ProcessName : "窗口有最小尺寸，请返回调整分辨率");
    });
    private void ShowActive()
    {
        if (session.Active is not { } active || Target is not { } display) return;
        if (!session.Windows.IsSameWindow(active)) throw new InvalidOperationException("窗口已经关闭，请刷新列表。");
        if (toolbar is null)
        {
        toolbar = new ToolbarWindow(ShowLibrary, delta => Run(() =>
        {
            session.AdjustZoom(delta);
            toolbar?.SetNotice($"本次字号调整 {session.Active?.ZoomSteps:+0;-0;0} 级");
        }), () => Run(FinishWorkspace), () => Run(RotatePhoneScreen));
        toolbar.DpiChanged += (_, _) => ScheduleDisplayRefresh();
        }
        var area = AppArea(display);
        var viewport = DeviceViewport(display);
        session.Windows.Place(active.Handle, area);
        toolbar.SetContentScale(ContentScale(display));
        double scale = display.Scale * ContentScale(display);
        toolbar.ConfigureLayout(viewport.Width / scale, viewport.Height / scale);
        toolbar.SetZoomEnabled(session.Windows.CanZoom(active));
        toolbar.SetRotationEnabled(!previewTimer.IsEnabled && CanUsePhoneFrame(display, session.Displays.Capture(display.DeviceName).Mode));
        toolbar.Show();
        OwnWindow.SetBounds(toolbar, new(viewport.X, area.Y + area.Height, viewport.Width, viewport.Height - area.Height));
        if (!session.Windows.Focus(active.Handle))
        {
            toolbar.Hide();
            throw new InvalidOperationException("无法激活窗口。请点击该应用一次后重试，管理员窗口可能受限。");
        }
        WorkspaceButton.Content = "继续当前窗口";
        Hide();
    }
    private void ShowLibrary()
    {
        toolbar?.Hide();
        Show();
        if (working) FitLibrary();
        Activate();
        RefreshWindows();
        WorkspaceButton.Content = session.Active is null ? working ? "返回桌面" : "进入工作台" : "继续当前窗口";
    }
    private void FinishClicked(object sender, RoutedEventArgs e) => Run(FinishWorkspace);
    private void RotateClicked(object sender, RoutedEventArgs e) => Run(RotatePhoneScreen);
    private void RotatePhoneScreen()
    {
        if (closed || closingInProgress || changingDisplay) return;
        if (previewTimer.IsEnabled) throw new InvalidOperationException("请先保留或回退正在试用的分辨率。");
        var display = session.Displays.ListDisplays().FirstOrDefault(d => d.DeviceName == Target?.DeviceName)
            ?? throw new InvalidOperationException("请先连接手机 UU 超级屏。");
        var current = session.Displays.Capture(display.DeviceName).Mode;
        if (!CanUsePhoneFrame(display, current)) throw new InvalidOperationException("当前画面不属于已识别的手机工作台，未切换方向。");
        var decision = RotationModePolicy.SelectSwappedOwned(display, current,
            session.Displays.ListModes(display.DeviceName), phoneProfile, ScreenPreferences);
        if (decision.Mode is not { } mode) throw new InvalidOperationException(decision.Reason);
        session.Displays.ValidateModeAvailable(display.DeviceName, mode);
        DisplayMode? selectedFallback = PhoneSessionPolicy.CanAutoEnter(display, mode, phoneProfile, ScreenPreferences)
            ? null : mode;
        // A manual choice wins even during the first stable-mode observation.
        portraitAttempt.Observe(display, current);
        portraitAttempt.SuppressForCurrentConnection();
        BeginDisplayChange();
        try { session.RotatePhoneDisplay(display.DeviceName, mode, phoneProfile, ScreenPreferences, selectedFallback: selectedFallback); }
        finally { EndDisplayChange(); }
        RefreshDisplays();
        if (!working) EnterWorkspace();
        else if (IsVisible) FitLibrary();
        else ShowActive();
        string direction = mode.Width < mode.Height ? "竖屏" : "横屏";
        ConnectionTitle.Text = $"UU 超级屏 · {mode.Width} × {mode.Height} · {direction}";
        SetStatus(FrameClearsControls(mode) ? $"已切换{direction} · {mode.Width} × {mode.Height}"
            : $"已切换{direction}；驱动暂无可避让工具栏的尺寸，请先在 UU 收起工具栏。", !FrameClearsControls(mode));
        toolbar?.SetNotice($"{direction} · {mode.Width} × {mode.Height}");
        RecoveryStore.WriteLog($"Manual rotation {display.DeviceName}: {current.Width}x{current.Height} -> {mode.Width}x{mode.Height}, orientation {mode.Orientation}.");
    }
    private void FinishWorkspace() => FinishWorkspace(suppressAutoReconnect: true);
    private void FinishWorkspace(bool suppressAutoReconnect, bool preserveCurrentDisplay = false)
    {
        if (suppressAutoReconnect) autoPolicy.SuppressUntilDisconnected(Target);
        targetMissingSince = null;
        previewTimer.Stop();
        Topmost = false;
        PreviewPanel.Visibility = Visibility.Collapsed;
        toolbar?.Hide();
        BeginDisplayChange();
        IReadOnlyList<string> errors;
        try { errors = session.RestoreAll(preserveCurrentDisplay); } finally { EndDisplayChange(); }
        RecoveryStore.WriteLog($"Workspace finished: preserveDisplay={preserveCurrentDisplay}, remainingRecovery={session.HasRecovery}, errors={errors.Count}.");
        working = false;
        automaticWorkspace = false;
        PhoneSurface.LayoutTransform = System.Windows.Media.Transform.Identity;
        UpdateResponsiveLayout();
        PhoneSurface.Margin = new Thickness(0);
        Show();
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        WindowState = WindowState.Normal;
        Width = Math.Min(960, SystemParameters.WorkArea.Width);
        Height = Math.Min(720, SystemParameters.WorkArea.Height);
        Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
        Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - Height) / 2;
        SettingsExpander.IsExpanded = true;
        WorkspaceButton.Content = "进入工作台";
        RefreshDisplays();
        Activate();
        string restored = session.AdjustedRestoreCount > 0 ? $"已还原；{session.AdjustedRestoreCount} 个窗口原位超出现有屏幕，已调整到可见区域。" : "窗口与本程序更改的分辨率已还原。可在 UU 中退出超级屏。";
        SetStatus(errors.Count == 0 ? restored : string.Join("；", errors), errors.Count > 0);
    }
    private void ApplyModeClicked(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (Target is not { } display || ModePicker.SelectedItem is not DisplayMode mode) return;
        automaticWorkspace = false;
        portraitAttempt.MarkAttempted();
        session.Displays.ValidateModeAvailable(display.DeviceName, mode);
        Topmost = true;
        BeginDisplayChange();
        try { session.ChangeDisplay(display.DeviceName, mode); }
        catch { Topmost = false; throw; }
        finally { EndDisplayChange(); }
        previewSeconds = 15;
        PreviewText.Text = "画面和点击正常吗？15 秒后自动回退";
        PreviewPanel.Visibility = Visibility.Visible;
        SettingsExpander.IsExpanded = false;
        previewTimer.Start();
        RefreshDisplays();
        // Keep confirmation reachable even when the desktop becomes narrow.
        working = true;
        FitLibrary();
        Activate();
        SetStatus("请确认手机画面与点击位置正常。");
    });
    private void KeepModeClicked(object sender, RoutedEventArgs e)
    {
        previewTimer.Stop();
        Topmost = false;
        PreviewPanel.Visibility = Visibility.Collapsed;
        SettingsExpander.IsExpanded = false;
        Run(UpdateDisplayDetails);
        SetStatus("已保留本次分辨率；结束工作台时自动还原。");
    }
    private void RevertModeClicked(object sender, RoutedEventArgs e) => RevertPreview();
    private void RevertPreview() => Run(FinishWorkspace);
    private void BeginDisplayChange()
    {
        changingDisplay = true;
        Interlocked.Increment(ref displayGeneration);
        displayRefreshTimer.Stop();
    }
    private void EndDisplayChange()
    {
        Interlocked.Increment(ref displayGeneration);
        changingDisplay = false;
        ScheduleDisplayRefresh();
    }
    private void ScheduleDisplayRefresh()
    {
        if (closed || closingInProgress || changingDisplay) return;
        displayRefreshTimer.Stop();
        displayRefreshTimer.Start();
    }
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        var generation = Interlocked.Read(ref displayGeneration);
        bool ownChange = changingDisplay;
        Dispatcher.BeginInvoke(() =>
        {
            if (closed || closingInProgress || ownChange || generation != Interlocked.Read(ref displayGeneration)) return;
            ScheduleDisplayRefresh();
        });
    }
    private void RefreshDisplayLayout()
    {
        if (changingDisplay || closed || closingInProgress) return;
        var attached = session.Displays.ListDisplays();
        if (WaitForTargetReconnect(attached)) return;
        if (working && automaticWorkspace && Target is { IsUuVirtual: true } autoTarget)
        {
            var liveTarget = attached.FirstOrDefault(d => d.DeviceName == autoTarget.DeviceName);
            var decision = ReadDeviceDecision(liveTarget);
            if (liveTarget is not null && (decision.Profile is null || !SameProfile(decision.Profile, phoneProfile)))
            {
                FinishWorkspace(suppressAutoReconnect: false, preserveCurrentDisplay: ControllerHandoverPolicy.ShouldPreserveCurrentDisplay(controllerService.Latest));
                return;
            }
        }
        var previous = Target;
        RefreshDisplays();
        if (Target is { } current && (previous?.Bounds != current.Bounds || previous?.Scale != current.Scale))
            RecoveryStore.WriteLog($"Display layout {current.DeviceName}: {current.Bounds.Width}x{current.Bounds.Height}, scale {current.Scale}.");
        if (working && IsVisible) FitLibrary();
        else if (working && session.Active is { } active && Target is { } display)
        {
            if (!session.Windows.IsSameWindow(active)) { session.RestoreActive(); ShowLibrary(); return; }
            session.Windows.Place(active.Handle, AppArea(display));
            ShowActive();
        }
    }
    private void CheckAutoConnection()
    {
        if (closed || closingInProgress || changingDisplay || previewTimer.IsEnabled) return;
        var displays = session.Displays.ListDisplays();
        var connectedUu = displays.FirstOrDefault(d => d.IsUuVirtual && d.IsPrimary) ?? displays.FirstOrDefault(d => d.IsUuVirtual);
        var reportedMode = connectedUu is null ? null : session.Displays.Capture(connectedUu.DeviceName).Mode;
        var deviceDecision = ResolveDeviceDecision(connectedUu, reportedMode,
            working && automaticWorkspace ? phoneProfile : legacyProfile);
        if (connectedUu is not null && observedReconnectionGeneration != controllerDisconnect.ReconnectionGeneration)
        {
            observedReconnectionGeneration = controllerDisconnect.ReconnectionGeneration;
            // UU may keep its virtual monitor attached across two control
            // sessions. Fresh single-controller evidence, not a resolution
            // change, permits the new session to start after a manual finish.
            if (autoPolicy.AllowCurrentConnection(connectedUu, deviceDecision))
            {
                portraitAttempt.ExplicitRetry();
                portraitFailure = null;
                recoveryRetryDevice = null;
                RecoveryStore.WriteLog("Confirmed a new mobile controller connection; reset this display's automatic adaptation attempt.");
            }
        }
        var desired = deviceDecision.Profile is { } found ? devicePreferences.Apply(found) : null;
        var eligibility = desired is null ? null : $"{desired.PortraitWidth}x{desired.PortraitHeight}";
        if (eligibility != eligibleProfileKey &&
            (desired is not null || connectedUu is null || HasFreshControllerEvidence()))
        {
            portraitAttempt.ExplicitRetry();
            eligibleProfileKey = eligibility;
            portraitFailure = null;
            recoveryRetryDevice = null;
        }
        if (desired is not null && !SameProfile(desired, phoneProfile))
        {
            if (working && automaticWorkspace)
                FinishWorkspace(suppressAutoReconnect: false, preserveCurrentDisplay: deviceDecision.IsControllerIdentified);
            phoneProfile = desired;
            portraitAttempt.ExplicitRetry();
            recoveryRetryDevice = null;
            portraitFailure = null;
            PhoneProfileText.Text = phoneProfile.Label;
            PortraitOnConnectBox.IsChecked = phoneProfile.PortraitOnConnect;
            RefreshViewportControls();
            RecoveryStore.WriteLog($"Device profile selected: {phoneProfile.Name}, {phoneProfile.PortraitWidth}x{phoneProfile.PortraitHeight}, source {deviceDecision.Source}.");
        }
        else if (desired is not null)
        {
            phoneProfile = desired;
            PhoneProfileText.Text = phoneProfile.Label;
            PortraitOnConnectBox.IsChecked = phoneProfile.PortraitOnConnect;
        }
        DeviceDetectionText.Text = deviceDecision.Reason;
        if (controllerService.Latest.Status == UuControllerStatus.Unknown && controllerService.Latest.Reason is { Length: > 0 } why)
            DeviceDetectionText.Text += " " + why;
        bool matchesPhone = desired is not null;
        // Eligibility is not connectivity: an intermediate resolution must not
        // reset a consumed portrait attempt or the user's stop-following choice.
        portraitAttempt.Observe(connectedUu, matchesPhone ? reportedMode : null);
        if (connectedUu is null) { recoveryRetryDevice = null; portraitFailure = null; }
        var selectedUu = autoPolicy.Observe(displays, AutoFollowBox.IsChecked == true);
        var uu = matchesPhone && selectedUu?.DeviceName == connectedUu?.DeviceName ? selectedUu : null;
        if (WaitForTargetReconnect(displays)) return;
        ConnectionTitle.Text = connectedUu is null ? "等待设备超级屏接入"
            : $"{(matchesPhone ? phoneProfile.Name : "UU 超级屏")} · {connectedUu.Bounds.Width} × {connectedUu.Bounds.Height}";
        if (connectedUu is null)
            ConnectionHint.Text = "在 UU 中开启超级屏，连接后自动匹配当前手机或平板。";
        else if (!matchesPhone)
        {
            ConnectionTitle.Text = $"UU · {connectedUu.Bounds.Width} × {connectedUu.Bounds.Height} · 未自动接管";
            ConnectionHint.Text = deviceDecision.Reason;
            // A PC can take over the same UU monitor without recreating it.
            // RestoreAll preserves an externally replaced display mode.
            if (working && automaticWorkspace) FinishWorkspace(suppressAutoReconnect: false, preserveCurrentDisplay: ControllerHandoverPolicy.ShouldPreserveCurrentDisplay(controllerService.Latest));
        }
        if (uu is null) return;
        if (!working && session.HasRecovery)
        {
            var recoveryDecision = session.AutomaticRecoveryDecision(controllerService.Latest);
            if (recoveryDecision == AutomaticRecoveryDecision.Defer)
            {
                SetStatus("控制者尚未确认，保留上次的恢复记录；可打开 UU 主窗口或手动点“还原”。", true);
                return;
            }
            var recoveryKey = $"{uu.DeviceName}|{phoneProfile.PortraitWidth}x{phoneProfile.PortraitHeight}|{reportedMode?.Width}x{reportedMode?.Height}";
            if (recoveryRetryDevice == recoveryKey && Environment.TickCount64 - recoveryRetryAt < 3000) return;
            recoveryRetryDevice = recoveryKey;
            recoveryRetryAt = Environment.TickCount64;
            BeginDisplayChange();
            IReadOnlyList<string> recoveryErrors;
            bool preserve = recoveryDecision == AutomaticRecoveryDecision.Preserve ||
                (!session.RequiresAutomaticRecoveryEvidence && deviceDecision.IsControllerIdentified);
            try { recoveryErrors = session.RestoreAll(preserveCurrentDisplay: preserve); } finally { EndDisplayChange(); }
            if (recoveryErrors.Count > 0)
            {
                SetStatus("上次还原尚未完成：" + string.Join("；", recoveryErrors), true);
                return;
            }
            RefreshDisplays();
            return;
        }
        // Initialize the whole frame before exposing app actions. A native phone
        // mode is recognizable but still needs fitting; entering it too early
        // could let an app snapshot prevent the initial display transaction.
        var initialFrame = reportedMode is null ? null : PhoneScreenGeometry.Calculate(phoneProfile,
            phoneProfile.PortraitOnConnect || reportedMode.Height > reportedMode.Width, ScreenPreferences);
        bool requiresInitialFrame = initialFrame is not null &&
            (reportedMode!.Width != initialFrame.Width || reportedMode.Height != initialFrame.Height);
        if (!working && !portraitAttempt.Ready && portraitFailure is null && requiresInitialFrame) return;
        if (TryApplyAutomaticDevice(uu, reportedMode, deviceDecision.IsControllerIdentified)) return;
        if (!working && deviceDecision.IsControllerIdentified && portraitFailure is not null &&
            !PhoneSessionPolicy.CanAutoEnter(uu, reportedMode, phoneProfile, ScreenPreferences)) return;
        if (working && Target?.DeviceName == uu.DeviceName)
        {
            if (Target.Bounds != uu.Bounds || Target.WorkArea != uu.WorkArea || Target.Scale != uu.Scale)
                ScheduleDisplayRefresh();
            return;
        }
        if (session.HasUncertainZoom) return;
        if (session.Active is not null) session.RestoreActive();
        updating = true;
        try { DisplayPicker.ItemsSource = displays; DisplayPicker.SelectedItem = uu; }
        finally { updating = false; }
        UpdateDisplayDetails();
        Show();
        EnterWorkspace(automatically: true);
        Activate();
        SetStatus(portraitFailure ?? $"已自动接入 UU · {uu.Bounds.Width} × {uu.Bounds.Height}，屏幕变化时自动调整。", portraitFailure is not null);
        RecoveryStore.WriteLog($"Auto-connected {uu.DeviceName}: {uu.Bounds.Width}x{uu.Bounds.Height}, scale {uu.Scale}.");
    }
    private bool TryApplyAutomaticDevice(DisplayInfo display, DisplayMode? current, bool identifiedController)
    {
        if (!portraitAttempt.Ready || current is null) return false;
        if (!identifiedController && !PhoneSessionPolicy.CanAutoEnter(display, current, phoneProfile, ScreenPreferences)) return false;
        // Only initialize this connection. Subsequent changes made by UU remain
        // authoritative, so a real landscape update is never forced back again.
        portraitAttempt.MarkAttempted();
        if (session.HasDisplayChange) return false;
        var modes = session.Displays.ListModes(display.DeviceName);
        var decision = DeviceModePolicy.Select(display, current, modes, phoneProfile, ScreenPreferences);
        if (!decision.ShouldApply || decision.Mode is not { } mode)
        {
            ConnectionHint.Text = decision.Reason;
            if (decision.Kind == PortraitModeSelectionKind.Unavailable)
            {
                portraitFailure = decision.Reason;
                SetStatus(decision.Reason, true);
            }
            return false;
        }
        try
        {
            // A rejected mode must not trigger captures or window restoration.
            mode = ValidatePreferredFrame(display, current, decision,
                phoneProfile.PortraitOnConnect || current.Height > current.Width, out var selectedFallback);
            BeginDisplayChange();
            try
            {
                session.ApplyDeviceProfile(display.DeviceName, mode, phoneProfile, ScreenPreferences, selectedFallback: selectedFallback);
            }
            finally { EndDisplayChange(); }
            updating = true;
            try
            {
                var refreshed = session.Displays.ListDisplays();
                DisplayPicker.ItemsSource = refreshed;
                DisplayPicker.SelectedItem = refreshed.FirstOrDefault(d => d.DeviceName == display.DeviceName);
            }
            finally { updating = false; }
            UpdateDisplayDetails();
            ShowLibrary();
            EnterWorkspace(automatically: true);
            ConnectionTitle.Text = $"{phoneProfile.Name} · {mode.Width} × {mode.Height}";
            ConnectionHint.Text = $"已按{phoneProfile.Name}自动适配。";
            portraitFailure = FrameClearsControls(mode) ? null
                : "已按设备方向恢复原生尺寸；驱动暂无可避让工具栏的尺寸，请先在 UU 收起工具栏。";
            SetStatus(portraitFailure ?? $"已适配 {mode.Width} × {mode.Height}；若点按错位，请在 UU 重新选择超级屏。", portraitFailure is not null);
            RecoveryStore.WriteLog($"Auto-device {phoneProfile.Name} {display.DeviceName}: {current.Width}x{current.Height} -> {mode.Width}x{mode.Height}, {mode.Frequency}Hz, orientation {mode.Orientation}.");
        }
        catch (Exception ex)
        {
            RecoveryStore.WriteLog("Auto-device failed: " + ex);
            ConnectionHint.Text = "设备尺寸未能应用，请打开设备屏设置查看。";
            portraitFailure = "自动适配未完成：" + ex.Message;
            SetStatus(portraitFailure, true);
        }
        return true;
    }
    private DisplayMode ValidatePreferredFrame(DisplayInfo display, DisplayMode current,
        PortraitModeDecision preferred, bool portrait, out DisplayMode? selectedFallback)
    {
        selectedFallback = null;
        if (preferred.Mode is { } ideal)
        {
            try { session.Displays.ValidateModeAvailable(display.DeviceName, ideal); return ideal; }
            catch (InvalidOperationException)
            {
                RecoveryStore.WriteLog($"Driver rejected fitted frame {ideal.Width}x{ideal.Height}; checking advertised safe modes.");
            }
        }
        var supported = DeviceModePolicy.SelectSupportedFrame(display, current,
            session.Displays.ListModes(display.DeviceName), phoneProfile, ScreenPreferences, portrait: portrait);
        if (supported.Mode is not { } fallback)
        {
            // Preserve a working native device/orientation when the driver's
            // limited mode table has no safe aspect ratio (notably some tablets).
            // This is explicitly reported as incomplete toolbar avoidance.
            var native = DeviceModePolicy.Select(display, current, session.Displays.ListModes(display.DeviceName),
                phoneProfile, new(false), portraitOverride: portrait);
            var nativeMode = native.Mode ?? (native.Kind == PortraitModeSelectionKind.AlreadyPortrait ? current : null);
            if (nativeMode is null) throw new InvalidOperationException(supported.Reason);
            session.Displays.ValidateModeAvailable(display.DeviceName, nativeMode);
            RecoveryStore.WriteLog($"No supported safe frame; retaining device orientation at {nativeMode.Width}x{nativeMode.Height}.");
            return nativeMode;
        }
        session.Displays.ValidateModeAvailable(display.DeviceName, fallback);
        selectedFallback = fallback;
        RecoveryStore.WriteLog($"Selected advertised safe frame {fallback.Width}x{fallback.Height}, {fallback.Frequency}Hz, orientation {fallback.Orientation}.");
        return fallback;
    }
    private bool FrameClearsControls(DisplayMode mode)
    {
        var insets = ScreenPreferences.Insets(mode.Height > mode.Width);
        return insets == ViewportInsets.None || PhoneScreenGeometry.IsSafeSupportedFrame(mode, phoneProfile, ScreenPreferences);
    }
    private bool WaitForTargetReconnect(IReadOnlyList<DisplayInfo> displays)
    {
        if (!working || Target is not { } target || displays.Any(d => d.DeviceName == target.DeviceName))
        {
            targetMissingSince = null;
            return false;
        }
        // Some virtual drivers briefly remove their monitor while changing modes.
        // Preserve the active workspace through that gap, and never interpret a
        // driver disconnect as the user's explicit choice to stop auto-following.
        targetMissingSince ??= Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(targetMissingSince.Value).TotalMilliseconds >= 1000)
            FinishWorkspace(suppressAutoReconnect: false);
        else
        {
            ConnectionTitle.Text = "等待超级屏重新接入…";
            ScheduleDisplayRefresh();
        }
        return true;
    }
    private void AutoFollowChanged(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (AutoFollowBox.IsChecked != true)
        {
            automaticWorkspace = false;
        }
        devicePreferences.SaveAutoFollow(AutoFollowBox.IsChecked == true);
        CheckAutoConnection();
    });
    private async Task RefreshControllerAsync(bool checkConnection = true)
    {
        if (readingController || closed || closingInProgress) return;
        readingController = true;
        try
        {
            var snapshot = await controllerService.PollAsync();
            var diagnostic = $"{snapshot.Status}: {string.Join(", ", snapshot.Names)}; {snapshot.Reason}";
            if (controllerDiagnostic != diagnostic)
            {
                controllerDiagnostic = diagnostic;
                RecoveryStore.WriteLog("UU controller " + diagnostic);
            }
            if (checkConnection && !closed && !closingInProgress) Run(CheckAutoConnection);
        }
        catch (Exception ex) { RecoveryStore.WriteLog("读取 UU 当前控制者：" + ex.Message); }
        finally { readingController = false; }
    }
    private DeviceSessionDecision ReadDeviceDecision(DisplayInfo? display = null)
    {
        display ??= session.Displays.ListDisplays().FirstOrDefault(d => d.IsUuVirtual && d.IsPrimary);
        var mode = display is null ? null : session.Displays.Capture(display.DeviceName).Mode;
        return ResolveDeviceDecision(display, mode, working && automaticWorkspace ? phoneProfile : legacyProfile);
    }
    private DeviceSessionDecision ResolveDeviceDecision(DisplayInfo? display, DisplayMode? mode, PhoneProfile savedProfile)
    {
        string? displayKey = display is { IsUuVirtual: true } ? display.DeviceName + "|" + display.AdapterDeviceId : null;
        bool wasDisconnected = controllerDisconnect.KnownDisconnected;
        bool disconnected = controllerDisconnect.Observe(controllerService.Latest, displayKey);
        if (disconnected != wasDisconnected)
            RecoveryStore.WriteLog(disconnected ? "Confirmed controller disconnect; dimension fallback is suspended."
                : "New controller/display connection; dimension fallback is available again.");
        if (disconnected)
            return new(null, false, "手机已断开，等待新的控制连接。");
        var snapshot = controllerService.Latest;
        var preference = viewportPreferences.Load(savedProfile);
        var decision = DeviceSessionPolicy.Resolve(snapshot, display, mode, savedProfile, preferences: preference);
        // A common desktop resolution never identifies a phone. Only an already
        // active, journaled phone session can continue its selected fallback.
        if (decision.Profile is null && working && automaticWorkspace && display is not null && mode is not null &&
            (snapshot.Status == UuControllerStatus.Unknown || !snapshot.IsFresh(DeviceSessionPolicy.MaximumControllerAge)) &&
            session.OwnsPhoneMode(display, mode, savedProfile, preference))
            return new(savedProfile, false, "控制者面板暂不可读；继续当前已记录的手机画面。");
        return decision;
    }
    private bool HasFreshControllerEvidence()
    {
        var snapshot = controllerService.Latest;
        return snapshot.IsFresh(DeviceSessionPolicy.MaximumControllerAge) && snapshot.Status != UuControllerStatus.Unknown;
    }
    private static bool SameProfile(PhoneProfile a, PhoneProfile b) =>
        a.PortraitWidth == b.PortraitWidth && a.PortraitHeight == b.PortraitHeight && a.LogicalWidth == b.LogicalWidth;
    private void PortraitOnConnectChanged(object sender, RoutedEventArgs e) => Run(() =>
    {
        phoneProfile = phoneProfile with { PortraitOnConnect = PortraitOnConnectBox.IsChecked == true };
        devicePreferences.Save(phoneProfile);
        portraitFailure = null;
        portraitAttempt.ExplicitRetry();
        CheckAutoConnection();
    });
    private void ShowLibraryPageClicked(object sender, RoutedEventArgs e)
    {
        SettingsExpander.IsExpanded = false;
        UpdateResponsiveLayout();
        Dispatcher.BeginInvoke(() => LibraryScroll.ScrollToTop(), DispatcherPriority.Loaded);
    }
    private void ShowPhoneSettingsClicked(object sender, RoutedEventArgs e)
    {
        SettingsExpander.IsExpanded = true;
        UpdateResponsiveLayout();
        Dispatcher.BeginInvoke(() => LibraryScroll.ScrollToTop(), DispatcherPriority.Loaded);
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closingInProgress) { e.Cancel = true; return; }
        closingInProgress = true;
        refreshTimer.Stop();
        autoFollowTimer.Stop();
        previewTimer.Stop();
        Topmost = false;
        PreviewPanel.Visibility = Visibility.Collapsed;
        BeginDisplayChange();
        try
        {
            var errors = session.RestoreAll();
            if (errors.Count > 0)
            {
                var message = "部分内容暂时无法还原：\n" + string.Join("\n", errors) + "\n\n是否退出并保留恢复记录？下次启动将重试。";
                e.Cancel = MessageBox.Show(this, message, "PocketDesk · 保留恢复记录", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes;
                if (e.Cancel) SetStatus("恢复记录已保留，请连接原显示器后再次还原。", true);
            }
        }
        catch (Exception ex) { e.Cancel = true; SetStatus("还原未完成：" + ex.Message, true); }
        finally
        {
            if (e.Cancel)
            {
                // Show() cannot run while WPF is still dispatching Closing.
                // Resume only after that event and any modal confirmation return.
                Dispatcher.BeginInvoke(() =>
                {
                    if (closed) return;
                    closingInProgress = false;
                    EndDisplayChange();
                    refreshTimer.Start();
                    autoFollowTimer.Start();
                    Run(ShowLibrary);
                });
            }
        }
    }
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            RecoveryStore.WriteLog(ex.ToString());
            if (!IsVisible) ShowLibrary();
            SetStatus(ex.Message, true);
        }
    }
    private void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        ManualZoomButton.Visibility = session.HasUncertainZoom ? Visibility.Visible : Visibility.Collapsed;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrorText" : "Muted");
    }
    private void ManualZoomClicked(object sender, RoutedEventArgs e) => Run(() => { session.AcknowledgeManualZoomRecovery(); FinishWorkspace(); });
}
