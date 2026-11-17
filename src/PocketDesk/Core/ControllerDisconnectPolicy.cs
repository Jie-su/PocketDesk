using PocketDesk.Native;

namespace PocketDesk.Core;

/// <summary>
/// Remembers explicit disconnection across a temporarily unreadable UU panel.
/// Unknown is not proof of reconnection and must not re-enable size fallback.
/// This policy only consumes observations; it never polls or mutates a device.
/// </summary>
public sealed class ControllerDisconnectPolicy
{
    public static readonly TimeSpan DisplayDisconnectGrace = TimeSpan.FromSeconds(1);
    private DateTimeOffset? absentSince;
    private DateTimeOffset? lastEvidenceAt;
    private UuControllerStatus lastEvidenceStatus;

    public bool KnownDisconnected { get; private set; }

    /// <param name="displayKey">Identity of an actually attached, verified UU
    /// display, or null when no such display is attached. UI visibility is not
    /// display connectivity and must not be used to generate this argument.</param>
    public bool Observe(UuControllerSnapshot? snapshot, string? displayKey, DateTimeOffset? now = null)
    {
        var observedNow = now ?? DateTimeOffset.UtcNow;
        bool attached = !string.IsNullOrWhiteSpace(displayKey);
        if (KnownDisconnected)
        {
            if (!attached)
            {
                if (absentSince is null || observedNow < absentSince.Value) absentSince = observedNow;
            }
            else if (absentSince is { } since)
            {
                // Native mode changes can momentarily remove the monitor. A
                // short gap must not undo the latch while we restore that mode.
                if (observedNow - since >= DisplayDisconnectGrace) KnownDisconnected = false;
                absentSince = null;
            }
        }
        else absentSince = null;

        if (snapshot is null) return KnownDisconnected;
        var age = observedNow - snapshot.ObservedAt;
        if (age < TimeSpan.Zero || age > DeviceSessionPolicy.MaximumControllerAge ||
            snapshot.Status is not (UuControllerStatus.None or UuControllerStatus.Available or UuControllerStatus.Ambiguous))
            return KnownDisconnected;

        // The same snapshot may be examined by several UI paths. After a real
        // disconnect/reconnect, replaying its old None must not immediately
        // latch again. Older evidence also cannot override a newer controller.
        bool newer = lastEvidenceAt is null || snapshot.ObservedAt > lastEvidenceAt.Value ||
            (snapshot.ObservedAt == lastEvidenceAt.Value && snapshot.Status != lastEvidenceStatus);
        if (!newer) return KnownDisconnected;
        lastEvidenceAt = snapshot.ObservedAt;
        lastEvidenceStatus = snapshot.Status;
        if (snapshot.Status == UuControllerStatus.None)
        {
            if (!KnownDisconnected)
            {
                KnownDisconnected = true;
                absentSince = attached ? null : observedNow;
            }
        }
        else
        {
            KnownDisconnected = false;
            absentSince = null;
        }
        return KnownDisconnected;
    }
}
