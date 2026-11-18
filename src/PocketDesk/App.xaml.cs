using System.Diagnostics;
using System.IO;
using System.Windows;
using PocketDesk.Core;
using PocketDesk.Native;

namespace PocketDesk;

public partial class App : Application
{
    private Mutex? singleton;
    private bool ownsSingleton;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length >= 3 && e.Args[0] == "--recover-after")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var pid = int.Parse(e.Args[1]);
                var started = long.Parse(e.Args[2]);
                try { using var parent = Process.GetProcessById(pid); if (parent.StartTime.ToUniversalTime().Ticks == started) parent.WaitForExit(); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                // Serialize recovery with a freshly restarted main process.
                using var recoveryLock = new Mutex(false, "Local\\PocketDesk.Main");
                bool acquired;
                try { acquired = recoveryLock.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (acquired)
                {
                    try
                    {
                        var session = new WorkspaceSession(new RecoveryStore());
                        UuControllerSnapshot? controller = null;
                        if (session.RequiresAutomaticRecoveryEvidence)
                        {
                            // The helper must never show/activate UU. PollAsync
                            // runs a bounded read-only MTA probe; stalled UIA
                            // returns Unknown, and a late result is discarded.
                            using var reader = new UuControllerService(timeout: TimeSpan.FromMilliseconds(1200));
                            controller = reader.PollAsync().GetAwaiter().GetResult();
                        }
                        var decision = session.AutomaticRecoveryDecision(controller);
                        if (decision == AutomaticRecoveryDecision.Defer)
                            RecoveryStore.WriteLog("恢复助手：无法确认当前 UU 控制端，保留完整恢复记录，未改显示模式或窗口。");
                        else
                            foreach (var error in session.RestoreAll(preserveCurrentDisplay: decision == AutomaticRecoveryDecision.Preserve))
                                RecoveryStore.WriteLog("恢复助手：" + error);
                    }
                    finally { recoveryLock.ReleaseMutex(); }
                }
            }
            catch (Exception ex) { RecoveryStore.WriteLog("恢复助手：" + ex); }
            Shutdown();
            return;
        }
        singleton = new Mutex(false, "Local\\PocketDesk.Main");
        try { ownsSingleton = singleton.WaitOne(0); } catch (AbandonedMutexException) { ownsSingleton = true; }
        if (!ownsSingleton) { MessageBox.Show("手机工作台已经运行或正在恢复，请稍后从任务栏打开。", "PocketDesk"); Shutdown(); return; }
        var buildVersion = typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        RecoveryStore.WriteLog($"PocketDesk started: {buildVersion ?? "unknown version"}.");
        DispatcherUnhandledException += (_, args) =>
        {
            RecoveryStore.WriteLog(args.Exception.ToString());
            MessageBox.Show("操作未完成：" + args.Exception.Message + "\n恢复记录已保留。", "PocketDesk");
            args.Handled = true;
        };
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            RecoveryStore.WriteLog(ex.ToString());
            MessageBox.Show("无法启动：" + ex.Message + "\n数据位置：" + RecoveryStore.DataDirectory, "PocketDesk");
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsSingleton) singleton?.ReleaseMutex();
        singleton?.Dispose();
        base.OnExit(e);
    }
}
