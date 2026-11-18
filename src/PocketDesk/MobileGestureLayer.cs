using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk;

/// <summary>Non-activating input surface limited to the selected application's visible client area.</summary>
public sealed class MobileGestureLayer : IDisposable
{
    private readonly MobileGesturePolicy policy = new();
    private readonly WindowService windows = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly Canvas surface = new() { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), ClipToBounds = true };
    private readonly Line guide = new() { Stroke = Brushes.DodgerBlue, StrokeThickness = 3, Opacity = .8, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly StackPanel scrollKeys = new() { Visibility = Visibility.Collapsed };
    private Window? overlay;
    private WindowSnapshot? target;
    private PixelRect? area, lastBounds;
    private GesturePoint start;
    private nint recipient, selectionRecipient;
    private TouchDevice? finger;
    private bool mouse, doublePress, releasing, disposed, forwarding;
    private double density = 1;
    private bool showKeys;
    private int pressedKey;
    private GesturePoint? wheelHole;
    public string LastStatus { get; private set; } = "尚未收到手势";
    private long resumeAt, bubbleUntil, lastTouch = -1000;
    public MobileGestureLayer()
    {
        timer.Tick += (_, _) => Refresh();
        foreach (var text in new[] { "↑", "↓" })
            scrollKeys.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(225, 38, 52, 68)),
                CornerRadius = new CornerRadius(6), Margin = new Thickness(0,1,0,1),
                Child = new TextBlock { Text = text, FontSize = 20, Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
    }
    public void Sync(WindowSnapshot target, PixelRect appArea, double pixelsPerPoint = 1, bool showScrollButtons = false)
    {
        if (disposed) return;
        if (this.target?.Handle != target.Handle || this.target?.ProcessStartTicks != target.ProcessStartTicks ||
            this.target?.WindowThreadId != target.WindowThreadId || area != appArea) Clear();
        this.target = target; area = appArea; density = pixelsPerPoint;
        showKeys = showScrollButtons;
        if (!timer.IsEnabled)
        {
            if (!windows.IsSameWindow(target)) { Suspend(); return; }
            timer.Start();
        }
        Refresh();
    }
    public void Suspend()
    {
        target = null; area = null; resumeAt = 0; timer.Stop(); HideLayer();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Suspend(); overlay?.Close();
    }
    private void EnsureLayer()
    {
        if (overlay != null) return;
        overlay = new Window
        {
            Title = "PocketDesk · 单指手势",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            Width = 1,
            Height = 1,
            Content = surface
        };
        Stylus.SetIsPressAndHoldEnabled(surface, false);
        Stylus.SetIsFlicksEnabled(surface, false);
        Stylus.SetIsTapFeedbackEnabled(surface, false);
        surface.Children.Add(guide);
        surface.Children.Add(scrollKeys);
        var hwnd = new WindowInteropHelper(overlay).EnsureHandle();
        SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000080); // WS_EX_NOACTIVATE | TOOLWINDOW
        HwndSource.FromHwnd(hwnd)!.AddHook(MouseMessage);
        surface.PreviewTouchDown += (_, e) =>
        {
            e.Handled = true;
            if (finger != null || mouse) { Clear(); return; }
            LastStatus = "已收到触屏按下";
            if (Begin(e.GetTouchPoint(surface).Position)) { finger = e.TouchDevice; if (!finger.Capture(surface)) Clear(); }
        };
        surface.PreviewTouchMove += (_, e) => { e.Handled = true; if (e.TouchDevice == finger) Move(e.GetTouchPoint(surface).Position); };
        surface.PreviewTouchUp += (_, e) => { e.Handled = true; lastTouch = Environment.TickCount64; if (e.TouchDevice == finger) End(e.GetTouchPoint(surface).Position); };
        surface.LostTouchCapture += (_, e) => { if (!releasing && e.TouchDevice == finger) Clear(); };
    }
    // Read mouse coordinates from the actual window message. Some remote clients post their
    // own client coordinates rather than moving the host's physical cursor for every sample.
    private nint MouseMessage(nint hwnd, int msg, nint wp, nint lp, ref bool handled)
    {
        if (msg == 0x21) { handled = true; return 3; } // MA_NOACTIVATE
        if (msg == 0x215 && mouse && !releasing) { Clear(); return 0; } // WM_CAPTURECHANGED
        if (msg is not (0x200 or 0x201 or 0x202 or 0x203 or 0x204 or 0x205 or 0x20a) || overlay?.IsVisible != true) return 0;
        if (finger != null || ((GetMessageExtraInfo().ToInt64() & 0xffffff00L) == 0xff515700L && Environment.TickCount64 - lastTouch < 250))
        { handled = true; return 0; }
        var pos = new NativePoint { X = (short)(long)lp, Y = (short)((long)lp >> 16) };
        if (msg != 0x20a && !ClientToScreen(hwnd, ref pos)) return 0;
        var p = new GesturePoint(pos.X, pos.Y); var local = surface.PointFromScreen(new(pos.X, pos.Y));
        handled = true;
        switch (msg)
        {
            case 0x201:
            case 0x203:
                LastStatus = "已收到按下";
                if (!mouse && Begin(local)) { mouse = true; doublePress = msg == 0x203; SetCapture(hwnd); }
                break;
            case 0x200:
                if (mouse) Move(local);
                else if ((wp.ToInt64() & 1) != 0 && Begin(local))
                {
                    // A remote client can enter this window with the primary button
                    // already down. Adopt only confirmed drags, never plain pointer motion.
                    LastStatus = "已收到拖动";
                    pressedKey = 0;
                    policy.AdoptHeldDrag(start, Environment.TickCount64, density);
                    mouse = true; SetCapture(hwnd);
                }
                else if (LastStatus == "尚未收到手势") LastStatus = "仅收到指针移动，未按下";
                break;
            case 0x202: if (mouse) End(local); break;
            case 0x205:
            case 0x20a:
                if (!policy.IsTracking && target != null && windows.IsSameWindow(target))
                {
                    policy.Cancel(); selectionRecipient = 0;
                    Send(GestureWindowMessages.Receiver(target, p), new(msg == 0x205 ? GestureAction.Menu : GestureAction.Wheel, p, p, (short)((long)wp >> 16)));
                }
                break;
        }
        return 0;
    }
    private void Refresh()
    {
        if (Environment.TickCount64 < resumeAt) return;
        if (target != null && policy.HasSelection && Environment.TickCount64 < bubbleUntil && GestureWindowMessages.DismissEdgeSelectionBubble(target))
        { bubbleUntil = 0; resumeAt = Environment.TickCount64 + 50; return; }
        if (target == null || area == null || !GestureWindowMessages.Available(target))
        { HideLayer(); return; }
        var bounds = GestureWindowMessages.Bounds(target, area);
        if (bounds == null) { HideLayer(); return; }
        EnsureLayer();
        if (!policy.IsTracking) { TransparentForDelivery(false); ClearHole(); }
        if (!overlay!.IsVisible) overlay.Show();
        LayoutKeys(bounds);
        nint hwnd = new WindowInteropHelper(overlay).Handle, above = GetWindow((nint)target.Handle, 3); // GW_HWNDPREV
        if (lastBounds == bounds && above == hwnd) return;
        if (lastBounds != null && lastBounds != bounds) { Clear(); ClearHole(); }
        if (!SetWindowPos(hwnd, above == hwnd ? 0 : above, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x10u | (above == hwnd ? 4u : 0)))
        { HideLayer(); return; }
        lastBounds = bounds;
    }
    private void HideLayer()
    {
        if (lastBounds != null || policy.IsTracking || selectionRecipient != 0) Clear();
        lastBounds = null;
        ClearHole();
        if (overlay?.IsVisible == true) overlay.Hide();
    }
    private GesturePoint Screen(Point local)
    {
        var p = surface.PointToScreen(local);
        return new((int)Math.Round(p.X), (int)Math.Round(p.Y));
    }
    private bool Begin(Point local)
    {
        if (target == null || !GestureWindowMessages.Available(target) || !windows.IsSameWindow(target)) { HideLayer(); return false; }
        ClearHole();
        start = Screen(local);
        if (overlay is null || !GestureWindowMessages.TargetAt(target, start, new WindowInteropHelper(overlay).Handle)) return false;
        pressedKey = KeyAt(local);
        if (pressedKey != 0) policy.Cancel();
        recipient = GestureWindowMessages.Receiver(target, start);
        if (recipient == 0) { Clear(); return false; }
        policy.Down(start, Environment.TickCount64, density);
        guide.X1 = local.X; guide.Y1 = local.Y;
        return true;
    }
    private void Move(Point local)
    {
        if (!policy.IsTracking || pressedKey != 0) return;
        var result = policy.Move(Screen(local), Environment.TickCount64);
        if (!policy.HasSelection) selectionRecipient = 0;
        if (policy.IsSelecting) { guide.X2 = local.X; guide.Y2 = local.Y; guide.Visibility = Visibility.Visible; }
        if (result.Action == GestureAction.Wheel) Send(recipient, result);
    }
    private void End(Point local)
    {
        if (!policy.IsTracking) return;
        if (pressedKey != 0)
        {
            var delta = pressedKey == KeyAt(local) ? pressedKey * 240 : 0;
            Clear();
            if (delta != 0 && target != null && lastBounds is { } bounds)
            {
                // The rail is on the edge (often over a scrollbar). Route a tap to
                // the middle of the same application's visible content instead.
                var p = new GesturePoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                Send(GestureWindowMessages.Receiver(target, p), new(GestureAction.Wheel, p, p, delta));
            }
            return;
        }
        var pending = policy.HasSelection;
        var result = policy.Up(Screen(local), Environment.TickCount64);
        if (doublePress && result.Action == GestureAction.Tap) result = result with { Action = GestureAction.DoubleTap };
        if (!pending) selectionRecipient = 0;
        Release();
        if (result.Action != GestureAction.None)
        {
            var receiver = result.Action == GestureAction.Menu && selectionRecipient != 0 ? selectionRecipient : recipient;
            if (Send(receiver, result) && result.Action == GestureAction.Select) { selectionRecipient = recipient; bubbleUntil = Environment.TickCount64 + 550; }
            else selectionRecipient = 0;
        }
        recipient = 0;
    }
    private bool Send(nint receiver, GestureResult result)
    {
        // Chromium and some editors hit-test the screen position again when processing a posted
        // wheel or click. Let that lookup reach the app instead of finding our input surface.
        // On a drag the surface already owns capture, so contact samples still reach this HWND.
        if (result.Action == GestureAction.Wheel && policy.IsTracking && target != null && lastBounds is { } bounds)
        {
            // WS_EX_TRANSPARENT on the whole non-foreground window during a real
            // drag loses subsequent moves/releases. A single pixel at the original
            // scroll anchor lets the app re-hit-test the wheel while this surface
            // remains under the moving finger. Synthetic posts alone do not test this.
            var p = result.Start;
            if (Math.Abs(p.X - result.End.X) < 2 && Math.Abs(p.Y - result.End.Y) < 2)
            {
                foreach (int dx in new[] { 4, -4 })
                {
                    var candidate = new GesturePoint(p.X + dx, p.Y);
                    if (candidate.X >= bounds.X && candidate.X < bounds.X + bounds.Width &&
                        GestureWindowMessages.Receiver(target, candidate) == receiver) { p = candidate; break; }
                }
            }
            if (PunchHole(bounds, p)) result = result with { End = p };
        }
        else TransparentForDelivery(true);
        if (target == null || area == null || overlay == null ||
            !GestureWindowMessages.Dispatch(target, area, receiver, result, new WindowInteropHelper(overlay).Handle))
        { LastStatus = "未能转发到当前窗口"; resumeAt = Environment.TickCount64 + 500; HideLayer(); return false; }
        LastStatus = result.Action switch
        {
            GestureAction.Wheel => "已转发滚轮", GestureAction.Menu => "已转发菜单",
            GestureAction.Select => "已转发拖选", _ => "已转发点击"
        };
        if (result.Action == GestureAction.Menu) { resumeAt = Environment.TickCount64 + 700; HideLayer(); }
        else if (!policy.IsTracking) resumeAt = Environment.TickCount64 + 65;
        return true;
    }
    private void TransparentForDelivery(bool value)
    {
        if (overlay == null || value == forwarding) return;
        nint hwnd = new WindowInteropHelper(overlay).Handle;
        int style = GetWindowLong(hwnd, -20);
        SetWindowLong(hwnd, -20, value ? style | 0x20 : style & ~0x20);
        forwarding = value;
    }
    private bool PunchHole(PixelRect bounds, GesturePoint p)
    {
        if (overlay is null) return false;
        if (wheelHole == p) return true;
        int x = p.X - bounds.X, y = p.Y - bounds.Y;
        if ((uint)x >= (uint)bounds.Width || (uint)y >= (uint)bounds.Height) return false;
        nint window = new WindowInteropHelper(overlay).Handle;
        nint region = CreateRectRgn(0, 0, bounds.Width, bounds.Height);
        nint hole = CreateRectRgn(x, y, x + 1, y + 1);
        try
        {
            if (region == 0 || hole == 0 || CombineRgn(region, region, hole, 4) == 0 ||
                SetWindowRgn(window, region, true) == 0) return false;
            region = 0; // ownership transferred to Windows
            wheelHole = p;
            return true;
        }
        finally { if (hole != 0) DeleteObject(hole); if (region != 0) DeleteObject(region); }
    }
    private void ClearHole()
    {
        if (wheelHole == null || overlay == null) return;
        SetWindowRgn(new WindowInteropHelper(overlay).Handle, 0, true);
        wheelHole = null;
    }
    private void LayoutKeys(PixelRect bounds)
    {
        double dpi = VisualTreeHelper.GetDpi(surface).DpiScaleX;
        double step = Math.Max(32, 40 * density) / dpi;
        double w = bounds.Width / dpi, h = bounds.Height / dpi;
        bool visible = showKeys && w > step * 2 && h >= step * 2;
        scrollKeys.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        scrollKeys.Width = step;
        foreach (Border b in scrollKeys.Children) { b.Height = step - 2; }
        Canvas.SetLeft(scrollKeys, w - step);
        Canvas.SetTop(scrollKeys, (h - step * 2) / 2);
    }
    private int KeyAt(Point local)
    {
        if (!showKeys || scrollKeys.Visibility != Visibility.Visible) return 0;
        double x = local.X - Canvas.GetLeft(scrollKeys), y = local.Y - Canvas.GetTop(scrollKeys), step = scrollKeys.Width;
        return x < 0 || x >= step || y < 0 || y >= step * 2 ? 0 : y < step ? 1 : -1;
    }
    private void Clear() { policy.Cancel(); pressedKey = 0; selectionRecipient = 0; recipient = 0; Release(); }
    private void Release()
    {
        releasing = true;
        try { if (mouse && overlay != null && GetCapture() == new WindowInteropHelper(overlay).Handle) ReleaseCapture(); if (finger?.Captured == surface) finger.Capture(null); }
        finally { releasing = false; mouse = false; doublePress = false; finger = null; guide.Visibility = Visibility.Collapsed; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref NativePoint pos);
    [DllImport("user32.dll")] private static extern nint GetMessageExtraInfo();
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(nint dest, nint left, nint right, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint rgn, bool redraw);
}
