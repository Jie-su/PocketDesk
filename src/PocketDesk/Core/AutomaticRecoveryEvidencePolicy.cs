using PocketDesk.Native;

namespace PocketDesk.Core;

public enum AutomaticRecoveryDecision { Restore, Preserve, Defer }

/// <summary>
/// Common fallback resolutions cannot identify the remote controller. Crash or
/// startup recovery therefore needs fresh controller evidence before touching
/// a display owned through such a mode. Explicit user finish is unaffected.
/// </summary>
public static class AutomaticRecoveryEvidencePolicy
{
    /// <summary>
    /// An unreadable controller need not block recovery when the entire original
    /// display state is already back. Return Preserve, never Restore: another
    /// controller may change the mode after this observation, and releasing the
    /// old target must not write that mode again. Window recovery remains intact.
    /// </summary>
    public static AutomaticRecoveryDecision AllowReleaseWhenAlreadyRestored(
        AutomaticRecoveryDecision decision, RecoveryState state, DisplaySnapshot? current)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (decision != AutomaticRecoveryDecision.Defer || state.Display is not { } original || current is null)
            return decision;
        if (string.IsNullOrWhiteSpace(original.DeviceName) || string.IsNullOrWhiteSpace(original.Identity) ||
            string.IsNullOrWhiteSpace(current.Identity) || original.Mode is null || current.Mode is null)
            return decision;
        return string.Equals(original.DeviceName, current.DeviceName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(original.Identity, current.Identity, StringComparison.OrdinalIgnoreCase) &&
            original.Mode == current.Mode && original.PositionX == current.PositionX && original.PositionY == current.PositionY
            ? AutomaticRecoveryDecision.Preserve : decision;
    }

    public static AutomaticRecoveryDecision Decide(RecoveryState state, UuControllerSnapshot? snapshot,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.PhoneDisplayOwner?.SelectedFallbackSizes is not { Count: > 0 })
            return AutomaticRecoveryDecision.Restore;
        if (snapshot is null) return AutomaticRecoveryDecision.Defer;
        var age = (now ?? DateTimeOffset.UtcNow) - snapshot.ObservedAt;
        if (age < TimeSpan.Zero || age > DeviceSessionPolicy.MaximumControllerAge)
            return AutomaticRecoveryDecision.Defer;
        if (snapshot.Status == UuControllerStatus.None) return AutomaticRecoveryDecision.Restore;
        if (snapshot.Status == UuControllerStatus.Ambiguous) return AutomaticRecoveryDecision.Preserve;
        if (snapshot.Status != UuControllerStatus.Available) return AutomaticRecoveryDecision.Defer;
        if (snapshot.Names is not { Count: 1 }) return AutomaticRecoveryDecision.Preserve;
        var currentProfile = DeviceProfileCatalog.FindByControllerName(snapshot.Names[0]);
        var ownerProfile = state.PhoneDisplayOwner.Profile;
        return currentProfile is not null && ownerProfile is not null &&
            currentProfile.PortraitWidth == ownerProfile.PortraitWidth &&
            currentProfile.PortraitHeight == ownerProfile.PortraitHeight
            ? AutomaticRecoveryDecision.Restore : AutomaticRecoveryDecision.Preserve;
    }
}
