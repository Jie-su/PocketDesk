using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk;

/// <summary>One edge strip and one expandable window, so native file pickers have a stable owner.</summary>
public sealed class ToolbarWindow : Window
{
    private readonly Grid surface = new();
    private readonly Border page;
    private readonly Button handle, shrink, poolTab, toolsTab, rotate, minus, plus, gestures, scrollButtons, autoZoom;
    private readonly TextBlock label, notice;
    private readonly ContentControl pool = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ScrollViewer poolScroll, toolsScroll;
    private ToolbarPlacement? placement;
    private HwndSource? source;
    private bool exiting;
    public bool IsExpanded { get; private set; }
    public event Action<bool>? ExpansionChanged;
    public event Action? ToggleGesturesRequested;
    public event Action? ToggleAutoZoomRequested;
    public event Action? ToggleScrollButtonsRequested;

    public ToolbarWindow(Action settings, Action<int> zoom, Action finish, Action rotation)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Title = "PocketDesk · 手机工作台";
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/PocketDesk;component/Assets/PocketDesk.ico"));
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SetResourceReference(BackgroundProperty, "Rail");

        handle = Button("展开 PocketDesk", Expand);
        handle.MinHeight = handle.MinWidth = 0;
        handle.Padding = new Thickness(0);
        handle.BorderThickness = new Thickness(0);
        handle.SetResourceReference(Control.BackgroundProperty, "Rail");
        handle.ToolTip = "展开常用应用和运行中的窗口";
        label = Text("⌃   PocketDesk", 13, "Accent", true);
        handle.Content = label;
        surface.Children.Add(handle);

        var header = new DockPanel();
        shrink = Button("收起", Collapse);
        DockPanel.SetDock(shrink, Dock.Right);
        header.Children.Add(shrink);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        titles.Children.Add(Text("PocketDesk", 18, "PrimaryText", true));
        notice = Text("手机工作台", 12, "Muted");
        titles.Children.Add(notice);
        header.Children.Add(titles);

        poolTab = Button("应用与窗口", () => ShowTools(false));
        toolsTab = Button("操作", () => ShowTools(true));
        var nav = Row(poolTab, toolsTab);
        nav.Margin = new Thickness(0, 10, 0, 8);

        var list = new StackPanel();
        gestures = Button("单指手势", () => ToggleGesturesRequested?.Invoke());
        list.Children.Add(gestures);
        var help = Text("滑动浏览；长按后拖选文字，再轻点打开右键菜单。", 12, "Muted");
        help.TextWrapping = TextWrapping.Wrap;
        help.Margin = new Thickness(2, 5, 2, 9);
        list.Children.Add(help);
        scrollButtons = Button("滚动辅助键 ↑↓", () => ToggleScrollButtonsRequested?.Invoke());
        list.Children.Add(scrollButtons);
        rotate = Button("横竖屏切换", () => { Collapse(); rotation(); });
        list.Children.Add(rotate);
        minus = Button("字号 −", () => { Collapse(); zoom(-1); });
        plus = Button("字号 ＋", () => { Collapse(); zoom(1); });
        var sizing = Row(minus, plus);
        sizing.Margin = new Thickness(0, 6, 0, 6);
        list.Children.Add(sizing);
        autoZoom = Button("进入应用时放大一级", () => ToggleAutoZoomRequested?.Invoke());
        list.Children.Add(autoZoom);
        list.Children.Add(Button("设备与设置", () => { Collapse(); settings(); }));
        var end = Button("结束并还原", () => { Collapse(); finish(); });
        end.SetResourceReference(Control.ForegroundProperty, "ErrorText");
        list.Children.Add(end);
        toolsScroll = new ScrollViewer { Content = list, PanningMode = PanningMode.VerticalOnly,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        poolScroll = new ScrollViewer { Content = pool, PanningMode = PanningMode.VerticalOnly,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        WorkspaceScroll.EnableMousePan(poolScroll);
        WorkspaceScroll.EnableMousePan(toolsScroll);
        var body = new Grid();
        body.Children.Add(poolScroll);
        body.Children.Add(toolsScroll);
        var inside = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(nav, Dock.Top);
        inside.Children.Add(header);
        inside.Children.Add(nav);
        inside.Children.Add(body);
        page = new Border { Padding = new Thickness(12), BorderThickness = new Thickness(1), Child = inside,
            Visibility = Visibility.Collapsed };
        page.SetResourceReference(Border.BackgroundProperty, "Card");
        page.SetResourceReference(Border.BorderBrushProperty, "Outline");
        surface.Children.Add(page);
        Content = new Grid { ClipToBounds = true, Children = { surface } };
        ShowTools(false);
        SetGestureState(false, false);
        SetScrollButtonsState(false, false);
        SetAutoZoomState(false, false);
        SetZoomEnabled(false);
        KeyboardNavigation.SetTabNavigation(page, KeyboardNavigationMode.Cycle);
        PreviewKeyDown += (_, e) => { if (IsExpanded && e.Key == Key.Escape) { e.Handled = true; Collapse(); } };
        SourceInitialized += (_, _) => {
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(HandleMessage);
            ApplyPosition();
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) Collapse(); else ApplyPosition(); };
        Loaded += (_, _) => ApplyPosition();
        Closing += (_, e) => { if (!exiting) { e.Cancel = true; Collapse(); } };
        Closed += (_, _) => source?.RemoveHook(HandleMessage);
    }

    private static TextBlock Text(string value, double size, string color, bool bold = false)
    {
        var text = new TextBlock { Text = value, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        text.SetResourceReference(TextBlock.ForegroundProperty, color);
        return text;
    }
    private static Button Button(string value, Action action)
    {
        var button = new Button { Content = value, Padding = new Thickness(8, 4, 8, 4), MinHeight = 44 };
        button.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
        button.SetResourceReference(Control.BackgroundProperty, "Fill");
        button.SetResourceReference(Control.BorderBrushProperty, "Outline");
        AutomationProperties.SetName(button, value);
        button.Click += (_, _) => action();
        return button;
    }
    private static Grid Row(params Button[] buttons)
    {
        var grid = new Grid();
        for (int i = 0; i < buttons.Length; i++) {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(buttons[i], i);
            buttons[i].Margin = new Thickness(i == 0 ? 0 : 3, 0, i == buttons.Length - 1 ? 0 : 3, 0);
            grid.Children.Add(buttons[i]);
        }
        return grid;
    }
    private void ShowTools(bool open)
    {
        poolScroll.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        toolsScroll.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (tab, selected) in new[] { (poolTab, !open), (toolsTab, open) }) {
            tab.SetResourceReference(Control.BackgroundProperty, selected ? "SoftBlue" : "Fill");
            tab.SetResourceReference(Control.ForegroundProperty, selected ? "Accent" : "PrimaryText");
            tab.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }
    private nint HandleMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021 && !IsExpanded) { handled = true; return 3; } // MA_NOACTIVATE on strip only.
        return 0;
    }
    public void ConfigurePlacement(ToolbarPlacement next, double contentScale)
    {
        if (!double.IsFinite(contentScale) || contentScale <= 0) throw new ArgumentOutOfRangeException(nameof(contentScale));
        ArgumentNullException.ThrowIfNull(next);
        if (surface.LayoutTransform.Value.M11 != contentScale)
            surface.LayoutTransform = contentScale == 1 ? Transform.Identity : new ScaleTransform(contentScale, contentScale);
        placement = next;
        label.Text = next.Edge == DockEdge.Bottom ? "⌃   PocketDesk" : "PocketDesk";
        label.LayoutTransform = next.Edge == DockEdge.Bottom ? Transform.Identity : new RotateTransform(next.Edge == DockEdge.Left ? -90 : 90);
        ApplyPosition();
    }
    private void ApplyPosition()
    {
        if (placement is { } p && source is not null) OwnWindow.SetBounds(this, IsExpanded ? p.Panel : p.Bar);
    }
    public void SetPoolContent(FrameworkElement content) => pool.Content = content;
    public void Expand() => OpenPanel(false);
    public void ShowRunningWindows() => OpenPanel(true);
    private void OpenPanel(bool focusWindows)
    {
        if (placement is null) return;
        if (IsExpanded && !focusWindows) return;
        bool opening = !IsExpanded;
        IsExpanded = true;
        handle.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
        ShowTools(false);
        poolScroll.ScrollToHome();
        if (pool.Content is AppPoolPanel apps) apps.OpenLibrary(focusWindows);
        if (opening) ExpansionChanged?.Invoke(true);
        ApplyPosition();
        if (IsVisible) { Activate(); shrink.Focus(); }
    }
    public void Collapse()
    {
        if (!IsExpanded) return;
        IsExpanded = false;
        page.Visibility = Visibility.Collapsed;
        handle.Visibility = Visibility.Visible;
        if (IsVisible) ApplyPosition();
        ExpansionChanged?.Invoke(false);
    }
    public void SetZoomEnabled(bool enabled) => plus.IsEnabled = minus.IsEnabled = enabled;
    public void SetRotationEnabled(bool enabled) => rotate.IsEnabled = enabled;
    public void SetAutoZoomState(bool enabled, bool available) => SetToggle(autoZoom, "进入应用时放大一级", enabled, available);
    public void SetScrollButtonsState(bool enabled, bool available) => SetToggle(scrollButtons, "滚动辅助键 ↑↓", enabled, available);
    private static void SetToggle(Button button, string caption, bool enabled, bool available)
    {
        button.IsEnabled = available;
        button.Content = $"{caption}：{(enabled ? "已开启" : "已关闭")}";
        AutomationProperties.SetName(button, (string)button.Content);
    }
    public void SetGestureState(bool enabled, bool available)
    {
        gestures.IsEnabled = available;
        gestures.Content = available ? $"单指手势：{(enabled ? "已开启" : "已关闭")}" : "单指手势：需手机或平板接入";
        AutomationProperties.SetName(gestures, (string)gestures.Content);
    }
    public void SetNotice(string text) { notice.Text = text; notice.ToolTip = text; }
    public void CloseForExit() { exiting = true; Close(); }
}
