using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestAutoPortraitAttemptPolicy()
    {
        var uu = new DisplayInfo(@"\\.\DISPLAY9", "UU", new(0, 0, 2622, 1206), new(0, 0, 2622, 1134), 1.5, true)
        { AdapterName = "GameViewer Virtual Display Adapter", AdapterDeviceId = @"Root\GameViewerIddDriver" };
        var landscape = new DisplayMode(2622, 1206, 144, 32, 0);
        var portrait = landscape with { Width = 1206, Height = 2622 };

        Test("Initial portrait attempt waits for 500 ms of a stable mode", () =>
        {
            long now = 0;
            var policy = new AutoPortraitAttemptPolicy(() => now);
            policy.Observe(uu, landscape);
            Require(!policy.Ready, "Initial sample was ready without stabilization.");
            now = 499; policy.Observe(uu, landscape); policy.MarkAttempted();
            Require(!policy.Ready, "499 ms incorrectly satisfied stabilization.");
            now = 500; policy.Observe(uu, landscape);
            Require(policy.Ready, "500 ms was not ready, or premature MarkAttempted consumed it.");
        });

        Test("A reported display mode change resets portrait stabilization", () =>
        {
            long now = 0;
            var policy = new AutoPortraitAttemptPolicy(() => now);
            policy.Observe(uu, landscape);
            now = 400; policy.Observe(uu, landscape with { Frequency = 60 });
            now = 899; policy.Observe(uu, landscape with { Frequency = 60 });
            Require(!policy.Ready, "Stability accumulated across a refresh change.");
            now = 900; policy.Observe(uu, landscape with { Frequency = 60 });
            Require(policy.Ready, "Stable changed mode did not become ready.");
        });

        Test("Consumed portrait attempts never force subsequent external landscape rotations", () =>
        {
            long now = 0;
            var policy = new AutoPortraitAttemptPolicy(() => now);
            policy.Observe(uu, landscape);
            now = 500; policy.Observe(uu, landscape); policy.MarkAttempted();
            Require(!policy.Ready, "MarkAttempted did not consume readiness.");
            now = 600; policy.Observe(uu, portrait);
            now = 1200; policy.Observe(uu, portrait);
            now = 1300; policy.Observe(uu, landscape);
            now = 2400; policy.Observe(uu, landscape);
            Require(!policy.Ready, "External rotation triggered another automatic portrait attempt.");
        });

        Test("A 999 ms driver reconstruction preserves the consumed attempt", () =>
        {
            long now = 0;
            var policy = new AutoPortraitAttemptPolicy(() => now);
            policy.Observe(uu, landscape);
            now = 500; policy.Observe(uu, landscape); policy.MarkAttempted();
            now = 600; policy.Observe(null, null);
            now = 1599; policy.Observe(uu, portrait);
            now = 2500; policy.Observe(uu, portrait);
            Require(!policy.Ready, "A brief removal created a portrait reconstruction loop.");
        });

        Test("An unconsumed attempt rechecks stabilization after a short absence", () =>
        {
            long now = 0;
            var policy = new AutoPortraitAttemptPolicy(() => now);
            policy.Observe(uu, landscape);
            now = 400; policy.Observe(null, null);
            now = 600; policy.Observe(uu, landscape);
            now = 1099; policy.Observe(uu, landscape);
            Require(!policy.Ready, "Missing samples counted toward stable mode time.");
            now = 1100; policy.Observe(uu, landscape);
            Require(policy.Ready, "Reconnected stable mode never became ready.");
        });

        Test("A full 1000 ms disconnect permits one new portrait attempt", () =>
        {
            long now = 0;
            var policy = new AutoPortraitAttemptPolicy(() => now);
            policy.Observe(uu, landscape);
            now = 500; policy.Observe(uu, landscape); policy.MarkAttempted();
            now = 600; policy.Observe(null, null);
            // The first sample after a full absence can already be reconnected.
            now = 1600; policy.Observe(uu, landscape);
            Require(!policy.Ready, "New connection bypassed mode stabilization.");
            now = 2100; policy.Observe(uu, landscape);
            Require(policy.Ready, "A new connection retained the previous consumed attempt.");
            policy.MarkAttempted();
            now = 3000; policy.Observe(uu, portrait);
            Require(!policy.Ready, "A new connection offered more than one attempt.");
        });

        Test("A different UU display identity requires a new stable attempt", () =>
        {
            long now = 0;
            var policy = new AutoPortraitAttemptPolicy(() => now);
            policy.Observe(uu, landscape);
            now = 500; policy.Observe(uu, landscape); policy.MarkAttempted();
            var other = uu with { DeviceName = @"\\.\DISPLAY10" };
            now = 600; policy.Observe(other, landscape);
            Require(!policy.Ready, "A new identity skipped stabilization.");
            now = 1100; policy.Observe(other, landscape);
            Require(policy.Ready, "A different display inherited the previous consumed attempt.");
        });

        Test("Explicit portrait retry is delayed and consumable only once", () =>
        {
            long now = 0;
            var policy = new AutoPortraitAttemptPolicy(() => now);
            policy.Observe(uu, landscape);
            now = 500; policy.Observe(uu, landscape); policy.MarkAttempted();
            now = 1000; policy.ExplicitRetry();
            Require(!policy.Ready, "Explicit retry bypassed stabilization.");
            now = 1499; policy.Observe(uu, landscape);
            Require(!policy.Ready, "Explicit retry became ready early.");
            now = 1500; policy.Observe(uu, landscape);
            Require(policy.Ready, "Explicit retry never became ready.");
            policy.MarkAttempted();
            now = 2500; policy.Observe(uu, portrait);
            Require(!policy.Ready, "Explicit retry was not consumed.");
        });

        Test("Unreadable modes do not manufacture a new connected display session", () =>
        {
            long now = 0;
            var policy = new AutoPortraitAttemptPolicy(() => now);
            policy.Observe(uu, landscape);
            now = 500; policy.Observe(uu, landscape); policy.MarkAttempted();
            now = 600; policy.Observe(uu, null);
            now = 3000; policy.Observe(uu, null);
            now = 3500; policy.Observe(uu, landscape);
            now = 4000; policy.Observe(uu, landscape);
            Require(!policy.Ready, "A temporarily unreadable mode cleared the consumed attempt.");
        });

        Test("Physical displays and invalid modes never become portrait-ready", () =>
        {
            long now = 0;
            var policy = new AutoPortraitAttemptPolicy(() => now);
            var physical = uu with { AdapterDeviceId = @"PCI\PHYSICAL" };
            policy.Observe(physical, landscape);
            now = 2000; policy.Observe(physical, landscape);
            Require(!policy.Ready, "A physical display became ready.");
            now = 3000; policy.Observe(uu, landscape with { Width = 0 });
            now = 5000; policy.Observe(uu, landscape with { Width = 0 });
            Require(!policy.Ready, "An invalid mode became ready.");
        });
    }
}
