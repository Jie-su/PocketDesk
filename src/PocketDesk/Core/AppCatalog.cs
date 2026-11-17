using System.Diagnostics;
using System.Windows.Media;
using PocketDesk.Native;

namespace PocketDesk.Core;

public sealed record AppWindowGroup(
    string AppName,
    string ProcessName,
    ImageSource? Icon,
    IReadOnlyList<WindowEntry> Windows)
{
    public string Key { get; init; } = "";
    public string Summary => $"{Windows.Count} 个窗口";
}

/// <summary>Builds the app library from window metadata; never reads app documents.</summary>
public static class AppCatalog
{
    private sealed record Metadata(string Name, string ProcessName, string Path, long StartTicks);
    private static readonly Dictionary<(int Pid, long StartTicks), Metadata> MetadataCache = [];
    private static readonly object CacheLock = new();

    public static IReadOnlyList<AppWindowGroup> GroupWindows(IEnumerable<WindowEntry> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var source = windows.ToArray();
        var metadataByPid = new Dictionary<int, Metadata>();
        var groups = new Dictionary<string, List<WindowEntry>>(StringComparer.OrdinalIgnoreCase);
        var groupMetadata = new Dictionary<string, Metadata>(StringComparer.OrdinalIgnoreCase);

        // Dictionaries preserve first-seen order: refreshing a title or searching
        // must not reshuffle cards under the user's finger.
        foreach (var window in source)
        {
            if (!metadataByPid.TryGetValue(window.ProcessId, out var metadata))
            {
                metadata = ReadMetadata(window);
                metadataByPid.Add(window.ProcessId, metadata);
            }
            // The same executable may own several processes (browsers/Electron).
            // If its path is inaccessible, keep different process identities apart
            // instead of merging unrelated programs that share an executable name.
            var key = metadata.Path.Length > 0
                ? "exe:" + metadata.Path
                : $"process:{window.ProcessName}:{window.ProcessId}:{metadata.StartTicks}";
            if (!groups.TryGetValue(key, out var entries))
            {
                entries = [];
                groups.Add(key, entries);
                groupMetadata.Add(key, metadata);
            }
            entries.Add(window);
        }

        var result = new List<AppWindowGroup>(groups.Count);
        foreach (var (key, entries) in groups)
        {
            var metadata = groupMetadata[key];
            var items = entries.ToArray();
            var icon = AppIconService.GetIcon(key, items, metadata.Path);
            result.Add(new AppWindowGroup(metadata.Name, metadata.ProcessName, icon, items) { Key = key });
        }

        // Cached bitmaps are frozen managed data, not live native icon handles.
        // A generous bound retains app identities when a search temporarily hides
        // their windows without allowing indefinite growth over a long session.
        lock (CacheLock)
        {
            if (MetadataCache.Count > 256)
            {
                var live = metadataByPid.Select(x => (x.Key, x.Value.StartTicks)).ToHashSet();
                foreach (var key in MetadataCache.Keys.Where(k => !live.Contains(k)).ToArray())
                    MetadataCache.Remove(key);
            }
        }
        return result;
    }

    private static Metadata ReadMetadata(WindowEntry window)
    {
        var fallback = string.IsNullOrWhiteSpace(window.ProcessName) ? "应用" : window.ProcessName;
        try
        {
            using var process = Process.GetProcessById(window.ProcessId);
            var started = process.StartTime.ToUniversalTime().Ticks;
            var key = (window.ProcessId, started);
            lock (CacheLock)
                if (MetadataCache.TryGetValue(key, out var cached))
                    return cached;

            string path = "";
            try { path = process.MainModule?.FileName ?? ""; }
            catch { /* Elevated/protected apps can still be listed by their process name. */ }

            var name = fallback;
            // FileDescription is executable resource metadata. Avoid fetching files
            // from network shares just to decorate an application card.
            if (path.Length > 0 && !path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                try
                {
                    var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                    if (!string.IsNullOrWhiteSpace(description))
                        name = description.Replace('\r', ' ').Replace('\n', ' ').Trim();
                }
                catch { /* Missing/unreadable version resources use the process name. */ }
            }
            var metadata = new Metadata(name, fallback, path, started);
            lock (CacheLock) MetadataCache[key] = metadata;
            return metadata;
        }
        catch
        {
            // The application can disappear between window enumeration and here.
            return new Metadata(fallback, fallback, "", 0);
        }
    }
}
