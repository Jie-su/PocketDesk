using System.Text.Json;
using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestAlreadyRestoredRecovery()
    {
        var now = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var profile = DeviceProfileCatalog.FindByControllerName("iPhone 17 Pro")!;
        var applied = new DisplayMode(1200, 1920, 144, 32, 1);
        var original = new DisplaySnapshot(@"\\.\RECONNECT_RECOVERY_TEST",
            new(2622, 1206, 144, 32, 0) { DisplayFlags = 1, FixedOutput = 2 })
            { Identity = "synthetic-reconnect-adapter", PositionX = -2622, PositionY = 80 };
        var state = new RecoveryState
        {
            Display = original,
            AppliedDisplayMode = applied,
            PhoneDisplayOwner = PhoneDisplayOwnership.Create(profile, new()).WithFallback(applied),
            Windows = [new() { Handle = 71, ProcessName = "synthetic-window", NormalBounds = new(20, 30, 800, 600) }]
        };
        var unknown = new UuControllerSnapshot(UuControllerStatus.Unknown, [], now);
        var deferred = AutomaticRecoveryEvidencePolicy.Decide(state, unknown, now);

        Test("Unknown recovery may release an exactly restored original without changing any journal data", () =>
        {
            Require(deferred == AutomaticRecoveryDecision.Defer, "Fixture lacks the common-resolution evidence gate.");
            WithIsolatedStore(store =>
            {
                store.Save(state);
                var diskBefore = System.IO.File.ReadAllText(store.PathName);
                var before = JsonSerializer.Serialize(state);
                var decision = AutomaticRecoveryEvidencePolicy.AllowReleaseWhenAlreadyRestored(deferred, state, original with { });
                Require(decision == AutomaticRecoveryDecision.Preserve,
                    "An exact original was blocked, or returned Restore and allowed a redundant native mode write.");
                Require(JsonSerializer.Serialize(state) == before && System.IO.File.ReadAllText(store.PathName) == diskBefore,
                    "A read-only decision altered the original display, selected owner or window recovery records.");
            });
        });

        Test("Already-restored recovery compares the complete mode tuple rather than only pixel dimensions", () =>
        {
            foreach (var mode in new[]
            {
                original.Mode with { Width = 2621 }, original.Mode with { Height = 1205 },
                original.Mode with { Frequency = 60 }, original.Mode with { BitsPerPixel = 64 },
                original.Mode with { Orientation = 2 }, original.Mode with { DisplayFlags = 0 },
                original.Mode with { FixedOutput = 0 }
            })
                Require(AutomaticRecoveryEvidencePolicy.AllowReleaseWhenAlreadyRestored(deferred, state,
                    original with { Mode = mode }) == AutomaticRecoveryDecision.Defer,
                    "A changed field in the display mode tuple released an unresolved recovery target.");
        });

        Test("Already-restored recovery requires matching device identity and both desktop coordinates", () =>
        {
            foreach (var current in new DisplaySnapshot?[]
            {
                null, original with { DeviceName = @"\\.\OTHER_RECONNECT_TEST" },
                original with { Identity = "another-adapter" }, original with { Identity = "" },
                original with { PositionX = original.PositionX + 1 },
                original with { PositionY = original.PositionY - 1 }
            })
                Require(AutomaticRecoveryEvidencePolicy.AllowReleaseWhenAlreadyRestored(deferred, state, current) ==
                    AutomaticRecoveryDecision.Defer, "Missing or different display identity/location released the old target.");
            foreach (var invalidOriginal in new[] { original with { Identity = "" }, original with { DeviceName = "" } })
                Require(AutomaticRecoveryEvidencePolicy.AllowReleaseWhenAlreadyRestored(deferred,
                    state with { Display = invalidOriginal }, invalidOriginal) == AutomaticRecoveryDecision.Defer,
                    "Missing original identity was accepted merely because both snapshots omitted it.");
        });

        Test("An owned fallback or a swapped original remains deferred when controller evidence is unavailable", () =>
        {
            foreach (var mode in new[] { applied, original.Mode with { Width = 1206, Height = 2622, Orientation = 1 } })
                Require(AutomaticRecoveryEvidencePolicy.AllowReleaseWhenAlreadyRestored(deferred, state,
                    original with { Mode = mode }) == AutomaticRecoveryDecision.Defer,
                    "Ownership or an unordered pixel pair was mistaken for the restored original mode.");
        });

        Test("Already-restored refinement cannot change existing authority or invent a missing original", () =>
        {
            foreach (var decision in new[] { AutomaticRecoveryDecision.Restore, AutomaticRecoveryDecision.Preserve })
                Require(AutomaticRecoveryEvidencePolicy.AllowReleaseWhenAlreadyRestored(decision, state,
                    original with { Mode = applied }) == decision, "An established controller decision was overridden.");
            Require(AutomaticRecoveryEvidencePolicy.AllowReleaseWhenAlreadyRestored(deferred,
                state with { Display = null }, original) == AutomaticRecoveryDecision.Defer,
                "A missing original display was treated as already restored.");
        });
    }
}
