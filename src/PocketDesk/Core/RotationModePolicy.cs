using PocketDesk.Native;

namespace PocketDesk.Core;

/// <summary>
/// Chooses the opposite orientation's configured native or safe-frame size.
/// It does not infer physical orientation or observe UU buttons.
/// </summary>
public static class RotationModePolicy
{
    /// <summary>
    /// Rotates an already authorized device frame by swapping its actual pixel
    /// dimensions. The caller must first establish native/canonical membership
    /// or ownership by the current session; this method does not identify a
    /// phone connection. Only an exact driver-enumerated counterpart is eligible,
    /// and applying it still requires CDS_TEST plus mode read-back.
    /// </summary>
    public static PortraitModeDecision SelectSwappedOwned(
        DisplayInfo display, DisplayMode current, IReadOnlyList<DisplayMode> availableModes, PhoneProfile profile,
        ViewportPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(availableModes);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(preferences);
        if (!display.IsUuVirtual)
            return new(null, PortraitModeSelectionKind.NotApplicable, "所选显示器不是 UU 虚拟屏，未切换方向。");
        if (!IsValid(current) || current.Width == current.Height)
            return new(null, PortraitModeSelectionKind.Unavailable, "当前显示模式无效或为正方形，无法互换方向。");

        int width = current.Height;
        int height = current.Width;
        bool portrait = height > width;
        var target = Rank(availableModes.Where(mode => mode is not null && IsValid(mode) &&
            mode.Width == width && mode.Height == height), current).FirstOrDefault();
        if (target is null)
            return new(null, PortraitModeSelectionKind.Unavailable,
                $"驱动未提供互换后的 {width} × {height} 模式，保持当前画面，不替换其他比例。");

        bool isBase;
        try { isBase = PhoneScreenGeometry.MatchesNativeOrFitted(target, profile, preferences); }
        catch (ArgumentException)
        {
            return new(null, PortraitModeSelectionKind.Unavailable, "设备规格或工具栏留白配置无效，未切换方向。");
        }
        bool isSafe = PhoneScreenGeometry.IsSafeSupportedFrame(target, profile, preferences);
        if (!isBase && !isSafe)
            return new(null, PortraitModeSelectionKind.Unavailable,
                $"互换后的 {width} × {height} 无法保留当前设置所需的 UU 工具栏空间，保持当前画面。");

        bool isNative = width == (portrait ? profile.PortraitWidth : profile.PortraitHeight) &&
            height == (portrait ? profile.PortraitHeight : profile.PortraitWidth);
        var insets = preferences.Insets(portrait);
        bool hasReservedEdge = insets.Left > 0 || insets.Top > 0 || insets.Right > 0 || insets.Bottom > 0;
        string notice = isNative && hasReservedEdge && !isSafe
            ? "原生尺寸未预留 UU 工具栏空间，工具栏展开时可能遮挡画面。"
            : "";
        return new(target, PortraitModeSelectionKind.Exact,
            $"使用驱动提供的 {width} × {height} {(portrait ? "竖屏" : "横屏")}模式，保持宽高互换。{notice}");
    }

    public static PortraitModeDecision Select(
        DisplayInfo display, DisplayMode current, IReadOnlyList<DisplayMode> availableModes, PhoneProfile profile,
        ViewportPreferences? preferences = null)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(availableModes);
        ArgumentNullException.ThrowIfNull(profile);
        if (!PhoneSessionPolicy.CanAutoEnter(display, current, profile, preferences))
            return new(null, PortraitModeSelectionKind.NotApplicable,
                "当前屏幕未匹配已配置设备的画面尺寸，未切换方向。");

        var targetIsPortrait = current.Width > current.Height;
        var target = PhoneScreenGeometry.Calculate(profile, targetIsPortrait, preferences ?? new(false));
        var width = target.Width;
        var height = target.Height;
        var directional = availableModes.Where(m => m is not null && IsValid(m) &&
            (targetIsPortrait ? m.Height > m.Width : m.Width > m.Height)).ToArray();
        var exact = Rank(directional.Where(m => m.Width == width && m.Height == height), current).FirstOrDefault();
        if (exact is not null)
            return new(exact, PortraitModeSelectionKind.Exact,
                $"使用驱动提供的 {width} × {height} {(targetIsPortrait ? "竖屏" : "横屏")}模式。");

        var exemplar = Rank(directional, current).FirstOrDefault();
        if (exemplar is null)
            return new(null, PortraitModeSelectionKind.Unavailable,
                "驱动未提供目标方向的有效模式，保持当前画面。");

        // Keep orientation/refresh/flags from evidence supplied by the driver.
        // Compute each direction independently: the two safe frames need not be
        // a width/height swap. CDS_TEST and actual
        // mode read-back in RotatePhoneDisplay are still mandatory.
        return new(exemplar with { Width = width, Height = height },
            PortraitModeSelectionKind.RequiresDriverValidation,
            $"驱动未列出 {width} × {height}，将在验证通过后切换；不会使用其他分辨率。");
    }

    private static bool IsValid(DisplayMode mode) =>
        mode.Width is > 0 and <= 65535 && mode.Height is > 0 and <= 65535 &&
        mode.Frequency >= 0 && mode.BitsPerPixel >= 32 && mode.Orientation is >= 0 and <= 3;

    private static IOrderedEnumerable<DisplayMode> Rank(IEnumerable<DisplayMode> modes, DisplayMode current) => modes
        .OrderBy(m => m.Frequency == current.Frequency ? 0 : m.Frequency <= 1 ? 3 :
            current.Frequency <= 1 || m.Frequency <= current.Frequency ? 1 : 2)
        .ThenBy(m => current.Frequency > 1 && m.Frequency > 1 && m.Frequency <= current.Frequency ? -m.Frequency : m.Frequency)
        .ThenBy(m => m.Orientation is 0 or 1 ? 0 : 1)
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
