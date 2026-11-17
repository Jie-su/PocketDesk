using System.IO;
using System.Text.Json;

namespace PocketDesk.Core;

public sealed record PhoneProfile
{
    public string Name { get; init; } = "跟随 UU 提供的尺寸";
    public int PortraitWidth { get; init; }
    public int PortraitHeight { get; init; }
    public int LogicalWidth { get; init; } = 402;
    public bool PortraitOnConnect { get; init; } = true;
    public bool HasDimensions => PortraitWidth > 0 && PortraitHeight > PortraitWidth;
    public string Label => HasDimensions ? $"{Name} · {PortraitWidth} × {PortraitHeight}" : Name;
}

public sealed class PhoneProfileStore
{
    private readonly string path = Path.Combine(RecoveryStore.DataDirectory, "phone.json");
    public PhoneProfile Load()
    {
        try
        {
            if (!File.Exists(path)) return new();
            var profile = JsonSerializer.Deserialize<PhoneProfile>(File.ReadAllText(path));
            if (profile is null || string.IsNullOrWhiteSpace(profile.Name)
                || profile.PortraitWidth < 0 || profile.PortraitHeight < 0
                || profile.PortraitWidth > 16384 || profile.PortraitHeight > 16384
                || profile.LogicalWidth is < 240 or > 1200
                || ((profile.PortraitWidth != 0 || profile.PortraitHeight != 0) && !profile.HasDimensions))
                throw new InvalidDataException("手机尺寸配置无效。");
            return profile;
        }
        catch (Exception ex)
        {
            RecoveryStore.WriteLog("读取手机配置：" + ex.Message);
            return new();
        }
    }
    public void Save(PhoneProfile profile)
    {
        Directory.CreateDirectory(RecoveryStore.DataDirectory);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
