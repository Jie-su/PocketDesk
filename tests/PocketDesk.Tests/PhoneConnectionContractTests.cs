using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    // These tests compose policy contracts with real attachment observations.
    // They do not instantiate MainWindow or claim to test its timer/event wiring.
    private static void TestPhoneConnectionContracts()
    {
        var uu = new DisplayInfo(@"\\.\DISPLAY9", "UU", new(0, 0, 2622, 1206), new(0, 0, 2622, 1134), 1.5, true)
        { AdapterName = "GameViewer Virtual Display Adapter", AdapterDeviceId = @"Root\GameViewerIddDriver" };
        var profile = new PhoneProfile { PortraitWidth = 1206, PortraitHeight = 2622 };
        var phone = new DisplayMode(2622, 1206, 144, 32, 0);
        var desktop = new DisplayMode(3840, 2160, 60, 32, 0);

        Test("Attached but unmatched phone sizes preserve a consumed portrait attempt", () =>
        {
            long now = 0;
            var attempt = new AutoPortraitAttemptPolicy(() => now);
            attempt.Observe(uu, phone);
            now = 500; attempt.Observe(uu, phone); attempt.MarkAttempted();
            Require(!PhoneSessionPolicy.CanAutoEnter(uu, desktop, profile), "Desktop mode passed the phone gate.");
            now = 600; attempt.Observe(uu, null);
            now = 2600; attempt.Observe(uu, null);
            now = 3000; attempt.Observe(uu, phone);
            now = 4000; attempt.Observe(uu, phone);
            Require(!attempt.Ready, "An attached display with an unmatched size manufactured a fresh portrait attempt.");
        });

        Test("Phone eligibility changes do not release same-attachment manual suppression", () =>
        {
            long now = 0;
            var auto = new AutoWorkspacePolicy(() => now);
            auto.SuppressUntilDisconnected(uu);
            now = 100; Require(auto.Observe([uu], true) is null, "Initial suppression did not apply.");
            Require(!PhoneSessionPolicy.CanAutoEnter(uu, desktop, profile), "Desktop mode passed the phone gate.");
            now = 2100; Require(auto.Observe([uu], true) is null, "Unmatched but attached UU cleared suppression.");
            Require(PhoneSessionPolicy.CanAutoEnter(uu, phone, profile), "Native phone mode failed the gate.");
            now = 4000; Require(auto.Observe([uu], true) is null, "Returning phone dimensions reopened a suppressed attachment.");
            now = 4100; auto.Observe([], true);
            now = 5100; Require(auto.Observe([uu], true) == uu, "A genuine disconnect failed to release suppression.");
        });

        Test("Manual rotation suppresses initial portrait even before 500 ms readiness", () =>
        {
            long now = 0;
            var attempt = new AutoPortraitAttemptPolicy(() => now);
            attempt.Observe(uu, phone);
            Require(!attempt.Ready, "First observation skipped stabilization.");
            now = 100; attempt.SuppressForCurrentConnection();
            now = 1000; attempt.Observe(uu, phone);
            Require(!attempt.Ready, "An early explicit rotation was later overwritten by initial portrait.");
        });

        Test("Manual rotation suppression survives external rotation and short reconstruction", () =>
        {
            long now = 0;
            var attempt = new AutoPortraitAttemptPolicy(() => now);
            attempt.Observe(uu, phone); attempt.SuppressForCurrentConnection();
            var portrait = phone with { Width = 1206, Height = 2622 };
            now = 100; attempt.Observe(uu, portrait);
            now = 1000; attempt.Observe(uu, portrait);
            now = 1100; attempt.Observe(null, null);
            now = 2099; attempt.Observe(uu, phone);
            now = 3000; attempt.Observe(uu, phone);
            Require(!attempt.Ready, "Mode changes or a short driver reconstruction cleared manual rotation priority.");
        });

        Test("A real disconnect permits initial portrait after manual rotation suppression", () =>
        {
            long now = 0;
            var attempt = new AutoPortraitAttemptPolicy(() => now);
            attempt.Observe(uu, phone); attempt.SuppressForCurrentConnection();
            now = 100; attempt.Observe(null, null);
            now = 1100; attempt.Observe(uu, phone);
            Require(!attempt.Ready, "Reconnect bypassed stabilization.");
            now = 1600; attempt.Observe(uu, phone);
            Require(attempt.Ready, "A new connection inherited the previous manual rotation suppression.");
        });
    }
}
