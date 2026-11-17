using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PocketDesk.Core;

namespace PocketDesk.UiPreview;

internal static partial class Program
{
    private static int RunInteractiveToolbarPreview(Application app, Options options, string[] args)
    {
        if (options.All || options.Show) throw new ArgumentException("Use --show-toolbar with a single size.");
        int secondsIndex = Array.IndexOf(args, "--seconds");
        int seconds = secondsIndex >= 0 && secondsIndex + 1 < args.Length ? int.Parse(args[secondsIndex + 1]) : 180;
        if (seconds is < 10 or > 600) throw new ArgumentOutOfRangeException(nameof(seconds), "Use --seconds 10–600.");
        ApplyPalette(app.Resources, options.Theme == "dark");
        void Record(string action) => Console.WriteLine($"{DateTimeOffset.Now:O} fixture-action: {action} (no product action executed)");
        var toolbar = new PocketDesk.ToolbarWindow(() => Record("applications"), delta => Record($"zoom {delta}"),
            () => Record("finish"), () => Record("rotation"));
        toolbar.Title = "PocketDesk · 工具条交互预览";
        toolbar.SetContentScale(options.Scale);
        toolbar.ConfigureLayout(options.Width / options.Scale, options.Height / options.Scale);
        toolbar.SetNotice("预览 · 操作不影响真实应用");
        toolbar.SetZoomEnabled(true);
        toolbar.SetRotationEnabled(true);
        toolbar.Width = options.Width;
        toolbar.Left = SystemParameters.WorkArea.Left + Math.Max(0, (SystemParameters.WorkArea.Width - toolbar.Width) / 2);
        toolbar.Top = SystemParameters.WorkArea.Bottom - toolbar.Height - 24;
        var menu = typeof(PocketDesk.ToolbarWindow).GetField("menu", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(toolbar) as Popup
            ?? throw new MissingFieldException("ToolbarWindow.menu changed; update the preview adapter.");
        menu.Opened += (_, _) => Record("menu opened");
        menu.Closed += (_, _) => Record("menu closed");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => { timer.Stop(); toolbar.CloseForExit(); app.Shutdown(); };
        toolbar.Closed += (_, _) => { timer.Stop(); app.Shutdown(); };
        timer.Start();
        Console.WriteLine($"Interactive fixture only; automatic close after {seconds} seconds. No MainWindow, UU service, workspace or recovery is constructed.");
        app.Run(toolbar);
        return 0;
    }

    private static int RunToolbarPreview(Application app, Options options, string[] args)
    {
        if (options.Show) throw new ArgumentException("Toolbar previews are offscreen only.");
        var results = new List<ToolbarPreviewResult>();
        var sizes = options.All
            ? args.Contains("--frame-supported") ? SupportedFramePresets : args.Contains("--frame-fit") ? FrameFitPresets : args.Contains("--effective") ? EffectivePresets : Presets
            : [(options.Width, options.Height)];
        if (args.Contains("--landscape-only")) sizes = sizes.Where(size => size.Width > size.Height).ToArray();
        foreach (var (width, height) in sizes)
        foreach (var theme in options.All ? new[] { "light", "dark" } : [options.Theme])
        {
            ApplyPalette(app.Resources, theme == "dark");
            int callbacks = 0, initialized = 0;
            var toolbar = new PocketDesk.ToolbarWindow(() => callbacks++, _ => callbacks++, () => callbacks++, () => callbacks++);
            toolbar.SourceInitialized += (_, _) => initialized++;
            toolbar.SetContentScale(options.Scale);
            toolbar.ConfigureLayout(width / options.Scale, height / options.Scale);
            toolbar.SetNotice("Microsoft Edge · 正在工作");
            toolbar.SetZoomEnabled(true);
            toolbar.SetRotationEnabled(true);
            var layout = ToolbarLayout.Measure(width / options.Scale, height / options.Scale);
            var root = (FrameworkElement)toolbar.Content;
            var barSize = new Size(width, layout.Height * options.Scale);
            ArrangePreview(root, barSize);
            var barFile = Path.Combine(options.Output, $"{width}x{height}-{theme}-toolbar.png");
            RenderVisual(root, toolbar.Background, barSize, barFile);
            results.Add(CheckToolbarGeometry(root, barSize, barFile, "toolbar", options.Scale, expectedButtons: 2));

            // Inspect the real Popup child without opening it or creating its
            // HWND. If its implementation changes, this narrow adapter fails.
            var menu = typeof(PocketDesk.ToolbarWindow).GetField("menu", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(toolbar) as Popup
                ?? throw new MissingFieldException("ToolbarWindow.menu changed; update the preview adapter.");
            if (menu.IsOpen || menu.Child is not FrameworkElement menuRoot)
                throw new InvalidOperationException("Expected an unopened toolbar popup with a visual child.");
            var maximum = new Size(layout.PopupMaxWidth * options.Scale, layout.PopupMaxHeight * options.Scale);
            menuRoot.Measure(maximum);
            var menuSize = new Size(Math.Min(maximum.Width, menuRoot.DesiredSize.Width), Math.Min(maximum.Height, menuRoot.DesiredSize.Height));
            ArrangePreview(menuRoot, menuSize);
            var menuFile = Path.Combine(options.Output, $"{width}x{height}-{theme}-operations.png");
            RenderVisual(menuRoot, (Brush)app.Resources["Bg"], menuSize, menuFile);
            results.Add(CheckToolbarGeometry(menuRoot, menuSize, menuFile, "operations", options.Scale, expectedButtons: 4));

            if (new WindowInteropHelper(toolbar).Handle != IntPtr.Zero || initialized != 0 || menu.IsOpen || callbacks != 0)
                throw new InvalidOperationException("Toolbar preview unexpectedly initialized a native window or invoked an action.");
            toolbar.CloseForExit();
            if (callbacks != 0) throw new InvalidOperationException("Closing the inert toolbar invoked a product action.");
        }
        var report = Path.Combine(options.Output, "toolbar-report.json");
        File.WriteAllText(report, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var result in results)
        {
            Console.WriteLine($"{result.File}: {result.Warnings.Count} geometry warning(s)");
            foreach (var warning in result.Warnings) Console.WriteLine("  " + warning);
        }
        Console.WriteLine($"Report: {report}. No toolbar HWND, popup HWND or product action was created.");
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
                // menuRoot itself owns its layout scale, outside the transform
                // to root; the toolbar has an inner transformed surface.
                if (kind == "operations") bounds.Scale(scale, scale);
                var name = AutomationProperties.GetName(button);
                buttons.Add(new(name, bounds.X, bounds.Y, bounds.Width, bounds.Height, button.IsEnabled));
                if (bounds.Left < -1 || bounds.Top < -1 || bounds.Right > size.Width + 1 || bounds.Bottom > size.Height + 1)
                    warnings.Add($"{name} extends outside {kind} viewport.");
                if (bounds.Width + 0.1 < 44 * scale || bounds.Height + 0.1 < 44 * scale)
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
