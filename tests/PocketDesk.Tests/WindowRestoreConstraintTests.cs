using System.Reflection;
using System.Runtime.InteropServices;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private const int FixtureSetMinimumMessage = 0x8001;

    private static void TestMaximizedRestoreBounds()
    {
        var desired = new PixelRect(0, 0, 1224, 1050);
        var work = new PixelRect(0, 0, 2622, 1134);
        bool Check(PixelRect normal, PixelRect visible, int frameX = 11, int frameY = 11) =>
            (bool)(typeof(WindowService).GetMethod("IsBoundedMaximizedAdjustment", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(nameof(WindowService), "IsBoundedMaximizedAdjustment"))
                .Invoke(null, [normal, desired, visible, work, frameX, frameY])!;

        Test("Normalized maximized bounds accept only measured frame displacement with a full visible work area", () =>
        {
            Require(Check(desired with { X = 11 }, work), "The observed 11-pixel native frame adjustment was rejected.");
            Require(Check(desired with { X = -11, Y = 11 }, work), "Measured negative/frame-axis displacement was rejected.");
        });

        Test("Maximized frame allowance does not accept extra displacement changed size or a different visible area", () =>
        {
            foreach (var normal in new[] { desired with { X = 12 }, desired with { Y = -12 },
                desired with { Width = desired.Width + 5 }, desired with { Height = desired.Height + 5 } })
                Require(!Check(normal, work), "An out-of-bound displacement or resized normal rectangle was treated as a frame adjustment.");
            Require(!Check(desired with { X = 11 }, work with { Width = work.Width - 100 }),
                "A window that did not visibly fill the intended work area was accepted as maximized.");
            Require(!Check(desired, work, -1, 11) && !Check(desired, work, 11, 129),
                "Invalid frame metrics widened restoration authority.");
        });
    }

    private static void TestChangedFixtureMinimum(long hwnd, PixelRect work)
    {
        Test("Restore measures a changed application minimum even when saved bounds remain contained", () =>
        {
            var compact = new PixelRect(work.X + 42, work.Y + 38,
                Math.Min(480, work.Width - 180), Math.Min(360, work.Height - 180));
            Windows.Place(hwnd, compact);
            var saved = Windows.Capture(hwnd);
            RequireNear(saved.CurrentBounds, compact, 4, "Compact fixture before minimum change");
            var changedMinimum = compact with { Width = compact.Width + 100, Height = compact.Height + 70 };
            try
            {
                SetFixtureMinimum(saved, changedMinimum.Width, changedMinimum.Height);
                Windows.Place(hwnd, changedMinimum with { X = compact.X + 60, Y = compact.Y + 50 });
                Require(Windows.Restore(saved), Windows.LastError ?? "Changed minimum restoration failed.");
                Require(Windows.LastRestoreAdjusted, "Measured application minimum was not reported as an adjustment.");
                var actual = Windows.Capture(hwnd);
                Require(actual.ShowCommand == 1, "A normal restore changed the window's show state.");
                RequireNear(actual.CurrentBounds, changedMinimum, 4, "Explicit measured minimum rectangle");
                RequireNear(actual.NormalBounds, changedMinimum, 4, "Persisted normal rectangle after minimum adjustment");
                RequireNear(saved.NormalBounds, compact, 4, "Unmodified original restore snapshot");
            }
            finally
            {
                SetFixtureMinimum(saved, 0, 0);
            }
        });
    }

    private static void SetFixtureMinimum(WindowSnapshot fixture, int width, int height)
    {
        Require(Windows.IsSameWindow(fixture), "Fixture identity changed before test-only constraint update.");
        Require(SendFixtureMessage(new nint(fixture.Handle), FixtureSetMinimumMessage,
            new nint(width), new nint(height), 2, 1000, out var result) != nint.Zero && result == new nint(1),
            "The dedicated fixture did not acknowledge its test-only constraint update.");
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern nint SendFixtureMessage(nint hwnd, int message, nint wParam, nint lParam,
        uint flags, uint timeout, out nint result);
}
