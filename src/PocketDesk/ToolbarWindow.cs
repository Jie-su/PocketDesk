using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PocketDesk.Core;

namespace PocketDesk;

/// <summary>A small workspace return strip; UU owns desktop/keyboard controls.</summary>
public sealed class ToolbarWindow : Window
{
    private readonly Button minus;
    private readonly Button plus;
    private readonly Button rotate;
    private readonly Button operations;
    private readonly TextBlock notice;
    private readonly TextBlock menuNotice;
    private readonly Grid outerRoot;
    private readonly Grid phoneSurface;
    private readonly Grid actionRow;
    private readonly Border menuRoot;
    private readonly Popup menu;
    private ToolbarLayout layout = ToolbarLayout.Measure(402, 874);
    private HwndSource? source;
    private double contentScale = 1;
    private bool exiting;

    public ToolbarWindow(Action library, Action<int> zoom, Action finish, Action rotation)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(zoom);
        ArgumentNullException.ThrowIfNull(finish);
        ArgumentNullException.ThrowIfNull(rotation);
        SetResourceReference(StyleProperty, typeof(Window));
        Title = "PocketDesk · 控制条";
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/PocketDesk;component/Assets/PocketDesk.ico"));
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        ShowActivated = false;
        Topmost = true;
        Height = layout.Height;
        SetResourceReference(BackgroundProperty, "Rail");

        outerRoot = new Grid { ClipToBounds = true };
        phoneSurface = new Grid { Height = layout.Height };
        actionRow = new Grid
        {
            Width = layout.ContentMaxWidth,
            Height = 44,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });

        var back = MakeBarButton("应用");
        back.SetResourceReference(Control.BackgroundProperty, "SoftBlue");
        back.SetResourceReference(Control.ForegroundProperty, "Accent");
        back.BorderThickness = new Thickness(0);
        back.ToolTip = "返回应用列表 · Ctrl + Alt + F12";
        AutomationProperties.SetName(back, "返回应用列表");
        back.Click += (_, _) => { CloseMenu(); library(); };
        actionRow.Children.Add(back);

        notice = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0)
        };
        notice.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Grid.SetColumn(notice, 1);
        actionRow.Children.Add(notice);

        operations = MakeBarButton("操作");
        operations.ToolTip = "窗口操作：旋转、字号和结束";
        AutomationProperties.SetName(operations, "打开窗口操作菜单");
        Grid.SetColumn(operations, 2);
        actionRow.Children.Add(operations);
        phoneSurface.Children.Add(actionRow);
        var separator = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        separator.SetResourceReference(Border.BackgroundProperty, "Separator");
        phoneSurface.Children.Add(separator);
        outerRoot.Children.Add(phoneSurface);
        Content = outerRoot;

        var menuContent = new StackPanel();
        var heading = new TextBlock { Text = "窗口操作", FontSize = 16, FontWeight = FontWeights.SemiBold };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryText");
        menuContent.Children.Add(heading);
        menuNotice = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 12),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        menuNotice.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        menuContent.Children.Add(menuNotice);

        rotate = MakeMenuButton("横竖屏切换", rotation);
        rotate.ToolTip = "横竖屏切换 · Ctrl + Alt + F11";
        AutomationProperties.SetName(rotate, "旋转设备屏幕");
        menuContent.Children.Add(rotate);

        var zoomRow = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        zoomRow.ColumnDefinitions.Add(new ColumnDefinition());
        zoomRow.ColumnDefinitions.Add(new ColumnDefinition());
        minus = MakeMenuButton("A−  字号", () => zoom(-1));
        plus = MakeMenuButton("A+  字号", () => zoom(1));
        minus.Margin = new Thickness(0, 0, 4, 0);
        plus.Margin = new Thickness(4, 0, 0, 0);
        AutomationProperties.SetName(minus, "缩小应用字号");
        AutomationProperties.SetName(plus, "放大应用字号");
        Grid.SetColumn(plus, 1);
        zoomRow.Children.Add(minus);
        zoomRow.Children.Add(plus);
        menuContent.Children.Add(zoomRow);

        var done = MakeMenuButton("结束并还原", finish);
        done.SetResourceReference(Control.ForegroundProperty, "ErrorText");
        done.ToolTip = "结束工作台并还原窗口与显示设置";
        AutomationProperties.SetName(done, "结束并还原");
        menuContent.Children.Add(done);

        var menuScroll = new ScrollViewer
        {
            Content = menuContent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
            Focusable = false
        };
        menuRoot = new Border
        {
            Child = menuScroll,
            Width = layout.PopupMaxWidth,
            MaxHeight = layout.PopupMaxHeight,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            Focusable = true
        };
        menuRoot.SetResourceReference(Border.BackgroundProperty, "Card");
        menuRoot.SetResourceReference(Border.BorderBrushProperty, "Outline");
        AutomationProperties.SetName(menuRoot, "窗口操作菜单");
        KeyboardNavigation.SetTabNavigation(menuRoot, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(menuRoot, KeyboardNavigationMode.Cycle);
        menuRoot.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            CloseMenu();
        };
        menu = new Popup
        {
            Child = menuRoot,
            PlacementTarget = outerRoot,
            Placement = PlacementMode.Custom,
            AllowsTransparency = true,
            StaysOpen = false,
            PopupAnimation = PopupAnimation.None,
            CustomPopupPlacementCallback = PlaceMenu
        };
        menu.Opened += (_, _) =>
        {
            operations.SetResourceReference(Control.BackgroundProperty, "SoftBlue");
            // Opening the menu is an explicit request for keyboard interaction.
            // Initially focus its container so Enter cannot accidentally end work.
            Keyboard.Focus(menuRoot);
        };
        menu.Closed += (_, _) =>
        {
            operations.SetResourceReference(Control.BackgroundProperty, "Card");
            // No focus restoration here: an action may already have focused the
            // target app, or the user may have clicked another application.
        };
        operations.Click += (_, _) => ToggleMenu();

        SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(HandleMessage);
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) CloseMenu(); };
        SizeChanged += (_, _) => { if (menu.IsOpen) CloseMenu(); };
        Closing += (_, e) =>
        {
            CloseMenu();
            if (!exiting) { e.Cancel = true; library(); }
        };
        Closed += (_, _) => source?.RemoveHook(HandleMessage);
    }

    private static Button MakeBarButton(string text)
    {
        var button = new Button
        {
            Content = text,
            Height = 44,
            MinHeight = 44,
            MinWidth = 44,
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Focusable = false,
            IsTabStop = false
        };
        button.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
        button.SetResourceReference(Control.BackgroundProperty, "Card");
        button.SetResourceReference(Control.BorderBrushProperty, "Outline");
        return button;
    }

    private Button MakeMenuButton(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 44,
            MinWidth = 44,
            Padding = new Thickness(8, 8, 8, 8),
            FontSize = 14,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
        button.SetResourceReference(Control.BackgroundProperty, "Fill");
        button.SetResourceReference(Control.BorderBrushProperty, "Outline");
        // Popup dismissal happens before a zoom callback calls WindowService.Focus.
        button.Click += (_, _) => { CloseMenu(); action(); };
        return button;
    }

    private nint HandleMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021 && !menu.IsOpen) // WM_MOUSEACTIVATE
        {
            handled = true;
            return 3; // MA_NOACTIVATE: still deliver the click to its button.
        }
        return 0;
    }

    private void ToggleMenu()
    {
        if (menu.IsOpen) { CloseMenu(); return; }
        if (!IsVisible || layout.PopupMaxHeight < 44 || layout.PopupMaxWidth < 128) return;
        Activate(); // Only the user's explicit menu entry may activate this strip.
        menu.IsOpen = true;
    }

    private CustomPopupPlacement[] PlaceMenu(Size popupSize, Size targetSize, Point offset)
    {
        // WPF invokes custom placement in device pixels, after transforming both
        // target and popup bounds. TranslatePoint includes our content scale but
        // returns DIPs, so its center and the logical margin still need DPI.
        var dpi = VisualTreeHelper.GetDpi(outerRoot);
        var center = operations.TranslatePoint(new Point(operations.ActualWidth / 2, 0), outerRoot).X * dpi.DpiScaleX;
        var rightLimit = Math.Max(0, targetSize.Width - popupSize.Width);
        var edge = Math.Min(8 * contentScale * dpi.DpiScaleX, rightLimit / 2);
        var x = Math.Clamp(center - popupSize.Width / 2, edge, rightLimit - edge);
        // Reserve the same 8 logical pixels used by ToolbarLayout's 16-pixel
        // vertical allowance; an unscaled gap would overrun very small scales.
        return [new CustomPopupPlacement(new Point(x, -popupSize.Height - 8 * contentScale * dpi.DpiScaleY), PopupPrimaryAxis.Horizontal)];
    }

    private void CloseMenu() => menu.IsOpen = false;

    public void ConfigureLayout(double logicalWidth, double logicalHeight)
    {
        var measured = ToolbarLayout.Measure(logicalWidth, logicalHeight);
        if (measured != layout) CloseMenu();
        layout = measured;
        phoneSurface.Height = layout.Height;
        actionRow.Width = layout.ContentMaxWidth;
        Height = layout.Height * contentScale;
        menuRoot.Width = layout.PopupMaxWidth;
        menuRoot.MaxHeight = layout.PopupMaxHeight;
        operations.IsEnabled = layout.PopupMaxHeight >= 44 && layout.PopupMaxWidth >= 128;
    }

    public void SetContentScale(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        if (Math.Abs(scale - contentScale) >= 0.001) CloseMenu();
        contentScale = scale;
        phoneSurface.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
        menuRoot.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
        Height = layout.Height * scale;
    }

    public void SetZoomEnabled(bool enabled)
    {
        plus.IsEnabled = minus.IsEnabled = enabled;
        plus.ToolTip = minus.ToolTip = enabled ? "发送应用默认字号快捷键；自定义绑定可能不同" : "此应用尚未适配字号快捷键";
    }

    public void SetRotationEnabled(bool enabled) => rotate.IsEnabled = enabled;
    public void SetNotice(string text)
    {
        notice.Text = menuNotice.Text = text;
        notice.ToolTip = menuNotice.ToolTip = text;
    }

    public void CloseForExit() { exiting = true; CloseMenu(); Close(); }
}
