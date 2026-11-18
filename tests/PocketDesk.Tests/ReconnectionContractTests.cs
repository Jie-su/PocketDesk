using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    // These compose policy contracts; MainWindow timer/event wiring is not instantiated.
    private static void TestReconnectionContracts()
    {
        var start = new DateTimeOffset(2026, 11, 17, 10, 30, 0, TimeSpan.Zero);
        var uu = new DisplayInfo(@"\\.\RECONNECT_POLICY_TEST", "Synthetic UU",
            new(0, 0, 2622, 1206), new(0, 0, 2622, 1134), 1.5, true)
        { AdapterName = "GameViewer Virtual Display Adapter", AdapterDeviceId = @"Root\GameViewerIddDriver" };
        var mode = new DisplayMode(2622, 1206, 144, 32, 0);
        var none = new UuControllerSnapshot(UuControllerStatus.None, [], start);
        var profile = DeviceProfileCatalog.FindByControllerName("iPhone 17 Pro")!;
        var knownPhone = DeviceSessionPolicy.Resolve(new(UuControllerStatus.Available, ["iPhone 17 Pro"], start),
            uu, mode, profile, start);
        UuControllerSnapshot Available(long milliseconds) =>
            new(UuControllerStatus.Available, ["iPhone 17 Pro"], start.AddMilliseconds(milliseconds));

        Test("Reconnection generation requires newer evidence and cannot loop through duplicate or reordered snapshots", () =>
        {
            var policy = new ControllerDisconnectPolicy();
            policy.Observe(none, uu.DeviceName, start);
            Require(policy.Observe(Available(0), uu.DeviceName, start) && policy.ReconnectionGeneration == 0,
                "Conflicting same-time evidence was mistaken for a new connection.");
            Require(!policy.Observe(Available(100), uu.DeviceName, start.AddMilliseconds(100)) &&
                policy.ReconnectionGeneration == 1, "A newer single controller failed to produce one reconnect.");
            foreach (var snapshot in new[] { none, Available(100), none, Available(200), Available(200) })
                Require(!policy.Observe(snapshot, uu.DeviceName, start.AddMilliseconds(200)) &&
                    policy.ReconnectionGeneration == 1, "Replayed or continuously connected evidence changed the generation.");
            var secondNone = none with { ObservedAt = start.AddMilliseconds(300) };
            Require(policy.Observe(secondNone, uu.DeviceName, start.AddMilliseconds(300)), "A fresh disconnect failed to latch.");
            Require(!policy.Observe(Available(400), uu.DeviceName, start.AddMilliseconds(400)) &&
                policy.ReconnectionGeneration == 2, "A second real reconnect did not produce exactly one additional generation.");
        });

        Test("Ambiguous malformed stale and future controller observations cannot unlock a confirmed disconnect", () =>
        {
            var policy = new ControllerDisconnectPolicy();
            policy.Observe(none, uu.DeviceName, start);
            foreach (var snapshot in new[]
            {
                new UuControllerSnapshot(UuControllerStatus.Ambiguous, ["iPhone 17 Pro", "Test PC"], start.AddMilliseconds(10)),
                new UuControllerSnapshot(UuControllerStatus.Available, [], start.AddMilliseconds(20)),
                new UuControllerSnapshot(UuControllerStatus.Available, [" "], start.AddMilliseconds(30)),
                new UuControllerSnapshot(UuControllerStatus.Available, ["iPhone 17 Pro", "Test PC"], start.AddMilliseconds(40)),
                new UuControllerSnapshot(UuControllerStatus.Unknown, [], start.AddMilliseconds(50)),
                Available(-6000), Available(1000)
            })
                Require(policy.Observe(snapshot, uu.DeviceName, start.AddMilliseconds(500)) &&
                    policy.ReconnectionGeneration == 0, "Unreliable controller evidence cleared the latch or created a retry.");
        });

        Test("Manual finish remains suppressed through Unknown or repeated Available on the same attachment", () =>
        {
            long tick = 0;
            var auto = new AutoWorkspacePolicy(() => tick);
            var attempt = new AutoPortraitAttemptPolicy(() => tick);
            var disconnect = new ControllerDisconnectPolicy();
            disconnect.Observe(Available(0), uu.DeviceName, start);
            attempt.Observe(uu, mode);
            tick = 500; attempt.Observe(uu, mode); attempt.MarkAttempted();
            auto.SuppressUntilDisconnected(uu);
            tick = 1000;
            disconnect.Observe(new(UuControllerStatus.Unknown, [], start.AddMilliseconds(tick)), uu.DeviceName, start.AddMilliseconds(tick));
            tick = 2000;
            disconnect.Observe(Available(tick), uu.DeviceName, start.AddMilliseconds(tick));
            attempt.Observe(uu, mode);
            Require(disconnect.ReconnectionGeneration == 0 && auto.Observe([uu], true) is null && !attempt.Ready,
                "A manual stop was reopened without a reliable disconnect/reconnect or a true monitor disconnect.");
        });

        Test("Reliable None then Available on the same UU releases suppression and permits one stabilized new attempt", () =>
        {
            long tick = 0;
            var auto = new AutoWorkspacePolicy(() => tick);
            var attempt = new AutoPortraitAttemptPolicy(() => tick);
            var disconnect = new ControllerDisconnectPolicy();
            attempt.Observe(uu, mode);
            tick = 500; attempt.Observe(uu, mode); attempt.MarkAttempted();
            auto.SuppressUntilDisconnected(uu);
            disconnect.Observe(none with { ObservedAt = start.AddMilliseconds(tick) }, uu.DeviceName, start.AddMilliseconds(tick));
            tick = 600;
            disconnect.Observe(new(UuControllerStatus.Unknown, [], start.AddMilliseconds(tick)), uu.DeviceName, start.AddMilliseconds(tick));
            Require(auto.Observe([uu], true) is null && disconnect.ReconnectionGeneration == 0,
                "An unreadable intermediate panel prematurely released the stopped connection.");
            tick = 700;
            disconnect.Observe(Available(tick), uu.DeviceName, start.AddMilliseconds(tick));
            Require(disconnect.ReconnectionGeneration == 1, "The same display identity prevented a controller reconnect event.");
            Require(auto.AllowCurrentConnection(uu, knownPhone), "A freshly identified known phone did not authorize connection release.");
            attempt.ExplicitRetry();
            Require(auto.Observe([uu], true) == uu && !attempt.Ready, "Reliable reconnect failed to release the target or bypassed stabilization.");
            tick = 1199; attempt.Observe(uu, mode);
            Require(!attempt.Ready, "Reconnection bypassed the 500 ms mode stability boundary.");
            tick = 1200; attempt.Observe(uu, mode);
            Require(attempt.Ready, "A newly connected controller inherited the previous consumed attempt.");
            attempt.MarkAttempted();
            disconnect.Observe(Available(tick), uu.DeviceName, start.AddMilliseconds(tick));
            tick = 2200; attempt.Observe(uu, mode);
            Require(disconnect.ReconnectionGeneration == 1 && !attempt.Ready,
                "Continuing controller samples created another automatic attempt.");
        });

        Test("Allowing a connection affects only that UU identity and never enables disabled automatic following", () =>
        {
            var auto = new AutoWorkspacePolicy(() => 0);
            var other = uu with { DeviceName = @"\\.\OTHER_RECONNECT_POLICY_TEST" };
            var physical = uu with { AdapterDeviceId = @"PCI\SYNTHETIC_PHYSICAL" };
            auto.SuppressUntilDisconnected(uu);
            auto.SuppressUntilDisconnected(other);
            Require(!auto.AllowCurrentConnection(null, knownPhone) && !auto.AllowCurrentConnection(physical, knownPhone),
                "Missing or physical display was accepted as the reconnected UU target.");
            Require(auto.Observe([uu], true) is null, "Invalid or physical display evidence released a UU target.");
            Require(auto.AllowCurrentConnection(uu, knownPhone), "The known phone failed to release its UU target.");
            Require(auto.Observe([uu, other], true) == uu && auto.Observe([other, uu], true) is null,
                "Releasing one connection cleared another display's manual suppression.");
            Require(auto.Observe([uu], false) is null, "Connection release silently enabled a disabled global preference.");
        });

        Test("A short Unknown monitor reconstruction preserves disconnect generation suppression and consumed attempts", () =>
        {
            long tick = 0;
            var auto = new AutoWorkspacePolicy(() => tick);
            var attempt = new AutoPortraitAttemptPolicy(() => tick);
            var disconnect = new ControllerDisconnectPolicy();
            attempt.Observe(uu, mode); attempt.SuppressForCurrentConnection();
            auto.SuppressUntilDisconnected(uu);
            disconnect.Observe(none, uu.DeviceName, start);
            tick = 100;
            auto.Observe([], true); attempt.Observe(null, null);
            disconnect.Observe(new(UuControllerStatus.Unknown, [], start.AddMilliseconds(tick)), null, start.AddMilliseconds(tick));
            tick = 1099;
            Require(auto.Observe([uu], true) is null, "A 999 ms driver gap released manual suppression.");
            attempt.Observe(uu, mode);
            Require(disconnect.Observe(new(UuControllerStatus.Unknown, [], start.AddMilliseconds(tick)), uu.DeviceName,
                start.AddMilliseconds(tick)) && disconnect.ReconnectionGeneration == 0, "A brief physical gap invented controller reconnection.");
            tick = 2000; attempt.Observe(uu, mode);
            Require(!attempt.Ready, "A short reconstruction reset the consumed initial adaptation.");
        });

        Test("None then an unsupported PC followed by Unknown native phone pixels cannot reopen a manually stopped workspace", () =>
        {
            long tick = 0;
            var auto = new AutoWorkspacePolicy(() => tick);
            var attempt = new AutoPortraitAttemptPolicy(() => tick);
            var disconnect = new ControllerDisconnectPolicy();
            auto.SuppressUntilDisconnected(uu);
            attempt.Observe(uu, mode); attempt.SuppressForCurrentConnection();
            disconnect.Observe(none, uu.DeviceName, start);
            tick = 100;
            var pc = new UuControllerSnapshot(UuControllerStatus.Available, ["Synthetic Test PC"], start.AddMilliseconds(tick));
            disconnect.Observe(pc, uu.DeviceName, start.AddMilliseconds(tick));
            Require(disconnect.ReconnectionGeneration == 1, "Fixture did not exercise a valid single-PC reconnection event.");
            var pcDecision = DeviceSessionPolicy.Resolve(pc, uu, mode, profile, start.AddMilliseconds(tick));
            Require(!auto.AllowCurrentConnection(uu, pcDecision), "A single unsupported PC released a phone's manual stop.");
            tick = 1000;
            var unknown = new UuControllerSnapshot(UuControllerStatus.Unknown, [], start.AddMilliseconds(tick));
            disconnect.Observe(unknown, uu.DeviceName, start.AddMilliseconds(tick));
            var fallback = DeviceSessionPolicy.Resolve(unknown, uu, mode, profile, start.AddMilliseconds(tick));
            Require(fallback.Source == DeviceSessionSource.DimensionFallback,
                "Fixture failed to exercise the dangerous old-native-size fallback.");
            Require(!auto.AllowCurrentConnection(uu, fallback), "Unknown dimensions reused a PC's generation to release manual suppression.");
            attempt.Observe(uu, mode);
            Require(auto.Observe([uu], true) is null && !attempt.Ready && disconnect.ReconnectionGeneration == 1,
                "PC-to-Unknown evidence reopened the stopped workspace or reset initial adaptation.");
        });

        Test("Only an identified valid phone decision can release manual suppression", () =>
        {
            var observations = new[]
            {
                new UuControllerSnapshot(UuControllerStatus.Unknown, [], start),
                new UuControllerSnapshot(UuControllerStatus.None, [], start),
                new UuControllerSnapshot(UuControllerStatus.Ambiguous, ["iPhone 17 Pro", "Test PC"], start),
                new UuControllerSnapshot(UuControllerStatus.Available, ["iPhone 17 Pro"], start.AddSeconds(-6)),
                new UuControllerSnapshot(UuControllerStatus.Available, ["iPhone 17 Pro"], start.AddSeconds(1))
            };
            var decisions = observations.Select(snapshot => DeviceSessionPolicy.Resolve(snapshot, uu, mode, profile, start))
                .Concat(new[]
                {
                    new DeviceSessionDecision(null, true, "Synthetic missing profile"),
                    new DeviceSessionDecision(profile with { PortraitWidth = 0 }, true, "Synthetic invalid dimensions"),
                    new DeviceSessionDecision(profile with { PortraitHeight = 16385 }, true, "Synthetic oversized dimensions"),
                    new DeviceSessionDecision(profile with { LogicalWidth = 0 }, true, "Synthetic invalid logical width")
                });
            foreach (var decision in decisions)
            {
                var auto = new AutoWorkspacePolicy(() => 0);
                auto.SuppressUntilDisconnected(uu);
                Require(!auto.AllowCurrentConnection(uu, decision) && auto.Observe([uu], true) is null,
                    $"Unavailable, ambiguous or invalid profile evidence released a stopped workspace: {decision.Reason}");
            }
        });
    }
}
