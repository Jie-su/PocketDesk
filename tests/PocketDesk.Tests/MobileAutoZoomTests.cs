using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestMobileAutoZoom()
    {
        Test("Only the official packaged ChatGPT desktop app accepts its interface zoom shortcuts", () =>
        {
            Require(ChatGptPackage.SupportsZoom("ChatGPT", "OpenAI.Codex_2p2nqsd0c76g0"), "Installed package missing.");
            Require(ChatGptPackage.SupportsZoom("ChatGPT", "OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0"), "Classic Store app missing.");
            Require(!ChatGptPackage.SupportsZoom("ChatGPT", null) &&
                !ChatGptPackage.SupportsZoom("ChatGPT", "OpenAI.Codex_someotherpublisher") &&
                !ChatGptPackage.SupportsZoom("msedge", "OpenAI.Codex_2p2nqsd0c76g0"), "Unrelated program would receive zoom.");
        });
        Test("Mobile zoom applies once per visit and disabling preserves manual increases", () =>
        {
            var policy = new MobileAutoZoomPolicy();
            int total = 2; // two pre-existing manual steps
            void Change(int delta) => total += delta;
            Require(policy.Apply(true, total, Change) == 1, "Initial step missing.");
            Require(policy.Apply(true, total, Change) == 0 && total == 3, "Reflow applied a second zoom.");
            Change(1); policy.NoteManualChange(1);
            Require(policy.Apply(false, total, Change) == -1 && total == 3, "Manual changes were lost.");
            Require(policy.Apply(false, total, Change) == 0, "Disabled twice.");
            Require(policy.Apply(true, total, Change) == 1 && total == 4, "Explicit re-enable did not take effect.");
            policy.Reset(); total = 0; // previous window restored, new window opened
            Require(policy.Apply(true, total, Change) == 1 && total == 1, "New window was skipped.");
        });
        Test("Manually reducing the automatic step is respected across reflow and disable", () =>
        {
            var policy = new MobileAutoZoomPolicy();
            int total = 0;
            void Change(int delta) => total += delta;
            policy.Apply(true, total, Change);
            foreach (int manual in new[] { 1, -1, -1, 1 })
            {
                Change(manual);
                policy.NoteManualChange(manual);
            }
            Require(policy.Apply(true, total, Change) == 0 && total == 1, "Auto zoom fought the user.");
            Require(policy.Apply(false, total, Change) == 0 && total == 1, "Disabling removed a user's increase.");
        });
        Test("Mobile zoom never reverses an unconfirmed or manually recovered automatic increase", () =>
        {
            var policy = new MobileAutoZoomPolicy();
            int calls = 0;
            void Fail(int delta) { calls++; throw new InvalidOperationException("fixture"); }
            try { policy.Apply(true, 0, Fail); } catch (InvalidOperationException) { }
            Require(calls == 1 && policy.Apply(true, 0, Fail) == 0 && policy.Apply(false, 0, Fail) == 0,
                "An uncertain shortcut was repeated or reversed.");
            Require(policy.Apply(true, 8, Fail) == 0, "Zoom was sent above the adjustment limit.");
            policy.Reset();
            policy.Apply(true, 0, _ => { });
            policy.NoteManuallyRestored();
            Require(policy.Apply(false, 0, Fail) == 0, "Manual recovery did not release the automatic step.");
        });
    }
}
