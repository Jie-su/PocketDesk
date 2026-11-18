using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PocketDesk;

internal static class WorkspaceScroll
{
    // UU may send a mouse drag rather than touch/manipulation events.
    // Start observing on press but take capture only after a clear vertical swipe;
    // a stationary button press (including Space/Enter and real touch) is untouched.
    internal static void EnableMousePan(ScrollViewer view)
    {
        Point? start = null;
        double offset = 0;
        bool dragging = false;
        bool ShouldLeaveAlone(DependencyObject? node)
        {
            while (node != null && node != view)
            {
                if (node is TextBoxBase or PasswordBox or ScrollBar or Thumb or Slider or ComboBox) return true;
                node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
            }
            return false;
        }
        void Reset()
        {
            start = null;
            dragging = false;
            if (view.IsMouseCaptured) view.ReleaseMouseCapture();
        }
        view.PreviewMouseLeftButtonDown += (_, e) =>
        {
            start = null;
            if (e.StylusDevice != null || view.ScrollableHeight <= 0 || ShouldLeaveAlone(e.OriginalSource as DependencyObject)) return;
            start = e.GetPosition(view);
            offset = view.VerticalOffset;
        };
        view.PreviewMouseMove += (_, e) =>
        {
            if (e.StylusDevice != null || start is not { } origin) return;
            if (e.LeftButton != MouseButtonState.Pressed) { Reset(); return; }
            var delta = e.GetPosition(view) - origin;
            if (!dragging)
            {
                if (Math.Abs(delta.Y) < 8 || Math.Abs(delta.Y) <= Math.Abs(delta.X)) return;
                if (!view.CaptureMouse()) { Reset(); return; }
                dragging = true;
            }
            e.Handled = true;
            view.ScrollToVerticalOffset(offset - delta.Y);
        };
        view.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.StylusDevice != null || start == null) return;
            if (dragging) e.Handled = true; // Never launch/press a row at the end of a swipe.
            Reset();
        };
        view.LostMouseCapture += (_, e) => { if (ReferenceEquals(e.OriginalSource, view)) { start = null; dragging = false; } };
        view.IsVisibleChanged += (_, _) => { if (!view.IsVisible && start != null) Reset(); };
    }
}
