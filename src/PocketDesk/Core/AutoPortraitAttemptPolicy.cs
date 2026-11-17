using PocketDesk.Native;

namespace PocketDesk.Core;

/// <summary>
/// Permits one initial portrait attempt per stable UU display connection. Later
/// rotations remain under UU/user control; neither mode changes nor brief driver
/// reconstruction gaps create another automatic attempt.
/// </summary>
public sealed class AutoPortraitAttemptPolicy
{
    private const long StableModeMilliseconds = 500;
    private const long DisconnectMilliseconds = 1000;
    private readonly Func<long> tickMs;
    private string? identity;
    private DisplayMode? stableMode;
    private long? stableSince;
    private long? missingSince;
    private bool attempted;

    public AutoPortraitAttemptPolicy(Func<long>? tickMs = null) =>
        this.tickMs = tickMs ?? (() => Environment.TickCount64);

    /// <summary>Updated by Observe. Consume it before changing any display mode.</summary>
    public bool Ready { get; private set; }

    public void Observe(DisplayInfo? connection, DisplayMode? current)
    {
        var now = tickMs();
        Ready = false;
        if (connection is not { IsUuVirtual: true })
        {
            if (identity is null) return;
            missingSince ??= now;
            // Missing observations are not evidence that a mode stayed stable.
            stableMode = null;
            stableSince = null;
            if (now - missingSince.Value >= DisconnectMilliseconds) ResetConnection();
            return;
        }

        // A long absence also counts if the first next sample is already back.
        if (missingSince is { } absentAt && now - absentAt >= DisconnectMilliseconds)
            ResetConnection();

        var key = IdentityOf(connection);
        var reconnected = missingSince.HasValue;
        if (!string.Equals(identity, key, StringComparison.OrdinalIgnoreCase))
        {
            ResetConnection();
            identity = key;
        }
        missingSince = null;

        // The display still exists when its mode cannot temporarily be read.
        // Do not mistake that for a new connection or clear a consumed attempt.
        if (current is null || current.Width <= 0 || current.Height <= 0 ||
            current.Frequency < 0 || current.BitsPerPixel < 32 || current.Orientation is < 0 or > 3)
        {
            stableMode = null;
            stableSince = null;
            return;
        }
        if (reconnected || stableMode != current || stableSince is null)
        {
            stableMode = current;
            stableSince = now;
        }
        Ready = !attempted && now - stableSince.Value >= StableModeMilliseconds;
    }

    /// <summary>
    /// Call immediately when consuming Ready, before validation or application.
    /// A rejected driver mode must not be retried on every subsequent poll.
    /// </summary>
    public void MarkAttempted()
    {
        if (!Ready) return;
        attempted = true;
        Ready = false;
    }

    /// <summary>A user's rotation takes precedence even before the initial mode is stable.</summary>
    public void SuppressForCurrentConnection()
    {
        if (identity is null) return;
        attempted = true;
        Ready = false;
    }

    /// <summary>Permits one user-requested retry after another stable 500 ms.</summary>
    public void ExplicitRetry()
    {
        attempted = false;
        Ready = false;
        stableSince = identity is not null && missingSince is null && stableMode is not null ? tickMs() : null;
    }

    private static string IdentityOf(DisplayInfo display) =>
        string.Join("|", display.DeviceName, display.AdapterDeviceId, display.AdapterName);

    private void ResetConnection()
    {
        identity = null;
        stableMode = null;
        stableSince = null;
        missingSince = null;
        attempted = false;
        Ready = false;
    }
}
