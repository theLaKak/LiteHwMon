using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LiteHwMon.Core;

/// <summary>
/// 特权后端探测。
///
/// 背景：LibreHardwareMonitor 0.9.5 起用 PawnIO 取代了 WinRing0（PR #1857），
/// 其自带的旧 WinRing0 分支被精简掉了 PCI 配置空间与 IO 端口访问，只剩 MSR 读写。
/// 实测本机该驱动只支持 6 个 IOCTL（MSR 读写 + 4 个桩），IO 端口读取恒返回 ERROR_GEN_FAILURE。
/// 因此：
///   - CPU 功耗：本程序自带 WinRing0 直读 RAPL MSR，**不依赖 PawnIO**；
///   - CPU 温度（AMD Tctl/Tdie）、主板 SuperIO 风扇等：需要 PawnIO（RyzenSMU / LpcIO 模块）。
/// </summary>
public static class PrivilegedBackend
{
    // -1 = 尚未探测，0 = 未安装，1 = 已安装
    private static int _pawnIo = -1;

    /// <summary>PawnIO 是否已安装（判断方式与 LibreHardwareMonitor 自身一致）。</summary>
    public static bool PawnIoInstalled
    {
        get
        {
            int cached = Volatile.Read(ref _pawnIo);
            if (cached >= 0) return cached == 1;
            int v = DetectPawnIo() ? 1 : 0;
            Volatile.Write(ref _pawnIo, v);
            return v == 1;
        }
    }

    /// <summary>安装/卸载 PawnIO 之后丢弃缓存，让下次判定重新探测。</summary>
    public static void InvalidatePawnIoCache() => Volatile.Write(ref _pawnIo, -1);

    /// <summary>官方下载页（给用户自行安装的入口，程序不会静默安装内核驱动）。</summary>
    public const string PawnIoDownloadUrl = PawnIoInstaller.DownloadUrl;

    public const string PawnIoHomeUrl = PawnIoInstaller.HomeUrl;

    /// <summary>是否 AMD 平台（其温度最依赖 PawnIO 的 RyzenSMU 模块）。</summary>
    public static bool IsAmdPlatform(string cpuName) =>
        cpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
        cpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) ||
        cpuName.Contains("Athlon", StringComparison.OrdinalIgnoreCase) ||
        cpuName.Contains("EPYC", StringComparison.OrdinalIgnoreCase);

    private static bool DetectPawnIo()
    {
        // 1) 注册表卸载项：与 LHM 的检测方式一致，无需管理员权限
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                if (key != null) return true;
            }
            catch { /* 读注册表失败时继续用设备名判断 */ }
        }

        // 2) 直接尝试打开设备（需要管理员权限；失败不代表未安装，故只作为补充判断）
        IntPtr h = IntPtr.Zero;
        try
        {
            h = CreateFileW(@"\\?\GLOBALROOT\Device\PawnIO", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            return h != IntPtr.Zero && h != new IntPtr(-1);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (h != IntPtr.Zero && h != new IntPtr(-1)) CloseHandle(h);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
