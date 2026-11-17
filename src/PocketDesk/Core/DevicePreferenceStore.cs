using System.IO;
using System.Text.Json;

namespace PocketDesk.Core;

/// <summary>Per-device orientation preferences; never replaces the legacy phone profile.</summary>
public sealed class DevicePreferenceStore
{
    private readonly string path = Path.Combine(RecoveryStore.DataDirectory, "device-preferences.json");
    private readonly Dictionary<string, bool> portrait = new(StringComparer.Ordinal);
    public DevicePreferenceStore()
    {
        try
        {
            if (File.Exists(path))
                foreach (var entry in JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(path)) ?? [])
                    portrait[entry.Key] = entry.Value;
        }
        catch (Exception ex) { RecoveryStore.WriteLog("读取设备方向偏好：" + ex.Message); }
    }
    public PhoneProfile Apply(PhoneProfile profile) => portrait.TryGetValue(Key(profile), out var value)
        ? profile with { PortraitOnConnect = value } : profile;
    public bool AutoFollow => !portrait.TryGetValue("auto-follow", out var enabled) || enabled;
    public void SaveAutoFollow(bool enabled) => SaveEntry("auto-follow", enabled);
    public void Save(PhoneProfile profile) => SaveEntry(Key(profile), profile.PortraitOnConnect);
    private void SaveEntry(string key, bool value)
    {
        var next = new Dictionary<string, bool>(portrait) { [key] = value };
        Directory.CreateDirectory(RecoveryStore.DataDirectory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
        portrait[key] = value;
    }
    private static string Key(PhoneProfile profile) => $"{profile.PortraitWidth}x{profile.PortraitHeight}";
}
