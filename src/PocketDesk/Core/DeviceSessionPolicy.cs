using PocketDesk.Native;

namespace PocketDesk.Core;

public enum DeviceSessionSource { None, KnownController, DimensionFallback }

public sealed record DeviceSessionDecision(PhoneProfile? Profile, bool IsControllerIdentified, string Reason)
{
    public DeviceSessionSource Source => Profile is null ? DeviceSessionSource.None
        : IsControllerIdentified ? DeviceSessionSource.KnownController : DeviceSessionSource.DimensionFallback;
}

/// <summary>
/// Gives reliable local UU controller evidence priority over dimension fallback.
/// A named but unsupported controller, no controller, or multiple controllers
/// cannot inherit a phone session merely by reporting the same pixels.
/// </summary>
public static class DeviceSessionPolicy
{
    public static readonly TimeSpan MaximumControllerAge = TimeSpan.FromSeconds(5);

    public static DeviceSessionDecision Resolve(UuControllerSnapshot? snapshot, DisplayInfo? display,
        DisplayMode? mode, PhoneProfile? legacyProfile, DateTimeOffset? now = null,
        ViewportPreferences? preferences = null)
    {
        if (display is not { IsUuVirtual: true })
            return new(null, false, "等待已确认的 UU 虚拟屏。");
        if (mode is null || mode.Width is <= 0 or > 65535 || mode.Height is <= 0 or > 65535 ||
            mode.Frequency < 0 || mode.BitsPerPixel < 32 || mode.Orientation is < 0 or > 3)
            return new(null, false, "等待可读取的有效 UU 显示模式。");

        var age = snapshot is null ? TimeSpan.MaxValue : (now ?? DateTimeOffset.UtcNow) - snapshot.ObservedAt;
        bool fresh = snapshot is not null && age >= TimeSpan.Zero && age <= MaximumControllerAge;
        var status = fresh ? snapshot!.Status : UuControllerStatus.Unknown;
        if (status == UuControllerStatus.Available)
        {
            if (snapshot!.Names is null || snapshot.Names.Count != 1 || string.IsNullOrWhiteSpace(snapshot.Names[0]))
                return new(null, false, "本机控制者信息不完整，未自动选择设备。");
            var profile = DeviceProfileCatalog.FindByControllerName(snapshot.Names[0]);
            if (profile is null)
                return new(null, false, "当前本机控制者未匹配已登记的手机或平板，保持其显示模式。");
            // The catalog owns native pixels and the canonical device name. User
            // preferences only carry over when the saved native pair matches.
            if (legacyProfile is not null && SameNativePair(profile, legacyProfile))
                profile = profile with
                {
                    PortraitOnConnect = legacyProfile.PortraitOnConnect,
                    LogicalWidth = legacyProfile.LogicalWidth is >= 240 and <= 1200
                        ? legacyProfile.LogicalWidth : profile.LogicalWidth
                };
            return new(profile, true, $"UU 本机控制者已识别为 {profile.Name}。");
        }
        if (status == UuControllerStatus.None)
            return new(null, false, "UU 当前没有本机控制者，未自动接管。");
        if (status == UuControllerStatus.Ambiguous)
            return new(null, false, "UU 有多个本机控制者，未自动猜测设备。");
        if (status != UuControllerStatus.Unknown)
            return new(null, false, "UU 控制者状态无效，未自动选择设备。");

        // Unknown or stale UIA evidence is not proof of a device model. Preserve
        // the existing conservative fallback only for an already-saved exact
        // native/saved safe-frame size; never infer a new model from a generic 4K mode.
        if (legacyProfile is not null && legacyProfile.LogicalWidth is >= 240 and <= 1200 &&
            PhoneSessionPolicy.CanAutoEnter(display, mode, legacyProfile, preferences))
            return new(legacyProfile, false, "控制者信息暂不可用；当前尺寸匹配已保存的设备配置。");
        return new(null, false, "等待本机控制者信息或已保存设备的匹配尺寸。");
    }

    private static bool SameNativePair(PhoneProfile a, PhoneProfile b) =>
        a.HasDimensions && b.HasDimensions && a.PortraitWidth == b.PortraitWidth && a.PortraitHeight == b.PortraitHeight;
}
