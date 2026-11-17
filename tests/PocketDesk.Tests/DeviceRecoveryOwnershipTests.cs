using System.Reflection;
using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestDeviceRecoveryOwnership()
    {
        var uu = new DisplayInfo(@"\\.\DEVICE_OWNERSHIP_TEST", "UU", new(0, 0, 3840, 2160), new(0, 0, 3840, 2088), 1.5, true)
        { AdapterName = "GameViewer Virtual Display Adapter", AdapterDeviceId = @"Root\GameViewerIddDriver" };
        var desktopMode = new DisplayMode(3840, 2160, 144, 32, 0);
        var phoneMode = new DisplayMode(1206, 2622, 144, 32, 1);
        var tabletMode = new DisplayMode(2388, 1668, 144, 32, 0);
        var original = new DisplaySnapshot(uu.DeviceName, desktopMode) { Identity = "same-reused-uu-adapter", PositionX = -3840, PositionY = 40 };
        var phone = DeviceProfileCatalog.FindByControllerName("iPhone 17 Pro")!;
        var tablet = DeviceProfileCatalog.FindByControllerName("iPad Pro (11 英寸, 第 3 代)")!;

        Test("Device-profile application journals pending ownership before any native mutation", () =>
        {
            var empty = new RecoveryState();
            var prepared = PrepareDeviceForTest(empty, uu, original, tabletMode, tablet);
            Require(prepared.Display == original && prepared.PendingDisplayMode == tabletMode && prepared.AppliedDisplayMode is null,
                "First device application lost the original display or marked an unconfirmed mode as applied.");
            Require(prepared.AutomaticPhoneMode == tabletMode && !ReferenceEquals(prepared.Windows, empty.Windows),
                "Prepared device ownership or isolated window list was missing.");
            Require(empty.Display is null && empty.PendingDisplayMode is null && empty.AutomaticPhoneMode is null,
                "Preparation modified the input state before persistence.");
        });

        Test("A new device profile cannot absorb any unresolved old display or window journal", () =>
        {
            var staleStates = new[]
            {
                new RecoveryState { Display = original, AppliedDisplayMode = phoneMode },
                new RecoveryState { Windows = [new WindowSnapshot { Handle = 0, ProcessName = "unresolved-old-window" }] },
                new RecoveryState { PendingDisplayMode = phoneMode }
            };
            foreach (var stale in staleStates)
                ExpectThrows(() => PrepareDeviceForTest(stale, uu, original, tabletMode, tablet),
                    "New device profile incorporated unresolved old recovery records.");
        });

        Test("Rotating a new profile cannot replace the old profile's ownership marker", () =>
        {
            var tabletCurrent = original with { Mode = tabletMode };
            var tabletPortrait = tabletMode with { Width = 1668, Height = 2388, Orientation = 1 };
            foreach (var stale in new[]
            {
                new RecoveryState { Display = original, AutomaticPhoneMode = phoneMode },
                new RecoveryState { Display = original, AppliedDisplayMode = phoneMode },
                new RecoveryState { Display = original, PendingDisplayMode = phoneMode }
            })
            {
                var automatic = stale.AutomaticPhoneMode;
                var applied = stale.AppliedDisplayMode;
                var pending = stale.PendingDisplayMode;
                ExpectThrows(() => PrepareRotationForTest(stale, uu, tabletCurrent, tabletPortrait, tablet),
                    "Same-adapter tablet rotation overwrote the old phone ownership pair.");
                Require(stale.Display == original && stale.AutomaticPhoneMode == automatic && stale.AppliedDisplayMode == applied && stale.PendingDisplayMode == pending,
                    "Rejected cross-profile rotation altered the old journal.");
            }
        });

        Test("Same-profile rotations preserve a generic pre-adaptation original snapshot", () =>
        {
            var owned = PrepareDeviceForTest(new RecoveryState(), uu, original, phoneMode, phone);
            owned = owned with { PendingDisplayMode = null, AppliedDisplayMode = phoneMode };
            var landscape = phoneMode with { Width = 2622, Height = 1206, Orientation = 0 };
            var rotated = PrepareRotationForTest(owned, uu, original with { Mode = phoneMode }, landscape, phone);
            Require(rotated.Display == original && rotated.PendingDisplayMode == landscape && rotated.AppliedDisplayMode == phoneMode,
                "Rotation replaced the original 4K snapshot or discarded confirmed ownership.");
        });

        Test("Pending application failure distinguishes untouched original owned target and external takeover", () =>
        {
            var pending = new RecoveryState { Display = original, PendingDisplayMode = phoneMode };
            Require(!ShouldRestoreForTest(pending, original), "Unchanged original mode would receive an unnecessary restore write.");
            Require(ShouldRestoreForTest(pending, original with { Mode = phoneMode }),
                "Applied-but-unconfirmed phone target was no longer restorable.");
            Require(!ShouldRestoreForTest(pending, original with { Mode = tabletMode }),
                "Pending phone application failure would overwrite a later tablet mode.");
            Require(!ShouldRestoreForTest(pending, original with { Mode = desktopMode with { Width = 1920, Height = 1080 } }),
                "Pending phone application failure would overwrite a later PC mode.");
        });

        Test("Confirmed ownership permits both phone directions but not a new tablet pair", () =>
        {
            var owned = new RecoveryState { Display = original, AppliedDisplayMode = phoneMode };
            Require(ShouldRestoreForTest(owned, original with { Mode = phoneMode }), "Confirmed portrait mode was not restorable.");
            Require(ShouldRestoreForTest(owned, original with { Mode = phoneMode with { Width = 2622, Height = 1206, Orientation = 0 } }),
                "Confirmed phone ownership was lost after a width-height swap.");
            Require(!ShouldRestoreForTest(owned, original with { Mode = tabletMode }), "Confirmed phone owner claimed tablet pixels.");
        });

        Test("Unknown legacy ownership and changed display identity do not authorize a restore write", () =>
        {
            var legacy = new RecoveryState { Display = original };
            Require(!ShouldRestoreForTest(legacy, original), "Already-original legacy mode was unnecessarily written.");
            ExpectThrows(() => ShouldRestoreForTest(legacy, original with { Mode = tabletMode }),
                "Legacy journal without ownership claimed a new device mode.");
            var owned = legacy with { AppliedDisplayMode = phoneMode };
            ExpectThrows(() => ShouldRestoreForTest(owned, original with { Mode = phoneMode, Identity = "another-adapter" }),
                "Replaced adapter identity was accepted for restore.");
            ExpectThrows(() => ShouldRestoreForTest(owned, original with { Mode = phoneMode, DeviceName = @"\\.\ANOTHER" }),
                "Different device name was accepted for restore.");
        });

        Test("Ownership journals reject orphaned mixed-profile and invalid mode markers", () =>
        {
            WithIsolatedStore(store =>
            {
                foreach (var state in new[]
                {
                    new RecoveryState { PendingDisplayMode = phoneMode },
                    new RecoveryState { Display = original, AppliedDisplayMode = phoneMode, PendingDisplayMode = tabletMode },
                    new RecoveryState { Display = original, AutomaticPhoneMode = tabletMode, PendingDisplayMode = phoneMode },
                    new RecoveryState { Display = original, PendingDisplayMode = phoneMode with { Orientation = 9 } }
                })
                {
                    store.Save(state);
                    var before = System.IO.File.ReadAllText(store.PathName);
                    ExpectThrows(() => store.Load(), "Invalid or mixed-profile ownership journal was accepted.");
                    Require(System.IO.File.ReadAllText(store.PathName) == before, "Rejected journal evidence was overwritten.");
                }
                var valid = new RecoveryState { Display = original, AppliedDisplayMode = phoneMode,
                    PendingDisplayMode = phoneMode with { Width = 2622, Height = 1206, Orientation = 0 } };
                store.Save(valid);
                var loaded = store.Load();
                Require(loaded.PendingDisplayMode == valid.PendingDisplayMode && loaded.AppliedDisplayMode == phoneMode,
                    "Same-pair pending and confirmed ownership failed a disk round-trip.");
            });
        });

        Test("Pending manual-preview ownership preserves a later actual display mode without native writes", () =>
        {
            WithIsolatedStore(store =>
            {
                var attached = Displays.ListDisplays().First();
                var observed = Displays.Capture(attached.DeviceName);
                var fakeTarget = observed.Mode with { Width = observed.Mode.Width + 1, Height = observed.Mode.Height + 2 };
                // Invalid original is a no-write sentinel. If the ownership check
                // takes the wrong branch, native validation fails before mutation.
                store.Save(new RecoveryState { Display = observed with { Mode = observed.Mode with { Width = 0 } }, PendingDisplayMode = fakeTarget });
                var session = new WorkspaceSession(store);
                Require(session.RestoreAll().Count == 0 && !session.HasRecovery,
                    "Pending preview journal tried to overwrite external pixels or remained stuck.");
                var loaded = store.Load();
                Require(loaded.PendingDisplayMode is null && loaded.AppliedDisplayMode is null && loaded.AutomaticPhoneMode is null,
                    "Releasing external takeover left ownership markers behind.");
                Require(Displays.Capture(attached.DeviceName).Mode == observed.Mode, "Safety test changed a real display mode.");
            });
        });

        Test("Explicit controller handoff preserves current pixels even when they equal the old phone pair", () =>
        {
            WithIsolatedStore(store =>
            {
                var attached = Displays.ListDisplays().First();
                var observed = Displays.Capture(attached.DeviceName);
                store.Save(new RecoveryState { Display = observed with { Mode = observed.Mode with { Width = 0 } }, AppliedDisplayMode = observed.Mode });
                var session = new WorkspaceSession(store);
                Require(session.RestoreAll(preserveCurrentDisplay: true).Count == 0 && !session.HasRecovery,
                    "Controller handoff ignored preserve-current-display and tried to restore matching pixels.");
                Require(Displays.Capture(attached.DeviceName).Mode == observed.Mode, "Explicit handoff changed a real display mode.");
            });
        });
    }

    private static RecoveryState PrepareDeviceForTest(RecoveryState state, DisplayInfo display,
        DisplaySnapshot current, DisplayMode requested, PhoneProfile profile, ViewportPreferences? preferences = null,
        DisplayMode? selectedFallback = null) =>
        (RecoveryState)(typeof(WorkspaceSession).GetMethod("PrepareDeviceApply", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(WorkspaceSession), "PrepareDeviceApply"))
            .Invoke(null, [state, display, current, requested, profile, preferences, selectedFallback])!;

    private static bool ShouldRestoreForTest(RecoveryState state, DisplaySnapshot current) =>
        (bool)(typeof(WorkspaceSession).GetMethod("ShouldRestoreDisplay", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(WorkspaceSession), "ShouldRestoreDisplay"))
            .Invoke(null, [state, current])!;
}
