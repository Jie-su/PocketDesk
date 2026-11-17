using PocketDesk.Native;

namespace PocketDesk.Core;

/// <summary>
/// Prepares a known or explicitly selected device's native or safe-frame pixels. The caller
/// must first establish current-controller evidence or a manual user choice;
/// the presence of a UU display alone never authorizes model-based conversion.
/// </summary>
public static class DeviceModePolicy
{
    public static PortraitModeDecision Select(
        DisplayInfo display, DisplayMode current, IReadOnlyList<DisplayMode> availableModes, PhoneProfile profile,
        ViewportPreferences? preferences = null, bool? portraitOverride = null)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(availableModes);
        ArgumentNullException.ThrowIfNull(profile);
        if (!display.IsUuVirtual)
            return new(null, PortraitModeSelectionKind.NotApplicable, "所选显示器不是 UU 虚拟屏。");
        if (profile.PortraitWidth is <= 0 or > 16384 || profile.PortraitHeight is <= 0 or > 16384 ||
            profile.PortraitHeight <= profile.PortraitWidth || !IsValid(current))
            return new(null, PortraitModeSelectionKind.Unavailable, "设备规格或当前显示模式无效。");
        if (portraitOverride is null && !profile.PortraitOnConnect && current.Width == current.Height)
            return new(null, PortraitModeSelectionKind.Unavailable, "当前模式为正方形，无法确认应保留的方向。");

        var portrait = portraitOverride ?? (profile.PortraitOnConnect || current.Height > current.Width);
        PhoneScreenSize target;
        try { target = PhoneScreenGeometry.Calculate(profile, portrait, preferences ?? new(false)); }
        catch (ArgumentException) { return new(null, PortraitModeSelectionKind.Unavailable, "设备规格或画面留白配置无效。"); }
        var width = target.Width;
        var height = target.Height;
        if (current.Width == width && current.Height == height)
            return new(null, PortraitModeSelectionKind.AlreadyPortrait, "当前已匹配设备画面尺寸，保持现有模式。");

        // The actual current mode itself is also evidence of a supported
        // orientation. Do not guess orientation values from aspect ratio alone.
        var directional = availableModes.Append(current).Where(m => m is not null && IsValid(m) &&
            (portrait ? m.Height > m.Width : m.Width > m.Height)).ToArray();
        var exact = Rank(directional.Where(m => m.Width == width && m.Height == height), current).FirstOrDefault();
        if (exact is not null)
            return new(exact, PortraitModeSelectionKind.Exact, $"使用驱动提供的 {width} × {height} 设备画面尺寸。");
        var exemplar = Rank(directional, current).FirstOrDefault();
        if (exemplar is null)
            return new(null, PortraitModeSelectionKind.Unavailable, "驱动没有目标方向的模式证据，未更改显示设置。");
        return new(exemplar with { Width = width, Height = height },
            PortraitModeSelectionKind.RequiresDriverValidation,
            $"将验证 {width} × {height} 设备画面尺寸；不替换为其他分辨率。");
    }

    /// <summary>
    /// Fallback after the ideal frame is rejected. Only select native-enumerated
    /// modes which fit entirely between the client's centered overlays. The
    /// selected mode needs explicit per-session ownership, never global admission.
    /// </summary>
    public static PortraitModeDecision SelectSupportedFrame(
        DisplayInfo display, DisplayMode current, IReadOnlyList<DisplayMode> availableModes, PhoneProfile profile,
        ViewportPreferences preferences, bool? portrait = null)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(availableModes);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(preferences);
        if (!display.IsUuVirtual)
            return new(null, PortraitModeSelectionKind.NotApplicable, "所选显示器不是 UU 虚拟屏。");
        if (!IsValid(current) || (portrait is null && !profile.PortraitOnConnect && current.Width == current.Height))
            return new(null, PortraitModeSelectionKind.Unavailable, "当前模式无法确认目标方向。");
        bool direction = portrait ?? (profile.PortraitOnConnect || current.Height > current.Width);
        var reserved = preferences.Insets(direction);
        if (reserved.Left == 0 && reserved.Top == 0 && reserved.Right == 0 && reserved.Bottom == 0)
            return new(null, PortraitModeSelectionKind.Unavailable, "当前方向未启用工具栏留白，原生尺寸不可用时不替换其他分辨率。");
        PhoneScreenSize ideal;
        try { ideal = PhoneScreenGeometry.Calculate(profile, direction, preferences); }
        catch (ArgumentException) { return new(null, PortraitModeSelectionKind.Unavailable, "设备画面配置无效。"); }
        double idealRatio = ideal.Width / (double)ideal.Height;
        var safeModes = availableModes.Where(m => m is not null && IsValid(m) &&
                (m.Height > m.Width) == direction &&
                PhoneScreenGeometry.IsSafeSupportedFrame(m, profile, preferences)).ToArray();
        double RatioError(DisplayMode mode) => Math.Abs(Math.Log((mode.Width / (double)mode.Height) / idealRatio));
        double closestError = safeModes.Select(RatioError).DefaultIfEmpty(double.PositiveInfinity).Min();
        // Treat aspect errors within 0.5% as one group: the fractional difference
        // between 1366x768 and 1920x1080 must not sacrifice usable desktop pixels.
        // Within that group prefer area, then preserve the existing refresh rank.
        var selected = Rank(safeModes.Where(m => RatioError(m) <= closestError + 0.005), current)
            .OrderByDescending(m => (long)m.Width * m.Height)
            .FirstOrDefault();
        return selected is null
            ? new(null, PortraitModeSelectionKind.Unavailable, "驱动没有同方向且能避开 UU 工具栏的可用模式，保持当前显示设置。")
            : new(selected, PortraitModeSelectionKind.Exact,
                $"使用驱动支持的安全画面 {selected.Width} × {selected.Height}；退出后还原。");
    }

    private static bool IsValid(DisplayMode mode) =>
        mode.Width is > 0 and <= 65535 && mode.Height is > 0 and <= 65535 &&
        mode.Frequency >= 0 && mode.BitsPerPixel >= 32 && mode.Orientation is >= 0 and <= 3;

    private static IOrderedEnumerable<DisplayMode> Rank(IEnumerable<DisplayMode> modes, DisplayMode current) => modes
        .OrderBy(m => m.Frequency == current.Frequency ? 0 : m.Frequency <= 1 ? 3 :
            current.Frequency <= 1 || m.Frequency <= current.Frequency ? 1 : 2)
        .ThenBy(m => current.Frequency > 1 && m.Frequency > 1 && m.Frequency <= current.Frequency ? -m.Frequency : m.Frequency)
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
}
