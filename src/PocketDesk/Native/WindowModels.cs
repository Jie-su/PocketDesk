namespace PocketDesk.Native;

/// <summary>A rectangle in physical screen pixels, including negative monitor coordinates.</summary>
public sealed record PixelRect(int X, int Y, int Width, int Height);

public sealed record WindowEntry(long Handle, string Title, string ProcessName, int ProcessId);

/// <summary>Persist this before changing a window. Bounds use screen pixels, not WINDOWPLACEMENT workspace units.</summary>
public sealed record WindowSnapshot
{
    public long Handle { get; init; }
    public int ProcessId { get; init; }
    public long ProcessStartTicks { get; init; }
    public string ProcessPath { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public string WindowClass { get; init; } = "";
    public uint WindowThreadId { get; init; }
    public PixelRect NormalBounds { get; init; } = new(0, 0, 0, 0);
    public PixelRect CurrentBounds { get; init; } = new(0, 0, 0, 0);
    public int ShowCommand { get; init; }
    public int PlacementFlags { get; init; }
    public int MinPositionX { get; init; } = -1;
    public int MinPositionY { get; init; } = -1;
    public int MaxPositionX { get; init; } = -1;
    public int MaxPositionY { get; init; } = -1;
    public bool PlacementUsesWorkspaceCoordinates { get; init; } = true;
    public int ZoomSteps { get; init; }
    public bool ZoomUncertain { get; init; }
}
