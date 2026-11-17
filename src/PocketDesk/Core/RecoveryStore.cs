using System.IO;
using System.Text.Json;
using PocketDesk.Native;

namespace PocketDesk.Core;

/// <summary>
/// A finite, ordered set of native/safe frame sizes for one device profile and
/// one saved preference. It is persisted before writing a mode and validated
/// against its source geometry when loaded; arbitrary desktop sizes cannot be
/// added to the ownership set by a damaged journal.
/// </summary>
public sealed record PhoneDisplayOwnership
{
    public PhoneProfile Profile { get; init; } = new();
    public ViewportPreferences Preferences { get; init; } = new(false);
    public string ProfileKey { get; init; } = "";
    public List<PhonePixelSize> AllowedSizes { get; init; } = [];
    // Only modes explicitly selected and driver-validated for this session.
    // At most one fallback per orientation; standard sizes are not global phone identifiers.
    public List<PhonePixelSize> SelectedFallbackSizes { get; init; } = [];

    public static PhoneDisplayOwnership Create(PhoneProfile profile, ViewportPreferences preferences) => new()
    {
        Profile = profile,
        Preferences = preferences,
        ProfileKey = PhoneScreenGeometry.ProfileKey(profile),
        AllowedSizes = PhoneScreenGeometry.AllowedSizes(profile, preferences).ToList()
    };

    public bool Contains(DisplayMode mode) => AllowedSizes.Contains(new(mode.Width, mode.Height)) ||
        SelectedFallbackSizes.Contains(new(mode.Width, mode.Height));

    public PhoneDisplayOwnership WithFallback(DisplayMode mode)
    {
        Validate();
        if (!PhoneScreenGeometry.IsSafeSupportedFrame(mode, Profile, Preferences))
            throw new InvalidOperationException("所选备用模式不能安全显示完整设备画面。");
        if (Contains(mode)) return this;
        if (SelectedFallbackSizes.Any(size => (size.Height > size.Width) == (mode.Height > mode.Width)))
            throw new InvalidOperationException("本次已选用另一档同方向备用模式，请先结束并还原工作台。");
        var updated = this with { SelectedFallbackSizes = [.. SelectedFallbackSizes, new(mode.Width, mode.Height)] };
        updated.Validate();
        return updated;
    }

    public bool Matches(PhoneProfile profile, ViewportPreferences preferences) =>
        ProfileKey == PhoneScreenGeometry.ProfileKey(profile) && Preferences == preferences;

    internal void Validate()
    {
        try
        {
            if (Profile is null || Preferences is null || AllowedSizes is null || SelectedFallbackSizes is null ||
                AllowedSizes.Any(size => size is null) || SelectedFallbackSizes.Any(size => size is null) ||
                ProfileKey != PhoneScreenGeometry.ProfileKey(Profile))
                throw new InvalidDataException("设备画面归属信息不完整。");
            var expected = PhoneScreenGeometry.AllowedSizes(Profile, Preferences);
            if (AllowedSizes.Count != expected.Count || !expected.ToHashSet().SetEquals(AllowedSizes))
                throw new InvalidDataException("设备画面归属包含未授权的尺寸。");
            if (SelectedFallbackSizes.Count > 2 ||
                SelectedFallbackSizes.GroupBy(size => size.Height > size.Width).Any(group => group.Count() > 1) ||
                SelectedFallbackSizes.Any(size => AllowedSizes.Contains(size) ||
                    !PhoneScreenGeometry.IsSafeSupportedFrame(new(size.Width, size.Height, 0, 32,
                        size.Height > size.Width ? 1 : 0), Profile, Preferences)))
                throw new InvalidDataException("设备备用画面归属包含不安全或重复的尺寸。");
        }
        catch (ArgumentException ex) { throw new InvalidDataException("设备画面归属配置无效。", ex); }
    }
}

public sealed record RecoveryState
{
    public int Version { get; init; } = 1;
    public List<WindowSnapshot> Windows { get; init; } = [];
    public DisplaySnapshot? Display { get; set; }
    // Optional in version 1: older journals contain only the original display.
    // Legacy-compatible phone pixel-pair marker. All new mode changes additionally
    // use the pending/applied ownership fields below, including manual previews.
    public DisplayMode? AutomaticPhoneMode { get; set; }
    // All display changes, including manual previews, journal their ownership
    // before the native write. Pending survives crashes/failed read-back.
    public DisplayMode? PendingDisplayMode { get; set; }
    public DisplayMode? AppliedDisplayMode { get; set; }
    // Present for native/safe-frame transitions. Older journals retain their
    // strict single unordered pixel-pair behavior without this field.
    public PhoneDisplayOwnership? PhoneDisplayOwner { get; set; }
}

public sealed class RecoveryStore
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PocketDesk");
    public string PathName { get; }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public RecoveryStore(string? path = null) => PathName = path ?? Path.Combine(DataDirectory, "recovery.json");
    public RecoveryState Load()
    {
        if (!File.Exists(PathName)) return new();
        var state = JsonSerializer.Deserialize<RecoveryState>(File.ReadAllText(PathName), JsonOptions) ?? throw new InvalidDataException("恢复记录为空，已停止修改窗口。");
        if (state.Version != 1) throw new InvalidDataException("恢复记录版本不兼容，已停止修改窗口。");
        if (state.Windows is null || state.Windows.Any(w => w is null)) throw new InvalidDataException("恢复记录中的窗口列表损坏，已停止修改窗口。");
        foreach (var mode in new[] { state.AutomaticPhoneMode, state.PendingDisplayMode, state.AppliedDisplayMode })
            if (mode is not null && (mode.Width is <= 0 or > 65535 || mode.Height is <= 0 or > 65535 ||
                mode.Frequency < 0 || mode.BitsPerPixel < 32 || mode.Orientation is < 0 or > 3))
                throw new InvalidDataException("恢复记录中的显示模式归属信息损坏，已停止修改显示模式。");
        var owners = new[] { state.AutomaticPhoneMode, state.PendingDisplayMode, state.AppliedDisplayMode }.OfType<DisplayMode>().ToArray();
        if (state.PhoneDisplayOwner is { } phoneOwner)
        {
            phoneOwner.Validate();
            if (state.Display is null || owners.Length == 0 || owners.Any(mode => !phoneOwner.Contains(mode)))
                throw new InvalidDataException("恢复记录混合了不同设备的画面归属，已停止修改显示设置。");
        }
        else if (owners.Length > 0 && (state.Display is null || owners.Any(mode =>
            Math.Min(mode.Width, mode.Height) != Math.Min(owners[0].Width, owners[0].Height) ||
            Math.Max(mode.Width, mode.Height) != Math.Max(owners[0].Width, owners[0].Height))))
            throw new InvalidDataException("恢复记录混合了不同设备的显示模式归属，已停止修改显示设置。");
        return state;
    }
    public void Save(RecoveryState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        string temporary = PathName + ".tmp";
        // Flush before rename: never move a window before its durable recovery record exists.
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, state, JsonOptions);
            file.Flush(true);
        }
        File.Move(temporary, PathName, true);
    }
    public static void WriteLog(string text)
    {
        try { Directory.CreateDirectory(DataDirectory); File.AppendAllText(Path.Combine(DataDirectory, "diagnostics.log"), $"{DateTimeOffset.Now:O} {text}\n"); } catch { }
    }
}
