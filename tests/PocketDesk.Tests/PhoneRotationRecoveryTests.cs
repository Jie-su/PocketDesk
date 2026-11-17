using System.Reflection;
using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestPhoneRotationRecovery()
    {
        var uu = new DisplayInfo(@"\\.\POCKETDESK_ROTATION_TEST", "UU", new(0, 0, 2622, 1206), new(0, 0, 2622, 1134), 1.5, true)
        { AdapterName = "GameViewer Virtual Display Adapter", AdapterDeviceId = @"Root\GameViewerIddDriver" };
        var profile = new PhoneProfile { PortraitWidth = 1206, PortraitHeight = 2622 };
        var landscape = new DisplayMode(2622, 1206, 144, 32, 0);
        var portrait = new DisplayMode(1206, 2622, 144, 32, 1);
        var original = new DisplaySnapshot(uu.DeviceName, landscape) { Identity = "test-only-identity", PositionX = -2622, PositionY = 40 };
        var window = new WindowSnapshot { Handle = 0, ProcessName = "test-only-snapshot", NormalBounds = new(100, 80, 640, 480), ShowCommand = 1 };

        Test("Repeated phone rotations preserve the first display and window snapshots", () =>
        {
            var initial = new RecoveryState { Windows = [window] };
            var prepared = initial;
            var current = original;
            for (int i = 0; i < 6; i++)
            {
                var requested = i % 2 == 0 ? portrait : landscape;
                var before = prepared;
                prepared = PrepareRotationForTest(before, uu, current, requested, profile);
                Require(prepared.Display == original, "A later rotation overwrote the original display snapshot.");
                Require(prepared.Windows.SequenceEqual([window]), "A later rotation replaced original window bounds.");
                Require(!ReferenceEquals(prepared.Windows, before.Windows), "Preparation aliases the mutable recovery window list.");
                Require(prepared.AutomaticPhoneMode == requested, "Prepared journal did not track the requested phone pixel pair.");
                current = original with { Mode = requested };
            }
            Require(initial.Display is null && initial.AutomaticPhoneMode is null && initial.Windows.SequenceEqual([window]),
                "Rotation preparation changed the input recovery state.");
            WithIsolatedStore(store =>
            {
                store.Save(prepared);
                var loaded = store.Load();
                Require(loaded.Display == original && loaded.Windows.SequenceEqual([window]),
                    "Repeated-rotation journal did not retain the initial snapshots on disk.");
            });
        });

        Test("Rotation journal preparation rejects unrelated screens and non-swapped modes", () =>
        {
            var state = new RecoveryState { Display = original, Windows = [window] };
            ExpectThrows(() => PrepareRotationForTest(state, uu, original, landscape, profile),
                "Same-direction request was accepted as a rotation.");
            ExpectThrows(() => PrepareRotationForTest(state, uu, original, portrait with { Width = 1080, Height = 1920 }, profile),
                "A different resolution was accepted as a phone rotation.");
            ExpectThrows(() => PrepareRotationForTest(state, uu with { AdapterDeviceId = @"PCI\PHYSICAL" }, original, portrait, profile),
                "A physical display was accepted for phone rotation.");
            ExpectThrows(() => PrepareRotationForTest(state, uu, original with { Identity = "different-identity" }, portrait, profile),
                "A replaced monitor reused the previous recovery snapshot.");
            ExpectThrows(() => PrepareRotationForTest(state, uu, original with { Identity = "" }, portrait, profile),
                "An unidentified monitor was accepted for phone rotation.");
            ExpectThrows(() => PrepareRotationForTest(state, uu with { DeviceName = @"\\.\OTHER" }, original, portrait, profile),
                "Mismatched display and snapshot device names were accepted.");
            Require(state.Display == original && state.AutomaticPhoneMode is null && state.Windows.SequenceEqual([window]),
                "Rejected rotation changed recovery state.");
        });

        Test("A missing-device rotation failure retains Active and the existing recovery journal", () =>
        {
            Require(Displays.ListDisplays().All(d => d.DeviceName != uu.DeviceName), "Synthetic test display unexpectedly exists.");
            WithIsolatedStore(store =>
            {
                store.Save(new RecoveryState { Display = original, Windows = [window] });
                var session = new WorkspaceSession(store);
                typeof(WorkspaceSession).GetProperty(nameof(WorkspaceSession.Active))!.SetValue(session, window);
                var before = System.IO.File.ReadAllText(store.PathName);
                ExpectThrows(() => session.RotatePhoneDisplay(uu.DeviceName, portrait, profile),
                    "An absent display was accepted for rotation.");
                Require(ReferenceEquals(session.Active, window), "Rotation failure discarded the active snapshot.");
                Require(System.IO.File.ReadAllText(store.PathName) == before, "Rotation failure rewrote the existing journal.");
            });
        });

        Test("A later PC mode survives recovery of a repeated-rotation journal", () =>
        {
            WithIsolatedStore(store =>
            {
                var attached = Displays.ListDisplays().First();
                var observed = Displays.Capture(attached.DeviceName);
                // Synthetic phone dimensions deliberately differ from the real
                // screen. Preparing journals is pure; no native Apply is called.
                var width = Math.Min(observed.Mode.Width, observed.Mode.Height) + 1;
                var height = Math.Max(observed.Mode.Width, observed.Mode.Height) + 2;
                var syntheticProfile = new PhoneProfile { PortraitWidth = width, PortraitHeight = height };
                var syntheticPhone = uu with { DeviceName = attached.DeviceName };
                var phoneLandscape = observed.Mode with { Width = height, Height = width };
                var phonePortrait = observed.Mode with { Width = width, Height = height };
                var beforePhone = observed with { Mode = phoneLandscape };
                var first = PrepareRotationForTest(new RecoveryState(), syntheticPhone, beforePhone, phonePortrait, syntheticProfile);
                var second = PrepareRotationForTest(first, syntheticPhone, beforePhone with { Mode = phonePortrait }, phoneLandscape, syntheticProfile);
                // If restore incorrectly tries to switch the real display, the
                // zero-width sentinel is rejected before any native mode write.
                store.Save(second with { Display = second.Display! with { Mode = second.Display!.Mode with { Width = 0 } } });
                var session = new WorkspaceSession(store);
                Require(session.RestoreAll().Count == 0, "Later PC dimensions were overwritten or treated as a failed phone rollback.");
                Require(!session.HasRecovery && store.Load().AutomaticPhoneMode is null,
                    "Superseded repeated-rotation journal was retained.");
                Require(Displays.Capture(attached.DeviceName).Mode == observed.Mode, "Read-only recovery changed the current display mode.");
            });
        });
    }

    private static RecoveryState PrepareRotationForTest(RecoveryState state, DisplayInfo display,
        DisplaySnapshot current, DisplayMode requested, PhoneProfile profile, ViewportPreferences? preferences = null,
        DisplayMode? selectedFallback = null)
    {
        var method = typeof(WorkspaceSession).GetMethod("PreparePhoneRotation", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(WorkspaceSession), "PreparePhoneRotation");
        return (RecoveryState)method.Invoke(null, [state, display, current, requested, profile, preferences, selectedFallback])!;
    }
}
