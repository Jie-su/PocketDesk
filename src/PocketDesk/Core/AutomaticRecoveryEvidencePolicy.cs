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
