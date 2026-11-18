using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk;

public partial class AppPoolPanel : UserControl
{
    private readonly Func<Window?> dialogOwner;
    private readonly Action<AppPoolLaunch> onLaunched;
    private readonly AppPoolStore store;
    private readonly Func<IReadOnlyList<WindowEntry>>? listWindows;
    private readonly Action<WindowEntry>? switchWindow;
    private IReadOnlyList<WindowEntry> windows = [];
    private bool allWindows, allPins;
    private IReadOnlyList<PoolApp> pins = [];
    private IReadOnlyList<PoolApp>? installed;
    private bool discovering;
    private bool managing;
    private bool adding;
    private sealed record Row(PoolApp App, ImageSource? Icon, Visibility RemoveVisibility);

    // The host supplies the enclosing toolbar window for modal positioning
    // and chooses how to match the launched executable to a workspace window.
    public AppPoolPanel(Func<Window?> dialogOwner, Action<AppPoolLaunch> onLaunched, AppPoolStore? store = null,
        Func<IReadOnlyList<WindowEntry>>? listWindows = null, Action<WindowEntry>? switchWindow = null)
    {
        this.dialogOwner = dialogOwner ?? throw new ArgumentNullException(nameof(dialogOwner));
        this.onLaunched = onLaunched ?? throw new ArgumentNullException(nameof(onLaunched));
        this.store = store ?? new AppPoolStore();
        if ((listWindows is null) != (switchWindow is null))
            throw new ArgumentException("Both window callbacks must be provided together.");
        this.listWindows = listWindows;
        this.switchWindow = switchWindow;
        InitializeComponent();
        Loaded += (_, _) => RefreshPins();
    }

    private void RefreshPins()
    {
        try
        {
            pins = store.Read();
            var shown = managing || allPins ? pins : pins.Take(2);
            PinnedList.ItemsSource = shown.Select(a => new Row(a, AppIconService.GetIcon("exe:" + a.ExecutablePath, [], a.ExecutablePath),
                managing ? Visibility.Visible : Visibility.Collapsed)).ToArray();
            MorePins.Visibility = !managing && pins.Count > 2 ? Visibility.Visible : Visibility.Collapsed;
            MorePins.Content = allPins ? "收起常用应用" : $"全部 {pins.Count} 个常用应用";
            Hint.Text = adding ? "固定程序后，就能在小栏里一键打开。" : managing ? "移除只影响应用池，不会卸载程序。" :
                pins.Count > 0 ? "点按程序即可打开。" : "还没有固定程序。点「添加」选一个。";
            UpdateChoices();
        }
        catch (Exception ex) when (CanReport(ex)) { Report(ex.Message); }
    }

    // Establish a new snapshot on entry. While browsing, the desktop may reorder
    // windows on every activation. Re-enumerate only on explicit refresh, and
    // verify the tapped identity before handing it to the workspace.
    internal void OpenLibrary(bool focusWindows)
    {
        allPins = false;
        allWindows = focusWindows;
        WindowFilter.Clear();
        SetAdding(false);
        RefreshRunningWindows();
        if (focusWindows) Dispatcher.BeginInvoke(() => RefreshWindowsButton.BringIntoView());
    }

    private void RefreshRunningWindows()
    {
        if (listWindows is null) return;
        try
        {
            windows = listWindows().ToArray();
            DisplayWindows();
        }
        catch (Exception ex) when (CanReport(ex))
        {
            windows = [];
            DisplayWindows();
            Report("无法读取窗口：" + ex.Message);
        }
    }
    private void DisplayWindows()
    {
        if (RunningList is null || listWindows is null) return;
        Running.Visibility = adding || managing ? Visibility.Collapsed : Visibility.Visible;
        string filter = allWindows ? WindowFilter.Text.Trim() : "";
        var matches = windows.Where(w => w.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
            || w.ProcessName.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        RunningList.ItemsSource = allWindows ? matches : matches.Take(3).ToArray();
        RunningSummary.Text = windows.Count == 0 ? "没有可切换的桌面窗口" :
            allWindows ? $"按标题或应用名搜索 · {matches.Length} 个" : $"共 {windows.Count} 个，点按切换";
        WindowFilter.Visibility = allWindows && windows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MoreWindows.Visibility = windows.Count > 3 || (allWindows && windows.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
        MoreWindows.Content = allWindows ? "收起窗口列表" : $"全部 {windows.Count} 个窗口 / 搜索";
    }
    private void RefreshWindows_Click(object sender, RoutedEventArgs e) => RefreshRunningWindows();
    private void WindowFilter_Changed(object sender, TextChangedEventArgs e) => DisplayWindows();
    private void MorePins_Click(object sender, RoutedEventArgs e) { allPins = !allPins; RefreshPins(); }
    private void MoreWindows_Click(object sender, RoutedEventArgs e)
    {
        allWindows = !allWindows;
        WindowFilter.Clear();
        DisplayWindows();
        RefreshWindowsButton.BringIntoView();
    }
    private void SwitchWindow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: WindowEntry selected } || listWindows is null || switchWindow is null) return;
        try
        {
            var current = listWindows().FirstOrDefault(w => w.ProcessId == selected.ProcessId && w.Handle == selected.Handle);
            if (current is null)
            {
                RefreshRunningWindows();
                Report("该窗口已关闭，请选择其他窗口。");
                return;
            }
            switchWindow(current);
        }
        catch (Exception ex) when (CanReport(ex)) { Report(ex.Message); }
    }

    private void Manage_Click(object sender, RoutedEventArgs e)
    {
        managing = !managing;
        Manage.Content = managing ? "完成" : "管理";
        Message.Visibility = Visibility.Collapsed;
        RefreshPins();
        DisplayWindows();
    }

    private void SetAdding(bool value)
    {
        adding = value;
        managing = false;
        Manage.Content = "管理";
        Add.Content = adding ? "返回" : "添加";
        Manage.Visibility = adding ? Visibility.Collapsed : Visibility.Visible;
        Pins.Visibility = adding ? Visibility.Collapsed : Visibility.Visible;
        Chooser.Visibility = adding ? Visibility.Visible : Visibility.Collapsed;
        Message.Visibility = Visibility.Collapsed;
        Query.Text = "";
        RefreshPins();
        DisplayWindows();
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        SetAdding(!adding);
        if (!adding || installed is not null || discovering) return;
        discovering = true;
        UpdateChoices();
        try { installed = await Task.Run(RegisteredApps.Read); }
        catch (Exception ex) when (CanReport(ex)) { Report("未能列出已安装程序，可用「浏览本地 .exe」。"); }
        finally { discovering = false; UpdateChoices(); }
    }

    private void Query_Changed(object sender, TextChangedEventArgs e) => UpdateChoices();

    private void UpdateChoices()
    {
        if (!adding || Candidates is null) return;
        var query = Query.Text.Trim();
        var list = (installed ?? []).Where(a => !pins.Any(p => string.Equals(a.ExecutablePath, p.ExecutablePath, StringComparison.OrdinalIgnoreCase)) &&
            a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).Take(13).ToList();
        Candidates.ItemsSource = list.Take(12).ToArray();
        Matches.Text = installed is null ? (discovering ? "正在读取程序列表…" : "") : list.Count switch
        {
            0 => "没有匹配的程序，可使用上面的「浏览本地 .exe」。",
            > 12 => "显示前 12 个匹配项，输入名称可缩小范围。",
            _ => "点 ＋ 固定到应用池。"
        };
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Row row }) return;
        AppPoolLaunch launch;
        try
        {
            var info = store.PrepareLaunch(row.App.ExecutablePath);
            using var process = Process.Start(info);
            launch = new(info.FileName, process?.Id);
        }
        catch (Exception ex) when (CanReport(ex)) { Report("无法启动：" + ex.Message); return; }
        Message.Visibility = Visibility.Collapsed;
        onLaunched(launch);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFileDialog { Filter = "Windows 程序 (*.exe)|*.exe", Title = "选择要固定的程序",
                CheckFileExists = true, DereferenceLinks = false };
            var owner = dialogOwner();
            if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) != true) return;
            Pin(new(Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName));
        }
        catch (Exception ex) when (CanReport(ex)) { Report(ex.Message); }
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PoolApp app }) Pin(app);
    }

    private void Pin(PoolApp app)
    {
        try
        {
            store.Add(app);
            SetAdding(false);
        }
        catch (Exception ex) when (CanReport(ex)) { Report(ex.Message); }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (!managing || sender is not Button { DataContext: Row row }) return;
        try { store.Remove(row.App.ExecutablePath); Message.Visibility = Visibility.Collapsed; RefreshPins(); }
        catch (Exception ex) when (CanReport(ex)) { Report(ex.Message); }
    }

    private void Report(string text) { Message.Visibility = Visibility.Visible; Message.Text = text; }
    private static bool CanReport(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or SecurityException or Win32Exception or JsonException or InvalidOperationException or ArgumentException or NotSupportedException;
}
