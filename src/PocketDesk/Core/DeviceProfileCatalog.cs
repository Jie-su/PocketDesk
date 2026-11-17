using System.Text;

namespace PocketDesk.Core;

/// <summary>
/// Small, verified model catalog. A model name is useful only when supplied by
/// the trusted current-controller reader; this class does not establish trust.
/// </summary>
public static class DeviceProfileCatalog
{
    // Apple specifications: https://support.apple.com/en-us/111897
    // The 834-point layout baseline is half the native short edge (@2x).
    private static readonly PhoneProfile IpadPro11ThirdGeneration = new()
    {
        Name = "iPad Pro 11 英寸（第 3 代）", PortraitWidth = 1668, PortraitHeight = 2388,
        LogicalWidth = 834, PortraitOnConnect = false
    };
    // Apple specifications: https://support.apple.com/en-us/125090
    private static readonly PhoneProfile Iphone17Pro = new()
    {
        Name = "iPhone 17 Pro", PortraitWidth = 1206, PortraitHeight = 2622,
        LogicalWidth = 402, PortraitOnConnect = true
    };

    public static IReadOnlyList<PhoneProfile> KnownProfiles { get; } =
        Array.AsReadOnly(new[] { Iphone17Pro, IpadPro11ThirdGeneration });

    public static PhoneProfile? FindByControllerName(string? controllerName)
    {
        if (string.IsNullOrWhiteSpace(controllerName)) return null;
        var key = Normalize(controllerName);
        return key switch
        {
            "iphone17pro" => Iphone17Pro,
            "ipadpro(11英寸,第3代)" or "ipadpro11英寸(第3代)" or
            "ipadpro11-inch(3rdgeneration)" or "ipadpro(11-inch)(3rdgeneration)" or
            "ipadpro(11-inch,3rdgeneration)" or "ipadpro11inch(3rdgeneration)" => IpadPro11ThirdGeneration,
            _ => null
        };
    }

    private static string Normalize(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC)
        .Where(character => !char.IsWhiteSpace(character)))
        .Replace('，', ',').Replace('‑', '-').Replace('–', '-').ToLowerInvariant();
}
