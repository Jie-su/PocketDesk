using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using PocketDesk.Core;

namespace PocketDesk.UiPreview;

/// <summary>
/// Loads product markup into a plain WPF Window. Product App/MainWindow are
/// never constructed: no session, recovery, native service, timer or hook runs.
/// Only the pure layout helper and theme palette are shared with the product.
/// </summary>
internal static partial class Program
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly (int Width, int Height)[] Presets = [(402, 874), (874, 402), (834, 1194), (1194, 834), (960, 720)];
    private static readonly (int Width, int Height)[] EffectivePresets = [(402, 692), (810, 402), (834, 1012), (1130, 834)];
    // Frame-fit examples use a 72-physical-pixel Windows taskbar at 150% DPI.
    // They are fixture sizes, not a measurement of the user's current work area.
    private static readonly (int Width, int Height)[] FrameFitPresets = [(402, 668), (746, 378), (834, 976), (1066, 798)];
    private static readonly (int Width, int Height)[] SupportedFramePresets = [(402, 619), (643, 378)];
    private static readonly HashSet<string> EventNames = typeof(Window).Assembly.GetExportedTypes()
        .Concat(typeof(UIElement).Assembly.GetExportedTypes())
        .SelectMany(type => type.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        .Select(info => info.Name).ToHashSet(StringComparer.Ordinal);

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            if (options.All && !args.Contains("--toolbar") && !args.Contains("--show-toolbar"))
                return RunIsolatedMatrix(options, args);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            LoadApplicationResources(app, options.Root);
            Directory.CreateDirectory(options.Output);
            if (args.Contains("--show-toolbar")) return RunInteractiveToolbarPreview(app, options, args);
            if (args.Contains("--toolbar")) return RunToolbarPreview(app, options, args);
            var results = new List<PreviewResult>();
            var sizes = options.All
                ? args.Contains("--frame-supported") ? SupportedFramePresets : args.Contains("--frame-fit") ? FrameFitPresets : args.Contains("--effective") ? EffectivePresets : Presets
                : [(options.Width, options.Height)];
            if (args.Contains("--landscape-only")) sizes = sizes.Where(size => size.Width > size.Height).ToArray();
            foreach (var (width, height) in sizes)
            foreach (var theme in options.All ? new[] { "light", "dark" } : [options.Theme])
            foreach (var page in options.All ? new[] { "library", "settings" } : [options.Page])
            {
                ApplyPalette(app.Resources, theme == "dark");
                var window = LoadWindow(options.Root);
                FillFixture(window, page, width, height, theme);
                if (options.State.Contains("confirm", StringComparison.Ordinal)) Find<FrameworkElement>(window, "PreviewPanel").Visibility = Visibility.Visible;
                if (options.State.Contains("recovery", StringComparison.Ordinal))
                {
                    Find<FrameworkElement>(window, "ManualZoomButton").Visibility = Visibility.Visible;
                    SetText(window, "StatusText", "某个应用暂未响应，还原记录已保留。请关闭提示或在应用响应后重试。不会覆盖设备当前的分辨率。");
                }
                var result = Render(window, width, height, theme, page, options.Scroll, options.Scale, options.State, options.Output);
                results.Add(result);
                Console.WriteLine($"{result.File}: {result.Warnings.Count} geometry warning(s)");
                foreach (var warning in result.Warnings) Console.WriteLine("  " + warning);
                if (options.Show)
                {
                    window.Title = $"PocketDesk layout preview — {width}×{height} {theme} {page}";
                    window.WindowStyle = WindowStyle.SingleBorderWindow;
                    window.ResizeMode = ResizeMode.NoResize;
                    window.SizeToContent = SizeToContent.WidthAndHeight;
                    ((FrameworkElement)window.Content).Width = width;
                    ((FrameworkElement)window.Content).Height = height;
                    window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) window.Close(); };
                    window.ShowDialog();
                }
                else window.Close();
            }
            var report = Path.Combine(options.Output, "layout-report.json");
            File.WriteAllText(report, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Report: {report}");
            app.Shutdown();
            return results.Any(result => result.Warnings.Count > 0) ? 2 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int RunIsolatedMatrix(Options options, string[] args)
    {
        // WPF can reuse deferred render data across detached Window roots in
        // one Application. Run each case in its own preview process so saved
        // images agree with independently measured geometry. Children never
        // receive --all or --show and cannot enter product code-behind.
        var sizes = args.Contains("--frame-supported") ? SupportedFramePresets
            : args.Contains("--frame-fit") ? FrameFitPresets
            : args.Contains("--effective") ? EffectivePresets : Presets;
        if (args.Contains("--landscape-only")) sizes = sizes.Where(size => size.Width > size.Height).ToArray();
        Directory.CreateDirectory(options.Output);
        var report = Path.Combine(options.Output, "layout-report.json");
        var results = new List<PreviewResult>();
        foreach (var (width, height) in sizes)
        foreach (var theme in new[] { "light", "dark" })
        foreach (var page in new[] { "library", "settings" })
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the preview executable.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(Program).Assembly.Location);
            foreach (var argument in new[] { "--root", options.Root, "--output", options.Output,
                "--width", width.ToString(CultureInfo.InvariantCulture), "--height", height.ToString(CultureInfo.InvariantCulture),
                "--theme", theme, "--page", page, "--scale", options.Scale.ToString(CultureInfo.InvariantCulture),
                "--scroll", options.Scroll.ToString(CultureInfo.InvariantCulture), "--state", options.State })
                start.ArgumentList.Add(argument);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the isolated preview.");
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            child.WaitForExit();
            Task.WaitAll(stdout, stderr);
            if (child.ExitCode is not (0 or 2))
                throw new InvalidOperationException($"Preview {width}×{height} {theme} {page} failed: {stderr.Result}");
            var rendered = JsonSerializer.Deserialize<List<PreviewResult>>(File.ReadAllText(report))
                ?? throw new InvalidDataException("Isolated preview did not return a report.");
            results.AddRange(rendered);
            foreach (var result in rendered)
            {
                Console.WriteLine($"{result.File}: {result.Warnings.Count} geometry warning(s)");
                foreach (var warning in result.Warnings) Console.WriteLine("  " + warning);
            }
        }
        File.WriteAllText(report, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Report: {report}. Each image was rendered in an isolated, offscreen preview process.");
        return results.Any(result => result.Warnings.Count > 0) ? 2 : 0;
    }

    private static void LoadApplicationResources(Application app, string root)
    {
        var document = XDocument.Load(Path.Combine(root, "src", "PocketDesk", "App.xaml"));
        var resources = document.Root?.Element(Presentation + "Application.Resources")
            ?? throw new InvalidDataException("App.xaml must declare Application.Resources.");
        var dictionary = new XElement(Presentation + "ResourceDictionary",
            new XAttribute("xmlns", Presentation.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", Xaml.NamespaceName), resources.Elements().Select(element => new XElement(element)));
        Sanitize(dictionary);
        using var reader = dictionary.CreateReader();
        app.Resources = (ResourceDictionary)XamlReader.Load(reader);
    }

    private static Window LoadWindow(string root)
    {
        var document = XDocument.Load(Path.Combine(root, "src", "PocketDesk", "MainWindow.xaml"));
        var markup = document.Root ?? throw new InvalidDataException("MainWindow.xaml is empty.");
        if (markup.Name != Presentation + "Window") throw new InvalidDataException("Expected a plain WPF Window root.");
        Sanitize(markup);
        if (markup.Attribute("Icon") is { } icon)
            icon.Value = Path.Combine(root, "src", "PocketDesk", icon.Value.Replace('/', Path.DirectorySeparatorChar));
        var context = new ParserContext { BaseUri = new Uri(Path.Combine(root, "src", "PocketDesk") + Path.DirectorySeparatorChar) };
        var window = (Window)XamlReader.Parse(markup.ToString(), context);
        if (window.GetType() != typeof(Window)) throw new InvalidDataException("Preview must not instantiate a product Window subclass.");
        window.MinWidth = window.MinHeight = 0;
        window.MaxWidth = window.MaxHeight = double.PositiveInfinity;
        window.ShowInTaskbar = false;
        return window;
    }

    private static void Sanitize(XElement root)
    {
        foreach (var element in root.DescendantsAndSelf().ToArray())
        {
            // Fail closed if future markup introduces custom runtime objects.
            // Supporting a new namespace requires an explicit preview review.
            if (element.Name.Namespace != Presentation)
                throw new InvalidDataException($"Preview does not allow element namespace {element.Name.NamespaceName}.");
            if (element.Name.LocalName is "EventSetter" or "ObjectDataProvider")
                throw new InvalidDataException($"Preview does not allow {element.Name.LocalName}.");
            foreach (var attribute in element.Attributes().ToArray())
            {
                if (attribute.IsNamespaceDeclaration && attribute.Value != Presentation.NamespaceName && attribute.Value != Xaml.NamespaceName)
                    throw new InvalidDataException($"Preview does not allow namespace declaration {attribute.Value}.");
                if (attribute.Name == Xaml + "Class" || attribute.Name == Xaml + "ClassModifier" || attribute.Name == Xaml + "ConnectionId")
                    attribute.Remove();
                else if (!attribute.IsNamespaceDeclaration && EventNames.Contains(attribute.Name.LocalName.Split('.').Last()))
                    attribute.Remove();
            }
        }
    }

    private static void ApplyPalette(ResourceDictionary resources, bool dark)
    {
        // Read data only; do not construct ThemeService (which subscribes to OS
        // events and reads persistent preferences) or change the user's theme.
        var field = typeof(ThemeService).GetField("Palette", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("ThemeService.Palette changed; update the preview's read-only palette adapter.");
        if (field.GetValue(null) is not ValueTuple<string, string, string>[] palette)
            throw new InvalidDataException("Unexpected product palette type.");
        foreach (var (name, light, night) in palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? night : light));
            brush.Freeze();
            resources[name] = brush;
        }
    }

    private static void FillFixture(Window window, string page, int width, int height, string theme)
    {
        Find<ItemsControl>(window, "WindowList").ItemsSource = new[]
        {
            new PreviewGroup("Microsoft Edge", "浏览器", [new("PocketDesk · 响应式布局预览"), new("较长的窗口标题：跨设备工作台设计与需求整理")]),
            new PreviewGroup("Visual Studio Code", "开发工具", [new("MainWindow.xaml — PocketDesk")]),
            new PreviewGroup("WPS Office", "办公", [new("本周项目进度与需求记录.docx"), new("设备适配验收表.xlsx")]),
            new PreviewGroup("Windows 终端", "终端", [new("PowerShell — 构建与测试")]),
            new PreviewGroup("文件资源管理器", "文件", [new("项目文件"), new("下载")]),
            new PreviewGroup("应用名称很长时应省略而不撑破卡片", "边界示例", [new("这是用于检查文字省略和点击区域布局的虚拟窗口标题")])
        };
        Find<Expander>(window, "SettingsExpander").IsExpanded = page == "settings";
        SetText(window, "WindowCount", "6 个 App · 9 个窗口");
        SetText(window, "ConnectionTitle", "iPad Pro · 已连接");
        SetText(window, "PhoneProfileText", "iPad Pro (11 英寸, 第 3 代)");
        SetText(window, "DeviceDetectionText", "已识别当前控制设备 · iPad Pro");
        SetText(window, "DisplayDetail", $"{width * 2} × {height * 2} 像素 · 缩放 150%");
        SetText(window, "StatusText", "布局预览 · 示例数据，不连接 UU");
        SetText(window, "ThemeHint", theme == "dark" ? "深色外观" : "浅色外观");
        if (window.FindName("RotateButton") is Button rotate) rotate.IsEnabled = true;
        if (window.FindName("EmptyText") is FrameworkElement empty) empty.Visibility = Visibility.Collapsed;
        foreach (var name in new[] { "DisplayPicker", "TargetPicker", "ModePicker" })
            if (window.FindName(name) is ComboBox combo)
            {
                combo.ItemsSource = new[] { new PreviewChoice(name == "ModePicker" ? "2388 × 1668 · 144 Hz" : "UU 超级屏 · 示例显示器") };
                combo.SelectedIndex = 0;
            }
        foreach (var (name, selected) in new[] { ("SystemThemeButton", false), ("LightThemeButton", theme == "light"), ("DarkThemeButton", theme == "dark") })
            if (window.FindName(name) is Button button)
            {
                button.SetResourceReference(Control.BackgroundProperty, selected ? "SoftBlue" : "Card");
                button.SetResourceReference(Control.ForegroundProperty, selected ? "Accent" : "PrimaryText");
            }
    }

    private static PreviewResult Render(Window window, int width, int height, string theme, string page, double scrollFraction, double scale, string state, string output)
    {
        var content = (FrameworkElement)window.Content;
        content.LayoutTransform = new ScaleTransform(scale, scale);
        var size = new Size(width, height);
        var applyLayout = typeof(ThemeService).Assembly.GetType("PocketDesk.Core.WorkspaceLayout")?
            .GetMethod("Apply", BindingFlags.Public | BindingFlags.Static, [typeof(FrameworkElement), typeof(double)])
            ?? throw new MissingMethodException("The product must provide WorkspaceLayout.Apply(FrameworkElement, double).");
        // A few deterministic measure/arrange passes allow item generation and
        // ElementName bindings to settle after the shared helper changes panels.
        for (var pass = 0; pass < 3; pass++)
        {
            content.Measure(size);
            content.Arrange(new Rect(size));
            content.UpdateLayout();
            var surface = Find<FrameworkElement>(window, "PhoneSurface");
            applyLayout.Invoke(null, [window, surface.ActualWidth]);
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
        }
        content.Measure(size);
        content.Arrange(new Rect(size));
        content.UpdateLayout();
        var scroll = Find<ScrollViewer>(window, "LibraryScroll");
        scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * scrollFraction);
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
        content.UpdateLayout();

        // A detached WPF visual can still have deferred render data after its
        // last arrange, especially when a previous preview window just closed.
        // Prime it once, then drain render work before writing the final image.
        var warmup = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        warmup.Render(content);
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
        content.UpdateLayout();

        // Render the actual markup on the Window background at 96 DPI. This is
        // a logical-coordinate capture, independent of the desktop's real DPI.
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(window.Background, null, new Rect(size));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        // Render the visual directly: VisualBrush's automatic layout can
        // remeasure a detached root and make the image disagree with geometry.
        bitmap.Render(content);
        var suffix = (scrollFraction > 0 ? "-scrolled" : "") + (scale == 1 ? "" : $"-scale{scale.ToString(CultureInfo.InvariantCulture)}") + (state == "normal" ? "" : $"-{state}");
        var file = Path.Combine(output, $"{width}x{height}-{theme}-{page}{suffix}.png");
        using (var stream = File.Create(file))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
        }
        var warnings = new List<string>();
        var geometry = CollectGeometry(content);
        if (scroll.ViewportHeight <= 0) warnings.Add("LibraryScroll has no visible vertical viewport.");
        if (scroll.ExtentWidth > scroll.ViewportWidth + 1) warnings.Add($"LibraryScroll horizontal extent {scroll.ExtentWidth:F1} exceeds viewport {scroll.ViewportWidth:F1}.");
        foreach (var name in new[] { "SideRail", "BottomNavigation", "MainPane", "RotateButton", "WorkspaceButton", "ManualZoomButton" })
            if (window.FindName(name) is FrameworkElement element && element.Visibility == Visibility.Visible)
            {
                var bounds = element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
                if (bounds.Left < -1 || bounds.Top < -1 || bounds.Right > content.ActualWidth + 1 || bounds.Bottom > content.ActualHeight + 1)
                    warnings.Add($"{name} extends beyond PhoneSurface: {bounds}.");
            }
        bool wide = content.ActualWidth >= 680;
        if (Find<FrameworkElement>(window, "SideRail").Visibility != (wide ? Visibility.Visible : Visibility.Collapsed)) warnings.Add("SideRail visibility disagrees with the logical-width breakpoint.");
        if (Find<FrameworkElement>(window, "BottomNavigation").Visibility != (wide ? Visibility.Collapsed : Visibility.Visible)) warnings.Add("BottomNavigation visibility disagrees with the logical-width breakpoint.");
        if (Math.Abs(content.ActualWidth - width / scale) > 1) warnings.Add("PhoneSurface width does not match viewport / content scale.");
        var list = Find<ItemsControl>(window, "WindowList");
        var cards = Enumerable.Range(0, page == "library" ? list.Items.Count : 0)
            .Select(index => list.ItemContainerGenerator.ContainerFromIndex(index)).OfType<FrameworkElement>()
            .Where(element => element.ActualWidth > 0 && element.ActualHeight > 0)
            .Select(element => element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize))).ToArray();
        for (var index = 0; index < cards.Length; index++)
        {
            var card = cards[index];
            geometry.Add(new($"AppContainer[{index}]", "ContentPresenter", card.X, card.Y, card.Width, card.Height));
        }
        int columns = cards.Length == 0 ? 0 : cards.Count(rect => Math.Abs(rect.Top - cards[0].Top) < 1);
        double minimumCardWidth = cards.Length == 0 ? 0 : cards.Min(rect => rect.Width - 12);
        var panel = FindVisual<WrapPanel>(list) as Panel ?? FindVisual<StackPanel>(list);
        var firstContainer = list.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
        var metrics = new Dictionary<string, double>
        {
            ["ListActualWidth"] = list.ActualWidth,
            ["ListDesiredWidth"] = list.DesiredSize.Width,
            ["PanelActualWidth"] = panel?.ActualWidth ?? 0,
            ["PanelDesiredWidth"] = panel?.DesiredSize.Width ?? 0,
            ["FirstContainerWidth"] = firstContainer is not null && double.IsFinite(firstContainer.Width) ? firstContainer.Width : 0,
            ["FirstContainerActualWidth"] = firstContainer?.ActualWidth ?? 0,
            ["FirstContainerDesiredWidth"] = firstContainer?.DesiredSize.Width ?? 0
        };
        if (page == "library" && columns == 0) warnings.Add("No fixture application cards were generated.");
        if (page == "library" && columns > 1 && minimumCardWidth < 250) warnings.Add($"Multi-column cards are only {minimumCardWidth:F1} logical units wide.");
        if (page == "library" && panel is WrapPanel && firstContainer is { Width: > 0 } && double.IsFinite(firstContainer.Width))
        {
            var requestedColumns = (int)Math.Floor((panel.ActualWidth + 0.001) / firstContainer.Width);
            if (requestedColumns > columns) warnings.Add($"Requested widths fit {requestedColumns} columns but only {columns} rendered; check DPI rounding of DesiredSize.");
        }
        if (page == "library")
        {
            var expectedColumns = (int)Math.Round(content.ActualWidth) switch { 402 or 643 => 1, 874 or 834 or 810 or 746 or 715 => 2, 1194 or 960 or 1130 or 1066 => 3, _ => 0 };
            if (expectedColumns > 0 && columns != expectedColumns) warnings.Add($"Viewport contract expects {expectedColumns} app columns, actual {columns}.");
            if (scrollFraction == 0 && firstContainer is not null && FindVisual<Button>(firstContainer) is { } firstAction)
            {
                var actionBounds = firstAction.TransformToAncestor(content).TransformBounds(new Rect(firstAction.RenderSize));
                var viewportBounds = scroll.TransformToAncestor(content).TransformBounds(new Rect(scroll.RenderSize));
                metrics["FirstActionTop"] = actionBounds.Top;
                metrics["FirstActionBottom"] = actionBounds.Bottom;
                metrics["ScrollViewportBottom"] = viewportBounds.Bottom;
                if (actionBounds.Top < viewportBounds.Top - 1 || actionBounds.Bottom > viewportBounds.Bottom + 1)
                    warnings.Add($"First application action is clipped vertically ({actionBounds.Top:F1}–{actionBounds.Bottom:F1}, viewport {viewportBounds.Top:F1}–{viewportBounds.Bottom:F1}).");
            }
        }
        return new(file, width, height, scale, state, content.ActualWidth, content.ActualHeight, columns, minimumCardWidth, metrics, theme, page, scroll.ViewportWidth, scroll.ViewportHeight, scroll.ExtentWidth, scroll.ExtentHeight, warnings, geometry);
    }

    private static List<ElementGeometry> CollectGeometry(FrameworkElement root)
    {
        var result = new List<ElementGeometry>();
        void Visit(DependencyObject node)
        {
            if (node is FrameworkElement hidden && hidden.Visibility != Visibility.Visible) return;
            if (node is FrameworkElement element && !string.IsNullOrEmpty(element.Name) && element.Visibility == Visibility.Visible)
            {
                var bounds = ReferenceEquals(element, root) ? new Rect(element.RenderSize)
                    : element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
                result.Add(new(element.Name, element.GetType().Name, Math.Round(bounds.X, 2), Math.Round(bounds.Y, 2), Math.Round(bounds.Width, 2), Math.Round(bounds.Height, 2)));
            }
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++) Visit(VisualTreeHelper.GetChild(node, index));
        }
        Visit(root);
        return result;
    }

    private static T Find<T>(Window window, string name) where T : class => window.FindName(name) as T
        ?? throw new InvalidDataException($"Required preview control '{name}' is missing or is not {typeof(T).Name}.");
    private static T? FindVisual<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T match) return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            if (FindVisual<T>(VisualTreeHelper.GetChild(node, index)) is { } child) return child;
        return null;
    }
    private static void SetText(Window window, string name, string text) { if (window.FindName(name) is TextBlock block) block.Text = text; }

    private sealed record Options(string Root, string Output, int Width, int Height, string Theme, string Page, double Scroll, double Scale, string State, bool All, bool Show)
    {
        public static Options Parse(string[] args)
        {
            string? Value(string key) { var index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
            var root = Value("--root") ?? FindRoot();
            var output = Path.GetFullPath(Value("--output") ?? Path.Combine(root, "artifacts", "ui-review"));
            int Dimension(string key, int fallback) => Value(key) is { } value ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;
            var width = Dimension("--width", 402);
            var height = Dimension("--height", 874);
            var theme = Value("--theme") ?? "light";
            var page = Value("--page") ?? "library";
            var scroll = Value("--scroll") is { } fraction ? double.Parse(fraction, CultureInfo.InvariantCulture) : 0;
            var scale = Value("--scale") is { } contentScale ? double.Parse(contentScale, CultureInfo.InvariantCulture) : 1;
            var state = Value("--state") ?? "normal";
            if (width is < 240 or > 4096 || height is < 240 or > 4096 || theme is not ("light" or "dark") || page is not ("library" or "settings") || !double.IsFinite(scroll) || scroll is < 0 or > 1 || !double.IsFinite(scale) || scale is < 0.75 or > 3 || state is not ("normal" or "confirm" or "recovery" or "confirm-recovery"))
                throw new ArgumentException("Use dimensions 240–4096, --theme light|dark, --page library|settings, --scroll 0–1, --scale 0.75–3 and --state normal|confirm|recovery|confirm-recovery.");
            bool all = args.Contains("--all"), show = args.Contains("--show");
            if (all && show) throw new ArgumentException("Use --show with one size; --all renders offscreen only.");
            return new(Path.GetFullPath(root), output, width, height, theme, page, scroll, scale, state, all, show);
        }

        private static string FindRoot()
        {
            foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
                for (var path = new DirectoryInfo(start); path is not null; path = path.Parent)
                    if (File.Exists(Path.Combine(path.FullName, "src", "PocketDesk", "MainWindow.xaml"))) return path.FullName;
            throw new DirectoryNotFoundException("Run inside the repository or pass --root.");
        }
    }

    public sealed record PreviewGroup(string AppName, string ProcessName, IReadOnlyList<PreviewWindow> Windows)
    {
        public ImageSource? Icon => null;
        public string Summary => $"{Windows.Count} 个窗口";
    }
    public sealed record PreviewWindow(string Title);
    public sealed record PreviewChoice(string Label);
    private sealed record ElementGeometry(string Name, string Type, double X, double Y, double Width, double Height);
    private sealed record PreviewResult(string File, int Width, int Height, double Scale, string State, double SurfaceWidth, double SurfaceHeight, int AppColumns, double MinimumCardWidth, IReadOnlyDictionary<string, double> LayoutMetrics, string Theme, string Page, double ViewportWidth, double ViewportHeight, double ExtentWidth, double ExtentHeight, IReadOnlyList<string> Warnings, IReadOnlyList<ElementGeometry> Geometry);
}
