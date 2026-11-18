using System.Text.Json;
using System.Runtime.InteropServices;
using PocketDesk.Native;
using PocketDesk.Core;

// Read-only display/window diagnostics. No mode writes, window moves or UIA probes.
var service = new DisplayService();
if (args.Contains("--recovery-decision"))
{
    var store = new RecoveryStore();
    var saved = store.Load();
    var session = new WorkspaceSession(store);
    var unknown = new UuControllerSnapshot(UuControllerStatus.Unknown, [], DateTimeOffset.UtcNow);
    DisplaySnapshot? observed = null;
    string? captureError = null;
    if (saved.Display is { } original)
    {
        try { observed = service.Capture(original.DeviceName); }
        catch (InvalidOperationException ex) { captureError = ex.Message; }
    }
    // Decisions and counts only: do not expose window identities or write the journal.
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        session.HasRecovery, session.HasDisplayChange, session.RequiresAutomaticRecoveryEvidence,
        EvidenceOnlyDecision = AutomaticRecoveryEvidencePolicy.Decide(saved, unknown).ToString(),
        VerifiedDisplayDecision = session.AutomaticRecoveryDecision(unknown).ToString(),
        SavedWindowCount = saved.Windows.Count,
        CaptureError = captureError,
        IdentityMatches = observed is not null && string.Equals(saved.Display?.Identity, observed.Identity, StringComparison.OrdinalIgnoreCase),
        OriginalMode = saved.Display?.Mode, CurrentMode = observed?.Mode,
        OriginalPosition = saved.Display is null ? null : new { saved.Display.PositionX, saved.Display.PositionY },
        CurrentPosition = observed is null ? null : new { observed.PositionX, observed.PositionY }
    }, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (args.Contains("--restore-constraints"))
{
    var recovery = new RecoveryStore().Load();
    var windows = new WindowService();
    var report = recovery.Windows.Where(windows.IsSameWindow).Select(saved =>
    {
        var current = windows.Capture(saved.Handle);
        return new { saved.ProcessName, Saved = saved.NormalBounds, SavedShow = saved.ShowCommand,
            Actual = current.NormalBounds, ActualCurrent = current.CurrentBounds, ActualShow = current.ShowCommand,
            Constraints = ConstraintProbe.Read(new nint(saved.Handle)) };
    });
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (args.Contains("--recovery-window-state"))
{
    var recovery = new RecoveryStore().Load();
    var windows = new WindowService();
    var report = recovery.Windows.Select(saved =>
    {
        var current = windows.IsSameWindow(saved) ? windows.Capture(saved.Handle) : null;
        // Never serialize WindowSnapshot directly: its identity fields include
        // executable paths and process/window identifiers needed for recovery,
        // but not for a shareable geometry report.
        return new
        {
            saved.ProcessName,
            SavedNormal = saved.NormalBounds, SavedCurrent = saved.CurrentBounds,
            SavedShow = saved.ShowCommand, SavedFlags = saved.PlacementFlags,
            Observed = current is null ? null : new
            {
                current.NormalBounds, current.CurrentBounds,
                current.ShowCommand, current.PlacementFlags
            }
        };
    });
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (args.Contains("--validate-fallbacks"))
{
    foreach (var display in service.ListDisplays().Where(d => d.IsUuVirtual))
    {
        var current = service.Capture(display.DeviceName).Mode;
        foreach (var (width, height) in new[] { (1200, 1920), (1920, 1200) })
        {
            var mode = service.ListModes(display.DeviceName).Where(m => m.Width == width && m.Height == height)
                .OrderBy(m => m.Frequency == current.Frequency ? 0 : 1).ThenBy(m => m.Orientation).FirstOrDefault();
            if (mode is null) { Console.WriteLine($"{width}x{height}: not advertised"); continue; }
            try { service.ValidateModeAvailable(display.DeviceName, mode); Console.WriteLine($"{mode.Label}: CDS_TEST accepted; no display write"); }
            catch (Exception ex) { Console.WriteLine($"{mode.Label}: rejected: {ex.Message}"); }
        }
    }
    return;
}
var result = service.ListDisplays().Select(display => new
{
    display.DeviceName, display.IsUuVirtual, display.IsPrimary, display.Bounds, display.WorkArea, display.Scale,
    Current = service.Capture(display.DeviceName).Mode,
    Modes = display.IsUuVirtual ? service.ListModes(display.DeviceName) : []
});
Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

static class ConstraintProbe
{
    public static object Read(nint hwnd)
    {
        var previous = SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi == 0) dpi = 96;
            var limits = new MinMax
            {
                MinTrack = new Point(GetSystemMetricsForDpi(34, dpi), GetSystemMetricsForDpi(35, dpi)),
                MaxTrack = new Point(GetSystemMetricsForDpi(59, dpi), GetSystemMetricsForDpi(60, dpi))
            };
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<MinMax>());
            bool read;
            try
            {
                Marshal.StructureToPtr(limits, ptr, false);
                read = SendMessageTimeoutW(hwnd, 0x24, 0, ptr, 2, 300, out _) != 0;
                limits = Marshal.PtrToStructure<MinMax>(ptr);
            }
            finally { Marshal.FreeHGlobal(ptr); }
            DwmGetWindowAttribute(hwnd, 9, out var visible, Marshal.SizeOf<Rect>());
            return new { Dpi = dpi, ReadMinimum = read,
                MinWidth = limits.MinTrack.X, MinHeight = limits.MinTrack.Y,
                MaxWidth = limits.MaxTrack.X, MaxHeight = limits.MaxTrack.Y,
                MaxPositionX = limits.MaxPosition.X, MaxPositionY = limits.MaxPosition.Y,
                FrameX = GetSystemMetricsForDpi(32, dpi) + GetSystemMetricsForDpi(92, dpi),
                FrameY = GetSystemMetricsForDpi(33, dpi) + GetSystemMetricsForDpi(92, dpi),
                VisibleBounds = new { visible.Left, visible.Top, visible.Right, visible.Bottom },
                Style = GetWindowLongPtrW(hwnd, -16).ToInt64().ToString("X"),
                Awareness = GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(hwnd)) };
        }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X,Y; public Point(int x,int y){X=x;Y=y;} }
    [StructLayout(LayoutKind.Sequential)] private struct MinMax { public Point Reserved,MaxSize,MaxPosition,MinTrack,MaxTrack; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindowDpiAwarenessContext(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index,uint dpi);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd,int index);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeoutW(nint hwnd,uint msg,nuint wParam,nint lParam,uint flags,uint timeout,out nuint result);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd,int attribute,out Rect value,int size);
}
