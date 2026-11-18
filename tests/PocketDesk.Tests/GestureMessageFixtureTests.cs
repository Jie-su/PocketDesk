using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static class GestureFixture
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate nint Procedure(nint h, uint message, nint wp, nint lp);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct Class
        {
            public uint Style;
            public nint Proc;
            public int ClassExtra, WindowExtra;
            public nint Instance, Icon, Cursor, Background, MenuName;
            public string Name;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern ushort RegisterClassW(ref Class details);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool UnregisterClassW(string name, nint instance);
        [DllImport("user32.dll")] public static extern nint DefWindowProcW(nint h, uint msg, nint wp, nint lp);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? name);
        [StructLayout(LayoutKind.Sequential)] public struct Pt { public int X, Y; }
        [DllImport("user32.dll")] public static extern nint WindowFromPoint(Pt p);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint CreateWindowEx(int ex, string cls, string title, int style, int x, int y, int w, int h, nint parent, nint menu, nint module, nint param);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(nint hwnd);
        [DllImport("user32.dll")] public static extern nint SendMessageW(nint h, uint msg, nint wp, nint lp);
        [DllImport("user32.dll")] public static extern bool PostMessageW(nint h, uint msg, nint wp, nint lp);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint FindWindowEx(nint parent, nint after, string? cls, string? title);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint h, out uint pid);
        [DllImport("user32.dll")] public static extern int GetMenuItemCount(nint h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetMenuString(nint h, uint id, StringBuilder text, int max, uint flags);
        [DllImport("user32.dll")] public static extern uint GetMenuState(nint h, uint id, uint flags);
        [DllImport("user32.dll")] public static extern uint GetMenuItemID(nint h, int pos);
    }
    private static void TestGestureMessageFixture()
    {
        Test("Gestures select real Edit text, expose enabled Copy on its native menu, and scroll only this fixture", () =>
        {
            nint root = 0, edit = 0, menuWindow = 0;
            nint renderer = 0, instance = GestureFixture.GetModuleHandleW(null);
            int rendererWheel = 0;
            const string rendererClass = "Chrome_RenderWidgetHostHWND";
            GestureFixture.Procedure proc = (h, message, wp, lp) =>
            {
                if (message == 0x20a) Interlocked.Increment(ref rendererWheel);
                return GestureFixture.DefWindowProcW(h, message, wp, lp);
            };
            Dispatcher? dispatcher = null;
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    dispatcher = Dispatcher.CurrentDispatcher;
                    root = GestureFixture.CreateWindowEx(0, "STATIC", "PocketDesk gesture fixture", 0x10cf0000, 80, 80, 430, 220, 0, 0, 0, 0);
                    var text = string.Join("\r\n", Enumerable.Range(1, 100).Select(n => $"line {n}: select and copy this text"));
                    edit = GestureFixture.CreateWindowEx(0, "EDIT", text, 0x50200044, 0, 0, 390, 120, root, 0, 0, 0);
                    var cls = new GestureFixture.Class { Instance = instance, Name = rendererClass, Proc = Marshal.GetFunctionPointerForDelegate(proc) };
                    if (GestureFixture.RegisterClassW(ref cls) != 0)
                        renderer = GestureFixture.CreateWindowEx(0x20, rendererClass, "", 0x50000000, 0, 120, 340, 55, root, 0, instance, 0);
                    if (root == 0 || edit == 0) throw new InvalidOperationException("Could not create fixture controls.");
                    GestureFixture.SetForegroundWindow(root);
                    ready.SetResult(true);
                    Dispatcher.Run();
                }
                catch (Exception ex) { ready.TrySetException(ex); }
                finally
                {
                    if (root != 0) GestureFixture.DestroyWindow(root);
                    if (renderer != 0) GestureFixture.UnregisterClassW(rendererClass, instance);
                    GC.KeepAlive(proc);
                }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
            try
            {
                Require(ready.Task.Wait(TimeSpan.FromSeconds(4)), "Timed out creating the native fixture.");
                var t = Windows.Capture(root);
                WaitUntil(() => GestureWindowMessages.Available(t), "Fixture could not obtain foreground.");
                var a = GestureWindowMessages.Bounds(t, t.CurrentBounds)!;
                var start = new GesturePoint(a.X + 5, a.Y + 8); var end = new GesturePoint(a.X + 72, a.Y + 8);
                Require(GestureWindowMessages.Receiver(t, start) == edit, "Did not hit the native Edit child.");
                Require(!GestureWindowMessages.Dispatch(t, a, root, new(GestureAction.Select, start, end)), "Accepted a different receiver.");
                using var layer = new PocketDesk.MobileGestureLayer(); layer.Sync(t, a, 1);
                void Pump(int ms)
                {
                    var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
                    timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
                }
                Pump(180);
                nint overlay = 0;
                while ((overlay = GestureFixture.FindWindowEx(0, overlay, null, "PocketDesk · 单指手势")) != 0)
                    if (GestureFixture.GetWindowThreadProcessId(overlay, out var process) == GestureFixture.GetCurrentThreadId() && process == Environment.ProcessId) break;
                Require(overlay != 0, "No overlay HWND.");
                void Mouse(uint msg, nint key, int x, int y) { Require(GestureFixture.GetWindowThreadProcessId(overlay, out var pid) == GestureFixture.GetCurrentThreadId() && pid == Environment.ProcessId, "Test surface disappeared."); GestureFixture.PostMessageW(overlay, msg, key, (nint)(x | (y << 16))); Pump(35); }
                Mouse(0x201, 1, 5, 8); Pump(500); Mouse(0x200, 1, 72, 8); Mouse(0x202, 0, 72, 8);
                long selection = 0;
                WaitUntil(() =>
                {
                    selection = (long)GestureFixture.SendMessageW(edit, 0xb0, 0, 0);
                    return (selection & 65535) < ((selection >> 16) & 65535);
                }, "Native Edit did not select any text.");
                Mouse(0x201, 1, 72, 60); Mouse(0x202, 0, 72, 60);
                WaitUntil(() =>
                {
                    for (nint h = 0; (h = GestureFixture.FindWindowEx(0, h, "#32768", null)) != 0;)
                    {
                        if (GestureFixture.GetWindowThreadProcessId(h, out var pid) == t.WindowThreadId && pid == t.ProcessId)
                        { menuWindow = h; return true; }
                    }
                    return false;
                }, "Native Edit did not open its context menu.");
                // Read the fixture's menu only; do not invoke Copy or alter the user's clipboard.
                nint hm = GestureFixture.SendMessageW(menuWindow, 0x1e1, 0, 0);
                bool copy = false;
                for (int i = 0; i < GestureFixture.GetMenuItemCount(hm); i++)
                {
                    var name = new StringBuilder(128); GestureFixture.GetMenuString(hm, (uint)i, name, 128, 0x400);
                    if (GestureFixture.GetMenuItemID(hm, i) == 0x301 || name.ToString().Contains("Copy", StringComparison.OrdinalIgnoreCase) || name.ToString().Contains("复制"))
                        copy = (GestureFixture.GetMenuState(hm, (uint)i, 0x400) & 3) == 0;
                }
                Require(copy, "Native menu had no enabled Copy command.");
                Require(!GestureWindowMessages.Available(t), "Gesture surface would cover an open native menu.");
                GestureFixture.PostMessageW(menuWindow, 0x100, 0x1b, 0); // Escape addressed solely to our own menu window.
                GestureFixture.PostMessageW(edit, 0x1f, 0, 0);
                WaitUntil(() => GestureWindowMessages.Available(t), "Native menu didn't dismiss.");
                menuWindow = 0;
                var before = GestureFixture.SendMessageW(edit, 0xce, 0, 0);
                Pump(850); Mouse(0x201, 1, 10, 90); Mouse(0x200, 1, 10, 50);
                Require(GestureFixture.WindowFromPoint(new() { X = a.X + 10, Y = a.Y + 50 }) == overlay,
                    "The input surface became click-through under the moving finger during scroll.");
                Mouse(0x202, 0, 10, 50);
                WaitUntil(() => GestureFixture.SendMessageW(edit, 0xce, 0, 0) > before, "Native Edit did not scroll.");
                Pump(180); before = GestureFixture.SendMessageW(edit, 0xce, 0, 0);
                Mouse(0x200, 0, 10, 85); Mouse(0x200, 0, 10, 30);
                Require(GestureFixture.SendMessageW(edit, 0xce, 0, 0) == before, "Hover scrolled without a press.");
                Mouse(0x200, 1, 10, 90); Mouse(0x200, 1, 10, 45); Mouse(0x202, 0, 10, 45);
                WaitUntil(() => GestureFixture.SendMessageW(edit, 0xce, 0, 0) > before, "Remote drag entry was ignored.");
                Pump(180); before = GestureFixture.SendMessageW(edit, 0xce, 0, 0);
                layer.Sync(t, a, 1, showScrollButtons: true);
                Mouse(0x201, 1, a.Width - 20, a.Height / 2 + 20); Mouse(0x202, 0, a.Width - 20, a.Height / 2 + 20);
                WaitUntil(() => GestureFixture.SendMessageW(edit, 0xce, 0, 0) > before, "Tap-only scroll key did not scroll the central content.");
                Pump(210);
                Require(renderer != 0 && GestureWindowMessages.Receiver(t, new(a.X + 40, a.Y + 160)) == renderer,
                    "Skipped the ChatGPT-style transparent renderer while the gesture surface was above it.");
                Mouse(0x201, 1, 40, 153); Mouse(0x200, 1, 40, 129); Mouse(0x202, 0, 40, 129);
                WaitUntil(() => Volatile.Read(ref rendererWheel) > 0, "ChatGPT-style renderer did not receive the swipe as a wheel event.");
            }
            finally
            {
                if (menuWindow != 0 && GestureFixture.GetWindowThreadProcessId(menuWindow, out var pid) == GestureFixture.GetWindowThreadProcessId(edit, out var editPid) && pid == Environment.ProcessId && editPid == pid)
                    GestureFixture.PostMessageW(menuWindow, 0x100, 0x1b, 0);
                if (edit != 0) GestureFixture.PostMessageW(edit, 0x1f, 0, 0);
                dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
                thread.Join(1500);
            }
        });
    }
}
