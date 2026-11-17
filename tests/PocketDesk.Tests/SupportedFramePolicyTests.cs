using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestSupportedFramePolicy()
    {
        var phone = DeviceProfileCatalog.FindByControllerName("iPhone 17 Pro")!;
        var tablet = DeviceProfileCatalog.FindByControllerName("iPad Pro (11 英寸, 第 3 代)")!;
        var preferences = new ViewportPreferences();
        var uu = ViewportDisplay(new(0, 0, 1206, 2622));
        var current = new DisplayMode(1206, 2622, 144, 32, 1);
        var supportedPortrait = new DisplayMode(1200, 1920, 144, 32, 1) { DisplayFlags = 2, FixedOutput = 1 };
        var supportedLandscape = new DisplayMode(1920, 1080, 144, 32, 0);

        Test("Portrait 16-by-9 is rejected because its centered frame overlaps the 91-point controls", () =>
        {
            var unsafePortrait = new DisplayMode(1080, 1920, 144, 32, 1);
            Require(!PhoneScreenGeometry.IsSafeSupportedFrame(unsafePortrait, phone, preferences),
                "A narrower 16:9 portrait frame was accepted despite insufficient top and bottom margins.");
            Require(!DeviceModePolicy.SelectSupportedFrame(uu, current, [unsafePortrait, current], phone, preferences).ShouldApply,
                "Fallback chose a driver-listed mode that still covers the client controls.");
        });

        Test("Enumerated 1200-by-1920 provides safe phone portrait margins and preserves driver fields", () =>
        {
            Require(PhoneScreenGeometry.IsSafeSupportedFrame(supportedPortrait, phone, preferences),
                "The confirmed safe portrait candidate was rejected.");
            var decision = DeviceModePolicy.SelectSupportedFrame(uu, current,
                [new(1080, 1920, 165, 32, 1), supportedPortrait], phone, preferences);
            Require(decision.ShouldApply && decision.Mode == supportedPortrait,
                "Fallback failed to return the actual enumerated safe mode with its driver flags.");
        });

        Test("Side-toolbar landscape uses a safe enumerated frame and rejects oversized desktop modes", () =>
        {
            var desktop = new DisplayMode(3840, 2160, 144, 32, 0);
            Require(PhoneScreenGeometry.IsSafeSupportedFrame(supportedLandscape, phone, preferences), "Safe phone landscape was rejected.");
            Require(!PhoneScreenGeometry.IsSafeSupportedFrame(desktop, phone, preferences), "An oversized 4K desktop was adopted as a phone frame.");
            var decision = DeviceModePolicy.SelectSupportedFrame(uu, current,
                [desktop, new(2560, 1440, 144, 32, 0), supportedLandscape], phone, preferences, portrait: false);
            Require(decision.Mode == supportedLandscape, "Landscape chose a larger desktop mode or guessed a different frame.");
            Require(DeviceModePolicy.SelectSupportedFrame(uu, current, [supportedLandscape], phone,
                preferences with { Landscape = LandscapeToolbarPlacement.Left }, portrait: false).Mode == supportedLandscape,
                "Moving a centered side overlay changed safe mode selection.");
        });

        Test("Explicit fallback direction overrides automatic portrait preference", () =>
        {
            var modes = new[] { supportedPortrait, supportedLandscape };
            Require(DeviceModePolicy.SelectSupportedFrame(uu, current, modes, phone, preferences, portrait: false).Mode == supportedLandscape,
                "An explicit landscape request was forced back to automatic portrait.");
            Require(DeviceModePolicy.SelectSupportedFrame(uu, supportedLandscape, modes,
                phone with { PortraitOnConnect = false }, preferences, portrait: true).Mode == supportedPortrait,
                "An explicit portrait request inherited the keep-current-direction preference.");
        });

        Test("Real UU near-equal landscape aspects preserve usable pixels and current refresh", () =>
        {
            // Public driver enumeration captured in verified-frame-live.json on 2026-11-17.
            // The old strict ratio ordering chose 1366x768, making our toolbar too small.
            var sizes = new (int Width, int Height)[] { (1360, 768), (1366, 768), (1536, 864), (1920, 1080) };
            var modes = (from size in sizes
                         from frequency in new[] { 144, 120, 90, 60 }
                         from orientation in new[] { 0, 2 }
                         select new DisplayMode(size.Width, size.Height, frequency, 32, orientation)).ToArray();
            foreach (var enumeration in new[] { modes, modes.Reverse().ToArray() })
            {
                var decision = DeviceModePolicy.SelectSupportedFrame(uu, supportedPortrait,
                    enumeration, phone, preferences, portrait: false);
                Require(decision.Mode == supportedLandscape,
                    "A negligible aspect difference sacrificed 1080p, current 144 Hz, or the normal landscape orientation.");
                var at120 = DeviceModePolicy.SelectSupportedFrame(uu, supportedPortrait with { Frequency = 120 },
                    enumeration, phone, preferences, portrait: false);
                Require(at120.Mode == supportedLandscape with { Frequency = 120 },
                    "The highest-area landscape candidate failed to preserve a supported current refresh rate.");
            }
        });

        Test("Safe fallback is deterministic by aspect fit area and supported refresh", () =>
        {
            var closest = new DisplayMode(1080, 1800, 144, 32, 1);
            var modes = new[] { supportedPortrait, new DisplayMode(900, 1500, 144, 32, 1), closest with { Frequency = 60 }, closest };
            Require(DeviceModePolicy.SelectSupportedFrame(uu, current, modes, phone, preferences).Mode == closest,
                "An inferior aspect fit or smaller equal-aspect mode outranked the best safe candidate.");
            Require(DeviceModePolicy.SelectSupportedFrame(uu, current, modes.Reverse().ToArray(), phone, preferences).Mode == closest,
                "Safe fallback depends on driver enumeration order.");
        });

        Test("Unsupported or invalid enumerations never synthesize a fallback resolution", () =>
        {
            Require(!DeviceModePolicy.SelectSupportedFrame(uu, current, [], phone, preferences).ShouldApply,
                "Empty enumeration manufactured a supported resolution.");
            var invalid = new[] { supportedPortrait with { Orientation = 9 }, supportedPortrait with { BitsPerPixel = 16 },
                supportedPortrait with { Frequency = -1 }, supportedPortrait with { Width = 0 } };
            Require(!DeviceModePolicy.SelectSupportedFrame(uu, current, invalid, phone, preferences).ShouldApply,
                "Invalid driver mode fields were accepted.");
            Require(!DeviceModePolicy.SelectSupportedFrame(uu with { AdapterDeviceId = @"PCI\PHYSICAL" }, current,
                [supportedPortrait], phone, preferences).ShouldApply, "A physical monitor was assigned a fallback phone mode.");
            Require(!DeviceModePolicy.SelectSupportedFrame(uu, current, [supportedPortrait], phone,
                preferences with { Enabled = false }).ShouldApply, "Disabled fitting still selected a smaller standard resolution.");
            Require(!DeviceModePolicy.SelectSupportedFrame(uu, current, [supportedLandscape], phone,
                preferences with { Landscape = LandscapeToolbarPlacement.Hidden }, portrait: false).ShouldApply,
                "A hidden landscape toolbar still caused fallback mode selection.");
        });

        Test("Tablet side-toolbar safety uses tablet geometry rather than phone-wide landscape assumptions", () =>
        {
            var safeTablet = new DisplayMode(1280, 1024, 60, 32, 0);
            Require(PhoneScreenGeometry.IsSafeSupportedFrame(safeTablet, tablet, preferences), "Safe tablet 5:4 landscape was rejected.");
            Require(!PhoneScreenGeometry.IsSafeSupportedFrame(supportedLandscape, tablet, preferences),
                "Phone-safe 16:9 landscape was incorrectly assumed to clear the tablet side controls.");
            Require(DeviceModePolicy.SelectSupportedFrame(uu, new(2388, 1668, 144, 32, 0),
                [supportedLandscape, safeTablet], tablet, preferences, portrait: false).Mode == safeTablet,
                "Tablet fallback used a phone aspect-ratio rule.");
        });

        Test("Standard fallback resolutions never become global device identity evidence", () =>
        {
            var now = new DateTimeOffset(2026, 11, 17, 7, 0, 0, TimeSpan.Zero);
            var unknown = new UuControllerSnapshot(UuControllerStatus.Unknown, [], now);
            var pc = new UuControllerSnapshot(UuControllerStatus.Available, ["Desktop PC"], now);
            foreach (var mode in new[] { supportedPortrait, supportedLandscape, new DisplayMode(3840, 2160, 144, 32, 0) })
            {
                Require(!PhoneSessionPolicy.CanAutoEnter(uu, mode, phone, preferences), "A selected fallback was added to the global phone-size gate.");
                Require(DeviceSessionPolicy.Resolve(unknown, uu, mode, phone, now).Profile is null,
                    "Unknown UIA plus a common resolution guessed the phone identity.");
                Require(DeviceSessionPolicy.Resolve(pc, uu, mode, phone, now).Profile is null,
                    "A reliable PC controller was claimed by fallback dimensions.");
            }
        });
    }
}
