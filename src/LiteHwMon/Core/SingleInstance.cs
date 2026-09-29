using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace LiteHwMon.Core;

/// <summary>
/// 单实例守卫：命名互斥体保证同一会话只有一个监控进程，并区分三种情况：
/// 1) 已有实例正常运行（把它的窗口叫回前台，而不是弹“已经启动”）；
/// 2) 上一进程尚未退出（提权重启的父子交接），短暂等待其让出互斥体；
/// 3) 上次退出留下的残留进程（窗口已无响应），提示用户结束后继续启动。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\LiteHwMon_Instance";

    /// <summary>
    /// 注册消息：请求已有实例显示主窗口。注册消息在 UIPI 白名单内，
    /// 因此普通权限的实例也能唤醒以管理员权限运行的实例。
    /// </summary>
    public static readonly int RestoreMessageId = RegisterWindowMessageW("LiteHwMon.RestoreWindowMessage.v1");

    private Mutex? _mutex;
    private bool _owned;

    public bool IsOwner => _owned;

    /// <summary>在给定时间内尝试取得单实例所有权。</summary>
    public bool TryAcquire(TimeSpan wait)
    {
        if (_owned) return true;
        try
        {
            _mutex ??= new Mutex(false, MutexName);
            _owned = _mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            _owned = true; // 上一进程异常结束，互斥体转交给我们
        }
        catch
        {
            _owned = false;
        }
        return _owned;
    }

    public void Release()
    {
        if (!_owned) return;
        try { _mutex?.ReleaseMutex(); } catch { }
        _owned = false;
    }

    public void Dispose()
    {
        Release();
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
    }

    // ------------------------------------------------------------ 进程探测

    private static string InstanceProcessName
    {
        get
        {
            string? path = Environment.ProcessPath;
            string name = string.IsNullOrEmpty(path) ? "LiteHwMon" : Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrEmpty(name) ? "LiteHwMon" : name;
        }
    }

    private static List<Process> OtherInstances()
    {
        var list = new List<Process>();
        try
        {
            foreach (var p in Process.GetProcessesByName(InstanceProcessName))
            {
                if (p.Id == Environment.ProcessId) { p.Dispose(); continue; }
                list.Add(p);
            }
        }
        catch { /* 枚举失败时按“没有其他实例”处理 */ }
        return list;
    }

    /// <summary>
    /// 把已有实例的主窗口叫回前台；找到可唤醒的实例返回 true。
    ///
    /// 判定顺序很重要：跨完整性级别（本程序通常以管理员运行，而第二次启动往往不是）时，
    /// WM_NULL 探测可能被 UIPI 拦下而返回“无响应”。此时若直接当成残留进程处理，
    /// 就会对着一个完全正常的实例弹“已在运行”。因此只有在
    /// “探测不到响应 **且** 我们有权结束它”时才认定为残留。
    /// 注册消息本身在 UIPI 白名单内，可以跨权限投递。
    /// </summary>
    public static bool ActivateRunningInstance()
    {
        bool found = false;
        foreach (var p in OtherInstances())
        {
            try
            {
                var windows = TopLevelWindows(p.Id);
                if (windows.Count == 0) continue; // 没有任何窗口，交给残留进程流程

                bool responsive = windows.Any(IsResponsive);
                if (!responsive && CanTerminate(p.Id))
                    continue; // 无响应且我们有权结束 → 真正的残留进程

                // 响应正常，或无法结束（提权实例、探测被拦）→ 都按“正在运行”处理并尝试唤醒
                foreach (var h in windows)
                    PostMessageW(h, RestoreMessageId, IntPtr.Zero, IntPtr.Zero);
                found = true;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return found;
    }

    /// <summary>
    /// 找出残留进程：我们能结束它（非提权 / 非其它会话），且它的顶层窗口全部已无响应。
    /// 正常退出不会留下这种进程；正在初始化的新实例由年龄判断排除。
    /// </summary>
    public static List<Process> FindStaleInstances()
    {
        var stale = new List<Process>();
        foreach (var p in OtherInstances())
        {
            bool keep = false;
            try
            {
                double age = (DateTime.Now - p.StartTime).TotalSeconds;
                bool anyAlive = TopLevelWindows(p.Id).Any(IsResponsive);
                keep = !anyAlive && age > 2 && CanTerminate(p.Id);
            }
            catch
            {
                keep = false; // 无法判断时保守处理，绝不误杀
            }

            if (keep) stale.Add(p);
            else p.Dispose();
        }
        return stale;
    }

    /// <summary>结束给定进程，返回成功结束的数量。</summary>
    public static int Terminate(IEnumerable<Process> processes)
    {
        int killed = 0;
        foreach (var p in processes)
        {
            try
            {
                p.Kill();
                p.WaitForExit(3000);
                killed++;
            }
            catch { /* 提权实例可能拒绝结束，交给上层提示 */ }
            finally { p.Dispose(); }
        }
        return killed;
    }

    private static List<IntPtr> TopLevelWindows(int pid)
    {
        var result = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint owner);
            if (owner == (uint)pid) result.Add(h);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>
    /// 窗口所属 UI 线程是否仍在处理消息。用 WM_NULL 探测，SMTO_ABORTIFHUNG 让挂起的线程
    /// 立刻或超时后返回 0。注意只依据返回值判断：GetLastWin32Error 在成功时也可能残留无关错误码。
    /// </summary>
    private static bool IsResponsive(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            IntPtr ok = SendMessageTimeoutW(hwnd, 0x0000 /*WM_NULL*/, IntPtr.Zero, IntPtr.Zero,
                0x0002 /*SMTO_ABORTIFHUNG*/, 500, out _);
            return ok != IntPtr.Zero;
        }
        catch
        {
            return true; // 探测本身失败时按存活处理
        }
    }

    /// <summary>是否有权限结束该进程（提权实例 / 其它会话会返回 false）。</summary>
    private static bool CanTerminate(int pid)
    {
        IntPtr h = IntPtr.Zero;
        try
        {
            h = OpenProcess(ProcessTerminate, false, pid);
            return h != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (h != IntPtr.Zero) CloseHandle(h);
        }
    }

    // ------------------------------------------------------------ Win32

    private const uint ProcessTerminate = 0x0001;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegisterWindowMessageW(string message);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
