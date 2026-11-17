using System.Reflection;
using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    // Pure journal preparation/read-back and policy composition. No native Apply,
    // RestoreAll, MainWindow or actual controller interaction is invoked here.
    private static void TestFittedDisplayRecovery()
    {
        var phone = DeviceProfileCatalog.FindByControllerName("iPhone 17 Pro")!;
        var tablet = DeviceProfileCatalog.FindByControllerName("iPad Pro (11 英寸, 第 3 代)")!;
        var preferences = new ViewportPreferences();
        var uu = ViewportDisplay(new(-3840, 0, 3840, 2160));
        var desktop = new DisplayMode(3840, 2160, 144, 32, 0);
        var portrait = new DisplayMode(1206, 2076, 144, 32, 1);
        var landscape = new DisplayMode(2238, 1206, 144, 32, 0);
        var original = new DisplaySnapshot(uu.DeviceName, desktop)
            { Identity = "fitted-display-test-identity", PositionX = -3840, PositionY = 20 };

        Test("Fitted application persists its finite frame family before an unconfirmed mode write", () =>
        {
            WithIsolatedStore(store =>
            {
                var empty = new RecoveryState();
                var prepared = PrepareDeviceForTest(empty, uu, original, portrait, phone, preferences);
                Require(prepared.Display == original && prepared.PendingDisplayMode == portrait && prepared.AppliedDisplayMode is null,
                    "Initial fitting replaced the original mode or prematurely confirmed the write.");
                Require(prepared.PhoneDisplayOwner is { AllowedSizes.Count: 4 } && prepared.PhoneDisplayOwner.Contains(landscape),
                    "The independent fitted landscape was absent from durable ownership.");
                Require(empty.Display is null && empty.PhoneDisplayOwner is null, "Preparation mutated the input journal.");
                store.Save(prepared);
                var loaded = store.Load();
                Require(loaded.Display == original && loaded.PendingDisplayMode == portrait && loaded.PhoneDisplayOwner!.Contains(landscape),
                    "Disk round-trip lost the first snapshot or fitted ownership family.");
            });
        });

        Test("Six fitted rotations retain original desktop and window snapshots across disk round-trips", () =>
        {
            WithIsolatedStore(store =>
            {
                var window = new WindowSnapshot { Handle = 42, ProcessName = "pure-original-window", NormalBounds = new(40, 50, 800, 600) };
                var state = PrepareDeviceForTest(new(), uu, original, portrait, phone, preferences)
                    with { Windows = [window], PendingDisplayMode = null, AppliedDisplayMode = portrait };
                var current = portrait;
                for (int i = 0; i < 6; i++)
                {
                    var target = i % 2 == 0 ? landscape : portrait;
                    var prepared = PrepareRotationForTest(state, uu, original with { Mode = current }, target, phone, preferences);
                    Require(prepared.Display == original && prepared.Windows.Single() == window,
                        "Repeated fitting overwrote the original display or window.");
                    Require(prepared.PendingDisplayMode == target && prepared.AppliedDisplayMode == current,
                        "An in-flight rotation discarded the previous confirmed mode.");
                    store.Save(prepared);
                    state = store.Load();
                    Require(ShouldRestoreForTest(state, original with { Mode = target }), "Reloaded pending target lost restoration authority.");
                    state = state with { PendingDisplayMode = null, AppliedDisplayMode = target };
                    current = target;
                }
                Require(state.Display == original && state.PhoneDisplayOwner!.AllowedSizes.Count == 4,
                    "The final journal accumulated intermediate snapshots or arbitrary sizes.");
            });
        });

        Test("Fitted pending failures restore owned frames while preserving new PC or tablet pixels", () =>
        {
            var pending = PrepareDeviceForTest(new(), uu, original, portrait, phone, preferences);
            Require(!ShouldRestoreForTest(pending, original), "Untouched original would receive an unnecessary mode write.");
            foreach (var owned in new[] { portrait, landscape, portrait with { Height = 2622 }, landscape with { Width = 2622 } })
                Require(ShouldRestoreForTest(pending, original with { Mode = owned }), "A fitted/native family frame became unrestorable.");
            foreach (var external in new[]
            {
                desktop with { Width = 1920, Height = 1080 },
                portrait with { Width = 1668, Height = 2024 },
                landscape with { Width = 2132, Height = 1668 },
                portrait with { Width = 2076, Height = 1206 },
                landscape with { Width = 1206, Height = 2238 }
            })
                Require(!ShouldRestoreForTest(pending, original with { Mode = external }), "An external or blindly swapped frame was claimed by phone recovery.");
            ExpectThrows(() => ShouldRestoreForTest(pending, original with { Mode = portrait, Identity = "replacement-adapter" }),
                "Fitted family matching bypassed the display identity check.");
        });

        Test("Cross-profile changed-preference and blindly swapped rotations leave the old journal intact", () =>
        {
            var state = PrepareDeviceForTest(new(), uu, original, portrait, phone, preferences)
                with { PendingDisplayMode = null, AppliedDisplayMode = portrait };
            var before = System.Text.Json.JsonSerializer.Serialize(state);
            ExpectThrows(() => PrepareRotationForTest(state, uu, original with { Mode = portrait },
                portrait with { Width = 2076, Height = 1206, Orientation = 0 }, phone, preferences), "A blind swap entered the fitted family.");
            ExpectThrows(() => PrepareRotationForTest(state, uu, original with { Mode = portrait },
                new(2622, 822, 144, 32, 0), phone, preferences with { Landscape = LandscapeToolbarPlacement.Bottom }),
                "A changed preference silently replaced a live ownership family.");
            ExpectThrows(() => PrepareRotationForTest(state, uu, original with { Mode = new(1668, 2024, 144, 32, 1) },
                new(2132, 1668, 144, 32, 0), tablet, preferences), "A new tablet reused the phone journal on the same adapter.");
            Require(System.Text.Json.JsonSerializer.Serialize(state) == before, "Rejected rotation mutated recovery evidence.");
        });

        Test("Corrupted fitted ownership cannot expand recovery authority to arbitrary desktop sizes", () =>
        {
            WithIsolatedStore(store =>
            {
                var valid = PrepareDeviceForTest(new(), uu, original, portrait, phone, preferences);
                var owner = valid.PhoneDisplayOwner!;
                foreach (var damaged in new[]
                {
                    owner with { AllowedSizes = [.. owner.AllowedSizes, new(3840, 2160)] },
                    owner with { AllowedSizes = owner.AllowedSizes.Skip(1).ToList() },
                    owner with { AllowedSizes = [owner.AllowedSizes[0], owner.AllowedSizes[0], owner.AllowedSizes[2], owner.AllowedSizes[3]] },
                    owner with { ProfileKey = "1668x2388@834" },
                    owner with { Preferences = preferences with { Landscape = LandscapeToolbarPlacement.Bottom } }
                })
                {
                    store.Save(valid with { PhoneDisplayOwner = damaged });
                    var before = System.IO.File.ReadAllText(store.PathName);
                    ExpectThrows(() => store.Load(), "A corrupted allowed-size family was trusted.");
                    Require(System.IO.File.ReadAllText(store.PathName) == before, "Rejected family journal was rewritten.");
                }
            });
        });

        Test("Fitted ownership rejects orphan journals and markers from another device", () =>
        {
            WithIsolatedStore(store =>
            {
                var valid = PrepareDeviceForTest(new(), uu, original, portrait, phone, preferences);
                foreach (var invalid in new[]
                {
                    valid with { Display = null },
                    valid with { AutomaticPhoneMode = null, PendingDisplayMode = null, AppliedDisplayMode = null },
                    valid with { AppliedDisplayMode = new(2132, 1668, 144, 32, 0) },
                    valid with { PendingDisplayMode = desktop }
                })
                {
                    store.Save(invalid);
                    ExpectThrows(() => store.Load(), "Orphaned or cross-device fitted ownership was accepted.");
                }
            });
        });

        Test("Disconnect recovery restores fitted frames but controller handoff preserves its new mode", () =>
        {
            var state = PrepareDeviceForTest(new(), uu, original, portrait, phone, preferences);
            var now = new DateTimeOffset(2026, 11, 17, 6, 0, 0, TimeSpan.Zero);
            var none = new UuControllerSnapshot(UuControllerStatus.None, [], now);
            var pc = new UuControllerSnapshot(UuControllerStatus.Available, ["Desktop PC"], now);
            Require(!ControllerHandoverPolicy.ShouldPreserveCurrentDisplay(none, now) &&
                ShouldRestoreForTest(state, original with { Mode = portrait }),
                "A fresh disconnect would drop the fitted display restore target.");
            Require(ControllerHandoverPolicy.ShouldPreserveCurrentDisplay(pc, now),
                "A reliable new controller did not authorize preserving its pixels.");
            var unknown = new UuControllerSnapshot(UuControllerStatus.Unknown, [], now);
            Require(!ControllerHandoverPolicy.ShouldPreserveCurrentDisplay(unknown, now) &&
                !ShouldRestoreForTest(state, original with { Mode = desktop with { Width = 1920, Height = 1080 } }),
                "Unavailable controller evidence let normal recovery overwrite an external PC mode.");
        });

        Test("Optional geometry parameters preserve legacy native-only journal behavior", () =>
        {
            var nativePortrait = portrait with { Height = 2622 };
            var nativeLandscape = landscape with { Width = 2622 };
            var method = typeof(WorkspaceSession).GetMethod("PrepareDeviceApply", BindingFlags.NonPublic | BindingFlags.Static)!;
            var legacy = (RecoveryState)method.Invoke(null, [new RecoveryState(), uu, original, nativePortrait, phone, Type.Missing, Type.Missing])!;
            Require(legacy.PhoneDisplayOwner is null && ShouldRestoreForTest(legacy, original with { Mode = nativeLandscape }),
                "An omitted optional preference changed legacy pair ownership.");
            Require(!ShouldRestoreForTest(legacy, original with { Mode = portrait }), "Legacy ownership silently adopted a fitted frame.");
            var rotated = PrepareRotationForTest(legacy, uu, original with { Mode = nativePortrait }, nativeLandscape, phone);
            Require(rotated.PhoneDisplayOwner is null && rotated.Display == original, "Explicit null preferences changed legacy restoration.");
        });
    }
}
