using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    // Legacy in-screen placement policy, retained for compatibility coverage.
    // Production now uses PhoneScreenGeometry to change the whole display mode;
    // these tests do not validate the new taskbar or streamed-frame behavior.
    private static void TestPhoneViewportPolicy()
    {
        var phone = new PhoneProfile { PortraitWidth = 1206, PortraitHeight = 2622, LogicalWidth = 402 };
        var portrait = ViewportDisplay(new(0, 0, 1206, 2622));

        Test("Phone portrait reserves client points using native pixel density", () =>
        {
            var area = PhoneViewportPolicy.Calculate(portrait, phone, new(0, 91, 0, 91));
            Require(area == new PixelRect(0, 273, 1206, 2076), "Each 91 point client inset was not converted to 273 native pixels.");
        });

        Test("Landscape keeps short-edge density and supports negative monitor coordinates", () =>
        {
            var landscape = ViewportDisplay(new(-2622, 100, 2622, 1206));
            var area = PhoneViewportPolicy.Calculate(landscape, phone, new(12, 10, 13, 20));
            Require(area == new PixelRect(-2586, 130, 2547, 1116),
                "Landscape used long-edge density or lost the monitor origin.");
        });

        Test("Viewport insets are independent of Windows DPI and round outward", () =>
        {
            var insets = new ViewportInsets(0.25, 0.5, 1.25, 1.5);
            var expected = new PixelRect(1, 2, 1201, 2615);
            foreach (var scale in new[] { 1d, 1.5, 2d })
                Require(PhoneViewportPolicy.Calculate(portrait with { Scale = scale }, phone, insets) == expected,
                    $"Windows scale {scale} changed client inset pixels or rounded inward.");
        });

        Test("Physical monitors and generic 4K UU modes do not receive phone insets", () =>
        {
            var insets = new ViewportInsets(0, 37, 0, 54);
            var physical = portrait with { AdapterName = "Physical GPU", AdapterDeviceId = @"PCI\VEN_TEST" };
            Require(PhoneViewportPolicy.Calculate(physical, phone, insets) == physical.WorkArea,
                "Native dimensions alone were treated as proof of a UU phone.");
            var desktop = ViewportDisplay(new(0, 0, 3840, 2160), new(0, 0, 3840, 2088));
            Require(PhoneViewportPolicy.Calculate(desktop, phone, insets) == desktop.WorkArea,
                "A generic 4K PC connection received phone safe areas.");
        });

        Test("Windows taskbar and UU client insets intersect instead of being subtracted twice", () =>
        {
            var withTaskbar = ViewportDisplay(portrait.Bounds, new(0, 0, 1206, 2550));
            var insets = new ViewportInsets(0, 0, 0, 54);
            Require(PhoneViewportPolicy.Calculate(withTaskbar, phone, insets) == new PixelRect(0, 0, 1206, 2460),
                "The 72 pixel Windows taskbar was deducted again from the client-inset bottom.");
            var tallTaskbar = withTaskbar with { WorkArea = new PixelRect(0, 0, 1206, 2400) };
            Require(PhoneViewportPolicy.Calculate(tallTaskbar, phone, insets) == tallTaskbar.WorkArea,
                "A taller Windows taskbar was allowed to overlap the usable viewport.");
            var leftTaskbar = withTaskbar with { WorkArea = new PixelRect(40, 0, 1166, 2622) };
            Require(PhoneViewportPolicy.Calculate(leftTaskbar, phone, new(12, 0, 0, 0)) == leftTaskbar.WorkArea,
                "A left Windows taskbar and client inset were summed rather than intersected.");
        });

        Test("Invalid or excessive insets fail closed to the original work area", () =>
        {
            foreach (var invalid in new[] { -1d, 201d, double.NaN, double.PositiveInfinity })
            {
                foreach (var insets in new[] {
                    new ViewportInsets(invalid, 0, 0, 0), new ViewportInsets(0, invalid, 0, 0),
                    new ViewportInsets(0, 0, invalid, 0), new ViewportInsets(0, 0, 0, invalid) })
                    Require(PhoneViewportPolicy.Calculate(portrait, phone, insets) == portrait.WorkArea,
                        "Invalid insets changed the work area.");
            }
            Require(PhoneViewportPolicy.Calculate(portrait, phone, new(200, 0, 200, 0)) == portrait.WorkArea,
                "A viewport narrower than the minimum usable logical width was accepted.");
            var shortLandscape = ViewportDisplay(new(0, 0, 2622, 1206));
            Require(PhoneViewportPolicy.Calculate(shortLandscape, phone, new(0, 200, 0, 200)) == shortLandscape.WorkArea,
                "A viewport shorter than the minimum usable logical height was accepted.");
        });

        Test("Invalid profiles and unusable intersections preserve the existing work area", () =>
        {
            foreach (var invalid in new[] {
                phone with { LogicalWidth = 0 }, phone with { LogicalWidth = 239 },
                phone with { LogicalWidth = 1201 }, phone with { PortraitHeight = 1206 },
                phone with { PortraitWidth = 18000, PortraitHeight = 20000 } })
                Require(PhoneViewportPolicy.Calculate(portrait, invalid, new(0, 37, 0, 54)) == portrait.WorkArea,
                    "Invalid profile changed the viewport.");
            var noIntersection = portrait with { WorkArea = new PixelRect(0, 2700, 1206, 200) };
            Require(PhoneViewportPolicy.Calculate(noIntersection, phone, new(0, 37, 0, 54)) == noIntersection.WorkArea,
                "An empty intersection produced a fabricated usable area.");
        });

        Test("iPad viewport uses its own two-pixel density in both orientations", () =>
        {
            var tablet = new PhoneProfile { PortraitWidth = 1668, PortraitHeight = 2388, LogicalWidth = 834, PortraitOnConnect = false };
            var insets = new ViewportInsets(0, 37, 0, 54);
            Require(PhoneViewportPolicy.Calculate(ViewportDisplay(new(0, 0, 1668, 2388)), tablet, insets)
                == new PixelRect(0, 74, 1668, 2206), "iPad portrait inherited phone density.");
            Require(PhoneViewportPolicy.Calculate(ViewportDisplay(new(0, 0, 2388, 1668)), tablet, insets)
                == new PixelRect(0, 74, 2388, 1486), "iPad landscape inherited phone density.");
        });
    }

    private static DisplayInfo ViewportDisplay(PixelRect bounds, PixelRect? workArea = null) =>
        new(@"\\.\DISPLAY_VIEWPORT_TEST", "UU", bounds, workArea ?? bounds, 1.5, true)
        {
            AdapterName = "GameViewer Virtual Display Adapter",
            AdapterDeviceId = @"Root\GameViewerIddDriver"
        };
}
