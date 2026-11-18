using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestToolbarLayout()
    {
        Test("Phone portrait positions the handle above UU and the taskbar inside the supplied viewport", () =>
        {
            var v = new PixelRect(-1800, 63, 1200, 1850);
            var p = ToolbarPlacement.Measure(v, 3, LandscapeToolbarPlacement.Right);
            Require(p.Edge == DockEdge.Bottom && p.Bar == new PixelRect(-1800, 1817, 1200, 96), "Strip was not anchored to the bottom of the safe viewport.");
            Require(p.App.Height + p.Bar.Height == v.Height && p.App.Y + p.App.Height == p.Bar.Y, "Workspace must end at the hit strip.");
            Require(p.Panel.Width <= v.Width && p.Panel.Y + p.Panel.Height == v.Y + v.Height, "Portrait sheet overlaps space outside viewport.");
        });
        Test("Left and right landscape placements keep the desk and sheet entirely inside the safe area", () =>
        {
            var v = new PixelRect(-1920, -80, 1920, 1100);
            foreach (var side in new[] { LandscapeToolbarPlacement.Left, LandscapeToolbarPlacement.Right })
            {
                var p = ToolbarPlacement.Measure(v, 2.5, side);
                Require(p.Bar.Width == 80 && p.Bar.Height == v.Height && p.App.Height == v.Height, "Landscape must reserve a side edge rather than a row.");
                Require(p.App.Width + p.Bar.Width == v.Width && (side == LandscapeToolbarPlacement.Left ? p.Bar.X + p.Bar.Width == p.App.X : p.App.X + p.App.Width == p.Bar.X), "Window and strip misaligned.");
                Require(p.Panel.X >= v.X && p.Panel.X+p.Panel.Width <= v.X+v.Width && p.Panel.Y >= v.Y && p.Panel.Y+p.Panel.Height <= v.Y+v.Height, "Panel escaped the viewport.");
            }
        });
        Test("Landscape bottom, hidden and square tablet select deterministic dock edges", () =>
        {
            var v = new PixelRect(0, 0, 1200, 800);
            Require(ToolbarPlacement.Measure(v, 2, LandscapeToolbarPlacement.Bottom).Edge == DockEdge.Bottom, "Bottom toolbar preference lost.");
            Require(ToolbarPlacement.Measure(v, 2, LandscapeToolbarPlacement.Hidden).Edge == DockEdge.Right, "Hidden UU controls should not force bottom in landscape.");
            Require(ToolbarPlacement.Measure(v with { Width=800 }, 2, LandscapeToolbarPlacement.Left).Edge == DockEdge.Bottom, "Square viewport should use the bottom handle.");
        });
        Test("Very small safe areas still produce bounded nonempty rectangles", () =>
        {
            var p = ToolbarPlacement.Measure(new PixelRect(30, 40, 12, 20), 3, LandscapeToolbarPlacement.Left);
            Require(p.App.Width > 0 && p.App.Height > 0 && p.Bar.Height == 19 && p.Panel == new PixelRect(30,40,12,20), "Tiny viewport overflowed.");
        });
        Test("Dock geometry rejects non-finite scales and overflowing screen coordinates", () =>
        {
            foreach (var bad in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
            {
                bool rejected = false; try { ToolbarPlacement.Measure(new(0,0,402,874), bad, default); }
                catch (ArgumentOutOfRangeException) { rejected = true; }
                Require(rejected, "Invalid scale was accepted.");
            }
            bool overflow = false;
            try { ToolbarPlacement.Measure(new(int.MaxValue,0,2,2), 1, default); } catch(ArgumentOutOfRangeException) { overflow = true; }
            Require(overflow, "Invalid screen bounds were accepted.");
        });
    }
}
