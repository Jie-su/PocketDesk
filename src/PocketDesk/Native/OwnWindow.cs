using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PocketDesk.Native;

internal static class OwnWindow
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint hwnd, int id);
    public static void SetBounds(Window window, PixelRect bounds)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (!SetWindowPos(handle, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0014))
            throw new InvalidOperationException("无法调整工作台窗口位置。");
    }
}
