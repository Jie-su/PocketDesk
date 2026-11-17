using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Microsoft.Win32;
using PocketDesk.Core;

namespace PocketDesk.Native;

public enum UuControllerStatus { Available, Unknown, None, Ambiguous }

public sealed record UuControllerSnapshot(
    UuControllerStatus Status, IReadOnlyList<string> Names, DateTimeOffset ObservedAt, string? Reason = null)
{
    public bool IsFresh(TimeSpan maxAge)
    {
        var age = DateTimeOffset.UtcNow - ObservedAt;
        return maxAge >= TimeSpan.Zero && age >= TimeSpan.Zero && age <= maxAge;
    }
}

/// <summary>A minimal, credential-free tree used by the pure scope parser.</summary>
public sealed record UuControllerNode(
    string ClassName, string AutomationId, string Name, IReadOnlyList<UuControllerNode> Children);

/// <summary>
/// Reads the installed UU application's local incoming-controller panel. It never
/// shows, activates or clicks a window, starts UU or reads the account/device list.
/// UIA providers can block indefinitely: each instance permits only one probe at
/// a time on a background MTA thread, and callers receive Unknown after timeout.
/// Dispose stops publication/subsequent probes without blocking the UI thread.
/// </summary>
public sealed class UuControllerService : IDisposable
{
    private const int MaximumNodes = 512;
    private readonly object gate = new();
    private readonly string? configuredExecutable;
    private readonly TimeSpan timeout;
    private Task<UuControllerSnapshot>? inFlight;
    private long inFlightVersion;
    private long publicationVersion;
    private long lastStructureAttempt;
    private string? lastStructureSignature;
    private UuControllerSnapshot latest = Unknown("尚未读取 UU 控制端信息。", DateTimeOffset.MinValue);
    private volatile bool disposed;

    public UuControllerSnapshot Latest => Volatile.Read(ref latest);

    public UuControllerService(string? installedExecutablePath = null, TimeSpan? timeout = null)
    {
        configuredExecutable = installedExecutablePath is null ? null : Path.GetFullPath(installedExecutablePath);
        this.timeout = timeout ?? TimeSpan.FromMilliseconds(1200);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<UuControllerSnapshot> PollAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        Task<UuControllerSnapshot> operation;
        long operationVersion;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (inFlight is null || inFlight.IsCompleted)
            {
                var completion = new TaskCompletionSource<UuControllerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                inFlight = completion.Task;
                inFlightVersion = ++publicationVersion;
                long version = inFlightVersion;
                var startedAt = DateTimeOffset.UtcNow;
                var startedTick = Stopwatch.GetTimestamp();
                var thread = new Thread(() =>
                {
                    UuControllerSnapshot result;
                    try { result = disposed ? Unknown("读取已停止。") : Probe(); }
                    catch (Exception ex)
                    {
                        // Do not log UI names, process command lines or provider
                        // exception messages, which may contain unrelated data.
                        RecoveryStore.WriteLog($"UU controller read exception: {ex.GetType().Name}, HRESULT 0x{ex.HResult:X8}.");
                        result = Unknown($"无法可靠读取 UU 本机控制端面板（{ex.GetType().Name}）。");
                    }
                    // ObservedAt measures the oldest observation, not completion:
                    // a provider that stalls after reading a name must not make
                    // that name look fresh when it eventually returns.
                    result = result with { ObservedAt = startedAt };
                    lock (gate)
                    {
                        if (Stopwatch.GetElapsedTime(startedTick) > this.timeout || publicationVersion != version)
                            result = Unknown("UU 控制端信息已超过读取期限。", startedAt);
                        if (!disposed && publicationVersion == version)
                            Volatile.Write(ref latest, result);
                    }
                    completion.TrySetResult(result);
                }) { IsBackground = true, Name = "PocketDesk.UU.Controller" };
                thread.SetApartmentState(ApartmentState.MTA);
                thread.Start();
            }
            operation = inFlight;
            operationVersion = inFlightVersion;
        }
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            lock (gate)
            {
                if (disposed || publicationVersion != operationVersion) return;
                publicationVersion++;
                Volatile.Write(ref latest, Unknown("UU 控制端读取已取消，未沿用上次设备名。"));
            }
        });
        try { return await operation.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            var result = Unknown("UU 控制端信息读取超时，未沿用上次设备名。");
            lock (gate)
            {
                // Serialize invalidation and worker publication. A late worker
                // cannot overwrite Unknown with its pre-timeout controller name.
                if (!disposed && publicationVersion == operationVersion)
                {
                    publicationVersion++;
                    Volatile.Write(ref latest, result);
                }
            }
            return result;
        }
    }

    public void Dispose()
    {
        lock (gate) { disposed = true; publicationVersion++; }
    }

    /// <summary>
    /// Only a local ControllerInfoView with its own exact title is authoritative.
    /// Names outside that scope, including the same account's device list, are ignored.
    /// Available means one verified controller name, not a recognized mobile model.
    /// </summary>
    public static UuControllerSnapshot ParseTree(UuControllerNode root, DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        if (root is null || !Identifies(root, "HomePageMainWindow"))
            return Unknown("没有可验证的 UU 主页面。", now);
        if (!TryFlatten(root, out var all)) return Unknown("UU 面板结构不完整。", now);
        var localScopes = new List<UuControllerNode>();
        foreach (var candidate in all.Where(n => Identifies(n, "ControllerInfoView")))
        {
            if (!TryFlatten(candidate, out var descendants, n =>
                    !Identifies(n, "ControllerInfoView") && !Identifies(n, "ControllerItemWidget")))
                return Unknown("UU 本机面板结构不完整。", now);
            if (descendants.Any(n => Identifies(n, "titleLabel") && n.Name?.Trim() == "本机被控中"))
                localScopes.Add(candidate);
        }
        if (localScopes.Count == 0) return Unknown("未找到已验证的本机被控面板。", now);
        if (localScopes.Count != 1)
            return new(UuControllerStatus.Ambiguous, Array.Empty<string>(), now, "本机控制端面板不唯一。");

        if (!TryFlatten(localScopes[0], out var local, n => !Identifies(n, "ControllerInfoView")))
            return Unknown("UU 本机面板结构不完整。", now);
        var items = local.Where(n => Identifies(n, "ControllerItemWidget")).ToArray();
        var names = new List<string>();
        foreach (var item in items)
        {
            if (!TryFlatten(item, out var fields)) return Unknown("控制端条目结构不完整。", now);
            var labels = fields.Where(n => Identifies(n, "controllerNameLabel")).ToArray();
            if (labels.Length > 1)
                return new(UuControllerStatus.Ambiguous, Array.Empty<string>(), now, "控制端名称不唯一。");
            if (labels.Length == 1 && IsUsableName(labels[0].Name)) names.Add(labels[0].Name.Trim());
        }
        if (items.Length > 1)
            return new(UuControllerStatus.Ambiguous, names.AsReadOnly(), now, "同时存在多个控制端，未猜测当前设备。");

        // A temporarily empty/half-updated panel is not proof of disconnection.
        // Only an explicit local count of zero can produce None from UIA.
        var counts = local.Where(n => Identifies(n, "textLabel"))
            .Select(n => Regex.Match(n.Name ?? "", @"^当前有\s*(\d+)\s*台设备正在控制此电脑$"))
            .Where(m => m.Success)
            .Select(m => int.TryParse(m.Groups[1].Value, out var count) ? count : -1).Distinct().ToArray();
        if (counts.Length > 1) return Unknown("UU 控制端数量尚未稳定。", now);
        if (items.Length == 0)
            return counts is [0]
                ? new(UuControllerStatus.None, Array.Empty<string>(), now, "本机当前没有控制端。")
                : Unknown("本机控制端条目暂不可读。", now);
        if (counts.Length == 1 && counts[0] != items.Length)
            return Unknown("UU 控制端列表正在变化。", now);
        if (names.Count != 1) return Unknown("本机控制端名称暂不可读。", now);
        return new(UuControllerStatus.Available, names.AsReadOnly(), now);
    }

    private UuControllerSnapshot Probe()
    {
        var expectedPaths = DiscoverInstalledExecutables();
        var trustedProcesses = new HashSet<int>();
        bool sawProcess = false;
        foreach (var process in Process.GetProcessesByName("GameViewer"))
        {
            using (process)
            {
                sawProcess = true;
                try
                {
                    // MainModule needs VM_READ and can fail against a higher
                    // integrity UU process. Limited process query is enough to
                    // verify its installed executable, without elevation.
                    var executable = ReadProcessImagePath(process.Id);
                    if (executable is not null && expectedPaths.Contains(Path.GetFullPath(executable)))
                        trustedProcesses.Add(process.Id);
                }
                catch { /* Inaccessible/unverified processes are not trusted. */ }
            }
        }
        // The host/server can continue independently of its GUI. Missing UI is
        // not an observed zero-controller session and must not trigger restore
        // or the explicit-disconnection latch. Only the verified local panel's
        // count of zero in ParseTree is authoritative None evidence.
        if (!sawProcess) return Unknown("UU 主界面程序不可用，无法确认当前控制端。");
        if (trustedProcesses.Count == 0) return Unknown("无法核对 UU 已安装程序的完整路径。");

        var handles = new List<nint>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var processId);
            if (trustedProcesses.Contains((int)processId)) handles.Add(window);
            return handles.Count <= 64;
        }, 0);
        if (handles.Count > 64) return Unknown("UU 顶层窗口数量异常。");

        var homes = new List<AutomationElement>();
        foreach (var handle in handles)
        {
            if (disposed) return Unknown("读取已停止。");
            try
            {
                var element = AutomationElement.FromHandle(handle);
                if (Identifies(ReadIdentity(element), "HomePageMainWindow")) homes.Add(element);
            }
            catch { /* A window may close between enumeration and inspection. */ }
        }
        if (homes.Count == 0) return Unknown("UU 主页面当前未提供可读面板。");
        if (homes.Count != 1)
            return new(UuControllerStatus.Ambiguous, Array.Empty<string>(), DateTimeOffset.UtcNow, "UU 主页面不唯一。");

        return ReadHomeSnapshot(homes[0]);
    }

    private UuControllerSnapshot ReadHomeSnapshot(AutomationElement home)
    {
        var scopeCondition = new OrCondition(
            new PropertyCondition(AutomationElement.AutomationIdProperty, "ControllerInfoView"),
            new PropertyCondition(AutomationElement.AutomationIdProperty, "HomePageMainWindow.ControllerInfoView"),
            new PropertyCondition(AutomationElement.ClassNameProperty, "ControllerInfoView"));
        var scopes = home.FindAll(TreeScope.Descendants, scopeCondition);
        if (scopes.Count == 0)
        {
            LogUnknownStructure(home);
            return Unknown("UU 主页面可读，但未提供本机控制端区域。");
        }
        if (scopes.Count > 8) return Unknown("UU 控制端面板数量异常。");
        var captured = new List<UuControllerNode>();
        var scopeDiagnostics = new List<string>();
        foreach (AutomationElement scope in scopes)
        {
            var nodes = ReadLocalScope(scope, out var diagnostic);
            if (nodes is not null) captured.Add(nodes);
            else scopeDiagnostics.Add(diagnostic);
        }
        if (captured.Count == 0)
        {
            LogUnknownStructure(home);
            return Unknown("UU 本机区域尚不可验证：" + string.Join("；", scopeDiagnostics));
        }
        var root = new UuControllerNode("", "HomePageMainWindow", "", captured);
        return ParseTree(root);
    }

    private UuControllerNode? ReadLocalScope(AutomationElement scope, out string diagnostic)
    {
        diagnostic = "未取得标题。";
        var scopeIdentity = ReadIdentity(scope);
        var descendants = scope.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        if (descendants.Count > MaximumNodes) throw new InvalidOperationException("UU 本机面板过大。");
        var identities = new List<(AutomationElement Element, UuControllerNode Identity)>();
        foreach (AutomationElement element in descendants)
            identities.Add((element, ReadIdentity(element)));

        // Identity properties are safe to inspect before the local header is
        // confirmed. Do not read controller names in any other panel.
        var titles = new List<UuControllerNode>();
        foreach (var (element, identity) in identities.Where(p => IsScopeField(scopeIdentity, p.Identity, "titleLabel")))
            titles.Add(identity with { Name = ReadName(element) });
        if (!titles.Any(n => n.Name.Trim() == "本机被控中"))
        {
            diagnostic = $"{descendants.Count} 个节点，{titles.Count} 个标题字段，{titles.Count(n => !string.IsNullOrWhiteSpace(n.Name))} 个非空标题，未匹配本机被控标题。";
            return null;
        }

        var children = new List<UuControllerNode>(titles);
        foreach (var (element, identity) in identities.Where(p => IsScopeField(scopeIdentity, p.Identity, "textLabel")))
            children.Add(identity with { Name = ReadName(element) });
        foreach (var (element, identity) in identities.Where(p => Identifies(p.Identity, "ControllerItemWidget")))
        {
            var fields = element.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            if (fields.Count > MaximumNodes) throw new InvalidOperationException("UU 控制端条目过大。");
            var labels = new List<UuControllerNode>();
            foreach (AutomationElement field in fields)
            {
                var fieldIdentity = ReadIdentity(field);
                if (Identifies(fieldIdentity, "controllerNameLabel"))
                    labels.Add(fieldIdentity with { Name = ReadName(field) });
            }
            children.Add(identity with { Children = labels });
        }
        return scopeIdentity with { Children = children };
    }

    private HashSet<string> DiscoverInstalledExecutables()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddFile(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            try
            {
                var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"')));
                if (Path.GetFileName(full).Equals("GameViewer.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(full)) paths.Add(full);
            }
            catch { }
        }
        void AddFolder(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            AddFile(Path.Combine(folder, "GameViewer.exe"));
            AddFile(Path.Combine(folder, "bin", "GameViewer.exe"));
        }
        if (configuredExecutable is not null) { AddFile(configuredExecutable); return paths; }
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var appPath = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\GameViewer.exe");
                AddFile(appPath?.GetValue("") as string);
            }
            catch { }
            foreach (var registryPath in new[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using var uninstall = hive.OpenSubKey(registryPath);
                    if (uninstall is null) continue;
                    foreach (var subkeyName in uninstall.GetSubKeyNames())
                    {
                        using var product = uninstall.OpenSubKey(subkeyName);
                        var name = product?.GetValue("DisplayName") as string;
                        if (name is null || !(name.Contains("UU远程", StringComparison.OrdinalIgnoreCase) || name.Contains("GameViewer", StringComparison.OrdinalIgnoreCase))) continue;
                        AddFolder(product?.GetValue("InstallLocation") as string);
                        if (product?.GetValue("DisplayIcon") is not string icon) continue;
                        icon = icon.Trim();
                        if (icon.StartsWith('"'))
                        {
                            int end = icon.IndexOf('"', 1);
                            if (end < 0) continue;
                            icon = icon[1..end];
                        }
                        else if (icon.LastIndexOf(',') is var comma && comma >= 0 && int.TryParse(icon[(comma + 1)..], out _)) icon = icon[..comma];
                        AddFile(icon);
                        AddFolder(Path.GetDirectoryName(icon));
                    }
                }
                catch { /* Only installation metadata is read; no user/authentication data. */ }
            }
        }
        return paths;
    }

    private static UuControllerNode ReadIdentity(AutomationElement element) => new(
        element.GetCurrentPropertyValue(AutomationElement.ClassNameProperty, true) as string ?? "",
        element.GetCurrentPropertyValue(AutomationElement.AutomationIdProperty, true) as string ?? "",
        "", Array.Empty<UuControllerNode>());
    private static string ReadName(AutomationElement element) =>
        element.GetCurrentPropertyValue(AutomationElement.NameProperty, true) as string ?? "";
    private static bool Identifies(UuControllerNode node, string name) =>
        string.Equals(node.ClassName, name, StringComparison.Ordinal) ||
        string.Equals(node.AutomationId, name, StringComparison.Ordinal) ||
        (node.AutomationId?.EndsWith("." + name, StringComparison.Ordinal) ?? false);
    private static bool IsUsableName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 256 && !name.Any(char.IsControl);
    private static bool IsScopeField(UuControllerNode scope, UuControllerNode field, string fieldName) =>
        !string.IsNullOrEmpty(scope.AutomationId) &&
        string.Equals(field.AutomationId, scope.AutomationId + "." + fieldName, StringComparison.Ordinal);
    private static bool TryFlatten(UuControllerNode root, out List<UuControllerNode> result,
        Func<UuControllerNode, bool>? descendInto = null)
    {
        result = [];
        var pending = new Stack<UuControllerNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is null || result.Count >= MaximumNodes || node.Children is null) return false;
            result.Add(node);
            if (!ReferenceEquals(node, root) && descendInto is not null && !descendInto(node)) continue;
            for (int i = node.Children.Count - 1; i >= 0; i--) pending.Push(node.Children[i]);
        }
        return true;
    }
    private static UuControllerSnapshot Unknown(string reason, DateTimeOffset? observedAt = null) =>
        new(UuControllerStatus.Unknown, Array.Empty<string>(), observedAt ?? DateTimeOffset.UtcNow, reason);

    private static string? ReadProcessImagePath(int processId)
    {
        nint process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, processId);
        if (process == 0) return null;
        try
        {
            var buffer = new StringBuilder(32768);
            int characters = buffer.Capacity;
            return QueryFullProcessImageNameW(process, 0, buffer, ref characters) ? buffer.ToString() : null;
        }
        finally { CloseHandle(process); }
    }

    private void LogUnknownStructure(AutomationElement verifiedHome)
    {
        // Diagnostic metadata only: no Name, Value, account fields or other app
        // windows are read. Qt may replace its compact panel with a full-width
        // page; these static object IDs let us recognize a supported layout.
        if (Environment.TickCount64 - lastStructureAttempt < 5000) return;
        lastStructureAttempt = Environment.TickCount64;
        try
        {
            var elements = verifiedHome.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            var identities = new SortedSet<string>(StringComparer.Ordinal);
            foreach (AutomationElement element in elements)
            {
                var node = ReadIdentity(element);
                var id = node.AutomationId;
                var type = node.ClassName;
                if (Regex.IsMatch(id, @"^[A-Za-z_][A-Za-z0-9_.]{0,119}$") &&
                    Regex.IsMatch(id, @"control|connect|local|home|titleLabel", RegexOptions.IgnoreCase) &&
                    Regex.IsMatch(type, @"^[A-Za-z_][A-Za-z0-9_.]{0,119}$"))
                    identities.Add(id + " (" + type + ")");
                if (identities.Count >= 64) break;
            }
            var signature = string.Join(" | ", identities);
            if (signature == lastStructureSignature) return;
            lastStructureSignature = signature;
            RecoveryStore.WriteLog($"UU controller unknown layout: {elements.Count} nodes; object IDs/classes only: {signature}");
        }
        catch (Exception ex)
        {
            RecoveryStore.WriteLog($"UU controller layout diagnostic exception: {ex.GetType().Name}, HRESULT 0x{ex.HResult:X8}.");
        }
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(nint process, uint flags, StringBuilder path, ref int characters);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
