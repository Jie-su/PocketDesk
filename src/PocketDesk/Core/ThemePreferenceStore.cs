using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PocketDesk.Core;

public enum ThemePreference { System, Light, Dark }

/// <summary>Stores only PocketDesk's appearance preference; never changes Windows settings.</summary>
public sealed class ThemePreferenceStore
{
    private readonly string path;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<ThemePreference>(allowIntegerValues: false) }
    };

    public ThemePreferenceStore(string? path = null) =>
        this.path = Path.GetFullPath(path ?? Path.Combine(RecoveryStore.DataDirectory, "theme.json"));

    public ThemePreference Load()
    {
        try
        {
            if (!File.Exists(path)) return ThemePreference.System;
            var saved = JsonSerializer.Deserialize<SavedTheme>(File.ReadAllText(path), Options);
            if (saved is null || saved.Version != 1 || !Enum.IsDefined(saved.Preference))
                throw new InvalidDataException("主题配置无效。");
            return saved.Preference;
        }
        catch (Exception ex)
        {
            RecoveryStore.WriteLog("读取主题配置：" + ex.Message);
            return ThemePreference.System;
        }
    }

    public void Save(ThemePreference preference)
    {
        if (!Enum.IsDefined(preference)) throw new ArgumentOutOfRangeException(nameof(preference));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new SavedTheme { Preference = preference }, Options));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { /* A cleanup failure must not conceal the original save error. */ }
        }
    }

    private sealed record SavedTheme
    {
        public int Version { get; init; } = 1;
        public ThemePreference Preference { get; init; } = ThemePreference.System;
    }
}
