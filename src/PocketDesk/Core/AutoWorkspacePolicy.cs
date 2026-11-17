using PocketDesk.Native;

namespace PocketDesk.Core;

/// <summary>Follows a real UU monitor connection, never generic monitor names or the UU process alone.</summary>
public sealed class AutoWorkspacePolicy
{
    private const long DisconnectGraceMs = 1000;
    // A null value means this suppressed display is currently present. Otherwise
    // the value is the monotonic timestamp of its first observed absence.
    private readonly Dictionary<string, long?> suppressed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<long> tickMs;
    public AutoWorkspacePolicy(Func<long>? tickMs = null) => this.tickMs = tickMs ?? (() => Environment.TickCount64);
    private static string Key(DisplayInfo d) => d.DeviceName + "|" + d.AdapterDeviceId;
    public void SuppressUntilDisconnected(DisplayInfo? display)
    {
        if (display is { IsUuVirtual: true }) suppressed[Key(display)] = null;
    }
    public DisplayInfo? Observe(IReadOnlyList<DisplayInfo> displays, bool enabled)
    {
        var connected = displays.Where(d => d.IsUuVirtual).ToArray();
        var keys = connected.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = tickMs();
        foreach (var key in suppressed.Keys.ToArray())
        {
            var absentSince = suppressed[key];
            if (absentSince is { } since && now - since >= DisconnectGraceMs)
            {
                // Also handle the next observation being an already-reconnected
                // display after a long absence, without requiring an extra poll.
                suppressed.Remove(key);
            }
            else if (keys.Contains(key))
            {
                // A brief mode-switch/recreation is still the same connection.
                // Reset its timer so separate short gaps cannot accumulate.
                suppressed[key] = null;
            }
            else if (absentSince is null)
            {
                suppressed[key] = now;
            }
        }
        if (!enabled) return null;
        // A super screen is primary. Multiple extended UU screens need an explicit choice.
        var selected = connected.FirstOrDefault(d => d.IsPrimary) ?? (connected.Length == 1 ? connected[0] : null);
        return selected is not null && !suppressed.ContainsKey(Key(selected)) ? selected : null;
    }
}
