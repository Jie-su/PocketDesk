namespace PocketDesk.Core;

/// <summary>
/// Shared measurements in content logical pixels: callers divide work-area
/// pixels by both effective DPI scale and the workspace's content scale.
/// The toolbar supplements UU's controls; it never duplicates its four entries.
/// </summary>
public sealed record ToolbarLayout(
    double Height, bool IsCompact, double ContentMaxWidth, double PopupMaxWidth, double PopupMaxHeight)
{
    public static ToolbarLayout Measure(double logicalWidth, double logicalHeight)
    {
        if (!double.IsFinite(logicalWidth) || logicalWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(logicalWidth));
        if (!double.IsFinite(logicalHeight) || logicalHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(logicalHeight));
        var height = Math.Min(44, logicalHeight);
        return new(height, logicalWidth > logicalHeight,
            Math.Min(480, Math.Max(0, logicalWidth - 16)),
            Math.Min(320, Math.Max(0, logicalWidth - 24)),
            Math.Min(360, Math.Max(0, logicalHeight - height - 16)));
    }
}
