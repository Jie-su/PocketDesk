using System.IO;
using System.Security;
using Microsoft.Win32;
using PocketDesk.Core;

namespace PocketDesk.Native;

/// <summary>Windows App Paths plus built-in utilities. No command lines, shortcuts or folder crawling.</summary>
internal static class RegisteredApps
{
    public static IReadOnlyList<PoolApp> Read()
    {
        var byPath = new Dictionary<string, PoolApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in AppPoolStore.BuiltIns()) byPath.TryAdd(app.ExecutablePath, app);
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var apps = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                if (apps is null) continue;
                foreach (var name in apps.GetSubKeyNames())
                {
                    try
                    {
                        using var entry = apps.OpenSubKey(name);
                        if (entry?.GetValue("") is not string raw ||
                            AppPoolStore.LocalExe(raw.Trim().Trim('"')) is not { } exe || !File.Exists(exe)) continue;
                        byPath.TryAdd(exe, new(Path.GetFileNameWithoutExtension(name), exe));
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException) { }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException) { }
        }
        return byPath.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
