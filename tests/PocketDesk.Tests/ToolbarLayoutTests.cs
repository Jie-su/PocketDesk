using PocketDesk.Core;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestToolbarLayout()
    {
        Test("Phone portrait uses a lightweight toolbar without duplicating UU controls", () =>
        {
            var layout = ToolbarLayout.Measure(402, 874);
            Require(layout.Height == 44 && !layout.IsCompact, "Portrait toolbar did not reserve one lightweight row.");
            Require(layout.ContentMaxWidth <= 386 && layout.ContentMaxWidth > 0, "Portrait controls exceed the available width.");
            Require(layout.PopupMaxWidth <= 386 && layout.PopupMaxWidth > 0, "Portrait action panel exceeds the screen.");
            Require(layout.PopupMaxHeight <= 874 - layout.Height - 16 && layout.PopupMaxHeight > 0,
                "Portrait action panel overlaps the reserved toolbar.");
        });

        Test("Short phone landscape toolbar keeps the same touch row and a bounded popup", () =>
        {
            var layout = ToolbarLayout.Measure(874, 402);
            Require(layout.Height == 44 && layout.IsCompact, "Landscape toolbar used a second reserved row.");
            Require(layout.ContentMaxWidth <= 480, "Landscape actions stretch beyond their shared width limit.");
            Require(layout.PopupMaxWidth <= 320 && layout.PopupMaxWidth > 0, "Landscape action panel exceeds its width limit.");
            Require(layout.PopupMaxHeight <= 402 - layout.Height - 16 && layout.PopupMaxHeight > 0,
                "Landscape action panel exceeds the remaining screen height.");
        });

        Test("Tablet toolbar sizing stays consistent across both orientations", () =>
        {
            var portrait = ToolbarLayout.Measure(834, 1194);
            var landscape = ToolbarLayout.Measure(1194, 834);
            Require(portrait.Height == 44 && landscape.Height == 44, "Tablet toolbar reservation changed with orientation.");
            Require(!portrait.IsCompact && landscape.IsCompact, "Tablet orientation state is reversed.");
            Require(portrait.ContentMaxWidth <= 480 && landscape.ContentMaxWidth <= 480,
                "Tablet controls stretch across the entire screen.");
            Require(landscape.PopupMaxHeight <= 834 - landscape.Height - 16,
                "Tablet action panel exceeds the work area.");
        });

        Test("Tiny available areas never produce a negative popup or toolbar overflow", () =>
        {
            var layout = ToolbarLayout.Measure(12, 20);
            Require(layout.Height == 20, "Toolbar extends below the entire available height.");
            Require(layout.ContentMaxWidth == 0 && layout.PopupMaxWidth == 0,
                "Small positive widths produced negative or overflowing content bounds.");
            Require(layout.PopupMaxHeight == 0, "Unavailable popup space must be represented as zero.");
        });

        Test("Toolbar rejects non-finite or non-positive dimensions before layout", () =>
        {
            foreach (var invalid in new[] { 0d, -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                bool widthRejected = false;
                bool heightRejected = false;
                try { ToolbarLayout.Measure(invalid, 874); }
                catch (ArgumentOutOfRangeException) { widthRejected = true; }
                try { ToolbarLayout.Measure(402, invalid); }
                catch (ArgumentOutOfRangeException) { heightRejected = true; }
                Require(widthRejected && heightRejected, $"Invalid dimension {invalid} was accepted.");
            }
        });
    }
}
