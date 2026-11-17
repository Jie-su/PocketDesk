using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestDeviceSessionPolicy()
    {
        var now = new DateTimeOffset(2026, 11, 17, 1, 0, 0, TimeSpan.Zero);
        var uu = new DisplayInfo(@"\\.\DISPLAY9", "UU", new(0, 0, 3840, 2160), new(0, 0, 3840, 2088), 1.5, true)
        { AdapterName = "GameViewer Virtual Display Adapter", AdapterDeviceId = @"Root\GameViewerIddDriver" };
        var desktop = new DisplayMode(3840, 2160, 144, 32, 0);
        var phone = new DisplayMode(1206, 2622, 144, 32, 1);
        var legacy = new PhoneProfile { Name = "Saved iPhone", PortraitWidth = 1206, PortraitHeight = 2622, LogicalWidth = 402, PortraitOnConnect = true };
        UuControllerSnapshot Snapshot(UuControllerStatus status, params string[] names) => new(status, names, now);
        DeviceSessionDecision Decide(UuControllerSnapshot? snapshot, DisplayMode? mode, PhoneProfile? saved = null) =>
            DeviceSessionPolicy.Resolve(snapshot, uu, mode, saved ?? legacy, now);

        Test("A single identified iPad selects native tablet pixels even while UU reports 4K", () =>
        {
            var decision = Decide(Snapshot(UuControllerStatus.Available, "iPad Pro (11 英寸, 第 3 代)"), desktop);
            Require(decision.Profile is { PortraitWidth: 1668, PortraitHeight: 2388, LogicalWidth: 834, PortraitOnConnect: false }
                && decision.IsControllerIdentified && decision.Source == DeviceSessionSource.KnownController,
                "Known iPad was gated on already-native pixels or inherited phone dimensions/scaling.");
        });

        Test("A single identified iPhone retains its independent native and logical dimensions", () =>
        {
            var decision = Decide(Snapshot(UuControllerStatus.Available, "iPhone 17 Pro"), desktop);
            Require(decision.Profile is { PortraitWidth: 1206, PortraitHeight: 2622, LogicalWidth: 402, PortraitOnConnect: true }
                && decision.IsControllerIdentified, "Known iPhone was confused with the tablet configuration.");
        });

        Test("A readable PC or unsupported controller cannot use phone-sized fallback", () =>
        {
            foreach (var name in new[] { "DESKTOP-USER-PC", "Windows PC", "Unknown iPad", "iPad Pro (11 英寸, 第 99 代)" })
                Require(Decide(Snapshot(UuControllerStatus.Available, name), phone).Profile is null,
                    $"Readable unsupported controller {name} inherited an exact-phone-pixel session.");
        });

        Test("Reliable absence or multiple controllers forbids pixel fallback", () =>
        {
            Require(Decide(Snapshot(UuControllerStatus.None), phone).Profile is null,
                "No controller inherited a phone session.");
            Require(Decide(Snapshot(UuControllerStatus.Ambiguous, "iPhone 17 Pro", "iPhone 17 Pro"), phone).Profile is null,
                "Two controllers were silently deduplicated into one known model.");
            Require(Decide(Snapshot(UuControllerStatus.Ambiguous, "iPhone 17 Pro", "DESKTOP-PC"), phone).Profile is null,
                "A known phone overrode a second PC controller.");
        });

        Test("Malformed available controller snapshots never become a single trusted identity", () =>
        {
            foreach (var names in new[] { Array.Empty<string>(), new[] { "" }, new[] { "iPhone 17 Pro", "iPhone 17 Pro" } })
                Require(Decide(Snapshot(UuControllerStatus.Available, names), phone).Profile is null,
                    "Malformed available status guessed a single controller.");
        });

        Test("Unknown UIA evidence permits only the saved exact pixel pair", () =>
        {
            var unknown = Snapshot(UuControllerStatus.Unknown);
            var fallback = Decide(unknown, phone);
            Require(fallback.Profile == legacy && !fallback.IsControllerIdentified && fallback.Source == DeviceSessionSource.DimensionFallback,
                "Saved native-pair fallback was lost or presented as controller recognition.");
            Require(Decide(unknown, phone with { Width = 2622, Height = 1206 }).Profile == legacy,
                "Saved native-pair fallback rejected a width/height swap.");
            Require(Decide(unknown, desktop).Profile is null, "Unknown evidence adapted generic 4K to a guessed device.");
            Require(Decide(unknown, phone with { Width = 2388, Height = 1668 }).Profile is null,
                "Unknown evidence guessed an unsaved iPad just from catalog dimensions.");
        });

        Test("Stale controller names cannot authorize adaptation from a generic desktop size", () =>
        {
            var fresh = Snapshot(UuControllerStatus.Available, "iPad Pro (11 英寸, 第 3 代)");
            var stale = fresh with { ObservedAt = now - TimeSpan.FromSeconds(5) - TimeSpan.FromMilliseconds(1) };
            Require(Decide(fresh with { ObservedAt = now - TimeSpan.FromSeconds(5) }, desktop).IsControllerIdentified,
                "Exactly five-second-old evidence expired before the specified boundary.");
            Require(Decide(stale, desktop).Profile is null, "Stale iPad name adapted a new generic desktop session.");
            var fallback = Decide(stale, phone);
            Require(fallback.Profile == legacy && !fallback.IsControllerIdentified,
                "Stale evidence was not reduced to the conservative saved-size fallback.");
            Require(Decide(fresh with { ObservedAt = now + TimeSpan.FromSeconds(1) }, desktop).Profile is null,
                "Future-dated evidence was treated as a fresh authoritative controller.");
        });

        Test("Identified controllers still require a real UU display and readable valid mode", () =>
        {
            var known = Snapshot(UuControllerStatus.Available, "iPhone 17 Pro");
            Require(DeviceSessionPolicy.Resolve(known, uu with { AdapterDeviceId = @"PCI\PHYSICAL" }, desktop, legacy, now).Profile is null,
                "Controller name authorized modifying a physical display.");
            Require(DeviceSessionPolicy.Resolve(known, null, desktop, legacy, now).Profile is null, "No display was accepted.");
            foreach (var mode in new DisplayMode?[] { null, desktop with { Width = 0 }, desktop with { BitsPerPixel = 16 }, desktop with { Orientation = 8 } })
                Require(Decide(known, mode).Profile is null, "Unreadable or invalid mode was accepted based on a controller name.");
        });

        Test("Saved preferences merge only into the same catalog native pixel pair", () =>
        {
            var savedPhone = legacy with { Name = "My phone", LogicalWidth = 420, PortraitOnConnect = false };
            var phoneDecision = Decide(Snapshot(UuControllerStatus.Available, "iPhone 17 Pro"), desktop, savedPhone);
            Require(phoneDecision.Profile is { PortraitWidth: 1206, PortraitHeight: 2622, LogicalWidth: 420, PortraitOnConnect: false }
                && phoneDecision.Profile.Name != savedPhone.Name, "Same-device preferences replaced catalog identity or were discarded.");
            var tabletDecision = Decide(Snapshot(UuControllerStatus.Available, "iPad Pro (11 英寸, 第 3 代)"), desktop, savedPhone);
            Require(tabletDecision.Profile is { PortraitWidth: 1668, PortraitHeight: 2388, LogicalWidth: 834, PortraitOnConnect: false },
                "Phone preferences leaked into a different tablet profile.");
        });
    }
}
