namespace PocketDesk.Core;

public readonly record struct GesturePoint(int X, int Y);
public enum GestureAction { None, Tap, DoubleTap, Wheel, Select, Menu }
public readonly record struct GestureResult(GestureAction Action, GesturePoint Start, GesturePoint End, int Delta = 0);

/// <summary>Points are physical screen pixels; thresholds follow the phone work area's input density.</summary>
public sealed class MobileGesturePolicy
{
    private enum Phase { Idle, Pressed, Scrolling, Selecting, Ignored }
    private Phase phase;
    private GesturePoint start;
    private long pressedAt;
    private double scale, remainder;
    private int lastY;
    private bool scrolled;
    private bool beganMovingBeforeHold;
    private GesturePoint? selection;
    public bool HasSelection => selection.HasValue;
    public bool IsSelecting => phase == Phase.Selecting;
    public bool IsTracking => phase != Phase.Idle;
    public void Cancel() { phase = Phase.Idle; selection = null; }
    public void Down(GesturePoint p, long ms, double density)
    {
        start = p; pressedAt = ms; lastY = p.Y; remainder = 0; scrolled = false; beganMovingBeforeHold = false;
        scale = double.IsFinite(density) ? Math.Clamp(density, .5, 5) : 1;
        phase = Phase.Pressed;
    }
    public void AdoptHeldDrag(GesturePoint p, long ms, double density)
    {
        Down(p, ms, density);
        phase = Phase.Scrolling;
        selection = null;
    }
    public GestureResult Move(GesturePoint p, long ms)
    {
        if (phase == Phase.Pressed)
        {
            double dx = Math.Abs((double)p.X - start.X), dy = Math.Abs((double)p.Y - start.Y);
            // Remote drags can be coalesced or arrive slowly. A finger already moving
            // before the hold deadline must not become a long press as it crosses slop.
            if (ms - pressedAt < 450 && Math.Max(dx, dy) >= 3 * scale) beganMovingBeforeHold = true;
            if (Math.Max(dx, dy) < 7 * scale) return default;
            phase = !beganMovingBeforeHold && ms - pressedAt >= 450 ? Phase.Selecting : dy >= dx ? Phase.Scrolling : Phase.Ignored;
            selection = null;
        }
        if (phase != Phase.Scrolling) return default;
        remainder += ((double)p.Y - lastY) / (32 * scale);
        lastY = p.Y;
        int ticks = (int)Math.Truncate(remainder);
        if (ticks == 0) return default;
        remainder -= ticks; scrolled = true;
        return new(GestureAction.Wheel, start, p, 120 * Math.Clamp(ticks, -16, 16));
    }
    public GestureResult Up(GesturePoint p, long ms)
    {
        var move = Move(p, ms); var ended = phase; phase = Phase.Idle;
        if (ended == Phase.Selecting)
        {
            // A point just inside the start of the drag also works on multiline selections.
            selection = new(start.X + Math.Sign(p.X - start.X), start.Y + Math.Sign(p.Y - start.Y));
            return new(GestureAction.Select, start, p);
        }
        if (ended == Phase.Scrolling)
            return move.Action == GestureAction.Wheel || scrolled || remainder == 0 ? move :
                new(GestureAction.Wheel, start, p, 120 * Math.Sign(remainder));
        if (ended != Phase.Pressed) return default;
        if (selection is { } anchor) { selection = null; return new(GestureAction.Menu, anchor, anchor); }
        return new(ms - pressedAt >= 450 ? GestureAction.Menu : GestureAction.Tap, start, start);
    }
}
