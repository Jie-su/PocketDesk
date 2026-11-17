using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PocketDesk.Core;

/// <summary>
/// Replaces semantic application brushes on the WPF dispatcher. Views must use
/// DynamicResource / SetResourceReference so already-open windows update too.
/// The owner must Dispose when its main window actually closes.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private readonly Application application;
    private readonly ThemePreferenceStore store;
    private volatile bool disposed;
    private bool applied;
    private int refreshQueued;

    public ThemePreference Preference { get; private set; }
    public bool IsDark { get; private set; }
    public event EventHandler? ThemeChanged;

    public ThemeService(Application application, ThemePreferenceStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.Dispatcher.VerifyAccess();
        this.application = application;
        this.store = store ?? new ThemePreferenceStore();
        Preference = this.store.Load();
        ApplyCurrentTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public void SetPreference(ThemePreference preference)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        application.Dispatcher.VerifyAccess();
        if (!Enum.IsDefined(preference)) throw new ArgumentOutOfRangeException(nameof(preference));
        // Persist first: a failed write must not leave UI and saved preference disagreeing.
        store.Save(preference);
        bool changed = Preference != preference;
        Preference = preference;
        ApplyCurrentTheme(notifyEvenIfSame: changed);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (disposed || application.Dispatcher.HasShutdownStarted ||
            Interlocked.Exchange(ref refreshQueued, 1) != 0) return;
        try
        {
            // The SystemEvents callback is not a WPF UI thread. Do not change
            // ResourceDictionary there, and coalesce a burst of preference events.
            application.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                Interlocked.Exchange(ref refreshQueued, 0);
                if (!disposed && Preference == ThemePreference.System) ApplyCurrentTheme();
            }));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref refreshQueued, 0); // Dispatcher is shutting down.
        }
    }

    private void ApplyCurrentTheme(bool notifyEvenIfSame = false)
    {
        application.Dispatcher.VerifyAccess();
        bool dark = Preference == ThemePreference.Dark ||
            (Preference == ThemePreference.System && ReadSystemDark());
        if (applied && IsDark == dark)
        {
            if (notifyEvenIfSame) ThemeChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        IsDark = dark;
        foreach (var (key, light, night) in Palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? night : light));
            brush.Freeze();
            application.Resources[key] = brush;
        }
        applied = true;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool ReadSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex)
        {
            RecoveryStore.WriteLog("读取 Windows 应用主题：" + ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        ThemeChanged = null;
    }

    // Keep the light fallback declarations in App.xaml aligned with this palette.
    private static readonly (string Key, string Light, string Dark)[] Palette =
    [
        ("Bg", "#F4F5F7", "#111113"),
        ("Rail", "#ECEEF2", "#1B1B1E"),
        ("Card", "#FFFFFF", "#252629"),
        ("Muted", "#626975", "#A6AAB3"),
        ("OnSurfaceVariant", "#515965", "#B4B9C3"),
        ("Accent", "#246FDA", "#3182F6"),
        ("PrimaryText", "#20242B", "#E6E8ED"),
        ("Fill", "#ECEEF3", "#2D2F34"),
        ("Separator", "#E2E5EB", "#34363C"),
        ("Outline", "#CDD2DB", "#484B54"),
        ("Hover", "#E6EAF1", "#34373E"),
        ("HoverOverlay", "#0C000000", "#14FFFFFF"),
        ("PressedOverlay", "#18000000", "#24FFFFFF"),
        ("SoftBlue", "#E2ECFD", "#203958"),
        ("Placeholder", "#737C89", "#9098A6"),
        ("ToggleOff", "#E8EBF0", "#30333A"),
        ("ToggleThumb", "#667181", "#B9C1CE"),
        ("OnAccent", "#FFFFFF", "#FFFFFF"),
        ("Success", "#28754D", "#70C99E"),
        ("SuccessBackground", "#E4F3EA", "#203A2E"),
        ("WarningBackground", "#FFF0D8", "#43351D"),
        ("WarningText", "#81510F", "#EAC57D"),
        ("ErrorText", "#B43D39", "#F18F89")
    ];
}
