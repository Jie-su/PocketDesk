namespace PocketDesk.Native;

/// <summary>All rectangles use physical desktop pixels, including negative coordinates.</summary>
public sealed record DisplayInfo(
    string DeviceName,
    string Label,
    PixelRect Bounds,
    PixelRect WorkArea,
    double Scale,
    bool IsPrimary)
{
    public string AdapterName { get; init; } = "";
    public string AdapterDeviceId { get; init; } = "";
    public bool IsUuVirtual => AdapterDeviceId.StartsWith(@"Root\GameViewerIddDriver", StringComparison.OrdinalIgnoreCase)
        && AdapterName.Contains("GameViewer", StringComparison.OrdinalIgnoreCase);
    public override string ToString() => Label;
}

public sealed record DisplayMode(
    int Width,
    int Height,
    int Frequency,
    int BitsPerPixel,
    int Orientation)
{
    public uint DisplayFlags { get; init; }
    public uint FixedOutput { get; init; }

    public string Label => $"{Width} × {Height}" +
        (Frequency > 1 ? $" · {Frequency} Hz" : "") +
        (Height > Width ? " · 竖屏" : Height == Width ? " · 方形" : " · 横屏") +
        (Orientation is 2 or 3 ? "（翻转）" : "");

    public override string ToString() => Label;
}

/// <summary>A serializable restore point. Capturing it does not alter any setting.</summary>
public sealed record DisplaySnapshot(string DeviceName, DisplayMode Mode)
{
    public int PositionX { get; init; }
    public int PositionY { get; init; }
    public string Identity { get; init; } = "";
}
