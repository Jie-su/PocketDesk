using System.Runtime.InteropServices;
using System.Text;
using PocketDesk.Core;

namespace PocketDesk.Native;

/// <summary>Routes user gestures to the selected app's client/child HWND only. No system input or hooks.</summary>
public static class GestureWindowMessages
{
    [StructLayout(LayoutKind.Sequential)] private struct Pt { public int X, Y; public Pt(GesturePoint p) { X = p.X; Y = p.Y; } }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiInfo { public uint Size, Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rect CaretRect; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out Rect r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref Pt p);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hwnd, ref Pt p);
    [DllImport("user32.dll")] private static extern nint ChildWindowFromPointEx(nint hwnd, Pt pt, uint flags);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Pt pt);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flag);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder buf, int len);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(nint hwnd, uint msg, nint w, nint l);
    private static bool Pack(int x, int y, out nint lp)
    {
        lp = (nint)((x & 0xffff) | (y << 16));
        return x is >= short.MinValue and <= short.MaxValue && y is >= short.MinValue and <= short.MaxValue;
    }

    public static bool Available(WindowSnapshot target)
    {
        nint root = (nint)target.Handle;
        uint thread = GetWindowThreadProcessId(root, out uint pid);
        if (GetForegroundWindow() != root || !IsWindowVisible(root) || !IsWindowEnabled(root) || IsIconic(root)
            || pid != target.ProcessId || thread == 0 || thread != target.WindowThreadId) return false;
        var info = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() };
        // Other menus, modal loops, move/size, or an application's native drag take precedence.
        return GetGUIThreadInfo(thread, ref info) && (info.Flags & 0x1e) == 0 && info.Capture == 0;
    }
    // Edge briefly opens a captured, owned mini-toolbar after our completed selection. Dismiss
    // that bubble like Escape without typing keys, changing the selection, or addressing another app.
    public static bool DismissEdgeSelectionBubble(WindowSnapshot target)
    {
        nint root = (nint)target.Handle;
        if (!target.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase) || GetForegroundWindow() != root) return false;
        var info = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() };
        if (!GetGUIThreadInfo(target.WindowThreadId, ref info) || info.Capture == 0 || (info.Flags & 0x1e) != 0) return false;
        nint bubble = info.Capture;
        if (GetWindow(bubble, 4) != root || GetAncestor(bubble, 2) != bubble || !IsWindowVisible(bubble) ||
            GetWindowThreadProcessId(bubble, out var pid) != target.WindowThreadId || pid != target.ProcessId) return false;
        var cls = new StringBuilder(128);
        GetClassName(bubble, cls, cls.Capacity);
        return cls.ToString() == "Chrome_WidgetWin_1" && PostMessage(bubble, 0x1f, 0, 0);
    }
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    public static PixelRect? Bounds(WindowSnapshot target, PixelRect area)
    {
        nint root = (nint)target.Handle;
        if (!GetClientRect(root, out var client)) return null;
        Pt origin = default;
        if (!ClientToScreen(root, ref origin)) return null;
        int l = Math.Max(area.X, origin.X), t = Math.Max(area.Y, origin.Y);
        int r = Math.Min(area.X + area.Width, origin.X + client.Right), b = Math.Min(area.Y + area.Height, origin.Y + client.Bottom);
        return r > l && b > t ? new(l, t, r - l, b - t) : null;
    }
    private static bool In(PixelRect r, GesturePoint p) => p.X >= r.X && p.Y >= r.Y && p.X - r.X < r.Width && p.Y - r.Y < r.Height;
    public static bool TargetAt(WindowSnapshot target, GesturePoint p, nint inputSurface)
    {
        var hit = WindowFromPoint(new Pt(p));
        if (hit == 0) return false;
        return (inputSurface != 0 && hit == inputSurface) ||
            (GetAncestor(hit, 2) == (nint)target.Handle && GetWindowThreadProcessId(hit, out var pid) != 0 && pid == target.ProcessId);
    }

    public static nint Receiver(WindowSnapshot target, GesturePoint p)
    {
        nint root = (nint)target.Handle, current = root;
        // A Chromium/Electron renderer (including the ChatGPT Windows app) can
        // be the real pointer recipient despite having WS_EX_TRANSPARENT. Trust
        // an OS hit only when it belongs to the already selected window/process.
        var hit = WindowFromPoint(new Pt(p));
        if (hit != 0 && hit != root && GetAncestor(hit, 2) == root && GetWindowThreadProcessId(hit, out var hitPid) != 0 &&
            hitPid == target.ProcessId && IsWindowEnabled(hit) && IsWindowVisible(hit)) return hit;
        for (int depth = 0; depth < 48; depth++)
        {
            var pos = new Pt(p);
            if (!ScreenToClient(current, ref pos)) return 0;
            nint child = ChildWindowFromPointEx(current, pos, 7);
            // Our own input surface is normally above the renderer at Down.
            // Include transparent children only for Chromium's known web input
            // window. Do not route to the GPU process's disabled D3D surface.
            nint transparent = ChildWindowFromPointEx(current, pos, 3);
            if (transparent != 0 && transparent != current && transparent != child &&
                GetWindowThreadProcessId(transparent, out var webPid) != 0 && webPid == target.ProcessId &&
                GetAncestor(transparent, 2) == root)
            {
                var cls = new StringBuilder(64);
                GetClassName(transparent, cls, cls.Capacity);
                if (cls.ToString() == "Chrome_RenderWidgetHostHWND") child = transparent;
            }
            if (child == 0 || child == current) return current;
            if (GetAncestor(child, 2) != root || GetWindowThreadProcessId(child, out var pid) == 0 || pid != target.ProcessId) return 0;
            current = child;
        }
        return 0;
    }
    public static bool Dispatch(WindowSnapshot target, PixelRect area, nint receiver, GestureResult result, nint inputSurface = 0)
    {
        if (receiver == 0 || !Available(target) || GetAncestor(receiver, 2) != (nint)target.Handle ||
            GetWindowThreadProcessId(receiver, out var pid) == 0 || pid != target.ProcessId ||
            !IsWindowEnabled(receiver) || !IsWindowVisible(receiver)) return false;
        var bounds = Bounds(target, area);
        if (bounds is null || !In(bounds, result.Start) || !In(bounds, result.End) ||
            !TargetAt(target, result.Start, inputSurface) || !TargetAt(target, result.End, inputSurface) ||
            Receiver(target, result.Start) != receiver) return false;
        var from = new Pt(result.Start); var to = new Pt(result.End);
        if (!ScreenToClient(receiver, ref from) || !ScreenToClient(receiver, ref to) ||
            !Pack(from.X, from.Y, out var first) || !Pack(to.X, to.Y, out var last)) return false;
        switch (result.Action)
        {
            case GestureAction.Tap:
            case GestureAction.DoubleTap:
                return PostMessage(receiver, result.Action == GestureAction.DoubleTap ? 0x203u : 0x201u, 1, first) && PostMessage(receiver, 0x202, 0, first);
            case GestureAction.Menu:
                var name = new StringBuilder(128);
                GetClassName(receiver, name, name.Capacity);
                if (name.ToString().Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
                    name.ToString().StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase))
                    return Pack(result.Start.X, result.Start.Y, out var context) && PostMessage(receiver, 0x7b, receiver, context);
                return PostMessage(receiver, 0x204, 2, first) && PostMessage(receiver, 0x205, 0, first);
            case GestureAction.Wheel:
                return result.Delta is >= short.MinValue and <= short.MaxValue && Pack(result.End.X, result.End.Y, out var wheel)
                    && PostMessage(receiver, 0x20a, (nint)(result.Delta << 16), wheel);
            case GestureAction.Select:
                if (!PostMessage(receiver, 0x201, 1, first)) return false;
                bool moved = PostMessage(receiver, 0x200, 1, last);
                return PostMessage(receiver, 0x202, 0, last) && moved;
            default: return false;
        }
    }
}
