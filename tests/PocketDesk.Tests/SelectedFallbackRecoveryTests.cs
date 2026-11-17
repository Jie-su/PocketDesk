using System.Reflection;
using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestSelectedFallbackRecovery()
    {
        var phone = DeviceProfileCatalog.FindByControllerName("iPhone 17 Pro")!;
        var preferences = new ViewportPreferences();
        var uu = ViewportDisplay(new(0, 0, 1206, 2622));
        var portrait = new DisplayMode(1200, 1920, 144, 32, 1);
        var landscape = new DisplayMode(1920, 1200, 144, 32, 0);
        var original = new DisplaySnapshot(uu.DeviceName, new(3840, 2160, 144, 32, 0))
            { Identity = "selected-fallback-test-identity", PositionX = -3840, PositionY = 40 };

        Test("A fallback is journaled only when the complete requested driver mode is explicitly selected", () =>
        {
            ExpectThrows(() => PrepareDeviceForTest(new(), uu, original, portrait, phone, preferences),
                "A standard resolution became authorized without explicit fallback selection.");
            ExpectThrows(() => PrepareDeviceForTest(new(), uu, original, portrait, phone, preferences, portrait with { Frequency = 60 }),
                "A different selected refresh mode authorized the actual request.");
            ExpectThrows(() => PrepareDeviceForTest(new(), uu, original, portrait, phone, null, portrait),
                "Fallback selection bypassed the required safe-frame preferences.");
            var state = PrepareDeviceForTest(new(), uu, original, portrait, phone, preferences, portrait);
            Require(state.Display == original && state.PendingDisplayMode == portrait && state.AppliedDisplayMode is null,
                "Fallback preparation lost the original or confirmed before application.");
            Require(state.PhoneDisplayOwner!.SelectedFallbackSizes.SequenceEqual([new PhonePixelSize(1200, 1920)]) &&
                !state.PhoneDisplayOwner.AllowedSizes.Contains(new(1200, 1920)),
                "Selected fallback was not separately scoped to this session.");
        });

        Test("Unknown continuation requires a matching persisted selected-frame session", () =>
        {
            var state = PrepareDeviceForTest(new(), uu, original, portrait, phone, preferences, portrait);
            var snapshot = original with { Mode = portrait };
            Require(OwnsPhoneSnapshotForTest(state, uu, snapshot, phone, preferences), "Selected frame was not owned by its matching journal.");
            Require(!OwnsPhoneSnapshotForTest(new(), uu, snapshot, phone, preferences), "An empty session claimed a standard desktop resolution.");
            var nativeOnly = PrepareDeviceForTest(new(), uu, original, new(1206, 2076, 144, 32, 1), phone, preferences);
            Require(!OwnsPhoneSnapshotForTest(nativeOnly, uu, snapshot, phone, preferences), "An unselected standard mode was treated as owned.");
            Require(!OwnsPhoneSnapshotForTest(state, uu, snapshot with { Identity = "reused-adapter" }, phone, preferences),
                "A replaced adapter inherited ownership of common pixels.");
            Require(!OwnsPhoneSnapshotForTest(state, uu, snapshot, phone,
                preferences with { Landscape = LandscapeToolbarPlacement.Bottom }), "Changed preferences reused the old selected frame.");
            var now = new DateTimeOffset(2026, 11, 17, 7, 0, 0, TimeSpan.Zero);
            Require(DeviceSessionPolicy.Resolve(new(UuControllerStatus.Unknown, [], now), uu, portrait, phone, now).Profile is null,
                "The global Unknown fallback adopted a session-only standard resolution.");
        });

        Test("Unowned standard-size rotation is rejected while an explicitly owned session can rotate", () =>
        {
            var current = original with { Mode = portrait };
            ExpectThrows(() => PrepareRotationForTest(new(), uu, current, landscape, phone, preferences, landscape),
                "A standard PC-size current mode was rotated without a prior device session.");
            var owned = PrepareDeviceForTest(new(), uu, original, portrait, phone, preferences, portrait)
                with { PendingDisplayMode = null, AppliedDisplayMode = portrait };
            var rotated = PrepareRotationForTest(owned, uu, current, landscape, phone, preferences, landscape);
            Require(rotated.PhoneDisplayOwner!.SelectedFallbackSizes.Count == 2 && rotated.Display == original,
                "Owned rotation did not retain both independently selected directions and the first snapshot.");
            ExpectThrows(() => PrepareRotationForTest(owned, uu, current, portrait, phone, preferences, portrait),
                "A same-direction fallback was accepted as a rotation.");
        });

        Test("Repeated fallback rotations preserve original snapshots and only two selected sizes on disk", () =>
        {
            WithIsolatedStore(store =>
            {
                var window = new WindowSnapshot { Handle = 43, ProcessName = "fallback-original", NormalBounds = new(20, 30, 800, 600) };
                var state = PrepareDeviceForTest(new(), uu, original, portrait, phone, preferences, portrait)
                    with { Windows = [window], PendingDisplayMode = null, AppliedDisplayMode = portrait };
                var current = portrait;
                for (int i = 0; i < 6; i++)
                {
                    var target = i % 2 == 0 ? landscape : portrait;
                    state = PrepareRotationForTest(state, uu, original with { Mode = current }, target, phone, preferences, target);
                    Require(state.PendingDisplayMode == target && state.AppliedDisplayMode == current, "Pending write erased the last confirmed mode.");
                    store.Save(state);
                    state = store.Load();
                    Require(state.Display == original && state.Windows.Single() == window && state.PhoneDisplayOwner!.SelectedFallbackSizes.Count == 2,
                        "Rotation or serialization replaced first snapshots or accumulated fallback modes.");
                    state = state with { PendingDisplayMode = null, AppliedDisplayMode = target };
                    current = target;
                }
            });
        });

        Test("Fallback recovery recognizes selected targets but preserves unselected safe modes and PC takeover", () =>
        {
            var state = PrepareDeviceForTest(new(), uu, original, portrait, phone, preferences, portrait);
            state = PrepareRotationForTest(state, uu, original with { Mode = portrait }, landscape, phone, preferences, landscape);
            Require(!ShouldRestoreForTest(state, original), "An unchanged original desktop would be unnecessarily restored.");
            Require(ShouldRestoreForTest(state, original with { Mode = portrait }) && ShouldRestoreForTest(state, original with { Mode = landscape }),
                "Applied or pending selected fallback lost restoration authority.");
            foreach (var external in new[]
            {
                new DisplayMode(1080, 1800, 144, 32, 1), new DisplayMode(1920, 1080, 144, 32, 0),
                new DisplayMode(3840, 2160, 60, 32, 0), new DisplayMode(1668, 2024, 144, 32, 1)
            })
                Require(!ShouldRestoreForTest(state, original with { Mode = external }),
                    "An unselected safe mode, PC or tablet was overwritten by fallback recovery.");
        });

        Test("Persisted fallback lists reject unsafe oversized duplicate or base-family injections", () =>
        {
            WithIsolatedStore(store =>
            {
                var valid = PrepareDeviceForTest(new(), uu, original, portrait, phone, preferences, portrait);
                foreach (var sizes in new List<PhonePixelSize>[]
                {
                    [new(1080, 1920)], [new(3840, 2160)], [new(1200, 1920), new(1080, 1800)],
                    [new(1200, 1920), new(1200, 1920)], [new(1206, 2076)], [new(1200, 1920), new(1920, 1080), new(1280, 720)]
                })
                {
                    store.Save(valid with { PhoneDisplayOwner = valid.PhoneDisplayOwner! with { SelectedFallbackSizes = sizes } });
                    var before = System.IO.File.ReadAllText(store.PathName);
                    ExpectThrows(() => store.Load(), "A malformed selected-frame list was accepted.");
                    Require(System.IO.File.ReadAllText(store.PathName) == before, "Rejected fallback evidence was rewritten.");
                }
            });
        });

        Test("A live selected family cannot silently choose a second fallback for the same direction", () =>
        {
            var state = PrepareDeviceForTest(new(), uu, original, portrait, phone, preferences, portrait);
            state = PrepareRotationForTest(state, uu, original with { Mode = portrait }, landscape, phone, preferences, landscape);
            var before = System.Text.Json.JsonSerializer.Serialize(state);
            var differentPortrait = new DisplayMode(1080, 1800, 144, 32, 1);
            ExpectThrows(() => PrepareRotationForTest(state, uu, original with { Mode = landscape }, differentPortrait,
                phone, preferences, differentPortrait), "The active family accumulated another same-direction desktop resolution.");
            Require(System.Text.Json.JsonSerializer.Serialize(state) == before, "Rejected reselection changed the recovery snapshot.");
            Require(!PhoneScreenGeometry.IsSafeSupportedFrame(portrait, phone, preferences with { Enabled = false }) &&
                !PhoneScreenGeometry.IsSafeSupportedFrame(landscape, phone, preferences with { Landscape = LandscapeToolbarPlacement.Hidden }),
                "Disabled or hidden fitting still authorized arbitrary smaller standard modes.");
        });

        Test("Owned phone and tablet fallback sessions can rotate to exact opposite native dimensions", () =>
        {
            var tablet = DeviceProfileCatalog.FindByControllerName("iPad Pro (11 英寸, 第 3 代)")!;
            foreach (var scenario in new[]
            {
                (Profile: phone, Current: portrait, Target: new DisplayMode(2622, 1206, 144, 32, 0)),
                (Profile: tablet, Current: new DisplayMode(1280, 1024, 60, 32, 0), Target: new DisplayMode(1668, 2388, 60, 32, 1))
            })
            {
                var owned = PrepareDeviceForTest(new(), uu, original, scenario.Current, scenario.Profile, preferences, scenario.Current)
                    with { PendingDisplayMode = null, AppliedDisplayMode = scenario.Current };
                var oldSelections = owned.PhoneDisplayOwner!.SelectedFallbackSizes.ToArray();
                var rotated = PrepareRotationForTest(owned, uu, original with { Mode = scenario.Current }, scenario.Target,
                    scenario.Profile, preferences);
                Require(rotated.Display == original && rotated.PendingDisplayMode == scenario.Target &&
                    rotated.AppliedDisplayMode == scenario.Current, "Native-direction downgrade replaced the first snapshot or confirmed marker.");
                Require(rotated.PhoneDisplayOwner!.SelectedFallbackSizes.SequenceEqual(oldSelections) &&
                    rotated.PhoneDisplayOwner.AllowedSizes.Contains(new(scenario.Target.Width, scenario.Target.Height)),
                    "An exact native target unnecessarily expanded the fallback ownership list.");
                Require(ShouldRestoreForTest(rotated, original with { Mode = scenario.Target }),
                    "Opposite native downgrade lost the original recovery target.");
            }
        });

        Test("Native rotation downgrade rejects the same orientation and another device profile", () =>
        {
            var tablet = DeviceProfileCatalog.FindByControllerName("iPad Pro (11 英寸, 第 3 代)")!;
            foreach (var scenario in new[]
            {
                (Profile: phone, Current: portrait, Same: new DisplayMode(1206, 2622, 144, 32, 1), Other: new DisplayMode(2388, 1668, 144, 32, 0)),
                (Profile: tablet, Current: new DisplayMode(1280, 1024, 60, 32, 0), Same: new DisplayMode(2388, 1668, 60, 32, 0), Other: new DisplayMode(1206, 2622, 60, 32, 1))
            })
            {
                var owned = PrepareDeviceForTest(new(), uu, original, scenario.Current, scenario.Profile, preferences, scenario.Current);
                var before = System.Text.Json.JsonSerializer.Serialize(owned);
                ExpectThrows(() => PrepareRotationForTest(owned, uu, original with { Mode = scenario.Current }, scenario.Same,
                    scenario.Profile, preferences), "Same-orientation native mode was accepted as a rotation downgrade.");
                ExpectThrows(() => PrepareRotationForTest(owned, uu, original with { Mode = scenario.Current }, scenario.Other,
                    scenario.Profile, preferences), "Another profile's native dimensions were accepted as a downgrade.");
                Require(System.Text.Json.JsonSerializer.Serialize(owned) == before, "Rejected native downgrade rewrote the old journal.");
            }
        });
    }

    private static bool OwnsPhoneSnapshotForTest(RecoveryState state, DisplayInfo display,
        DisplaySnapshot current, PhoneProfile profile, ViewportPreferences preferences) =>
        (bool)(typeof(WorkspaceSession).GetMethod("OwnsPhoneModeSnapshot", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(WorkspaceSession), "OwnsPhoneModeSnapshot"))
            .Invoke(null, [state, display, current, profile, preferences])!;
}
