using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestAutomaticRecoveryEvidencePolicy()
    {
        var now = new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero);
        var phone = DeviceProfileCatalog.FindByControllerName("iPhone 17 Pro")!;
        var preferences = new ViewportPreferences();
        var mode = new DisplayMode(1200, 1920, 144, 32, 1);
        var original = new DisplaySnapshot(@"\\.\AUTOMATIC_EVIDENCE_TEST", new(3840, 2160, 144, 32, 0))
            { Identity = "automatic-evidence-test-adapter", PositionX = -3840 };
        var selected = new RecoveryState
        {
            Display = original, PendingDisplayMode = mode,
            PhoneDisplayOwner = PhoneDisplayOwnership.Create(phone, preferences).WithFallback(mode),
            Windows = [new WindowSnapshot { Handle = 44, ProcessName = "pure-recovery-window", NormalBounds = new(10, 20, 800, 600) }]
        };

        Test("Automatic evidence gating does not block legacy or exact-family restoration", () =>
        {
            foreach (var state in new[]
            {
                new RecoveryState(),
                selected with { PhoneDisplayOwner = null },
                selected with { PendingDisplayMode = new(1206, 2076, 144, 32, 1), PhoneDisplayOwner = PhoneDisplayOwnership.Create(phone, preferences) }
            })
                Require(AutomaticRecoveryEvidencePolicy.Decide(state, null, now) == AutomaticRecoveryDecision.Restore,
                    "A journal without selected common fallback sizes was unnecessarily deferred.");
        });

        Test("Fallback automatic recovery defers on unknown absent stale or future evidence", () =>
        {
            var before = System.Text.Json.JsonSerializer.Serialize(selected);
            foreach (var snapshot in new UuControllerSnapshot?[]
            {
                null,
                new(UuControllerStatus.Unknown, [], now),
                new(UuControllerStatus.Unknown, ["iPhone 17 Pro"], now),
                new(UuControllerStatus.Available, ["iPhone 17 Pro"], now.AddSeconds(-6)),
                new(UuControllerStatus.None, [], now.AddMilliseconds(1))
            })
                Require(AutomaticRecoveryEvidencePolicy.Decide(selected, snapshot, now) == AutomaticRecoveryDecision.Defer,
                    "Missing reliable evidence allowed a common resolution to trigger automatic restore or journal release.");
            Require(System.Text.Json.JsonSerializer.Serialize(selected) == before, "Deferred decisions changed the in-memory journal.");
        });

        Test("Fresh explicit None restores fallback sessions including the freshness boundary", () =>
        {
            var none = new UuControllerSnapshot(UuControllerStatus.None, [], now.AddSeconds(-5));
            Require(AutomaticRecoveryEvidencePolicy.Decide(selected, none, now) == AutomaticRecoveryDecision.Restore,
                "Fresh disconnect evidence did not authorize normal restoration.");
            Require(AutomaticRecoveryEvidencePolicy.Decide(selected, none, now.AddTicks(1)) == AutomaticRecoveryDecision.Defer,
                "Expired disconnect evidence was used to restore a common resolution.");
        });

        Test("A freshly identified owning phone authorizes recovery despite changed layout preferences", () =>
        {
            var samePhone = new UuControllerSnapshot(UuControllerStatus.Available, ["iPhone 17 Pro"], now);
            Require(AutomaticRecoveryEvidencePolicy.Decide(selected, samePhone, now) == AutomaticRecoveryDecision.Restore,
                "The current owning device did not authorize recovery.");
            var layoutVariant = phone with { LogicalWidth = 420, PortraitOnConnect = false };
            var variantState = selected with { PhoneDisplayOwner = PhoneDisplayOwnership.Create(layoutVariant, preferences).WithFallback(mode) };
            Require(AutomaticRecoveryEvidencePolicy.Decide(variantState, samePhone, now) == AutomaticRecoveryDecision.Restore,
                "Layout preferences were incorrectly treated as a different physical device model.");
        });

        Test("PC another device or ambiguous controller preserves pixels even when they equal the selected fallback", () =>
        {
            Require(ShouldRestoreForTest(selected, original with { Mode = mode }),
                "Fixture does not exercise the dangerous same-pixel ownership branch.");
            foreach (var snapshot in new[]
            {
                new UuControllerSnapshot(UuControllerStatus.Available, ["Desktop PC"], now),
                new UuControllerSnapshot(UuControllerStatus.Available, ["iPad Pro (11 英寸, 第 3 代)"], now),
                new UuControllerSnapshot(UuControllerStatus.Available, ["Unregistered phone"], now),
                new UuControllerSnapshot(UuControllerStatus.Available, [], now),
                new UuControllerSnapshot(UuControllerStatus.Available, ["iPhone 17 Pro", "Desktop PC"], now),
                new UuControllerSnapshot(UuControllerStatus.Ambiguous, ["iPhone 17 Pro", "Desktop PC"], now)
            })
                Require(AutomaticRecoveryEvidencePolicy.Decide(selected, snapshot, now) == AutomaticRecoveryDecision.Preserve,
                    "Same fallback pixels overrode evidence of another or ambiguous controller.");
        });

        Test("Session-level Defer keeps the complete persisted display window and ownership journal untouched", () =>
        {
            WithIsolatedStore(store =>
            {
                store.Save(selected);
                var before = System.IO.File.ReadAllText(store.PathName);
                var session = new WorkspaceSession(store);
                Require(session.RequiresAutomaticRecoveryEvidence, "Selected fallback session did not request controller evidence.");
                Require(session.AutomaticRecoveryDecision(new(UuControllerStatus.Unknown, [], now), now) == AutomaticRecoveryDecision.Defer,
                    "Session wrapper did not defer automatic recovery.");
                Require(session.HasRecovery && session.HasDisplayChange && System.IO.File.ReadAllText(store.PathName) == before,
                    "Defer discarded or rewrote durable recovery data.");
                var retained = store.Load();
                Require(retained.Display == original && retained.PendingDisplayMode == mode && retained.Windows.Count == 1 &&
                    retained.PhoneDisplayOwner!.SelectedFallbackSizes.SequenceEqual([new PhonePixelSize(1200, 1920)]),
                    "Deferred journal lost windows, original display or selected fallback authority.");
                store.Save(selected with { PhoneDisplayOwner = null });
                Require(!new WorkspaceSession(store).RequiresAutomaticRecoveryEvidence,
                    "Legacy session was newly blocked by selected-fallback evidence rules.");
            });
        });
    }
}
