using PocketDesk.Native;

namespace PocketDesk.Core;

public enum PortraitModeSelectionKind
{
    NotApplicable,
    AlreadyPortrait,
    Exact,
    RequiresDriverValidation,
    Unavailable
}

public sealed record PortraitModeDecision(
    DisplayMode? Mode,
    PortraitModeSelectionKind Kind,
    string Reason)
{
    public bool ShouldApply => Mode is not null;
    public bool RequiresDriverValidation => Kind == PortraitModeSelectionKind.RequiresDriverValidation;
}

/// <summary>
/// Chooses the initial portrait mode from an explicit phone profile, or from a
/// real UU virtual display's reported pixels when no profile is supplied.
/// Call once when entering a new connection, not on every resize: later
/// landscape changes made by UU/user must remain possible. This policy does not
/// infer a phone's current physical orientation or change any native settings.
/// </summary>
public static class PortraitModePolicy
{
    public static PortraitModeDecision Select(
        DisplayInfo display, DisplayMode current, IReadOnlyList<DisplayMode> availableModes,
        int? preferredPortraitWidth = null, int? preferredPortraitHeight = null)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(availableModes);

        if (!display.IsUuVirtual)
            return new(null, PortraitModeSelectionKind.NotApplicable, "所选显示器不是 UU 虚拟屏，保持当前模式。");
        if (!IsValid(current))
            return new(null, PortraitModeSelectionKind.Unavailable, "当前显示模式无效，未自动调整。");
        var hasProfile = preferredPortraitWidth.HasValue || preferredPortraitHeight.HasValue;
        int width, height;
        if (hasProfile)
        {
            if (preferredPortraitWidth is not (> 0 and <= 65535) ||
                preferredPortraitHeight is not (> 0 and <= 65535) ||
                preferredPortraitWidth >= preferredPortraitHeight)
                return new(null, PortraitModeSelectionKind.Unavailable, "手机竖屏规格无效，必须同时提供宽度和更大的高度。");
            width = preferredPortraitWidth.Value;
            height = preferredPortraitHeight.Value;
            if (current.Width == width && current.Height == height)
                return new(null, PortraitModeSelectionKind.AlreadyPortrait, "当前已匹配手机竖屏规格，保持原刷新率。");
        }
        else
        {
            if (current.Height > current.Width)
                return new(null, PortraitModeSelectionKind.AlreadyPortrait, "当前已是竖屏，保持原分辨率和刷新率。");
            if (current.Height == current.Width)
                return new(null, PortraitModeSelectionKind.NotApplicable, "当前屏幕为正方形，无需切换方向。");
            width = current.Height;
            height = current.Width;
        }
        var portraits = availableModes.Where(m => m is not null && IsValid(m) && m.Height > m.Width).ToArray();
        var exact = Rank(portraits.Where(m => m.Width == width && m.Height == height), current).FirstOrDefault();
        if (exact is not null)
            return new(exact, PortraitModeSelectionKind.Exact, $"使用驱动提供的 {width} × {height} 竖屏模式。");

        var exemplar = Rank(portraits, current).FirstOrDefault();
        if (exemplar is null)
            return new(null, PortraitModeSelectionKind.Unavailable, $"驱动未提供可确认方向的竖屏模式，未更改 {current.Width} × {current.Height}。");

        // Only the two pixel dimensions are synthesized. Orientation, refresh,
        // bit depth and mode flags all come from an enumerated portrait mode.
        // Its support at this different size is NOT established by enumeration:
        // the caller must use DisplayService.Apply/WorkspaceSession.ChangeDisplay,
        // which performs mandatory CDS_TEST before changing the mode and verifies
        // the actual result. Never fall back to the exemplar's unrelated pixels.
        var candidate = exemplar with { Width = width, Height = height };
        return new(candidate, PortraitModeSelectionKind.RequiresDriverValidation,
            $"驱动未列出 {width} × {height}；仅在驱动验证通过后尝试此尺寸，不替换成其他分辨率。");
    }

    private static bool IsValid(DisplayMode mode) =>
        mode.Width is > 0 and <= 65535 && mode.Height is > 0 and <= 65535 &&
        mode.Frequency >= 0 && mode.BitsPerPixel >= 32 && mode.Orientation is >= 0 and <= 3;

    private static IOrderedEnumerable<DisplayMode> Rank(IEnumerable<DisplayMode> modes, DisplayMode current) => modes
        // Keep the current refresh where the portrait driver modes support it.
        // Otherwise prefer the highest supported refresh no faster than current;
        // only then the lowest faster rate, and finally a driver-default rate.
        .OrderBy(m => FrequencyRank(m.Frequency, current.Frequency))
        .ThenBy(m => FrequencyOrder(m.Frequency, current.Frequency))
        .ThenBy(m => m.Orientation == current.Orientation ? 0 : m.Orientation is 0 or 1 ? 1 : 2)
        .ThenBy(m => m.BitsPerPixel == current.BitsPerPixel ? 0 : 1)
        .ThenBy(m => m.DisplayFlags == current.DisplayFlags ? 0 : 1)
        .ThenBy(m => m.FixedOutput == current.FixedOutput ? 0 : 1)
        .ThenBy(m => m.Orientation)
        .ThenBy(m => m.BitsPerPixel)
        .ThenBy(m => m.DisplayFlags)
        .ThenBy(m => m.FixedOutput)
        .ThenBy(m => m.Width)
        .ThenBy(m => m.Height);

    private static int FrequencyRank(int frequency, int current) =>
        frequency == current ? 0 : frequency <= 1 ? 3 : current <= 1 || frequency <= current ? 1 : 2;

    private static int FrequencyOrder(int frequency, int current) =>
        current > 1 && frequency > 1 && frequency <= current ? -frequency : frequency;
}
