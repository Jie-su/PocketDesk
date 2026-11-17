using PocketDesk.Native;

namespace PocketDesk.Core;

public sealed record PhonePixelSize(int Width, int Height);

public sealed record PhoneScreenSize(int Width, int Height, double LogicalWidth, double LogicalHeight, bool IsFitted);

/// <summary>
/// Separates device pixels from the entire desktop frame streamed by UU. UU
/// centers that frame, so avoiding an overlay at one edge requires equal space
/// at its opposite edge. These dimensions never change Windows DPI or taskbars.
/// </summary>
public static class PhoneScreenGeometry
{
    public static PhoneScreenSize Calculate(PhoneProfile profile, bool portrait, ViewportPreferences preferences)
    {
        ValidateProfile(profile);
        ArgumentNullException.ThrowIfNull(preferences);
        if (!Enum.IsDefined(preferences.Landscape)) throw new ArgumentOutOfRangeException(nameof(preferences));
        double density = profile.PortraitWidth / (double)profile.LogicalWidth;
        int nativeWidth = portrait ? profile.PortraitWidth : profile.PortraitHeight;
        int nativeHeight = portrait ? profile.PortraitHeight : profile.PortraitWidth;
        var insets = preferences.Insets(portrait);
        int width = nativeWidth - (int)Math.Ceiling(2 * Math.Max(insets.Left, insets.Right) * density);
        int height = nativeHeight - (int)Math.Ceiling(2 * Math.Max(insets.Top, insets.Bottom) * density);
        // Invalid or very small usable frames retain the native mode. Keep the
        // orientation recognizable by all callers; do not turn a short tablet
        // into an accidental landscape frame while it is held in portrait.
        if (width < Math.Ceiling(240 * density) || height < Math.Ceiling(160 * density) ||
            (portrait ? height <= width : width <= height))
        {
            width = nativeWidth;
            height = nativeHeight;
        }
        return new(width, height, width / density, height / density,
            width != nativeWidth || height != nativeHeight);
    }

    public static IReadOnlyList<PhonePixelSize> AllowedSizes(PhoneProfile profile, ViewportPreferences preferences)
    {
        var portrait = Calculate(profile, true, preferences);
        var landscape = Calculate(profile, false, preferences);
        return new[]
        {
            new PhonePixelSize(profile.PortraitWidth, profile.PortraitHeight),
            new PhonePixelSize(profile.PortraitHeight, profile.PortraitWidth),
            new PhonePixelSize(portrait.Width, portrait.Height),
            new PhonePixelSize(landscape.Width, landscape.Height)
        }.Distinct().ToArray();
    }

    public static bool MatchesNativeOrFitted(DisplayMode mode, PhoneProfile profile, ViewportPreferences preferences) =>
        AllowedSizes(profile, preferences).Contains(new PhonePixelSize(mode.Width, mode.Height));

    /// <summary>
    /// Geometric check only; a caller must also prove that this fallback is
    /// driver-enumerated and passes CDS_TEST before applying it. A safe standard
    /// size is never evidence of a phone connection on its own.
    /// </summary>
    public static bool IsSafeSupportedFrame(DisplayMode mode, PhoneProfile profile, ViewportPreferences preferences)
    {
        if (mode is null || mode.Width <= 0 || mode.Height <= 0 || mode.Width == mode.Height ||
            mode.Frequency < 0 || mode.BitsPerPixel < 32 || mode.Orientation is < 0 or > 3)
            return false;
        try
        {
            var portrait = mode.Height > mode.Width;
            _ = Calculate(profile, portrait, preferences); // Validate the source profile/preferences.
            double nativeWidth = portrait ? profile.PortraitWidth : profile.PortraitHeight;
            double nativeHeight = portrait ? profile.PortraitHeight : profile.PortraitWidth;
            if (mode.Width > nativeWidth || mode.Height > nativeHeight) return false;
            double density = profile.PortraitWidth / (double)profile.LogicalWidth;
            double fit = Math.Min(nativeWidth / mode.Width, nativeHeight / mode.Height);
            var insets = preferences.Insets(portrait);
            // With no reserved edge the user's target is the native frame, not
            // an arbitrary smaller desktop mode with new black bars.
            if (insets.Left == 0 && insets.Top == 0 && insets.Right == 0 && insets.Bottom == 0) return false;
            double horizontal = (nativeWidth - mode.Width * fit) / 2;
            double vertical = (nativeHeight - mode.Height * fit) / 2;
            return mode.Width * fit / density >= 240 && mode.Height * fit / density >= 160 &&
                horizontal + 0.000001 >= Math.Max(insets.Left, insets.Right) * density &&
                vertical + 0.000001 >= Math.Max(insets.Top, insets.Bottom) * density;
        }
        catch (ArgumentException) { return false; }
    }

    public static string ProfileKey(PhoneProfile profile)
    {
        ValidateProfile(profile);
        return $"{profile.PortraitWidth}x{profile.PortraitHeight}@{profile.LogicalWidth}";
    }

    private static void ValidateProfile(PhoneProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.PortraitWidth is <= 0 or > 16384 || profile.PortraitHeight is <= 0 or > 16384 ||
            profile.PortraitHeight <= profile.PortraitWidth || profile.LogicalWidth is < 240 or > 1200)
            throw new ArgumentOutOfRangeException(nameof(profile), "设备原生尺寸或逻辑宽度无效。");
    }
}
