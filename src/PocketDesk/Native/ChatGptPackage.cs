using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PocketDesk.Native;

/// <summary>ChatGPT uses app-wide zoom shortcuts. A browser page title, window class
/// or standalone file named ChatGPT.exe does not establish this capability.</summary>
public static class ChatGptPackage
{
    public static bool SupportsZoom(string processName, string? packageFamily) =>
        string.Equals(processName, "ChatGPT", StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(packageFamily, "OpenAI.Codex_2p2nqsd0c76g0", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(packageFamily, "OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0", StringComparison.OrdinalIgnoreCase));

    public static bool SupportsZoom(Process process)
    {
        if (!string.Equals(process.ProcessName, "ChatGPT", StringComparison.OrdinalIgnoreCase)) return false;
        var buffer = new StringBuilder(128);
        uint size = (uint)buffer.Capacity;
        return GetPackageFamilyName(process.SafeHandle, ref size, buffer) == 0 &&
            SupportsZoom(process.ProcessName, buffer.ToString());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackageFamilyName(SafeProcessHandle process, ref uint length, StringBuilder family);
}
