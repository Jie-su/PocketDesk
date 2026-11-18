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

    public bool KnownDisconnected { get; private set; }
    /// <summary>
    /// Increases once when a newer, fresh single controller confirms connection
    /// after an explicit None. Consumers can observe this after any UI read path
    /// without losing the event or treating a repeated snapshot as another retry.
    /// Physical monitor reconnection remains governed by the existing gap policy.
    /// </summary>
    public long ReconnectionGeneration { get; private set; }

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
        if (snapshot.Status == UuControllerStatus.None && snapshot.Names is not { Count: 0 })
            return KnownDisconnected;
        if (snapshot.Status == UuControllerStatus.Available &&
            (snapshot.Names is not { Count: 1 } || !IsUsableName(snapshot.Names[0])))
            return KnownDisconnected;

        // The same snapshot may be examined by several UI paths. After a real
        // disconnect/reconnect, replaying its old None must not immediately
        // latch again. A conflicting status with the same timestamp is also not
        // newer evidence; accepting it could alternate None/Available forever.
        bool newer = lastEvidenceAt is null || snapshot.ObservedAt > lastEvidenceAt.Value;
        if (!newer) return KnownDisconnected;
        lastEvidenceAt = snapshot.ObservedAt;
        if (snapshot.Status == UuControllerStatus.None)
        {
            if (!KnownDisconnected)
            {
                KnownDisconnected = true;
                absentSince = attached ? null : observedNow;
            }
        }
        else if (snapshot.Status == UuControllerStatus.Available)
        {
            if (KnownDisconnected) ReconnectionGeneration++;
            KnownDisconnected = false;
            absentSince = null;
        }
        // Multiple controllers cannot authorize clearing a manual stop or
        // consuming another initial portrait attempt. Keep the None latch until
        // a newer single-controller observation or a real monitor reconnection.
        return KnownDisconnected;
    }

    private static bool IsUsableName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 256 && !name.Any(char.IsControl);
}
