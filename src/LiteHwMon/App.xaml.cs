using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using LiteHwMon.Core;

namespace LiteHwMon;

public partial class App : Application
{
    private SingleInstanceGuard? _guard;
    private int _exiting;

    public static AppSettings Settings { get; private set; } = new();
    public static bool IsElevated { get; private set; }
    public static bool ElevatedLaunchDeclined { get; private set; }

    /// <summary>已进入“完全退出”流程：窗口据此放行关闭，避免重复询问。</summary>
    public static bool IsExiting => (Current as App)?._exiting == 1;

    public App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnDispatcherException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        IsElevated = CheckElevated();
        Settings = AppSettings.Load();

        // 提权重启出来的子进程带 --relaunched：只为防止无限提权循环
        bool relaunch = e.Args is ["--relaunched", ..];

        // 脚本化入口：只安装 PawnIO 后端然后退出（UI 上的「安装 PawnIO」走同一条代码路径）
        if (e.Args.Length > 0 && e.Args[0] == "--install-pawnio")
        {
            InstallPawnIoAndExit(e.Args.Length > 1 ? e.Args[1] : null);
            return;
        }

        // 先提权、再抢单实例。
        // 顺序很关键：跨权限级别时 UIPI 会拦下 PostMessage，普通权限的第二个实例
        // 无法把已在运行的管理员实例窗口叫出来，只会弹“已在运行”。
        // 先提权保证两个实例处于同一权限级别，唤醒才一定成功。
        if (Settings.Elevate && !IsElevated && !relaunch)
        {
            if (TryRelaunchElevated())
            {
                Shutdown(0);
                return;
            }
            ElevatedLaunchDeclined = true; // 用户拒绝 UAC，未提权继续运行
        }

        if (!AcquireSingleInstance()) return;
        new MainWindow().Show();
    }

    // ---------------------------------------------------------------- 单实例

    /// <summary>--install-pawnio [日志文件]：安装 PawnIO 后端后退出（供 UI 与脚本共用同一条路径）。</summary>
    private void InstallPawnIoAndExit(string? reportPath)
    {
        PawnIoInstaller.Result result;
        try
        {
            result = PawnIoInstaller.Install();
        }
        catch (Exception ex)
        {
            result = new PawnIoInstaller.Result(false, "安装异常：" + ex);
        }

        LogLine(result.Ok ? "OK: " + result.Message : "FAILED: " + result.Message);
        if (!string.IsNullOrEmpty(reportPath))
        {
            try { File.WriteAllText(reportPath, (result.Ok ? "OK" : "FAILED") + Environment.NewLine + result.Message); }
            catch { }
        }
        Shutdown(result.Ok ? 0 : 1);
    }

    private static void LogLine(string text)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            File.AppendAllText(Path.Combine(AppSettings.DataDir, "pawnio-install.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}\r\n");
        }
        catch { }
    }

    /// <summary>
    /// 取得单实例所有权。取不到时依次尝试：唤醒已有实例 → 等上一进程收尾 →
    /// 清理“上次未正常退出”的残留进程。
    /// </summary>
    private bool AcquireSingleInstance()
    {
        _guard = new SingleInstanceGuard();
        if (_guard.TryAcquire(TimeSpan.Zero)) return true;

        // 已有实例在跑：把它的窗口叫回前台（用户多半只是以为程序没启动）
        if (SingleInstanceGuard.ActivateRunningInstance())
        {
            Shutdown(0);
            return false;
        }

        // 互斥体持有者可能正在收尾，稍等再试
        if (_guard.TryAcquire(TimeSpan.FromSeconds(2))) return true;

        // 仍被占着 → 只可能是没有窗口、也不响应消息的残留进程
        var stale = SingleInstanceGuard.FindStaleInstances();
        if (stale.Count > 0 && ConfirmTerminateStale(stale))
        {
            int killed = SingleInstanceGuard.Terminate(stale);
            if (killed == 0)
            {
                MessageBox.Show(
                    "残留进程无法结束（可能正在以管理员权限运行）。\r\n" +
                    "请打开任务管理器结束 LiteHwMon.exe 后重试。",
                    "LiteHwMon", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(0);
                return false;
            }
            if (_guard.TryAcquire(TimeSpan.FromSeconds(3))) return true;
        }

        MessageBox.Show(
            "轻量硬件监控已在运行。\r\n\r\n" +
            "如果看不到窗口，请在系统托盘图标上双击以显示主窗口。\r\n" +
            "（若它正以管理员权限运行，请同样以管理员身份运行本程序来唤起它）",
            "LiteHwMon", MessageBoxButton.OK, MessageBoxImage.Information);
        Shutdown(0);
        return false;
    }

    private static bool ConfirmTerminateStale(IReadOnlyList<Process> stale)
    {
        string pids = string.Join("、", stale.Select(p => p.Id.ToString()));
        return MessageBox.Show(
            $"检测到 LiteHwMon 上次未能正常退出，仍有 {stale.Count} 个残留进程（PID {pids}）占用着运行状态。\r\n\r\n" +
            "是否结束这些残留进程并继续启动？（它们已无窗口，结束不会丢失数据）",
            "LiteHwMon", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes) == MessageBoxResult.Yes;
    }

    // ---------------------------------------------------------------- 退出

    /// <summary>
    /// 请求完全退出。confirm=true 时先二次确认；用户取消则继续后台运行。
    /// 托盘「退出应用」、标题栏关闭按钮、Alt+F4 都走这里。
    /// </summary>
    public static void RequestExit(bool confirm) => (Current as App)?.ExitApplication(confirm);

    private void ExitApplication(bool confirm)
    {
        if (Interlocked.CompareExchange(ref _exiting, 1, 0) != 0) return; // 已在退出流程中

        if (confirm && !ConfirmExit())
        {
            Volatile.Write(ref _exiting, 0); // 用户取消 → 继续后台运行
            return;
        }

        ShutdownApplication();
    }

    private bool ConfirmExit()
    {
        const string text =
            "确定要完全退出应用吗？\r\n\r\n" +
            "将关闭主窗口与 OSD 悬浮窗，停止监控与全部后台线程。\r\n" +
            "若只想让它在后台继续运行，请选择“否”。";

        var main = Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible);
        return main != null
            ? MessageBox.Show(main, text, "退出 LiteHwMon", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes
            : MessageBox.Show(text, "退出 LiteHwMon", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    /// <summary>依序释放全部资源并结束进程；任何一步失败都不能阻止退出。</summary>
    private void ShutdownApplication()
    {
        try { Settings.Save(); } catch { }

        // 1) 主窗口收尾：自身统计定时器、监控引擎、托盘图标、OSD 悬浮窗
        foreach (var w in Windows.OfType<MainWindow>().ToArray())
        {
            try { w.ShutdownServices(); } catch { }
        }

        // 2) 关闭所有窗口（OSD / OSD 设置 / 主窗口）
        foreach (Window w in Windows.Cast<Window>().ToArray())
        {
            try { w.Close(); } catch { }
        }

        // 3) 释放单实例锁，让用户能立刻重新启动
        try { _guard?.Release(); } catch { }

        // 4) 兜底看门狗：万一驱动/内核 IO 卡住导致优雅退出无法完成，也绝不残留进程
        StartExitWatchdog();

        Shutdown(0);
    }

    private static void StartExitWatchdog()
    {
        var watchdog = new Thread(() =>
        {
            try { Thread.Sleep(3000); } catch { }
            Environment.Exit(0); // 后台线程：进程正常退出时随之消失，卡住时强制收尾
        })
        {
            IsBackground = true,
            Name = "LiteHwMon.ExitWatchdog",
        };
        watchdog.Start();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        Interlocked.Exchange(ref _exiting, 1);
        ShutdownApplication(); // 注销/关机时不询问
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Settings.Save(); } catch { }
        try { _guard?.Dispose(); } catch { } // 内部会 Release 互斥体
        _guard = null;
        base.OnExit(e);
    }

    // ---------------------------------------------------------------- 提权

    /// <summary>以管理员身份重新启动自身；成功返回 true。单实例锁保持持有，由子进程接管。</summary>
    public static bool TryRelaunchElevated()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "--relaunched",
            });
            return true;
        }
        catch
        {
            return false; // 用户取消了 UAC
        }
    }

    private static bool CheckElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // ---------------------------------------------------------------- 诊断

    private static void LogError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            File.AppendAllText(Path.Combine(AppSettings.DataDir, "error.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}\r\n\r\n");
        }
        catch { /* 日志失败不应影响程序 */ }
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);
        MessageBox.Show($"发生未处理的错误，程序将继续运行。\r\n{e.Exception.Message}", "LiteHwMon",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
