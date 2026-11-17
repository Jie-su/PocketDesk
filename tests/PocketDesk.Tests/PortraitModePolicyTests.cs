using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestPortraitModePolicy()
    {
        var uu = new DisplayInfo(@"\\.\DISPLAY9", "UU", new(0, 0, 2622, 1206), new(0, 0, 2622, 1134), 1.5, true)
        {
            AdapterName = "GameViewer Virtual Display Adapter", AdapterDeviceId = @"Root\GameViewerIddDriver"
        };
        var current = new DisplayMode(2622, 1206, 144, 32, 0);

        Test("Portrait selection only changes verified UU landscape displays", () =>
        {
            var modes = new[] { new DisplayMode(1206, 2622, 144, 32, 0) };
            var physical = uu with { AdapterName = "Physical Display Adapter", AdapterDeviceId = @"PCI\DISPLAY" };
            var spoofed = uu with { AdapterName = "GameViewer Virtual Display Adapter", AdapterDeviceId = @"Root\OtherDriver" };
            Require(!PortraitModePolicy.Select(physical, current, modes).ShouldApply, "Physical display was selected.");
            Require(!PortraitModePolicy.Select(spoofed, current, modes).ShouldApply, "Name alone accepted as UU identity.");
            Require(!PortraitModePolicy.Select(uu, current with { Width = 0 }, modes).ShouldApply, "Invalid current mode was accepted.");
        });

        Test("Portrait and square displays retain their current mode", () =>
        {
            var portrait = current with { Width = 1206, Height = 2622, Orientation = 1 };
            var decision = PortraitModePolicy.Select(uu, portrait, []);
            Require(decision.Kind == PortraitModeSelectionKind.AlreadyPortrait && !decision.ShouldApply,
                "An existing portrait mode was changed.");
            Require(!PortraitModePolicy.Select(uu, current with { Height = 2622 }, []).ShouldApply,
                "Square mode was changed.");
        });

        Test("Native portrait selection preserves exact phone pixels and supported refresh", () =>
        {
            var native = new DisplayMode(1206, 2622, 144, 32, 1) { DisplayFlags = 2, FixedOutput = 1 };
            var modes = new[] { new DisplayMode(768, 1360, 144, 32, 0),
                new DisplayMode(1206, 2622, 60, 32, 0), native };
            var decision = PortraitModePolicy.Select(uu, current, modes);
            Require(decision.Kind == PortraitModeSelectionKind.Exact && decision.Mode == native,
                "Did not preserve the exact driver's pixel dimensions, refresh, orientation and flags.");
            Require(!decision.RequiresDriverValidation, "An enumerated exact mode was labeled synthesized.");
        });

        Test("Exact portrait pixels outrank unrelated resolutions with a higher refresh", () =>
        {
            var exact = new DisplayMode(1206, 2622, 60, 32, 0);
            var decision = PortraitModePolicy.Select(uu, current,
                [new DisplayMode(768, 1360, 144, 32, 0), exact]);
            Require(decision.Mode == exact && decision.Kind == PortraitModeSelectionKind.Exact,
                "Selected unrelated pixels instead of a supported exact portrait mode.");
        });

        Test("Portrait refresh fallback uses a reported rate and is independent of mode order", () =>
        {
            var modes = new[] { new DisplayMode(1206, 2622, 165, 32, 0),
                new DisplayMode(1206, 2622, 120, 32, 0), new DisplayMode(1206, 2622, 60, 32, 0) };
            var selected = PortraitModePolicy.Select(uu, current, modes);
            var reversed = PortraitModePolicy.Select(uu, current, modes.Reverse().ToArray());
            Require(selected.Mode?.Frequency == 120 && reversed.Mode == selected.Mode,
                "Refresh fallback invented a rate or depended on enumeration order.");
        });

        Test("Unlisted exact pixels require driver validation and retain a proven portrait orientation", () =>
        {
            var exemplar = new DisplayMode(768, 1360, 60, 32, 1) { DisplayFlags = 2, FixedOutput = 2 };
            var decision = PortraitModePolicy.Select(uu, current, [exemplar]);
            Require(decision.RequiresDriverValidation && decision.Kind == PortraitModeSelectionKind.RequiresDriverValidation,
                "A synthesized mode was presented as confirmed driver support.");
            Require(decision.Mode == exemplar with { Width = 1206, Height = 2622 },
                "Candidate guessed direction/refresh or reused unrelated pixel dimensions.");
        });

        Test("No portrait orientation evidence produces no automatic mode", () =>
        {
            var modes = new[] { current, new DisplayMode(1920, 1080, 60, 32, 0),
                new DisplayMode(1206, 2622, 144, 32, 99), new DisplayMode(1206, 2622, 144, 16, 0) };
            var decision = PortraitModePolicy.Select(uu, current, modes);
            Require(decision.Kind == PortraitModeSelectionKind.Unavailable && !decision.ShouldApply && decision.Mode is null,
                "Unsupported portrait orientation or invalid mode was invented.");
            Require(!PortraitModePolicy.Select(uu, current, []).ShouldApply, "An empty mode list guessed an orientation.");
        });

        Test("Explicit phone profile overrides an unrelated UU landscape default", () =>
        {
            var default4k = current with { Width = 3840, Height = 2160 };
            var phone = new DisplayMode(1206, 2622, 144, 32, 0);
            var decision = PortraitModePolicy.Select(uu, default4k,
                [new DisplayMode(2160, 3840, 144, 32, 0), phone], 1206, 2622);
            Require(decision.Mode == phone && decision.Kind == PortraitModeSelectionKind.Exact,
                "UU's generic 4K mode overrode the explicit phone profile.");
        });

        Test("Explicit profile corrects a different portrait size but keeps an already matching mode", () =>
        {
            var otherPortrait = current with { Width = 1080, Height = 1920 };
            var phone = new DisplayMode(1206, 2622, 144, 32, 0);
            Require(PortraitModePolicy.Select(uu, otherPortrait, [phone], 1206, 2622).Mode == phone,
                "An already-portrait but incorrect size bypassed the phone profile.");
            Require(!PortraitModePolicy.Select(uu, phone with { Frequency = 60 }, [phone], 1206, 2622).ShouldApply,
                "An already-matching phone profile unnecessarily changed refresh.");
        });

        Test("Incomplete or invalid explicit phone profiles never change a mode", () =>
        {
            var modes = new[] { new DisplayMode(1206, 2622, 144, 32, 0) };
            Require(!PortraitModePolicy.Select(uu, current, modes, 1206, null).ShouldApply,
                "An incomplete profile was silently accepted.");
            Require(!PortraitModePolicy.Select(uu, current, modes, 2622, 1206).ShouldApply,
                "A landscape profile was silently accepted as portrait.");
            Require(!PortraitModePolicy.Select(uu, current, modes, 0, 2622).ShouldApply,
                "A zero-width profile was silently accepted.");
        });
    }
}
