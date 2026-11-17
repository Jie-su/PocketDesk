using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestPhoneScreenGeometry()
    {
        var phone = DeviceProfileCatalog.FindByControllerName("iPhone 17 Pro")!;
        var tablet = DeviceProfileCatalog.FindByControllerName("iPad Pro (11 英寸, 第 3 代)")!;
        var preferences = new ViewportPreferences();
        var uu = ViewportDisplay(new(0, 0, 1206, 2622));
        var nativePortrait = new DisplayMode(1206, 2622, 144, 32, 1);
        var nativeLandscape = new DisplayMode(2622, 1206, 144, 32, 0);
        var fittedPortrait = nativePortrait with { Height = 2076 };
        var fittedLandscape = nativeLandscape with { Width = 2238 };

        Test("Portrait fitting changes the whole desktop frame rather than window placement", () =>
        {
            var frame = PhoneScreenGeometry.Calculate(phone, true, preferences);
            Require(frame is { Width: 1206, Height: 2076, LogicalWidth: 402, LogicalHeight: 692, IsFitted: true },
                "The streamed desktop frame still includes the client toolbar area.");
            var decision = DeviceModePolicy.Select(uu, nativePortrait, [nativePortrait, fittedPortrait], phone, preferences);
            Require(decision.Mode == fittedPortrait && decision.ShouldApply,
                "A native screen was left unchanged while only application placement was adjusted.");
            Require(!DeviceModePolicy.Select(uu, fittedPortrait, [fittedPortrait], phone, preferences).ShouldApply,
                "An already-fitted desktop frame would be repeatedly resized.");
        });

        Test("Side-toolbar landscape is independently fitted with symmetric outer margins", () =>
        {
            var frame = PhoneScreenGeometry.Calculate(phone, false, preferences);
            Require(frame is { Width: 2238, Height: 1206, LogicalWidth: 746, LogicalHeight: 402, IsFitted: true },
                "Landscape failed to reserve equal sides for UU's centered desktop frame.");
            var portrait = PhoneScreenGeometry.Calculate(phone, true, preferences);
            Require(frame.Width != portrait.Height && frame.Height == portrait.Width,
                "Landscape was calculated by blindly swapping the fitted portrait frame.");
            var left = PhoneScreenGeometry.Calculate(phone, false, preferences with { Landscape = LandscapeToolbarPlacement.Left });
            Require(left == frame, "Left and right overlays should produce the same centered frame dimensions.");
        });

        Test("iPad fitted modes use the tablet density and retain each orientation", () =>
        {
            var portrait = PhoneScreenGeometry.Calculate(tablet, true, preferences);
            var landscape = PhoneScreenGeometry.Calculate(tablet, false, preferences);
            Require(portrait is { Width: 1668, Height: 2024, LogicalWidth: 834, LogicalHeight: 1012, IsFitted: true },
                "iPad portrait inherited phone dimensions or density.");
            Require(landscape is { Width: 2132, Height: 1668, LogicalWidth: 1066, LogicalHeight: 834, IsFitted: true },
                "iPad landscape inherited the portrait crop or changed orientation.");
        });

        Test("Bottom hidden and disabled toolbar preferences produce distinct whole-frame modes", () =>
        {
            var bottom = PhoneScreenGeometry.Calculate(phone, false, preferences with { Landscape = LandscapeToolbarPlacement.Bottom });
            Require(bottom is { Width: 2622, Height: 822, IsFitted: true },
                "Bottom toolbar fitting must reserve symmetric top and bottom space.");
            var hidden = preferences with { Landscape = LandscapeToolbarPlacement.Hidden };
            Require(PhoneScreenGeometry.Calculate(phone, false, hidden) is { Width: 2622, Height: 1206, IsFitted: false },
                "A hidden landscape toolbar still reduced the streamed frame.");
            Require(PhoneScreenGeometry.Calculate(phone, true, hidden).IsFitted,
                "The landscape preference unexpectedly disabled portrait fitting.");
            var disabled = preferences with { Enabled = false };
            Require(PhoneScreenGeometry.Calculate(phone, true, disabled) is { Width: 1206, Height: 2622, IsFitted: false } &&
                PhoneScreenGeometry.Calculate(phone, false, disabled) is { Width: 2622, Height: 1206, IsFitted: false },
                "Disabling fitting failed to restore both native target sizes.");
        });

        Test("Fitted session admission is an exact ordered family and never admits 4K PC modes", () =>
        {
            var expected = new HashSet<PhonePixelSize>
            {
                new(1206, 2622), new(2622, 1206), new(1206, 2076), new(2238, 1206)
            };
            Require(expected.SetEquals(PhoneScreenGeometry.AllowedSizes(phone, preferences)),
                "The allowed family contains a guessed mode or omits a native/fitted orientation.");
            foreach (var mode in new[] { nativePortrait, nativeLandscape, fittedPortrait, fittedLandscape })
                Require(PhoneSessionPolicy.CanAutoEnter(uu, mode, phone, preferences), "An exact family mode was not admitted.");
            foreach (var mode in new[]
            {
                fittedPortrait with { Width = 2076, Height = 1206 },
                fittedLandscape with { Width = 1206, Height = 2238 },
                nativeLandscape with { Width = 3840, Height = 2160 },
                nativeLandscape with { Width = 1920, Height = 1080 }
            })
                Require(!PhoneSessionPolicy.CanAutoEnter(uu, mode, phone, preferences), "An unrelated or blind-swapped size entered the phone session.");
            Require(!PhoneSessionPolicy.CanAutoEnter(uu with { AdapterDeviceId = @"PCI\PHYSICAL" }, fittedPortrait, phone, preferences),
                "Fitted pixels allowed a physical monitor through the UU gate.");
            Require(!PhoneSessionPolicy.CanAutoEnter(uu, fittedPortrait, phone), "Legacy native-only selection silently widened its dimensions.");
        });

        Test("Fitted manual rotation selects the opposite orientation rather than swapping pixels", () =>
        {
            var blindLandscape = fittedPortrait with { Width = 2076, Height = 1206, Orientation = 0 };
            var blindPortrait = fittedLandscape with { Width = 1206, Height = 2238, Orientation = 1 };
            var modes = new[] { blindLandscape, blindPortrait, fittedPortrait, fittedLandscape };
            Require(RotationModePolicy.Select(uu, fittedPortrait, modes, phone, preferences).Mode == fittedLandscape,
                "Portrait-to-landscape selected the swapped fitted dimensions.");
            Require(RotationModePolicy.Select(uu, fittedLandscape, modes, phone, preferences).Mode == fittedPortrait,
                "Landscape-to-portrait selected the swapped fitted dimensions.");
            Require(RotationModePolicy.Select(uu, nativePortrait, modes, phone, preferences).Mode == fittedLandscape,
                "Rotation from a native frame ignored the enabled fitting preference.");
        });

        Test("Unlisted fitted rotation requires driver validation and keeps proven mode fields", () =>
        {
            var evidence = new DisplayMode(1920, 1080, 60, 32, 2) { DisplayFlags = 2, FixedOutput = 1 };
            var decision = RotationModePolicy.Select(uu, fittedPortrait, [evidence], phone, preferences);
            Require(decision.RequiresDriverValidation && decision.Mode == evidence with { Width = 2238, Height = 1206 },
                "Fitted rotation reused an unrelated resolution or invented driver fields.");
            Require(!RotationModePolicy.Select(uu, fittedPortrait, [fittedPortrait], phone, preferences).ShouldApply,
                "Missing target-orientation evidence produced a guessed fitted mode.");
            Require(!RotationModePolicy.Select(uu, nativeLandscape with { Width = 3840, Height = 2160 },
                [fittedPortrait, fittedLandscape], phone, preferences).ShouldApply,
                "Manual fitting claimed a new PC desktop based only on a UU adapter.");
        });

        Test("Invalid geometry cannot manufacture fitted display dimensions", () =>
        {
            foreach (var invalid in new[]
            {
                phone with { LogicalWidth = 0 }, phone with { PortraitWidth = 0 },
                phone with { PortraitHeight = phone.PortraitWidth }, phone with { PortraitWidth = 18000, PortraitHeight = 20000 }
            })
                ExpectThrows(() => PhoneScreenGeometry.Calculate(invalid, true, preferences), "Invalid profile produced display dimensions.");
            ExpectThrows(() => PhoneScreenGeometry.Calculate(phone, false,
                preferences with { Landscape = (LandscapeToolbarPlacement)99 }), "Unknown toolbar edge produced a fitted mode.");
            var shortProfile = phone with { PortraitWidth = 1206, PortraitHeight = 1300 };
            var small = PhoneScreenGeometry.Calculate(shortProfile, true, preferences);
            Require(small is { Width: 1206, Height: 1300, IsFitted: false },
                "Excessive fitting inverted a portrait frame instead of retaining its native size.");
        });
    }
}
