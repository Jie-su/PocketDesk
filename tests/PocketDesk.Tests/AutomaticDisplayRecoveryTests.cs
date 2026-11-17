using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestAutomaticDisplayRecovery()
    {
        Test("Version 1 journals without automatic phone mode remain readable", () =>
        {
            WithIsolatedStore(store =>
            {
                System.IO.File.WriteAllText(store.PathName, "{\"Version\":1,\"Windows\":[],\"Display\":null}");
                var loaded = store.Load();
                Require(loaded.AutomaticPhoneMode is null, "A legacy journal acquired an automatic mode.");
                Require(!new WorkspaceSession(store).HasRecovery, "Empty legacy recovery became active.");
            });
        });

        Test("Automatic phone mode survives a recovery journal round-trip", () =>
        {
            WithIsolatedStore(store =>
            {
                var mode = new DisplayMode(1206, 2622, 60, 32, 0);
                store.Save(new RecoveryState
                {
                    Display = new DisplaySnapshot("test-only-device", mode),
                    AutomaticPhoneMode = mode
                });
                Require(store.Load().AutomaticPhoneMode == mode, "Confirmed automatic mode was not persisted.");
            });
        });

        Test("A later desktop size keeps its mode and clears automatic display recovery", () =>
        {
            WithIsolatedStore(store =>
            {
                var device = Displays.ListDisplays().First();
                var observed = Displays.Capture(device.DeviceName);
                // A zero-width original is a mutation sentinel: if the recovery
                // mistakenly tries to apply it, validation throws before any
                // native display write. This test never switches a display mode.
                var sentinel = observed with { Mode = observed.Mode with { Width = 0 } };
                var oldPhoneMode = observed.Mode with { Width = observed.Mode.Width + 1 };
                store.Save(new RecoveryState { Display = sentinel, AutomaticPhoneMode = oldPhoneMode });
                var session = new WorkspaceSession(store);
                Require(session.RestoreAll().Count == 0, "External size was overwritten or treated as a failed rollback.");
                var loaded = store.Load();
                Require(loaded.Display is null && loaded.AutomaticPhoneMode is null, "Superseded display journal was retained.");
                Require(!session.HasRecovery, "Superseded automatic recovery remains active.");
                Require(Displays.Capture(device.DeviceName).Mode == observed.Mode, "Read-only recovery changed the current mode.");
            });
        });

        Test("Swapping phone width and height still requires the original-mode recovery", () =>
        {
            WithIsolatedStore(store =>
            {
                var device = Displays.ListDisplays().First();
                var observed = Displays.Capture(device.DeviceName);
                // Same sentinel as above: taking the restore branch is observable
                // as validation failure, without changing the user's display.
                var sentinel = observed with { Mode = observed.Mode with { Width = 0 } };
                var rotatedPhoneMode = observed.Mode with { Width = observed.Mode.Height, Height = observed.Mode.Width };
                store.Save(new RecoveryState { Display = sentinel, AutomaticPhoneMode = rotatedPhoneMode });
                var session = new WorkspaceSession(store);
                Require(session.RestoreAll().Count > 0, "A rotated matching phone size was incorrectly discarded.");
                var loaded = store.Load();
                Require(loaded.Display is not null && loaded.AutomaticPhoneMode == rotatedPhoneMode,
                    "Uncompleted phone recovery lost its journal.");
                Require(Displays.Capture(device.DeviceName).Mode == observed.Mode, "Validation-only test changed the mode.");
            });
        });

        Test("Disconnected automatic phone display keeps both recovery fields", () =>
        {
            WithIsolatedStore(store =>
            {
                const string absent = @"\\.\POCKETDESK_TEST_NO_SUCH_DISPLAY";
                Require(Displays.ListDisplays().All(d => d.DeviceName != absent), "Test display unexpectedly exists.");
                var mode = new DisplayMode(1206, 2622, 60, 32, 0);
                store.Save(new RecoveryState
                {
                    Display = new DisplaySnapshot(absent, mode),
                    AutomaticPhoneMode = mode
                });
                var session = new WorkspaceSession(store);
                Require(session.RestoreAll().Count > 0, "Missing automatic display was silently discarded.");
                var loaded = store.Load();
                Require(loaded.Display is not null && loaded.AutomaticPhoneMode == mode,
                    "Disconnected automatic display lost its recovery journal.");
            });
        });
    }
}
