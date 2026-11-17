using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestDeviceModePolicy()
    {
        var uu = new DisplayInfo(@"\\.\DISPLAY9", "UU", new(0, 0, 3840, 2160), new(0, 0, 3840, 2088), 1.5, true)
        { AdapterName = "GameViewer Virtual Display Adapter", AdapterDeviceId = @"Root\GameViewerIddDriver" };
        var desktop = new DisplayMode(3840, 2160, 144, 32, 0);
        var tablet = DeviceProfileCatalog.FindByControllerName("iPad Pro (11 英寸, 第 3 代)")!;
        var phone = DeviceProfileCatalog.FindByControllerName("iPhone 17 Pro")!;

        Test("Device catalog recognizes only explicit supported model names and aliases", () =>
        {
            Require(tablet is { PortraitWidth: 1668, PortraitHeight: 2388, LogicalWidth: 834, PortraitOnConnect: false },
                "Catalog iPad native size or 834-point layout is wrong.");
            Require(phone is { PortraitWidth: 1206, PortraitHeight: 2622, LogicalWidth: 402, PortraitOnConnect: true },
                "Catalog iPhone native size or 402-point layout is wrong.");
            Require(DeviceProfileCatalog.FindByControllerName("iPad Pro 11 英寸（第 3 代）") == tablet,
                "Canonical localized tablet name was not recognized.");
            foreach (var name in new[] { "", "iPad", "iPad Pro", "iPhone 17 Pro Max", "My iPhone 17 Pro PC", "DESKTOP-PC", "iPad Pro (11 英寸, 第 2 代)" })
                Require(DeviceProfileCatalog.FindByControllerName(name) is null, $"Unsupported model name {name} was guessed.");
        });

        Test("Known iPad adapts a 4K landscape to exact 2388 by 1668 without forcing portrait", () =>
        {
            var native = new DisplayMode(2388, 1668, 144, 32, 0);
            var decision = DeviceModePolicy.Select(uu, desktop, [desktop, native], tablet);
            Require(decision.Mode == native && decision.Kind == PortraitModeSelectionKind.Exact,
                "Tablet landscape used phone portrait pixels or an unrelated mode.");
        });

        Test("Known iPad preserves an existing portrait direction and native refresh", () =>
        {
            var native = new DisplayMode(1668, 2388, 60, 32, 1);
            var decision = DeviceModePolicy.Select(uu, desktop with { Width = 2160, Height = 3840, Orientation = 1 }, [native], tablet);
            Require(decision.Mode == native, "Tablet portrait was forced into landscape.");
            Require(!DeviceModePolicy.Select(uu, native, [native with { Frequency = 144 }], tablet).ShouldApply,
                "Already-native tablet mode was unnecessarily changed to another refresh rate.");
        });

        Test("Known iPhone still requests 1206 by 2622 portrait independently of the tablet", () =>
        {
            var native = new DisplayMode(1206, 2622, 144, 32, 1);
            var decision = DeviceModePolicy.Select(uu, desktop,
                [new DisplayMode(2388, 1668, 144, 32, 0), native], phone);
            Require(decision.Mode == native, "Phone selection leaked tablet dimensions or orientation preference.");
        });

        Test("Unlisted device dimensions retain proven direction fields and require validation", () =>
        {
            var proven = desktop with { Orientation = 2, DisplayFlags = 2, FixedOutput = 1 };
            var decision = DeviceModePolicy.Select(uu, proven, [], tablet);
            Require(decision.RequiresDriverValidation && decision.Mode == proven with { Width = 2388, Height = 1668 },
                "Unlisted tablet dimensions were marked supported or guessed driver mode fields.");
            Require(!DeviceModePolicy.Select(uu, desktop, [desktop], phone).ShouldApply,
                "Phone portrait direction was invented without any portrait mode evidence.");
        });

        Test("Device sizing rejects physical adapters invalid profiles and ambiguous square direction", () =>
        {
            Require(!DeviceModePolicy.Select(uu with { AdapterDeviceId = @"PCI\PHYSICAL" }, desktop, [], tablet).ShouldApply,
                "Physical display was resized to a named remote device.");
            Require(!DeviceModePolicy.Select(uu, desktop with { Width = 2160 }, [], tablet).ShouldApply,
                "Tablet direction was guessed from a square current mode.");
            Require(!DeviceModePolicy.Select(uu, desktop, [], tablet with { PortraitWidth = 0 }).ShouldApply,
                "Invalid native profile dimensions were accepted.");
            Require(!DeviceModePolicy.Select(uu, desktop with { BitsPerPixel = 16 }, [], tablet).ShouldApply,
                "Invalid current mode was accepted.");
        });

        Test("Device sizing never substitutes an unrelated resolution with a preferred refresh", () =>
        {
            var exact = new DisplayMode(2388, 1668, 60, 32, 0);
            var decision = DeviceModePolicy.Select(uu, desktop,
                [new DisplayMode(1920, 1080, 144, 32, 0), exact], tablet);
            Require(decision.Mode == exact, "An unrelated high-refresh resolution outranked native tablet pixels.");
        });
    }
}
