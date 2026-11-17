using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestAutoWorkspacePolicy()
    {
        var area = new PixelRect(0, 0, 1080, 1920);
        var physical = new DisplayInfo(@"\\.\DISPLAY1", "Generic PnP Monitor", area, area, 1.5, true)
        {
            AdapterName = "NVIDIA GeForce", AdapterDeviceId = @"PCI\VEN_10DE&DEV_1234"
        };
        var uu = new DisplayInfo(@"\\.\DISPLAY32", "Generic PnP Monitor", area, area, 1.5, true)
        {
            AdapterName = "GameViewer Virtual Display Adapter", AdapterDeviceId = @"Root\GameViewerIddDriver\0000"
        };
        var otherUu = uu with { DeviceName = @"\\.\DISPLAY33", IsPrimary = false };

        Test("Auto policy requires UU adapter identity, not generic or display names", () =>
        {
            var labelOnly = physical with { Label = "UU Super Screen GameViewer" };
            var nameOnly = physical with { AdapterName = uu.AdapterName };
            var idOnly = physical with { AdapterDeviceId = uu.AdapterDeviceId };
            Require(!physical.IsUuVirtual && !labelOnly.IsUuVirtual && !nameOnly.IsUuVirtual && !idOnly.IsUuVirtual,
                "A physical/generic monitor was incorrectly identified as UU.");
            Require(new AutoWorkspacePolicy().Observe([physical, labelOnly, nameOnly, idOnly], true) is null,
                "A physical/generic display started the workspace.");
        });
        Test("Auto policy selects the genuine primary UU adapter", () =>
        {
            Require(uu.IsUuVirtual, "A genuine UU adapter identity was rejected.");
            var selected = new AutoWorkspacePolicy().Observe([physical with { IsPrimary = false }, otherUu, uu], true);
            Require(selected == uu, "Primary UU super screen was not selected.");
        });
        Test("Manual exit suppresses automatic reopening throughout the same connection", () =>
        {
            var policy = new AutoWorkspacePolicy();
            Require(policy.Observe([uu], true) == uu, "Initial UU connection was not selected.");
            policy.SuppressUntilDisconnected(uu);
            Require(policy.Observe([uu], true) is null && policy.Observe([uu, otherUu], true) is null,
                "Suppressed super screen automatically reopened.");
        });
        Test("A full second of absence releases suppression even if the next poll is reconnected", () =>
        {
            long now = 0;
            var policy = new AutoWorkspacePolicy(() => now);
            policy.SuppressUntilDisconnected(uu);
            Require(policy.Observe([physical], true) is null, "Physical screen selected after disconnect.");
            now = 1000;
            Require(policy.Observe([uu], true) == uu, "New connection remained suppressed.");
        });
        Test("Brief display reconstruction preserves suppression and separate short gaps do not accumulate", () =>
        {
            long now = 0;
            var policy = new AutoWorkspacePolicy(() => now);
            policy.SuppressUntilDisconnected(uu);
            Require(policy.Observe([physical], true) is null, "Physical screen was selected during a short gap.");
            now = 999;
            var rotated = uu with { Bounds = new PixelRect(0, 0, 1920, 1080), WorkArea = new PixelRect(0, 0, 1920, 1040) };
            Require(policy.Observe([rotated], true) is null, "Short rotation gap cleared manual suppression.");
            now = 1500;
            Require(policy.Observe([physical], true) is null, "Physical screen selected during the second gap.");
            now = 2499;
            Require(policy.Observe([uu], true) is null, "Separate short gaps incorrectly accumulated into a disconnect.");
            now = 4000;
            Require(policy.Observe([uu], true) is null, "Suppression expired while the display remained connected.");
        });
        Test("Repeated absent observations release suppression only after a stable one-second disconnect", () =>
        {
            long now = 50;
            var policy = new AutoWorkspacePolicy(() => now);
            policy.SuppressUntilDisconnected(uu);
            policy.Observe([physical], true);
            now = 1049;
            policy.Observe([physical], true);
            now = 1050;
            policy.Observe([physical], true);
            now = 1051;
            Require(policy.Observe([uu], true) == uu, "Stable disconnect failed to release manual suppression.");
        });
        Test("Phone rotation on the same display does not masquerade as disconnect", () =>
        {
            var landscape = uu with { Bounds = new PixelRect(0, 0, 1920, 1080), WorkArea = new PixelRect(0, 0, 1920, 1040) };
            var policy = new AutoWorkspacePolicy();
            Require(policy.Observe([uu], true) == uu && policy.Observe([landscape], true) == landscape,
                "Rotation lost the selected display identity.");
            policy.SuppressUntilDisconnected(uu);
            Require(policy.Observe([landscape], true) is null && policy.Observe([uu], true) is null,
                "Rotation cleared same-session manual suppression.");
        });
        Test("Multiple non-primary UU screens require explicit user selection", () =>
        {
            var policy = new AutoWorkspacePolicy();
            Require(policy.Observe([physical, uu with { IsPrimary = false }, otherUu], true) is null,
                "Policy guessed among multiple extended UU screens.");
        });
        Test("Disabled auto mode never selects a display and still tracks disconnection", () =>
        {
            long now = 0;
            var policy = new AutoWorkspacePolicy(() => now);
            Require(policy.Observe([uu], false) is null, "Disabled auto mode selected a display.");
            policy.SuppressUntilDisconnected(uu);
            Require(policy.Observe([physical], false) is null, "Disabled physical-only state selected a display.");
            now = 1000;
            Require(policy.Observe([uu], true) == uu, "A disconnect while disabled did not clear suppression.");
        });
        Test("Adapter identity matching and suppression are case insensitive", () =>
        {
            var changedCase = uu with { DeviceName = uu.DeviceName.ToLowerInvariant(),
                AdapterDeviceId = uu.AdapterDeviceId.ToLowerInvariant(), AdapterName = uu.AdapterName.ToUpperInvariant() };
            Require(changedCase.IsUuVirtual, "Casing changed genuine UU adapter recognition.");
            var policy = new AutoWorkspacePolicy();
            policy.SuppressUntilDisconnected(uu);
            Require(policy.Observe([changedCase], true) is null, "Casing change cleared same-session suppression.");
        });
        Test("Bounds normalization handles negative-coordinate monitors and preserves valid geometry", () =>
        {
            var work = new PixelRect(-1600, 40, 1600, 860);
            var contained = new PixelRect(-1500, 100, 600, 500);
            Require(WindowService.NormalizeBoundsToWorkArea(contained, work) == contained, "Already visible geometry was changed.");
            Require(WindowService.NormalizeBoundsToWorkArea(new PixelRect(-100, 700, 600, 500), work)
                == new PixelRect(-600, 400, 600, 500), "Partially off-screen rectangle was not relocated into work area.");
            Require(WindowService.NormalizeBoundsToWorkArea(new PixelRect(-1800, -100, 2200, 1200), work) == work,
                "Oversized bounds were not fitted to the current work area.");
        });
    }
}
