using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestRotationModePolicy()
    {
        var uu = new DisplayInfo(@"\\.\DISPLAY9", "UU", new(0, 0, 1206, 2622), new(0, 0, 1206, 2550), 1.5, true)
        { AdapterName = "GameViewer Virtual Display Adapter", AdapterDeviceId = @"Root\GameViewerIddDriver" };
        var profile = new PhoneProfile { Name = "iPhone 17 Pro", PortraitWidth = 1206, PortraitHeight = 2622 };
        var portrait = new DisplayMode(1206, 2622, 144, 32, 1);
        var landscape = new DisplayMode(2622, 1206, 144, 32, 0);

        Test("Manual phone rotation selects exact native pixels in both directions", () =>
        {
            var toLandscape = RotationModePolicy.Select(uu, portrait, [portrait, landscape], profile);
            var toPortrait = RotationModePolicy.Select(uu, landscape, [landscape, portrait], profile);
            Require(toLandscape.Kind == PortraitModeSelectionKind.Exact && toLandscape.Mode == landscape,
                "Portrait did not select the exact native landscape mode.");
            Require(toPortrait.Kind == PortraitModeSelectionKind.Exact && toPortrait.Mode == portrait,
                "Landscape did not select the exact native portrait mode.");
        });

        Test("Manual phone rotation requires both real UU adapter identity fields", () =>
        {
            foreach (var display in new[]
            {
                uu with { AdapterDeviceId = @"PCI\PHYSICAL" },
                uu with { AdapterName = "Other virtual display" },
                uu with { AdapterName = "NVIDIA", AdapterDeviceId = @"PCI\DISPLAY" }
            })
                Require(!RotationModePolicy.Select(display, portrait, [landscape], profile).ShouldApply,
                    "Manual rotation accepted an unverified or physical adapter.");
        });

        Test("Manual phone rotation rejects desktop sizes and invalid profiles", () =>
        {
            foreach (var current in new[]
            {
                portrait with { Width = 1080, Height = 1920 },
                landscape with { Width = 3840, Height = 2160 },
                portrait with { Width = 0 }, portrait with { BitsPerPixel = 16 },
                portrait with { Orientation = 9 }
            })
                Require(!RotationModePolicy.Select(uu, current, [portrait, landscape], profile).ShouldApply,
                    "A different client size or invalid mode was rotated.");
            foreach (var invalidProfile in new[]
            {
                new PhoneProfile(), profile with { PortraitWidth = 0 },
                profile with { PortraitWidth = 2622, PortraitHeight = 1206 }
            })
                Require(!RotationModePolicy.Select(uu, portrait, [landscape], invalidProfile).ShouldApply,
                    "An invalid phone profile was rotated.");
        });

        Test("Exact rotation dimensions outrank unrelated resolutions and refresh rates", () =>
        {
            var exact = landscape with { Frequency = 60 };
            var decision = RotationModePolicy.Select(uu, portrait,
                [landscape with { Width = 1920, Height = 1080, Frequency = 165 }, exact], profile);
            Require(decision.Mode == exact && !decision.RequiresDriverValidation,
                "Manual rotation replaced native phone pixels with a different resolution.");
        });

        Test("Unlisted rotation pixels require validation and a driver-proven target direction", () =>
        {
            var landscapeExample = new DisplayMode(1920, 1080, 60, 32, 2) { DisplayFlags = 2, FixedOutput = 1 };
            var portraitExample = new DisplayMode(768, 1360, 60, 32, 3) { DisplayFlags = 4, FixedOutput = 2 };
            var toLandscape = RotationModePolicy.Select(uu, portrait, [landscapeExample], profile);
            var toPortrait = RotationModePolicy.Select(uu, landscape, [portraitExample], profile);
            Require(toLandscape.RequiresDriverValidation && toLandscape.Mode == landscapeExample with { Width = 2622, Height = 1206 },
                "Landscape candidate invented mode fields or reused the exemplar resolution.");
            Require(toPortrait.RequiresDriverValidation && toPortrait.Mode == portraitExample with { Width = 1206, Height = 2622 },
                "Portrait candidate invented mode fields or reused the exemplar resolution.");
        });

        Test("No valid target-direction mode prevents manual rotation", () =>
        {
            Require(!RotationModePolicy.Select(uu, portrait, [portrait], profile).ShouldApply,
                "Landscape direction was guessed without driver evidence.");
            Require(!RotationModePolicy.Select(uu, landscape, [landscape], profile).ShouldApply,
                "Portrait direction was guessed without driver evidence.");
            Require(!RotationModePolicy.Select(uu, portrait,
                [landscape with { Orientation = 9 }, landscape with { BitsPerPixel = 16 }], profile).ShouldApply,
                "Invalid target modes counted as direction evidence.");
            Require(!RotationModePolicy.Select(uu, portrait, [], profile).ShouldApply,
                "Empty mode enumeration produced a guessed mode.");
        });

        Test("Manual rotation refresh fallback is supported and order independent", () =>
        {
            var modes = new[] { landscape with { Frequency = 165 }, landscape with { Frequency = 60 }, landscape with { Frequency = 120 } };
            var selected = RotationModePolicy.Select(uu, portrait, modes, profile);
            var reversed = RotationModePolicy.Select(uu, portrait, modes.Reverse().ToArray(), profile);
            Require(selected.Mode?.Frequency == 120 && reversed.Mode == selected.Mode,
                "Rotation invented a refresh rate or depended on enumeration order.");
        });

        Test("Manual rotation remains available when automatic portrait is disabled", () =>
        {
            Require(RotationModePolicy.Select(uu, portrait, [landscape], profile with { PortraitOnConnect = false }).Mode == landscape,
                "The automatic portrait preference blocked an explicit manual rotation.");
        });
    }
}
