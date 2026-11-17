using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PocketDesk.Native;

/// <summary>
/// Controls modes of displays that already exist. It neither installs a display
/// driver nor creates a virtual display, and never persists modes to the registry.
/// </summary>
public sealed class DisplayService
{
    private const uint EnumCurrentSettings = 0xFFFFFFFF;
    private const uint EdsRotatedMode = 0x00000004;
    private const uint CdsTest = 0x00000002;
    private const uint DmPosition = 0x00000020;
    private const uint DmDisplayOrientation = 0x00000080;
    private const uint DmBitsPerPixel = 0x00040000;
    private const uint DmWidth = 0x00080000;
    private const uint DmHeight = 0x00100000;
    private const uint DmDisplayFlags = 0x00200000;
    private const uint DmFrequency = 0x00400000;
    private const uint DmFixedOutput = 0x20000000;

    public IReadOnlyList<DisplayInfo> ListDisplays()
    {
        var displays = new List<DisplayInfo>();
        // GetMonitorInfo's work-area coordinates must use the same physical pixel
        // space as EnumDisplaySettingsEx, independent of the caller's DPI context.
        using var awareness = new PhysicalPixelScope();
        MonitorEnumProc callback = (nint monitor, nint hdc, ref NativeRect rect, nint data) =>
        {
            var info = MonitorInfo.Create();
            if (!GetMonitorInfoW(monitor, ref info))
                return true;

            var mode = NativeMode.Create();
            var hasMode = EnumDisplaySettingsExW(info.DeviceName, EnumCurrentSettings, ref mode, 0);
            var bounds = hasMode
                ? new PixelRect(mode.PositionX, mode.PositionY, checked((int)mode.Width), checked((int)mode.Height))
                : ToPixelRect(info.Monitor);
            var scale = ReadEffectiveScale(monitor, bounds);

            var primary = (info.Flags & 1) != 0;
            var name = ReadFriendlyName(info.DeviceName);
            var adapter = ReadAdapter(info.DeviceName);
            var label = $"{name} · {info.DeviceName} · {bounds.Width} × {bounds.Height}" +
                (primary ? " · 主屏" : "");
            displays.Add(new DisplayInfo(info.DeviceName, label, bounds, ToPixelRect(info.Work), scale, primary)
            { AdapterName = adapter.Name, AdapterDeviceId = adapter.Id });
            return true;
        };

        if (!EnumDisplayMonitors(0, 0, callback, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法枚举显示器。");

        return displays.OrderByDescending(d => d.IsPrimary)
            .ThenBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<DisplayMode> ListModes(string deviceName)
    {
        RequireConnected(deviceName);
        var modes = EnumerateNativeModes(deviceName).Select(ToMode).ToHashSet();
        // A currently selected custom mode is not always repeated in the driver's
        // enumerated mode list. Keep it available as an explicit restore choice.
        modes.Add(ToMode(ReadCurrentMode(deviceName)));
        return modes.Where(m => m.Width > 0 && m.Height > 0 && m.BitsPerPixel >= 32)
            .OrderBy(m => m.Width).ThenBy(m => m.Height)
            .ThenByDescending(m => m.Frequency).ThenBy(m => m.Orientation).ToArray();
    }

    public DisplaySnapshot Capture(string deviceName)
    {
        RequireConnected(deviceName);
        var mode = ReadCurrentMode(deviceName);
        return new DisplaySnapshot(deviceName, ToMode(mode))
        {
            PositionX = mode.PositionX,
            PositionY = mode.PositionY,
            Identity = ReadIdentity(deviceName)
        };
    }

    /// <summary>
    /// Asks the driver to validate a mode without changing display settings,
    /// creating recovery state, or moving windows. Apply still repeats this
    /// validation because the monitor configuration may change afterward.
    /// </summary>
    public void ValidateModeAvailable(string deviceName, DisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        RequireConnected(deviceName);
        ValidateMode(mode);
        var current = ReadCurrentMode(deviceName);
        var requested = FindNativeMode(deviceName, mode, current);
        SetModeFields(ref requested, mode, current.PositionX, current.PositionY);
        var result = ChangeDisplaySettingsExW(deviceName, ref requested, 0, CdsTest, 0);
        if (result != 0)
            throw ModeException("验证显示模式", result);
    }

    public void Apply(string deviceName, DisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        RequireConnected(deviceName);
        ValidateMode(mode);
        var current = ReadCurrentMode(deviceName);
        var requested = FindNativeMode(deviceName, mode, current);
        // Changing resolution must not also change the selected display's origin.
        SetModeFields(ref requested, mode, current.PositionX, current.PositionY);
        ApplyNative(deviceName, ref requested, "切换显示模式");
    }

    public void Restore(DisplaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Mode);
        RequireConnected(snapshot.DeviceName);
        var currentIdentity = ReadIdentity(snapshot.DeviceName);
        if (string.IsNullOrWhiteSpace(snapshot.Identity) ||
            !string.Equals(snapshot.Identity, currentIdentity, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("显示器身份已变化或还原记录缺少身份信息，未修改显示设置。请重新选择显示器。");
        }

        ValidateMode(snapshot.Mode);
        var current = ReadCurrentMode(snapshot.DeviceName);
        var original = FindNativeMode(snapshot.DeviceName, snapshot.Mode, current);
        SetModeFields(ref original, snapshot.Mode, snapshot.PositionX, snapshot.PositionY);
        ApplyNative(snapshot.DeviceName, ref original, "还原显示模式");
    }

    private void RequireConnected(string deviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        if (!ListDisplays().Any(d => string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"显示器 {deviceName} 当前未连接，未修改显示设置。");
    }

    private static IEnumerable<NativeMode> EnumerateNativeModes(string deviceName)
    {
        // No EDS_RAWMODE: only modes compatible with the connected monitor.
        // EDS_ROTATEDMODE includes driver-reported portrait orientations.
        for (uint index = 0; index < 65536; index++)
        {
            var mode = NativeMode.Create();
            if (!EnumDisplaySettingsExW(deviceName, index, ref mode, EdsRotatedMode))
                yield break;
            if (mode.Width > 0 && mode.Height > 0 && mode.BitsPerPixel >= 32)
                yield return mode;
        }
    }

    private static NativeMode ReadCurrentMode(string deviceName)
    {
        var mode = NativeMode.Create();
        if (!EnumDisplaySettingsExW(deviceName, EnumCurrentSettings, ref mode, 0))
            throw new InvalidOperationException($"无法读取显示器 {deviceName} 的当前模式。");
        return mode;
    }

    private static NativeMode FindNativeMode(string deviceName, DisplayMode wanted, NativeMode current)
    {
        if (ToMode(current) == wanted)
            return current;
        foreach (var native in EnumerateNativeModes(deviceName))
            if (ToMode(native) == wanted)
                return native;

        // A saved custom mode can disappear from enumeration after a temporary
        // switch. Rebuild only documented display fields; CDS_TEST is mandatory.
        return current;
    }

    private static void SetModeFields(ref NativeMode native, DisplayMode mode, int x, int y)
    {
        var supportsFixedOutput = (native.Fields & DmFixedOutput) != 0;
        native.DriverExtra = 0;
        native.Fields = DmPosition | DmDisplayOrientation | DmBitsPerPixel | DmWidth |
            DmHeight | DmDisplayFlags | DmFrequency;
        if (supportsFixedOutput || mode.FixedOutput != 0)
            native.Fields |= DmFixedOutput;
        native.PositionX = x;
        native.PositionY = y;
        native.Orientation = (uint)mode.Orientation;
        native.FixedOutput = mode.FixedOutput;
        native.BitsPerPixel = (uint)mode.BitsPerPixel;
        native.Width = (uint)mode.Width;
        native.Height = (uint)mode.Height;
        native.DisplayFlags = mode.DisplayFlags;
        native.Frequency = (uint)mode.Frequency;
    }

    private static void ApplyNative(string deviceName, ref NativeMode mode, string action)
    {
        var expected = mode;
        if (MatchesRequestedMode(ReadCurrentMode(deviceName), expected))
        {
            // Retrying an already-restored snapshot must not broadcast another
            // display change or rearrange windows a second time.
            VerifyStableMode(deviceName, expected, action);
            return;
        }

        var tested = mode;
        var testResult = ChangeDisplaySettingsExW(deviceName, ref tested, 0, CdsTest, 0);
        if (testResult != 0)
            throw ModeException(action, testResult);
        // Intentionally no CDS_UPDATEREGISTRY, CDS_SET_PRIMARY, or global reset.
        var result = ChangeDisplaySettingsExW(deviceName, ref mode, 0, 0, 0);
        if (result != 0)
            throw ModeException(action, result);
        // Driver acceptance is not sufficient evidence to delete a restore
        // record. The actual mode and desktop position must settle first.
        VerifyStableMode(deviceName, expected, action);
    }

    private static void VerifyStableMode(string deviceName, NativeMode expected, string action)
    {
        var elapsed = Stopwatch.StartNew();
        var consecutiveMatches = 0;
        var observed = "暂时无法读取当前模式";
        // Three matching samples span at least 150 ms. The bounded wait gives
        // driver changes time to settle without leaving the UI blocked for long.
        while (elapsed.ElapsedMilliseconds < 1500)
        {
            var current = NativeMode.Create();
            if (EnumDisplaySettingsExW(deviceName, EnumCurrentSettings, ref current, 0))
            {
                observed = $"当前为 {current.Width} × {current.Height}、{current.Frequency} Hz、方向 {current.Orientation}、位置 ({current.PositionX}, {current.PositionY})";
                consecutiveMatches = MatchesRequestedMode(current, expected) ? consecutiveMatches + 1 : 0;
                if (consecutiveMatches >= 3)
                    return;
            }
            else
            {
                consecutiveMatches = 0;
                observed = "暂时无法读取当前模式";
            }
            Thread.Sleep(75);
        }
        throw new InvalidOperationException($"{action}尚未确认完成：{observed}。恢复记录将保留，请稍后重试还原。");
    }

    private static bool MatchesRequestedMode(NativeMode current, NativeMode expected) =>
        current.Width == expected.Width && current.Height == expected.Height &&
        current.BitsPerPixel == expected.BitsPerPixel && current.Orientation == expected.Orientation &&
        current.PositionX == expected.PositionX && current.PositionY == expected.PositionY &&
        // DEVMODE frequencies 0 and 1 mean the driver's default frequency.
        (expected.Frequency <= 1 || current.Frequency == expected.Frequency) &&
        current.DisplayFlags == expected.DisplayFlags &&
        ((expected.Fields & DmFixedOutput) == 0 || current.FixedOutput == expected.FixedOutput);

    private static InvalidOperationException ModeException(string action, int result)
    {
        var reason = result switch
        {
            1 => "此模式需要重启，无法临时应用",
            -1 => "显示驱动拒绝了此次更改",
            -2 => "显示器或驱动不支持所选分辨率、方向或刷新率",
            -3 => "系统无法完成显示设置更新",
            -4 => "显示模式标志无效",
            -5 => "显示模式参数无效",
            -6 => "当前多屏配置不支持此模式",
            _ => $"系统返回错误代码 {result}"
        };
        return new InvalidOperationException($"{action}失败：{reason}。");
    }

    private static void ValidateMode(DisplayMode mode)
    {
        if (mode.Width <= 0 || mode.Height <= 0 || mode.Frequency < 0 ||
            mode.BitsPerPixel < 32 || mode.Orientation is < 0 or > 3)
            throw new ArgumentException("显示模式参数无效。", nameof(mode));
    }

    private static DisplayMode ToMode(NativeMode mode) => new(
        checked((int)mode.Width), checked((int)mode.Height), checked((int)mode.Frequency),
        checked((int)mode.BitsPerPixel), checked((int)mode.Orientation))
    {
        DisplayFlags = mode.DisplayFlags,
        FixedOutput = mode.FixedOutput
    };

    private static PixelRect ToPixelRect(NativeRect rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private static double ReadEffectiveScale(nint monitor, PixelRect bounds)
    {
        // GetScaleFactorForMonitor can report a resource scale bucket (e.g. 140%)
        // while the actual desktop HWND DPI is 144 (150%). A transient PMv2 HWND
        // reads the same effective DPI that WPF will use on this monitor.
        using var awareness = new PhysicalPixelScope();
        if (awareness.IsActive && bounds.Width > 0 && bounds.Height > 0)
        {
            const uint wsPopup = 0x80000000;
            const uint wsExToolWindow = 0x00000080;
            const uint wsExNoActivate = 0x08000000;
            var probe = CreateWindowExW(wsExToolWindow | wsExNoActivate, "STATIC", "",
                wsPopup, bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2,
                1, 1, 0, 0, 0, 0);
            if (probe != 0)
            {
                try
                {
                    // No WS_VISIBLE and no ShowWindow/activation: this probe
                    // never appears on screen or takes keyboard focus.
                    if (MonitorFromWindow(probe, 0) == monitor)
                    {
                        var dpi = GetDpiForWindow(probe);
                        if (dpi > 0)
                            return dpi / 96.0;
                    }
                }
                finally { DestroyWindow(probe); }
            }
        }
        return GetScaleFactorForMonitor(monitor, out var percent) == 0 && percent > 0
            ? percent / 100.0 : 1.0;
    }

    private static string ReadFriendlyName(string deviceName)
    {
        var monitor = NativeDisplayDevice.Create();
        if (EnumDisplayDevicesW(deviceName, 0, ref monitor, 0) &&
            !string.IsNullOrWhiteSpace(monitor.DeviceString))
            return monitor.DeviceString.Trim();
        return "显示器";
    }

    private static (string Name, string Id) ReadAdapter(string deviceName)
    {
        for (uint index = 0; ; index++)
        {
            var adapter = NativeDisplayDevice.Create();
            if (!EnumDisplayDevicesW(null, index, ref adapter, 0)) return ("", "");
            if (string.Equals(adapter.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                return (adapter.DeviceString.Trim(), adapter.DeviceId.Trim());
        }
    }

    private static string ReadIdentity(string deviceName)
    {
        var identities = new List<string>();
        for (uint i = 0; ; i++)
        {
            var adapter = NativeDisplayDevice.Create();
            if (!EnumDisplayDevicesW(null, i, ref adapter, 0))
                break;
            if (!string.Equals(adapter.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                continue;
            identities.Add($"adapter:{adapter.DeviceId}|{adapter.DeviceKey}");
            break;
        }
        for (uint i = 0; ; i++)
        {
            var monitor = NativeDisplayDevice.Create();
            // EDD_GET_DEVICE_INTERFACE_NAME gives a monitor device-interface path.
            if (!EnumDisplayDevicesW(deviceName, i, ref monitor, 1))
                break;
            if ((monitor.StateFlags & 1) != 0)
                identities.Add($"monitor:{monitor.DeviceId}|{monitor.DeviceKey}");
        }
        return string.Join("\n", identities.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    private sealed class PhysicalPixelScope : IDisposable
    {
        private readonly nint previous = SetThreadDpiAwarenessContext(new nint(-4));
        public bool IsActive => previous != 0;
        public void Dispose()
        {
            if (previous != 0)
                SetThreadDpiAwarenessContext(previous);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public static MonitorInfo Create() => new()
        {
            Size = (uint)Marshal.SizeOf<MonitorInfo>(), DeviceName = ""
        };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeDisplayDevice
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        public static NativeDisplayDevice Create() => new()
        {
            Size = (uint)Marshal.SizeOf<NativeDisplayDevice>(), DeviceName = "",
            DeviceString = "", DeviceId = "", DeviceKey = ""
        };
    }

    // DEVMODEW is 220 bytes on both x86 and x64. The first union's display
    // layout is POINTL + DWORD orientation + DWORD fixed-output (16 bytes).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPixel, Width, Height, DisplayFlags, Frequency;
        public uint ICMMethod, ICMIntent, MediaType, DitherType, Reserved1, Reserved2;
        public uint PanningWidth, PanningHeight;
        public static NativeMode Create() => new()
        {
            Size = (ushort)Marshal.SizeOf<NativeMode>(), DeviceName = "", FormName = ""
        };
    }

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, ref NativeRect rect, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsExW(string deviceName, uint mode, ref NativeMode settings, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(string? deviceName, uint index, ref NativeDisplayDevice device, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int ChangeDisplaySettingsExW(string deviceName, ref NativeMode mode, nint hwnd, uint flags, nint parameter);

    [DllImport("shcore.dll")]
    private static extern int GetScaleFactorForMonitor(nint monitor, out int scale);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extendedStyle, string className, string title,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);
}
