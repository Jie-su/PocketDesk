using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestSwappedOwnedRotation()
    {
        Test("Owned frame rotation swaps exact dimensions in both directions without ratio substitution", () =>
        {
            var phone = DeviceProfileCatalog.FindByControllerName("iPhone 17 Pro")!;
            var preferences = new ViewportPreferences();
            var uu = ViewportDisplay(new(0, 0, 1200, 1920));
            var portrait = new DisplayMode(1200, 1920, 144, 32, 1);
            var landscape = new DisplayMode(1920, 1200, 144, 32, 0);
            var unrelated = new DisplayMode(1920, 1080, 144, 32, 0);
            var modes = new[] { unrelated, landscape with { Frequency = 60 }, portrait with { Frequency = 60 }, landscape, portrait };
            foreach (var enumeration in new[] { modes, modes.Reverse().ToArray() })
            {
                var toLandscape = RotationModePolicy.SelectSwappedOwned(uu, portrait, enumeration, phone, preferences);
                var toPortrait = RotationModePolicy.SelectSwappedOwned(uu, landscape, enumeration, phone, preferences);
                Require(toLandscape.Kind == PortraitModeSelectionKind.Exact && toLandscape.Mode == landscape,
                    "Owned portrait rotation changed the short edge or lost the supported current 144 Hz.");
                Require(toPortrait.Kind == PortraitModeSelectionKind.Exact && toPortrait.Mode == portrait,
                    "Owned landscape rotation failed to return to the same portrait pixels and refresh rate.");
            }
            var missing = RotationModePolicy.SelectSwappedOwned(uu, portrait, [unrelated], phone, preferences);
            Require(!missing.ShouldApply && missing.Mode is null,
                "A missing exact swap silently chose 16:9 or manufactured an unlisted mode.");
            var unsafePortrait = unrelated with { Width = 1080, Height = 1920, Orientation = 1 };
            Require(!RotationModePolicy.SelectSwappedOwned(uu, unrelated, [unsafePortrait], phone, preferences).ShouldApply,
                "An exact swap bypassed the required portrait toolbar margins.");
            Require(!RotationModePolicy.SelectSwappedOwned(uu with { AdapterDeviceId = @"PCI\PHYSICAL" },
                portrait, [landscape], phone, preferences).ShouldApply, "A physical monitor was rotated as an owned UU frame.");
        });
    }
}
