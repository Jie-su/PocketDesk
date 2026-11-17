using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestPhoneSessionPolicy()
    {
        var uu = new DisplayInfo(@"\\.\DISPLAY9", "UU", new(0, 0, 2622, 1206), new(0, 0, 2622, 1134), 1.5, true)
        { AdapterName = "GameViewer Virtual Display Adapter", AdapterDeviceId = @"Root\GameViewerIddDriver" };
        var profile = new PhoneProfile { Name = "iPhone 17 Pro", PortraitWidth = 1206, PortraitHeight = 2622 };
        var landscape = new DisplayMode(2622, 1206, 144, 32, 0);
        var portrait = landscape with { Width = 1206, Height = 2622 };

        Test("Automatic phone workspace accepts exactly the configured portrait and landscape pair", () =>
        {
            Require(PhoneSessionPolicy.CanAutoEnter(uu, landscape, profile), "Matching landscape was rejected.");
            Require(PhoneSessionPolicy.CanAutoEnter(uu, portrait, profile), "Matching portrait was rejected.");
        });

        Test("Automatic phone workspace rejects desktop resolutions and other portrait sizes", () =>
        {
            foreach (var size in new[] { (3840, 2160), (1920, 1080), (2560, 1440), (2160, 3840), (1080, 1920), (768, 1360) })
                Require(!PhoneSessionPolicy.CanAutoEnter(uu, landscape with { Width = size.Item1, Height = size.Item2 }, profile),
                    $"Unmatched size {size.Item1}x{size.Item2} was accepted.");
        });

        Test("A UU adapter alone or a matching size on a physical adapter cannot trigger phone mode", () =>
        {
            Require(!PhoneSessionPolicy.CanAutoEnter(uu with { AdapterDeviceId = @"PCI\PHYSICAL" }, portrait, profile),
                "A physical adapter was accepted based on dimensions.");
            Require(!PhoneSessionPolicy.CanAutoEnter(uu with { AdapterName = "Other virtual adapter" }, portrait, profile),
                "An unverified adapter name was accepted.");
            Require(!PhoneSessionPolicy.CanAutoEnter(null, portrait, profile), "No display was accepted.");
            Require(!PhoneSessionPolicy.CanAutoEnter(uu, landscape with { Width = 3840, Height = 2160 }, profile),
                "UU adapter identity alone bypassed the dimension gate.");
        });

        Test("Automatic phone workspace requires a complete valid configured profile", () =>
        {
            Require(!PhoneSessionPolicy.CanAutoEnter(uu, portrait, null), "Missing profile was accepted.");
            Require(!PhoneSessionPolicy.CanAutoEnter(uu, portrait, new PhoneProfile()), "Unconfigured profile was accepted.");
            Require(!PhoneSessionPolicy.CanAutoEnter(uu, portrait, profile with { PortraitWidth = 0 }), "Incomplete profile was accepted.");
            Require(!PhoneSessionPolicy.CanAutoEnter(uu, portrait, profile with { PortraitWidth = 2622, PortraitHeight = 1206 }),
                "Invalid landscape profile was accepted.");
            Require(!PhoneSessionPolicy.CanAutoEnter(uu, new DisplayMode(20000, 40000, 60, 32, 0),
                profile with { PortraitWidth = 20000, PortraitHeight = 40000 }), "Out-of-range profile was accepted.");
        });

        Test("Phone matching uses physical pixels rather than DPI-scaled dimensions", () =>
        {
            Require(!PhoneSessionPolicy.CanAutoEnter(uu, portrait with { Width = 804, Height = 1748 }, profile),
                "Logical dimensions were treated as native phone pixels.");
            Require(PhoneSessionPolicy.CanAutoEnter(uu with { Scale = 2 }, portrait, profile),
                "Desktop DPI scaling incorrectly changed the physical dimension match.");
        });

        Test("Phone dimension gate preserves refresh flexibility and the manual portrait preference", () =>
        {
            Require(PhoneSessionPolicy.CanAutoEnter(uu, portrait with { Frequency = 60, Orientation = 1 }, profile),
                "Supported refresh/orientation changes broke a pixel match.");
            Require(PhoneSessionPolicy.CanAutoEnter(uu, landscape, profile with { PortraitOnConnect = false }),
                "Disabling automatic portrait unexpectedly disabled a correctly sized mobile workspace.");
        });

        Test("Changing the phone profile changes the automatic dimension gate immediately", () =>
        {
            var other = profile with { Name = "Other phone", PortraitWidth = 1440, PortraitHeight = 3200 };
            Require(!PhoneSessionPolicy.CanAutoEnter(uu, portrait, other), "Old phone dimensions remained accepted.");
            Require(PhoneSessionPolicy.CanAutoEnter(uu, portrait with { Width = 1440, Height = 3200 }, other),
                "Newly configured phone dimensions were ignored.");
        });

        Test("Unreadable or invalid reported modes never trigger an automatic phone workspace", () =>
        {
            Require(!PhoneSessionPolicy.CanAutoEnter(uu, null, profile), "Unreadable mode was accepted.");
            Require(!PhoneSessionPolicy.CanAutoEnter(uu, portrait with { Frequency = -1 }, profile), "Invalid refresh was accepted.");
            Require(!PhoneSessionPolicy.CanAutoEnter(uu, portrait with { Orientation = 9 }, profile), "Invalid orientation was accepted.");
        });
    }
}
