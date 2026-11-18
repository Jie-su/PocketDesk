using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk.UiPreview;

internal static partial class Program
{
    private static PocketDesk.ToolbarWindow PreviewDock(Action<string> record)
    {
        var window = new PocketDesk.ToolbarWindow(() => record("settings"), n => record($"zoom {n}"),
            () => record("finish"), () => record("rotate"));
        window.ExpansionChanged += expanded => record(expanded ? "expand" : "collapse");
        window.ToggleGesturesRequested += () => record("toggle gestures");
        window.ToggleAutoZoomRequested += () => record("toggle auto-zoom");
        window.ToggleScrollButtonsRequested += () => record("toggle scroll buttons");
        window.SetAutoZoomState(true, true);
        window.SetScrollButtonsState(true, true);
        var example = new Button { Content = "示例应用", MinHeight = 44, VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(example, "示例应用");
        example.Click += (_, _) => record("fixture-app");
        window.SetPoolContent(example);
        window.SetZoomEnabled(true);
        window.SetRotationEnabled(true);
        window.SetGestureState(false, true);
        window.SetNotice("预览 · 无实际应用操作");
        return window;
    }
    private static int RunInteractiveToolbarPreview(Application app, Options options, string[] args)
    {
        if (options.All || options.Show) throw new ArgumentException("Use --show-toolbar with a single size.");
        int index = Array.IndexOf(args, "--seconds");
        int seconds = index >= 0 && index + 1 < args.Length ? int.Parse(args[index + 1]) : 180;
        if (seconds is < 10 or > 600) throw new ArgumentOutOfRangeException(nameof(seconds));
        ApplyPalette(app.Resources, options.Theme == "dark");
        var toolbar = PreviewDock(action => Console.WriteLine($"fixture-action: {action}"));
        toolbar.Title = "PocketDesk · 工具条交互预览";
        // Primary monitor work area is expressed in device-independent units.
        var hwnd = new WindowInteropHelper(toolbar).EnsureHandle();
        var transform = HwndSource.FromHwnd(hwnd)!.CompositionTarget!.TransformToDevice;
        var work = Rect.Transform(SystemParameters.WorkArea, transform);
        int w = Math.Min(options.Width, (int)work.Width), h = Math.Min(options.Height, (int)work.Height);
        var v = new PixelRect((int)(work.Right - w), (int)(work.Bottom - h), w, h);
        var placement = ToolbarPlacement.Measure(v, options.Scale * transform.M11, LandscapeToolbarPlacement.Right);
        toolbar.ConfigurePlacement(placement, options.Scale);
        int exitCode = 0;
        if (args.Contains("--verify-toolbar"))
        {
            toolbar.ContentRendered += async (_, _) =>
            {
                var root = (FrameworkElement)toolbar.Content;
                async Task Verify(PixelRect expected) {
                    await toolbar.Dispatcher.InvokeAsync(() => root.UpdateLayout(), DispatcherPriority.ContextIdle);
                    var tl = root.PointToScreen(new Point(0,0));
                    var br = root.PointToScreen(new Point(root.ActualWidth, root.ActualHeight));
                    if (Math.Abs(tl.X - expected.X) > 2 || Math.Abs(tl.Y - expected.Y) > 2 ||
                        Math.Abs((br.X-tl.X) - expected.Width) > 2 || Math.Abs((br.Y-tl.Y) - expected.Height) > 2)
                        throw new InvalidOperationException($"Expected {expected}; visible workspace tool bounds {tl}..{br}");
                }
                try {
                    await Verify(placement.Bar);
                    FindButton(root, "展开 PocketDesk").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Verify(placement.Panel);
                    FindButton(root, "收起").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Verify(placement.Bar);
                    Console.WriteLine($"Verified actual {placement.Edge} strip and expanded panel bounds.");
                } catch (Exception ex) { Console.Error.WriteLine(ex.Message); exitCode = 2; }
                finally { toolbar.CloseForExit(); }
            };
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => { timer.Stop(); toolbar.CloseForExit(); app.Shutdown(); };
        toolbar.Closed += (_, _) => { timer.Stop(); app.Shutdown(); };
        timer.Start();
        app.Run(toolbar);
        return exitCode;
    }
    private static Button FindButton(DependencyObject element, string name)
    {
        if (element is Button button && AutomationProperties.GetName(button) == name) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (Search(VisualTreeHelper.GetChild(element, i)) is { } result) return result;
        throw new InvalidOperationException($"Missing preview action: {name}");
        Button? Search(DependencyObject current) {
            if (current is Button b && AutomationProperties.GetName(b) == name) return b;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
                if (Search(VisualTreeHelper.GetChild(current, i)) is { } found) return found;
            return null;
        }
    }
    private static int RunToolbarPreview(Application app, Options options, string[] args)
    {
        if (options.Show) throw new ArgumentException("Toolbar previews are offscreen only.");
        var results = new List<ToolbarPreviewResult>();
        var sizes = options.All
            ? args.Contains("--frame-supported") ? SupportedFramePresets : args.Contains("--frame-fit") ? FrameFitPresets : args.Contains("--effective") ? EffectivePresets : Presets
            : [(options.Width, options.Height)];
        if (args.Contains("--landscape-only")) sizes = sizes.Where(size => size.Width > size.Height).ToArray();
        foreach (var (w, h) in sizes)
        foreach (var theme in options.All ? new[] { "light", "dark" } : [options.Theme])
        {
            ApplyPalette(app.Resources, theme == "dark");
            var calls = new List<string>();
            var bar = PreviewDock(calls.Add);
            var sample = new List<WindowEntry> { new(101,"测试文档","测试编辑器",12), new(102,"演示页面","测试浏览器",14) };
            var settingsFile = Path.GetTempFileName();
            File.WriteAllText(settingsFile, JsonSerializer.Serialize(new[]{new PoolApp("示例程序",Path.ChangeExtension(settingsFile,"exe"))}));
            try {
                bar.SetPoolContent(new AppPoolPanel(() => bar, _ => throw new InvalidOperationException("Preview must never launch apps."),
                    new AppPoolStore(settingsFile), () => sample.ToArray(), e => calls.Add("window:" + e.Handle)));
                var p = ToolbarPlacement.Measure(new(0,0,w,h), options.Scale, LandscapeToolbarPlacement.Right);
                bar.ConfigurePlacement(p, options.Scale);
                var root = (FrameworkElement)bar.Content;
                void Render(string kind, PixelRect bounds, int buttons) {
                    var size = new Size(bounds.Width, bounds.Height);
                    ArrangePreview(root, size);
                    var file = Path.Combine(options.Output, $"{w}x{h}-{theme}-{kind}.png");
                    RenderVisual(root, (Brush)app.Resources["Rail"], size, file);
                    results.Add(CheckToolbarGeometry(root,size,file,kind,options.Scale,buttons));
                }
                Render("handle",p.Bar,1);
                FindButton(root,"展开 PocketDesk").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Render("pool",p.Panel,9);  // three fixed actions, add/manage/pin/refresh and two running window rows
                var firstWindow = FindButton(root, "切换到 测试文档");
                sample.Reverse();
                firstWindow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!bar.IsExpanded || !calls.SequenceEqual(["expand", "window:101"]))
                    throw new InvalidOperationException("Selecting a live row did not use its identity in the same panel.");
                sample.RemoveAll(w => w.Handle == 101);
                firstWindow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!bar.IsExpanded || calls.Count != 2)
                    throw new InvalidOperationException("A stale row switched the wrong window.");
                FindButton(root,"操作").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Render("operations",p.Panel,11);
                bar.Collapse();
                if (!calls.SequenceEqual(new[]{"expand","window:101","collapse"}) || new WindowInteropHelper(bar).Handle != IntPtr.Zero)
                    throw new InvalidOperationException("Expected only panel state changes; no native window or product action.");
                bar.CloseForExit();
            } finally { File.Delete(settingsFile); }
        }
        var report = Path.Combine(options.Output, "toolbar-report.json");
        File.WriteAllText(report, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var result in results)
            Console.WriteLine($"{result.File}: {result.Warnings.Count} warnings {string.Join("; ", result.Warnings)}");
        Console.WriteLine($"Report: {report}");
        app.Shutdown();
        return results.Any(result => result.Warnings.Count > 0) ? 2 : 0;
    }
    private static void ArrangePreview(FrameworkElement visual, Size size)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            visual.Measure(size);
            visual.Arrange(new Rect(size));
            visual.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
        }
    }

    private static void RenderVisual(FrameworkElement visual, Brush background, Size size, string file)
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen()) context.DrawRectangle(background, null, new Rect(size));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }

    private static ToolbarPreviewResult CheckToolbarGeometry(FrameworkElement root, Size size, string file, string kind, double scale, int expectedButtons)
    {
        var warnings = new List<string>();
        var buttons = new List<ToolbarButtonGeometry>();
        void Visit(DependencyObject node)
        {
            if (node is FrameworkElement hidden && hidden.Visibility != Visibility.Visible) return;
            if (node is Button button)
            {
                var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                // The root wraps the real content scale in all three views.


                var name = AutomationProperties.GetName(button);
                buttons.Add(new(name, bounds.X, bounds.Y, bounds.Width, bounds.Height, button.IsEnabled));
                // Section contents intentionally scroll on short screens; horizontal overflow is never expected.
                bool inScroll = false;
                for (DependencyObject? parent = VisualTreeHelper.GetParent(button); parent != null; parent = VisualTreeHelper.GetParent(parent))
                    if (parent is ScrollViewer) { inScroll = true; break; }
                if (bounds.Left < -1 || bounds.Right > size.Width + 1 || (!inScroll && (bounds.Top < -1 || bounds.Bottom > size.Height + 1)))
                    warnings.Add($"{name} extends outside {kind} viewport.");
                if (kind != "handle" && (bounds.Width + 0.1 < 44 * scale || bounds.Height + 0.1 < 44 * scale))
                    warnings.Add($"{name} has a touch target smaller than 44 logical units.");
            }
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++) Visit(VisualTreeHelper.GetChild(node, index));
        }
        Visit(root);
        if (buttons.Count != expectedButtons) warnings.Add($"Expected {expectedButtons} buttons, found {buttons.Count}.");
        return new(file, kind, size.Width, size.Height, warnings, buttons);
    }

    private sealed record ToolbarButtonGeometry(string Name, double X, double Y, double Width, double Height, bool Enabled);
    private sealed record ToolbarPreviewResult(string File, string Kind, double Width, double Height, IReadOnlyList<string> Warnings, IReadOnlyList<ToolbarButtonGeometry> Buttons);
}
