using PocketDesk.Native;

namespace PocketDesk.Core;

/// <summary>
/// A conservative pixel-size gate for automatic mobile workspaces. It does not
/// read or prove the remote client's identity: a desktop client can also request
/// these dimensions. Manual workspace and mode actions are outside this policy.
/// Re-evaluate using UU's actually reported mode on every observation, including
/// while an automatic workspace is active.
/// </summary>
public static class PhoneSessionPolicy
{
    public const string WaitingMessage = "等待 UU 上报与手机匹配的尺寸";

    public static bool CanAutoEnter(DisplayInfo? display, DisplayMode? current, PhoneProfile? profile,
        ViewportPreferences? preferences = null)
    {
        if (display is not { IsUuVirtual: true } || current is null || profile is null)
            return false;
        if (profile.PortraitWidth is <= 0 or > 16384 ||
            profile.PortraitHeight is <= 0 or > 16384 ||
            profile.PortraitHeight <= profile.PortraitWidth)
            return false;
        if (current.Width <= 0 || current.Height <= 0 || current.Frequency < 0 ||
            current.BitsPerPixel < 32 || current.Orientation is < 0 or > 3)
            return false;

        if (preferences is not null)
        {
            try { return PhoneScreenGeometry.MatchesNativeOrFitted(current, profile, preferences); }
            catch (ArgumentException) { return false; }
        }
        var width = profile.PortraitWidth;
        var height = profile.PortraitHeight;
        return (current.Width == width && current.Height == height)
            || (current.Width == height && current.Height == width);
    }
}
