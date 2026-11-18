using PocketDesk.Native;

namespace PocketDesk.Core;

public enum DockEdge { Bottom, Left, Right }

/// <summary>All rectangles use physical screen coordinates. The caller has already accounted for UU and Windows insets.</summary>
public sealed record ToolbarPlacement(PixelRect Bar, PixelRect Panel, PixelRect App, DockEdge Edge)
{
    public const int StripPoints = 32;
    public static ToolbarPlacement Measure(PixelRect viewport, double pixelsPerPoint, LandscapeToolbarPlacement landscape)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        if (viewport.Width < 1 || viewport.Height < 1 || (long)viewport.X + viewport.Width > int.MaxValue ||
            (long)viewport.Y + viewport.Height > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(viewport));
        if (!double.IsFinite(pixelsPerPoint) || pixelsPerPoint <= 0) throw new ArgumentOutOfRangeException(nameof(pixelsPerPoint));
        int Pixels(int points, int max) => Math.Clamp((int)Math.Min(max, Math.Ceiling(points * pixelsPerPoint)), 1, max);
        var (x, y, w, h) = viewport;
        var edge = w <= h || landscape == LandscapeToolbarPlacement.Bottom ? DockEdge.Bottom :
            landscape == LandscapeToolbarPlacement.Left ? DockEdge.Left : DockEdge.Right;
        // Full span, modest thickness. The panel overlays the workspace only while open.
        int n = Pixels(StripPoints, Math.Max(1, (edge == DockEdge.Bottom ? h : w) - 1));
        var bar = edge switch {
            DockEdge.Left => new PixelRect(x, y, n, h),
            DockEdge.Right => new PixelRect(x + w - n, y, n, h),
            _ => new PixelRect(x, y + h - n, w, n)
        };
        var app = edge switch {
            DockEdge.Left when w > n => new PixelRect(x + n, y, w - n, h),
            DockEdge.Right when w > n => new PixelRect(x, y, w - n, h),
            DockEdge.Bottom when h > n => new PixelRect(x, y, w, h - n),
            _ => viewport
        };
        int pw = Pixels(408, w), ph = Pixels(452, h);
        int px = edge == DockEdge.Left ? x : edge == DockEdge.Right ? x + w - pw : x + (w - pw) / 2;
        int py = edge == DockEdge.Bottom ? y + h - ph : y + (h - ph) / 2;
        return new(bar, new PixelRect(px, py, pw, ph), app, edge);
    }
}
