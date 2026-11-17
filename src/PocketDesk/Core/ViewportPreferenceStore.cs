using System.IO;
using System.Text.Json;

namespace PocketDesk.Core;

public enum LandscapeToolbarPlacement { Bottom, Left, Right, Hidden }
public sealed record ViewportPreferences(bool Enabled = true, LandscapeToolbarPlacement Landscape = LandscapeToolbarPlacement.Right)
{
    // Snug's portrait inset is an empirical starting point, not an OS safe-area API.
    // Landscape is independent: UU's controls can be placed along a different edge.
    public ViewportInsets Insets(bool portrait) => !Enabled ? ViewportInsets.None
        : portrait ? new(0, 91, 0, 91)
        : Landscape switch
        {
            LandscapeToolbarPlacement.Left => new(64, 0, 0, 0),
            LandscapeToolbarPlacement.Right => new(0, 0, 64, 0),
            LandscapeToolbarPlacement.Hidden => ViewportInsets.None,
            _ => new(0, 0, 0, 64)
        };
}

public sealed class ViewportPreferenceStore
{
    private readonly string path = Path.Combine(RecoveryStore.DataDirectory, "viewport-preferences.json");
    private readonly Dictionary<string, ViewportPreferences> profiles = [];
    public ViewportPreferenceStore()
    {
        try
        {
            if (File.Exists(path))
                foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, ViewportPreferences>>(File.ReadAllText(path)) ?? [])
                    if (pair.Value is { } value && Enum.IsDefined(value.Landscape)) profiles[pair.Key] = value;
        }
        catch (Exception ex) { RecoveryStore.WriteLog("读取工具栏留白配置：" + ex.Message); }
    }
    public ViewportPreferences Load(PhoneProfile profile) => profiles.GetValueOrDefault(Key(profile)) ?? new();
    public void Save(PhoneProfile profile, ViewportPreferences value)
    {
        if (!Enum.IsDefined(value.Landscape)) throw new ArgumentOutOfRangeException(nameof(value));
        var next = new Dictionary<string, ViewportPreferences>(profiles) { [Key(profile)] = value };
        Directory.CreateDirectory(RecoveryStore.DataDirectory);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
        profiles[Key(profile)] = value;
    }
    private static string Key(PhoneProfile profile) => $"{profile.PortraitWidth}x{profile.PortraitHeight}";
}
