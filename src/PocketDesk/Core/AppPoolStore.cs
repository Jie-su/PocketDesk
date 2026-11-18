using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace PocketDesk.Core;

public sealed record PoolApp(string Name, string ExecutablePath);
public sealed record AppPoolLaunch(string ExecutablePath, int? ProcessId);

/// <summary>Explicit local application pins. Loading or saving the pool never launches anything.</summary>
public sealed class AppPoolStore
{
    public const int Limit = 24;
    private readonly string path;
    public AppPoolStore(string? path = null) =>
        this.path = path ?? Path.Combine(RecoveryStore.DataDirectory, "app-pool.json");

    public static IEnumerable<PoolApp> BuiltIns()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var (name, exe) in new[] { ("文件资源管理器", Path.Combine(windows, "explorer.exe")),
            ("记事本", Path.Combine(Environment.SystemDirectory, "notepad.exe")) })
            if (File.Exists(exe)) yield return new(name, exe);
    }

    public static string? LocalExe(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length < 4 || !char.IsAsciiLetter(raw[0]) ||
            raw[1] != ':' || raw[2] is not ('\\' or '/') || raw[2..].IndexOfAny([':', '"', '|', '*', '?', '<', '>']) >= 0 ||
            raw.Any(char.IsControl)) return null;
        try
        {
            var full = Path.GetFullPath(raw);
            return string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { return null; }
    }

    public IReadOnlyList<PoolApp> Read()
    {
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length > 1024 * 1024) throw new InvalidDataException("应用池文件过大。");
            var apps = JsonSerializer.Deserialize<List<PoolApp?>>(file)
                ?? throw new InvalidDataException("应用池文件为空。");
            var result = new List<PoolApp>();
            foreach (var app in apps)
            {
                if (app is null || LocalExe(app.ExecutablePath) is not { } exe || Contains(result, exe)) continue;
                result.Add(new(Label(app.Name, exe), exe));
                if (result.Count == Limit) break;
            }
            return result;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return BuiltIns().ToArray(); }
    }

    public void Add(PoolApp app)
    {
        var exe = LocalExe(app.ExecutablePath);
        if (exe is null || !File.Exists(exe)) throw new InvalidOperationException("请选择已安装的本地 .exe 程序。");
        var current = Read().ToList();
        if (Contains(current, exe)) return;
        if (current.Count >= Limit) throw new InvalidOperationException($"应用池最多可固定 {Limit} 个程序。");
        current.Add(new(Label(app.Name, exe), exe));
        Save(current);
    }

    public void Remove(string pathToRemove)
    {
        var apps = Read().ToList();
        if (apps.RemoveAll(a => StringComparer.OrdinalIgnoreCase.Equals(a.ExecutablePath, pathToRemove)) > 0) Save(apps);
    }

    public ProcessStartInfo PrepareLaunch(string selectedPath)
    {
        var found = Read().SingleOrDefault(a => StringComparer.OrdinalIgnoreCase.Equals(a.ExecutablePath, selectedPath));
        if (found is null) throw new InvalidOperationException("此程序已从应用池移除。");
        if (!File.Exists(found.ExecutablePath)) throw new FileNotFoundException("程序已移动或卸载，请重新添加。");
        return new ProcessStartInfo(found.ExecutablePath) { UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(found.ExecutablePath)! };
    }

    private static string Label(string? name, string exe)
    {
        var text = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(exe) : name.Trim();
        return string.Concat(text.Where(c => !char.IsControl(c)).Take(100));
    }

    private static bool Contains(IEnumerable<PoolApp> apps, string exe) =>
        apps.Any(a => StringComparer.OrdinalIgnoreCase.Equals(a.ExecutablePath, exe));

    private void Save(List<PoolApp> apps)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(apps));
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
}
