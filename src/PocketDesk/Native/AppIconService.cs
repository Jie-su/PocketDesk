using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PocketDesk.Native;

internal static class AppIconService
{
    private sealed record CachedIcon(ImageSource? Image, DateTime ReadAt);
    private static readonly Dictionary<string, CachedIcon> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();

    public static ImageSource? GetIcon(string appKey, IReadOnlyList<WindowEntry> windows, string executablePath)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(appKey, out var cached) &&
                (cached.Image is not null || DateTime.UtcNow - cached.ReadAt < TimeSpan.FromSeconds(30)))
                return cached.Image;
        }

        ImageSource? image = null;
        // A bounded read-only message cannot hang the library on an unresponsive
        // application. Checking the owner prevents using a recycled window handle.
        foreach (var window in windows.Take(2))
        {
            image = ReadWindowIcon(window);
            if (image is not null) break;
        }
        image ??= ReadFileIcon(executablePath);
        lock (CacheLock)
        {
            if (Cache.Count >= 256)
            {
                var oldest = Cache.MinBy(x => x.Value.ReadAt).Key;
                Cache.Remove(oldest);
            }
            Cache[appKey] = new CachedIcon(image, DateTime.UtcNow);
        }
        return image;
    }

    private static ImageSource? ReadWindowIcon(WindowEntry window)
    {
        var hwnd = new nint(window.Handle);
        if (GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid != window.ProcessId)
            return null;

        const uint wmGetIcon = 0x007F;
        const uint flags = 0x0001 | 0x0002 | 0x0020; // BLOCK | ABORTIFHUNG | ERRORONEXIT
        foreach (var kind in new nint[] { 1, 2 }) // Large, then the app's small icon.
        {
            if (SendMessageTimeoutW(hwnd, wmGetIcon, kind, 0, flags, 40, out var icon) == 0 || icon == 0)
                continue;
            // WM_GETICON returns a shared application-owned handle. Destroy only
            // our copy, after WPF has copied its pixels into a frozen bitmap.
            var copy = CopyIcon(icon);
            if (copy == 0) continue;
            try
            {
                var image = CopyPixels(copy);
                if (image is not null) return image;
            }
            finally { DestroyIcon(copy); }
        }
        return null;
    }

    private static ImageSource? ReadFileIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Length >= 260)
            return null;
        var info = new ShellFileInfo { DisplayName = "", TypeName = "" };
        try
        {
            const uint shgfiIcon = 0x00000100; // The default size is the large icon.
            if (SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), shgfiIcon) == 0 || info.Icon == 0)
                return null;
            return CopyPixels(info.Icon);
        }
        catch { return null; }
        finally
        {
            // Unlike WM_GETICON, SHGFI_ICON transfers ownership to the caller.
            if (info.Icon != 0) DestroyIcon(info.Icon);
        }
    }

    private static ImageSource? CopyPixels(nint icon)
    {
        try
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint SendMessageTimeoutW(nint window, uint message, nint wParam, nint lParam,
        uint flags, uint timeout, out nint result);

    [DllImport("user32.dll")]
    private static extern nint CopyIcon(nint icon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint SHGetFileInfoW(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
}
