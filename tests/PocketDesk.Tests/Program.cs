using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.Tests;

internal static partial class Program
{
    private static int passed;
    private static int failed;
    private static readonly WindowService Windows = new();
    private static readonly DisplayService Displays = new();
    private static Action? fixtureReset;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--fixture"))
        {
            RunFixture();
            return 0;
        }

        if (args.Contains("--layout-tests"))
        {
            Console.WriteLine("PocketDesk pure layout tests — no windows or display modes are changed.");
            TestToolbarLayout();
            TestPhoneViewportPolicy();
            return Finish();
        }

        if (args.Contains("--geometry-tests"))
        {
            Console.WriteLine("PocketDesk pure screen geometry/recovery tests — no native display writes or windows.");
            TestControllerHandoverPolicy();
            TestControllerDisconnectPolicy();
            TestPhoneScreenGeometry();
            TestFittedDisplayRecovery();
            TestSupportedFramePolicy();
            TestSelectedFallbackRecovery();
            TestAutomaticRecoveryEvidencePolicy();
            TestAlreadyRestoredRecovery();
            TestReconnectionContracts();
            TestSwappedOwnedRotation();
            TestMaximizedRestoreBounds();
            return Finish();
        }

        Console.WriteLine("PocketDesk native integration tests — only our test fixture is moved.");
        Console.WriteLine("No display modes, user applications, or real keyboard input are modified.");

        TestRecoveryStore();
        TestAutoWorkspacePolicy();
        TestPortraitModePolicy();
        TestAutoPortraitAttemptPolicy();
        TestPhoneSessionPolicy();
        TestAutomaticDisplayRecovery();
        TestRotationModePolicy();
        TestPhoneConnectionContracts();
        TestPhoneRotationRecovery();
        TestUuControllerParsing();
        TestDeviceSessionPolicy();
        TestDeviceModePolicy();
        TestDeviceRecoveryOwnership();
        TestToolbarLayout();
        TestPhoneViewportPolicy();
        TestControllerHandoverPolicy();
        TestControllerDisconnectPolicy();
        TestPhoneScreenGeometry();
        TestFittedDisplayRecovery();
        TestSupportedFramePolicy();
        TestSelectedFallbackRecovery();
        TestAutomaticRecoveryEvidencePolicy();
        TestAlreadyRestoredRecovery();
        TestReconnectionContracts();
        TestSwappedOwnedRotation();
        TestMaximizedRestoreBounds();

        IReadOnlyList<DisplayInfo>? displays = null;
        Test("Enumerate attached displays and current mode (read-only)", () =>
        {
            displays = Displays.ListDisplays();
            Require(displays.Count > 0, "No attached desktop display.");
            Require(displays.Any(d => d.IsPrimary), "No primary display was found.");
            foreach (var display in displays)
            {
                Require(display.Bounds.Width > 0 && display.Bounds.Height > 0, "Invalid screen size.");
                Require(display.WorkArea.Width > 0 && display.WorkArea.Height > 0, "Invalid work area.");
                Require(display.Scale > 0, "Invalid DPI scale.");
                var snapshot = Displays.Capture(display.DeviceName);
                Require(snapshot.DeviceName == display.DeviceName, "Captured a different display.");
                Require(snapshot.Mode.Width > 0 && snapshot.Mode.Height > 0, "Invalid current mode.");
                var modes = Displays.ListModes(display.DeviceName);
                Require(modes.Count > 0, "No display modes found.");
                Require(modes.All(m => m.Width > 0 && m.Height > 0), "Invalid enumerated mode.");
            }
        });

        if (displays is null || displays.Count == 0)
            return Finish();

        using var fixture = StartFixture();
        try
        {
            var hwnd = WaitForWindow(fixture);
            var primary = displays.First(d => d.IsPrimary);
            var work = primary.WorkArea;
            var target = new PixelRect(work.X + 36, work.Y + 48,
                Math.Min(570, work.Width - 72), Math.Min(610, work.Height - 96));
            WindowSnapshot? normal = null;

            Test("Window list includes a real external fixture and stable identity", () =>
            {
                var entry = Windows.ListWindows().SingleOrDefault(w => w.Handle == hwnd);
                Require(entry is not null, "Fixture absent from window enumeration.");
                Require(entry!.ProcessId == fixture.Id, "Incorrect fixture PID.");
                normal = Windows.Capture(hwnd);
                Require(Windows.IsSameWindow(normal), "Fresh snapshot identity does not match.");
                Require(normal.ProcessId == fixture.Id && normal.ProcessStartTicks > 0, "Incomplete identity.");
                fixtureReset = () =>
                {
                    if (Windows.IsSameWindow(normal)) Require(Windows.Restore(normal), "Could not reset fixture between tests.");
                };
            });

            Test("Move, resize and restore a normal window", () =>
            {
                var before = Windows.Capture(hwnd);
                var placed = Windows.Place(hwnd, target);
                RequireNear(placed, target, 3, "Placed rectangle");
                RequireNear(Windows.Capture(hwnd).CurrentBounds, placed, 3, "Read-back rectangle");
                Require(Windows.Restore(before), "Restore reported failure.");
                Thread.Sleep(180);
                RequireNear(Windows.Capture(hwnd).CurrentBounds, before.CurrentBounds, 3, "Restored rectangle");
            });

            Test("Persisted snapshot survives JSON round-trip", () =>
            {
                var before = Windows.Capture(hwnd);
                var json = System.Text.Json.JsonSerializer.Serialize(before);
                var loaded = System.Text.Json.JsonSerializer.Deserialize<WindowSnapshot>(json);
                Require(loaded is not null && Windows.IsSameWindow(loaded), "Round-trip lost native identity.");
                Windows.Place(hwnd, target);
                Require(Windows.Restore(loaded!), "Deserialized snapshot could not restore.");
                Thread.Sleep(180);
                RequireNear(Windows.Capture(hwnd).CurrentBounds, before.CurrentBounds, 3, "JSON restored rectangle");
            });

            Test("A new session recovers a fixture from its persisted pre-move record", () =>
            {
                WithIsolatedStore(store =>
                {
                    var before = Windows.Capture(hwnd);
                    store.Save(new RecoveryState { Windows = [before] });
                    Windows.Place(hwnd, target);
                    var recovered = new WorkspaceSession(store);
                    Require(recovered.HasRecovery, "Pending recovery was not loaded.");
                    Require(recovered.RestoreAll().Count == 0, "Restart recovery reported errors.");
                    Require(!recovered.HasRecovery && store.Load().Windows.Count == 0, "Completed recovery record was retained.");
                    RequireNear(Windows.Capture(hwnd).CurrentBounds, before.CurrentBounds, 3, "Restart restored rectangle");
                });
            });

            Test("Failed display recovery preserves window records for a later retry", () =>
            {
                WithIsolatedStore(store =>
                {
                    const string nonexistentDisplay = @"\\.\POCKETDESK_TEST_NO_SUCH_DISPLAY";
                    Require(Displays.ListDisplays().All(d => d.DeviceName != nonexistentDisplay), "Test display unexpectedly exists.");
                    var before = Windows.Capture(hwnd);
                    var fakeDisplay = Displays.Capture(primary.DeviceName) with { DeviceName = nonexistentDisplay };
                    store.Save(new RecoveryState { Windows = [before], Display = fakeDisplay });
                    Windows.Place(hwnd, target);
                    var recovery = new WorkspaceSession(store);
                    Require(recovery.RestoreAll().Count > 0, "Missing display restoration was not reported.");
                    var retained = store.Load();
                    Require(retained.Display is not null, "Failed display record was discarded.");
                    Require(retained.Windows.Any(w => w.Handle == hwnd), "Window record was discarded while display restoration is still pending.");
                    RequireNear(Windows.Capture(hwnd).CurrentBounds, before.CurrentBounds, 3, "Partial recovery rectangle");
                });
            });

            Test("Uncertain zoom restores placement but retains a record until manual acknowledgment", () =>
            {
                WithIsolatedStore(store =>
                {
                    var before = Windows.Capture(hwnd);
                    var uncertain = before with { ZoomSteps = 2, ZoomUncertain = true };
                    store.Save(new RecoveryState { Windows = [uncertain] });
                    Windows.Place(hwnd, target);
                    var recovery = new WorkspaceSession(store);
                    Require(recovery.HasUncertainZoom, "Uncertain zoom flag was not loaded.");
                    Require(recovery.RestoreAll().Count > 0, "Uncertain zoom was silently treated as recovered.");
                    RequireNear(Windows.Capture(hwnd).CurrentBounds, before.CurrentBounds, 3, "Uncertain zoom placement recovery");
                    Require(store.Load().Windows.Single().ZoomUncertain, "Uncertain zoom evidence was discarded.");
                    recovery.AcknowledgeManualZoomRecovery();
                    Require(!recovery.HasUncertainZoom, "Manual recovery acknowledgement was not applied.");
                    Require(recovery.RestoreAll().Count == 0 && !recovery.HasRecovery, "Acknowledged recovery could not complete.");
                });
            });

            Test("Stale PID/start-time snapshots cannot move a live window", () =>
            {
                var original = Windows.Capture(hwnd);
                var wrongPid = original with { ProcessId = fixture.Id + 100000 };
                var wrongStart = original with { ProcessStartTicks = original.ProcessStartTicks + 1 };
                Require(!Windows.IsSameWindow(wrongPid), "Mismatched PID was accepted.");
                Require(!Windows.IsSameWindow(wrongStart), "Mismatched process start time was accepted.");
                Require(!Windows.Restore(wrongPid), "Restore accepted mismatched PID.");
                Require(!Windows.Restore(wrongStart), "Restore accepted mismatched start time.");
                RequireNear(Windows.Capture(hwnd).CurrentBounds, original.CurrentBounds, 3, "Unmodified fixture");
            });

            Test("Invalid geometry and corrupt show-state are rejected before mutation", () =>
            {
                var before = Windows.Capture(hwnd);
                ExpectThrows(() => Windows.Place(hwnd, new PixelRect(0, 0, 0, 400)), "Zero-width placement was accepted.");
                ExpectThrows(() => Windows.Place(hwnd, new PixelRect(int.MaxValue, 0, 400, 400)), "Overflowing placement was accepted.");
                Require(!Windows.Restore(before with { ShowCommand = 0 }), "Corrupt show-state could hide the fixture.");
                RequireNear(Windows.Capture(hwnd).CurrentBounds, before.CurrentBounds, 3, "Invalid request left fixture unchanged");
            });

            Test("A minimized window is listed, fitted, then restored minimized", () =>
            {
                ShowWindow((nint)hwnd, 6);
                WaitUntil(() => IsIconic((nint)hwnd), "Window did not minimize.");
                Require(Windows.ListWindows().Any(w => w.Handle == hwnd), "Minimized fixture disappeared from list.");
                var before = Windows.Capture(hwnd);
                Windows.Place(hwnd, target);
                Require(!IsIconic((nint)hwnd), "Place left window minimized.");
                Require(Windows.Restore(before), "Minimized restoration failed.");
                WaitUntil(() => IsIconic((nint)hwnd), "Minimized state was not restored.");
                ShowWindow((nint)hwnd, 9);
                WaitUntil(() => !IsIconic((nint)hwnd), "Fixture remained minimized after test.");
            });

            Test("A maximized window is fitted, then restored maximized", () =>
            {
                ShowWindow((nint)hwnd, 3);
                WaitUntil(() => IsZoomed((nint)hwnd), "Window did not maximize.");
                var before = Windows.Capture(hwnd);
                Windows.Place(hwnd, target);
                Require(!IsZoomed((nint)hwnd), "Place left window maximized.");
                Require(Windows.Restore(before), "Maximized restoration failed.");
                WaitUntil(() => IsZoomed((nint)hwnd), "Maximized state was not restored.");
                ShowWindow((nint)hwnd, 9);
                Thread.Sleep(150);
                if (normal is not null) Require(Windows.Restore(normal), "Original normal state restore failed.");
            });

            Test("Repeated restores already at target never override the next maximize", () =>
            {
                var saved = Windows.Capture(hwnd);
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    Require(Windows.Restore(saved), "Could not prepare the original normal state.");
                    Require(Windows.Restore(saved) && Windows.Restore(saved), "Idempotent restore reported failure.");
                    ShowWindow((nint)hwnd, 3);
                    WaitUntil(() => IsZoomed((nint)hwnd), "A queued restore prevented the next maximize.");
                    for (int poll = 0; poll < 10; poll++)
                    {
                        Thread.Sleep(20);
                        Require(IsZoomed((nint)hwnd), "A delayed normal-state restore overrode the next maximize.");
                    }
                    ShowWindow((nint)hwnd, 9);
                    WaitUntil(() => !IsZoomed((nint)hwnd), "Could not reset maximized fixture.");
                }
            });

            Test("A minimized maximized window keeps its maximize-on-restore state", () =>
            {
                ShowWindow((nint)hwnd, 3);
                WaitUntil(() => IsZoomed((nint)hwnd), "Fixture did not maximize.");
                ShowWindow((nint)hwnd, 6);
                WaitUntil(() => IsIconic((nint)hwnd), "Fixture did not minimize.");
                var before = Windows.Capture(hwnd);
                Windows.Place(hwnd, target);
                Require(Windows.Restore(before), "Minimized-maximized restoration failed.");
                WaitUntil(() => IsIconic((nint)hwnd), "Minimized-maximized fixture was not minimized again.");
                ShowWindow((nint)hwnd, 9);
                WaitUntil(() => IsZoomed((nint)hwnd), "Restore-to-maximized flag was lost.");
                ShowWindow((nint)hwnd, 9);
                if (normal is not null) Require(Windows.Restore(normal), "Could not reset fixture after maximize test.");
            });

            Test("Zoom whitelist rejects an unsupported application without typing", () =>
            {
                var snapshot = Windows.Capture(hwnd);
                Require(!Windows.CanZoom(snapshot), "Test fixture should not be a supported editor.");
            });

            Test("Restore relocates an off-screen record into a visible work area", () =>
            {
                var before = Windows.Capture(hwnd);
                var offScreen = before with
                {
                    NormalBounds = new PixelRect(100000, 100000, 420, 420),
                    CurrentBounds = new PixelRect(100000, 100000, 420, 420)
                };
                try
                {
                    Require(Windows.Restore(offScreen), "Off-screen fallback restore failed.");
                    var actual = Windows.Capture(hwnd).CurrentBounds;
                    Require(displays.Any(d => actual.X >= d.WorkArea.X && actual.Y >= d.WorkArea.Y
                        && actual.X + actual.Width <= d.WorkArea.X + d.WorkArea.Width
                        && actual.Y + actual.Height <= d.WorkArea.Y + d.WorkArea.Height), "Fallback window is still outside the visible work area.");
                }
                finally { Require(Windows.Restore(before), "Could not restore original fixture after off-screen test."); }
            });

            Test("Partially off-screen minimized bounds are normalized without losing minimized state", () =>
            {
                ShowWindow((nint)hwnd, 6);
                WaitUntil(() => IsIconic((nint)hwnd), "Fixture did not minimize.");
                var saved = Windows.Capture(hwnd);
                var partial = saved with { NormalBounds = new PixelRect(work.X + work.Width - 240,
                    work.Y + work.Height - 180, Math.Min(500, work.Width - 40), Math.Min(420, work.Height - 40)) };
                Windows.Place(hwnd, target);
                Require(Windows.Restore(partial), Windows.LastError ?? "Partially off-screen minimized restore failed.");
                Require(Windows.LastRestoreAdjusted, "Adjusted bounds were not reported.");
                Require(IsIconic((nint)hwnd), "Normalized minimized window became visible.");
                RequireNear(Windows.Capture(hwnd).NormalBounds,
                    WindowService.NormalizeBoundsToWorkArea(partial.NormalBounds, work), 4, "Normalized minimized bounds");
            });

            Test("Oversized persisted bounds fit the current work area", () =>
            {
                var saved = Windows.Capture(hwnd);
                var large = saved with { NormalBounds = new PixelRect(work.X - 300, work.Y - 200,
                    work.Width + 600, work.Height + 400) };
                Require(Windows.Restore(large), Windows.LastError ?? "Oversized restore failed.");
                Require(Windows.LastRestoreAdjusted, "Oversized normalization was not reported.");
                RequireNear(Windows.Capture(hwnd).CurrentBounds, work, 4, "Oversized normalized bounds");
            });

            Test("A contained original rectangle restores exactly without adjustment", () =>
            {
                Windows.Place(hwnd, target);
                var saved = Windows.Capture(hwnd);
                var different = target with { X = target.X + 20, Y = target.Y + 20 };
                Windows.Place(hwnd, different);
                Require(Windows.Restore(saved), Windows.LastError ?? "Contained restore failed.");
                Require(!Windows.LastRestoreAdjusted, "Contained original geometry was needlessly normalized.");
                RequireNear(Windows.Capture(hwnd).CurrentBounds, target, 3, "Contained original bounds");
            });

            TestChangedFixtureMinimum(hwnd, work);

            Test("Closed windows cannot be confused with a persisted snapshot", () =>
            {
                var before = Windows.Capture(hwnd);
                fixture.CloseMainWindow();
                Require(fixture.WaitForExit(5000), "Fixture failed to close.");
                Require(!Windows.IsSameWindow(before), "Closed fixture identity still matched.");
                Require(!Windows.Restore(before), "Restore accepted a closed window.");
            });
        }
        finally
        {
            fixtureReset = null;
            if (!fixture.HasExited)
            {
                fixture.CloseMainWindow();
                if (!fixture.WaitForExit(3000)) fixture.Kill();
            }
        }
        return Finish();
    }

    private static void Test(string name, Action action)
    {
        try
        {
            action();
            passed++;
            Console.WriteLine($"PASS  {name}");
        }
        catch (Exception error)
        {
            failed++;
            Console.WriteLine($"FAIL  {name}\n      {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            try { fixtureReset?.Invoke(); }
            catch (Exception error)
            {
                failed++;
                Console.WriteLine($"FAIL  Fixture reset after {name}\n      {error.Message}");
            }
        }
    }

    private static int Finish()
    {
        Console.WriteLine($"Result: {passed} passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static void TestRecoveryStore()
    {
        var directory = System.IO.Path.Combine(AppContext.BaseDirectory, "recovery-test-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "recovery.json");
        var store = new RecoveryStore(path);
        try
        {
            Test("Recovery store rejects corrupt/unsupported records without deleting evidence", () =>
            {
                const string corrupt = "{ interrupted write";
                System.IO.File.WriteAllText(path, corrupt);
                ExpectThrows(() => store.Load(), "Corrupt recovery record was accepted.");
                Require(System.IO.File.ReadAllText(path) == corrupt, "Corrupt record was silently replaced.");
                const string future = "{\"Version\":99,\"Windows\":[]}";
                System.IO.File.WriteAllText(path, future);
                ExpectThrows(() => store.Load(), "Unsupported record version was accepted.");
                Require(System.IO.File.ReadAllText(path) == future, "Unsupported record was silently replaced.");
            });
            Test("Recovery store persists and reloads a valid session in an isolated path", () =>
            {
                var state = new RecoveryState();
                store.Save(state);
                var restored = store.Load();
                Require(restored.Version == state.Version && restored.Windows.Count == 0 && restored.Display is null,
                    "Empty session did not round-trip.");
                Require(!System.IO.File.Exists(path + ".tmp"), "Temporary recovery file was not committed.");
            });
            Test("Recovery store rejects a structurally invalid null window list", () =>
            {
                System.IO.File.WriteAllText(path, "{\"Version\":1,\"Windows\":null}");
                ExpectThrows(() => store.Load(), "Null window list was accepted.");
                System.IO.File.WriteAllText(path, "{\"Version\":1,\"Windows\":[null]}");
                ExpectThrows(() => store.Load(), "Null window entry was accepted.");
            });
        }
        finally
        {
            // These two exact test-owned paths are the only files created here.
            System.IO.File.Delete(path);
            System.IO.File.Delete(path + ".tmp");
            System.IO.Directory.Delete(directory, false);
        }
    }

    private static void ExpectThrows(Action action, string message)
    {
        try { action(); }
        catch { return; }
        throw new InvalidOperationException(message);
    }

    private static void WithIsolatedStore(Action<RecoveryStore> action)
    {
        var directory = System.IO.Path.Combine(AppContext.BaseDirectory, "session-test-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "recovery.json");
        try { action(new RecoveryStore(path)); }
        finally
        {
            System.IO.File.Delete(path);
            System.IO.File.Delete(path + ".tmp");
            System.IO.Directory.Delete(directory, false);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RequireNear(PixelRect actual, PixelRect expected, int tolerance, string label)
    {
        Require(Math.Abs(actual.X - expected.X) <= tolerance
            && Math.Abs(actual.Y - expected.Y) <= tolerance
            && Math.Abs(actual.Width - expected.Width) <= tolerance
            && Math.Abs(actual.Height - expected.Height) <= tolerance,
            $"{label}: expected {expected}, actual {actual}.");
    }

    private static void WaitUntil(Func<bool> predicate, string message)
    {
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (predicate()) return;
            Thread.Sleep(50);
        }
        throw new TimeoutException(message);
    }

    private static Process StartFixture()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No executable path.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--fixture");
        return Process.Start(start) ?? throw new InvalidOperationException("Fixture process failed to start.");
    }

    private static long WaitForWindow(Process fixture)
    {
        long hwnd = 0;
        WaitUntil(() =>
        {
            if (fixture.HasExited) throw new InvalidOperationException($"Fixture exited with {fixture.ExitCode}.");
            fixture.Refresh();
            hwnd = fixture.MainWindowHandle.ToInt64();
            return hwnd != 0;
        }, "Fixture did not create a window.");
        Thread.Sleep(200);
        return hwnd;
    }

    private static void RunFixture()
    {
        var app = new Application();
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new StackPanel { Margin = new Thickness(20, 18, 20, 14) };
        header.Children.Add(new TextBlock { Text = "手机联调测试", FontSize = 26,
            Foreground = Brushes.White, FontWeight = FontWeights.SemiBold });
        var dimensions = new TextBlock { FontSize = 13, Foreground = Brushes.LightGray,
            Margin = new Thickness(0, 8, 0, 10), TextWrapping = TextWrapping.Wrap };
        header.Children.Add(dimensions);
        int tapCount = 0;
        var counter = new TextBlock { Text = "点按次数：0", FontSize = 28,
            Foreground = new SolidColorBrush(Color.FromRgb(100, 235, 194)), FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(counter);
        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tap = new Button { Content = "点按测试 +1", MinHeight = 56, FontSize = 20,
            FontWeight = FontWeights.SemiBold, Padding = new Thickness(8), Margin = new Thickness(0, 0, 10, 0),
            Background = new SolidColorBrush(Color.FromRgb(100, 235, 194)), Foreground = Brushes.Black };
        tap.Click += (_, _) => counter.Text = $"点按次数：{++tapCount}";
        var reset = new Button { Content = "清零", MinHeight = 56, FontSize = 18, Padding = new Thickness(8) };
        reset.Click += (_, _) => { tapCount = 0; counter.Text = "点按次数：0"; };
        Grid.SetColumn(reset, 1);
        buttons.Children.Add(tap);
        buttons.Children.Add(reset);
        header.Children.Add(buttons);
        layout.Children.Add(header);
        var reading = new StackPanel();
        reading.Children.Add(new TextBlock { Text = "往下滚动，检查竖屏阅读", FontSize = 20,
            Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14),
            TextWrapping = TextWrapping.Wrap });
        foreach (var paragraph in new[]
        {
            "这是专门用于测试的窗口，没有真实用户数据。点击上面的绿色按钮，计数应每次增加一；点击“清零”，计数应立即回到零。",
            "把手机竖起来时，文字应跟随窗口宽度自动换行，不需要左右来回拖动。窗口底部应保留手机工作台的返回按钮。",
            "再把手机横过来，观察窗口尺寸是否跟随变化，以及内容是否仍然清楚、完整。旋转屏幕后可以继续点按按钮，检查手指点下的位置是否准确。",
            "这段内容用于检查滚动体验。请使用 UU 远程当前提供的滚动方式往下阅读；伴侣暂时不会改变你的触控手势。",
            "从工作台返回应用列表，再次选择这个窗口。计数应该仍然保留，窗口会重新铺满当前工作区。",
            "测试结束后，点击工作台的结束按钮，查看此窗口是否回到开始测试时的位置和大小。你也可以直接关闭本测试窗口。",
            "你已经到达测试文章的末尾。这里没有链接、账号或提交操作，所有点按计数只保存在当前测试窗口里。"
        })
            reading.Children.Add(new TextBlock { Text = paragraph, FontSize = 18, LineHeight = 29,
                Foreground = new SolidColorBrush(Color.FromRgb(216, 225, 237)), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 20) });
        var scroll = new ScrollViewer { Content = reading, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(20, 0, 20, 18) };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        var window = new Window
        {
            Title = "PocketDesk Test Fixture",
            Width = Math.Min(620, SystemParameters.WorkArea.Width - 60),
            Height = Math.Min(430, SystemParameters.WorkArea.Height - 60), MinWidth = 240, MinHeight = 180,
            Left = SystemParameters.WorkArea.Left + 30, Top = SystemParameters.WorkArea.Top + 30,
            Background = new SolidColorBrush(Color.FromRgb(21, 30, 44)), Content = layout
        };
        void UpdateDimensions()
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            dimensions.Text = $"窗口尺寸：{window.ActualWidth * dpi.DpiScaleX:0} × {window.ActualHeight * dpi.DpiScaleY:0} 像素\n显示缩放：{dpi.DpiScaleX:P0} · 专用测试窗口";
        }
        window.SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(
                (nint handle, int message, nint width, nint height, ref bool handled) =>
                {
                    if (message != FixtureSetMinimumMessage) return nint.Zero;
                    var dpi = VisualTreeHelper.GetDpi(window);
                    // This private test-only message affects this fixture process alone.
                    window.MinWidth = width == 0 ? 240 : width.ToInt32() / dpi.DpiScaleX;
                    window.MinHeight = height == 0 ? 180 : height.ToInt32() / dpi.DpiScaleY;
                    handled = true;
                    return new nint(1);
                });
        };
        window.Loaded += (_, _) => UpdateDimensions();
        window.SizeChanged += (_, _) => UpdateDimensions();
        // Do not leave an orphan fixture behind if a test runner terminates unexpectedly.
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        timeout.Tick += (_, _) => app.Shutdown();
        timeout.Start();
        app.Run(window);
    }

    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint hwnd);
}
