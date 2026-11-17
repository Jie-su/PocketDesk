using PocketDesk.Native;

namespace PocketDesk.Core;

/// <summary>
/// Insets in the remote device's logical points, relative to its current screen
/// edges. The caller selects portrait/landscape preferences; no defaults or
/// automatic rotation of these edges are inferred here.
/// </summary>
public sealed record ViewportInsets(double Left, double Top, double Right, double Bottom)
{
    public const double MaximumInset = 200;
    public static ViewportInsets None { get; } = new(0, 0, 0, 0);
    public bool IsValid => Valid(Left) && Valid(Top) && Valid(Right) && Valid(Bottom);
    private static bool Valid(double value) => double.IsFinite(value) && value is >= 0 and <= MaximumInset;
}

/// <summary>
/// Pure layout policy for keeping content clear of the UU client's overlays.
/// It never changes the native display mode, DPI, taskbar or recovery state.
/// Unsupported/invalid inputs retain the original Windows work area.
/// </summary>
public static class PhoneViewportPolicy
{
    public const double MinimumLogicalWidth = 240;
    public const double MinimumLogicalHeight = 160;

    public static PixelRect Calculate(DisplayInfo display, PhoneProfile profile, ViewportInsets insets)
    {
        ArgumentNullException.ThrowIfNull(display);
        var fallback = display.WorkArea;
        if (!display.IsUuVirtual || profile is null || insets is null || !insets.IsValid ||
            profile.PortraitWidth is <= 0 or > 16384 || profile.PortraitHeight is <= 0 or > 16384 ||
            profile.PortraitHeight <= profile.PortraitWidth || profile.LogicalWidth is < 240 or > 1200 ||
            !ValidRectangle(display.Bounds) || !ValidRectangle(display.WorkArea))
            return fallback;

        var bounds = display.Bounds;
        bool matchesNativePair =
            (bounds.Width == profile.PortraitWidth && bounds.Height == profile.PortraitHeight) ||
            (bounds.Width == profile.PortraitHeight && bounds.Height == profile.PortraitWidth);
        if (!matchesNativePair) return fallback;

        // Device points are physical pixels / the device's native pixel ratio.
        // Windows DPI is a separate scale and must not be multiplied in again.
        // The native short-side ratio remains the same after device rotation.
        double pixelsPerPoint = profile.PortraitWidth / (double)profile.LogicalWidth;
        long Pixels(double points) => (long)Math.Ceiling(points * pixelsPerPoint);
        long left = (long)bounds.X + Pixels(insets.Left);
        long top = (long)bounds.Y + Pixels(insets.Top);
        long right = (long)bounds.X + bounds.Width - Pixels(insets.Right);
        long bottom = (long)bounds.Y + bounds.Height - Pixels(insets.Bottom);

        // Clip to Windows' work area after reserving screen-relative overlays.
        // Starting from WorkArea and subtracting again would double-count a
        // taskbar that already occupies the same screen edge.
        var work = display.WorkArea;
        left = Math.Max(left, work.X);
        top = Math.Max(top, work.Y);
        right = Math.Min(right, (long)work.X + work.Width);
        bottom = Math.Min(bottom, (long)work.Y + work.Height);
        long width = right - left;
        long height = bottom - top;
        if (width < Math.Ceiling(MinimumLogicalWidth * pixelsPerPoint) ||
            height < Math.Ceiling(MinimumLogicalHeight * pixelsPerPoint) ||
            left is < int.MinValue or > int.MaxValue || top is < int.MinValue or > int.MaxValue ||
            width > int.MaxValue || height > int.MaxValue)
            return fallback;
        return new PixelRect((int)left, (int)top, (int)width, (int)height);
    }

    private static bool ValidRectangle(PixelRect? rectangle) => rectangle is { Width: > 0, Height: > 0 } &&
        (long)rectangle.X + rectangle.Width <= int.MaxValue &&
        (long)rectangle.Y + rectangle.Height <= int.MaxValue;
}
