using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestControllerHandoverPolicy()
    {
        var now = new DateTimeOffset(2026, 11, 17, 6, 0, 0, TimeSpan.Zero);

        Test("A fresh disconnected controller does not discard the display restore target", () =>
        {
            var disconnected = new UuControllerSnapshot(UuControllerStatus.None, [], now);
            Require(!ControllerHandoverPolicy.ShouldPreserveCurrentDisplay(disconnected, now),
                "A fresh empty session was treated as a new controller and suppressed restoration.");
        });

        Test("Fresh identified or ambiguous controllers preserve the new connection mode", () =>
        {
            foreach (var snapshot in new[]
            {
                new UuControllerSnapshot(UuControllerStatus.Available, ["Test MacBook Air"], now),
                new UuControllerSnapshot(UuControllerStatus.Available, ["iPhone 17 Pro"], now),
                new UuControllerSnapshot(UuControllerStatus.Ambiguous, ["iPad Pro", "Desktop PC"], now)
            })
                Require(ControllerHandoverPolicy.ShouldPreserveCurrentDisplay(snapshot, now),
                    "Reliable controller evidence was not preserved for explicit handoff.");
        });

        Test("Unavailable stale or future observations do not authorize dropping recovery", () =>
        {
            foreach (var snapshot in new UuControllerSnapshot?[]
            {
                null,
                new(UuControllerStatus.Unknown, [], now),
                new(UuControllerStatus.Available, ["Desktop PC"], now.AddSeconds(-6)),
                new(UuControllerStatus.Ambiguous, ["Phone", "PC"], now.AddSeconds(-6)),
                new(UuControllerStatus.Available, ["Desktop PC"], now.AddMilliseconds(1))
            })
                Require(!ControllerHandoverPolicy.ShouldPreserveCurrentDisplay(snapshot, now),
                    "Unreliable evidence released the session's saved display without normal ownership checks.");
        });

        Test("Controller handoff freshness includes exactly five seconds but no later sample", () =>
        {
            var exact = new UuControllerSnapshot(UuControllerStatus.Available, ["Desktop PC"], now.AddSeconds(-5));
            Require(ControllerHandoverPolicy.ShouldPreserveCurrentDisplay(exact, now), "Freshness boundary was rejected.");
            Require(!ControllerHandoverPolicy.ShouldPreserveCurrentDisplay(exact, now.AddTicks(1)),
                "Expired handoff evidence remained authoritative.");
        });
    }
}
