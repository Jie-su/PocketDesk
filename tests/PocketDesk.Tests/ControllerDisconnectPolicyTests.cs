using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestControllerDisconnectPolicy()
    {
        var start = new DateTimeOffset(2026, 11, 17, 6, 0, 0, TimeSpan.Zero);
        const string display = "verified-uu-display";
        var none = new UuControllerSnapshot(UuControllerStatus.None, [], start);

        Test("Explicit disconnection survives an unreadable stale or future controller observation", () =>
        {
            var policy = new ControllerDisconnectPolicy();
            Require(policy.Observe(none, display, start), "Fresh None failed to latch disconnection.");
            Require(policy.Observe(new(UuControllerStatus.Unknown, [], start.AddSeconds(1)), display, start.AddSeconds(1)),
                "An unreadable panel reopened size fallback after a confirmed disconnect.");
            Require(policy.Observe(new(UuControllerStatus.Available, ["PC"], start), display, start.AddSeconds(6)),
                "Stale evidence cleared the disconnection latch.");
            Require(policy.Observe(new(UuControllerStatus.Available, ["PC"], start.AddSeconds(8)), display, start.AddSeconds(7)),
                "Future-dated evidence cleared the disconnection latch.");
            Require(policy.KnownDisconnected, "Observe and the exposed state disagree.");
        });

        Test("A new reliable single controller clears the disconnect latch", () =>
        {
            var policy = new ControllerDisconnectPolicy();
            policy.Observe(none, display, start);
            Require(!policy.Observe(new(UuControllerStatus.Available, ["test-controller"], start.AddSeconds(1)), display, start.AddSeconds(1)),
                "New reliable controller evidence did not release disconnection.");
            Require(!policy.KnownDisconnected && policy.ReconnectionGeneration == 1,
                "Cleared evidence failed to expose exactly one reconnection.");
        });

        Test("Short monitor reconstruction gaps cannot reopen a disconnected controller", () =>
        {
            var policy = new ControllerDisconnectPolicy();
            policy.Observe(none, display, start);
            Require(policy.Observe(null, null, start.AddMilliseconds(100)), "Missing monitor prematurely cleared disconnection.");
            Require(policy.Observe(null, display, start.AddMilliseconds(1099)), "A 999 ms gap cleared disconnection.");
            Require(policy.Observe(null, null, start.AddMilliseconds(1200)), "A second short gap cleared disconnection.");
            Require(policy.Observe(null, display, start.AddMilliseconds(2199)), "Separate short gaps were accumulated into one reconnect.");
        });

        Test("A real one-second display disconnect releases the latch only on reconnection", () =>
        {
            var policy = new ControllerDisconnectPolicy();
            policy.Observe(none, display, start);
            Require(policy.Observe(null, null, start.AddMilliseconds(100)), "Initial absence cleared the latch.");
            Require(policy.Observe(null, null, start.AddMilliseconds(1100)), "Absence alone was treated as a new connection.");
            Require(!policy.Observe(none, display, start.AddMilliseconds(1100)),
                "A real reconnect failed to clear the latch or replayed the same None immediately.");
            Require(!policy.KnownDisconnected, "Exposed latch did not reflect the new display connection.");
            Require(policy.ReconnectionGeneration == 0, "A physical reconnect invented a controller evidence event.");
        });

        Test("Replaying old None cannot override a more recent connected controller", () =>
        {
            var policy = new ControllerDisconnectPolicy();
            policy.Observe(none, display, start);
            var connected = new UuControllerSnapshot(UuControllerStatus.Available, ["iPhone 17 Pro"], start.AddMilliseconds(500));
            Require(!policy.Observe(connected, display, start.AddMilliseconds(500)), "Connected controller did not clear None.");
            Require(!policy.Observe(none, display, start.AddMilliseconds(600)), "Older None overrode newer connected evidence.");
            Require(!policy.Observe(none, display, start.AddMilliseconds(700)), "Repeated old None relatched disconnection.");
        });

        Test("A genuinely new None can latch again after a previous disconnect was cleared", () =>
        {
            var policy = new ControllerDisconnectPolicy();
            policy.Observe(none, display, start);
            policy.Observe(new(UuControllerStatus.Available, ["iPad Pro"], start.AddSeconds(1)), display, start.AddSeconds(1));
            var disconnectedAgain = new UuControllerSnapshot(UuControllerStatus.None, [], start.AddSeconds(2));
            Require(policy.Observe(disconnectedAgain, display, start.AddSeconds(2)), "A second true disconnect was suppressed by deduplication.");
            Require(policy.Observe(disconnectedAgain, display, start.AddSeconds(3)), "A duplicate None toggled the latched state.");
        });
    }
}
