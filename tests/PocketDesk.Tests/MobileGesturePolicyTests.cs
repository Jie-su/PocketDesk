using PocketDesk.Core;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static void TestMobileGesturePolicy()
    {
        Test("Quick single-finger drags scroll in both directions without tapping", () =>
        {
            var p = new MobileGesturePolicy(); p.Down(new(30, 100), 100, 2);
            var first = p.Move(new(32, 35), 140);
            Require(first.Action == GestureAction.Wheel && first.Delta == -120, "Wrong direction or units.");
            Require(p.Move(new(32, 164), 170).Delta == 240, "Direction reversal lost.");
            Require(p.Up(new(32, 164), 600).Action == GestureAction.None, "Scroll became a selection.");
        });
        Test("Holding before dragging selects; next tap opens menu at selection, once", () =>
        {
            var p = new MobileGesturePolicy(); p.Down(new(30, 50), 100, 1);
            Require(p.Move(new(32, 51), 290).Action == GestureAction.None, "Jitter became input.");
            p.Move(new(80, 55), 570); Require(p.IsSelecting, "Did not enter selection mode.");
            var s = p.Up(new(94, 60), 600);
            Require(s.Action == GestureAction.Select && s.Start == new GesturePoint(30, 50) && s.End == new GesturePoint(94, 60), "Incorrect selection range.");
            p.Down(new(3, 3), 700, 1); var menu = p.Up(new(3, 3), 800);
            Require(menu.Action == GestureAction.Menu && menu.Start == new GesturePoint(31, 51), "Menu lost the selection.");
            p.Down(new(3, 3), 900, 1); Require(p.Up(new(3, 3), 925).Action == GestureAction.Tap, "Ordinary tap swallowed.");
        });
        Test("Short swipes advance one notch and cancellation drops a pending menu", () =>
        {
            var p = new MobileGesturePolicy(); p.Down(new(5, 30), 0, 1);
            Require(p.Up(new(6, 19), 130).Delta == -120, "A short swipe was swallowed.");
            p.Down(new(3, 3), 200, 1); p.Up(new(60, 3), 705); p.Cancel();
            p.Down(new(3, 3), 800, 1); Require(p.Up(new(3, 3), 900).Action == GestureAction.Tap, "Cancelled selection survived.");
            p.Down(new(3, 3), 1100, 1); Require(p.Up(new(3, 3), 1600).Action == GestureAction.Menu, "Stationary long press failed.");
        });
        Test("Slow movement before the hold deadline and drag entry with a held button remain scrolling", () =>
        {
            var p = new MobileGesturePolicy();
            p.Down(new(20, 110), 0, 3);
            p.Move(new(20, 99), 250);
            p.Move(new(20, 88), 540);
            Require(p.Up(new(20, 80), 820).Delta == -120, "Slow continuous swipe became a selection.");
            p.AdoptHeldDrag(new(20, 75), 1000, 1);
            Require(p.Up(new(20, 75), 1500).Action == GestureAction.None, "A missed press produced a click or menu.");
        });
    }
}
