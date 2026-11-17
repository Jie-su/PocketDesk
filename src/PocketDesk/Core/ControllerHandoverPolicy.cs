using PocketDesk.Native;

namespace PocketDesk.Core;

/// <summary>
/// Distinguishes evidence of another/current controller from an empty session.
/// A fresh None is a disconnect, not a handover: it must not discard our saved
/// display restoration merely because it is a recent observation.
/// </summary>
public static class ControllerHandoverPolicy
{
    public static bool ShouldPreserveCurrentDisplay(UuControllerSnapshot? snapshot, DateTimeOffset? now = null)
    {
        if (snapshot is null) return false;
        var age = (now ?? DateTimeOffset.UtcNow) - snapshot.ObservedAt;
        if (age < TimeSpan.Zero || age > DeviceSessionPolicy.MaximumControllerAge) return false;
        // Multiple controllers can include a new desktop connection, so retain
        // its mode. Unknown/stale evidence cannot authorize dropping recovery;
        // the session's separate mode-ownership check still protects external
        // resolutions when normal restoration is attempted.
        return snapshot.Status is UuControllerStatus.Available or UuControllerStatus.Ambiguous;
    }
}
