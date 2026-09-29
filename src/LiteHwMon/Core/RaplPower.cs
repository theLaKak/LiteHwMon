using System.Runtime.InteropServices;
using System.IO;

namespace LiteHwMon.Core;

/// <summary>
/// 可选回退：通过 WinRing0 设备直读 RAPL（运行平均功耗限制）MSR，得到 CPU 封装功耗。
/// 仅当 WinRing0 驱动可用时工作；不可用时返回 null，绝不伪造数值。
/// 设备名与 IOCTL 与项目内 AmdProbe / README 描述的 WinRing0 方案保持一致。
/// </summary>
public sealed class RaplPowerReader : IDisposable
{
    private const string DevicePath = @"\\.\WinRing0_1_2_0";
    private const string ServiceName = "WinRing0";
    private const uint MsrRead = 0x9C402084;  // OLS_READ_MSR（缓冲式，输入 4 字节索引，输出 8 字节）
    private const uint MsrUnit = 0xC0010299;   // AMD RAPL 单位
    private const uint MsrEnergy = 0xC001029B; // AMD 封装能耗计数器（32 位）

    private IntPtr _handle;
    private double _energyUnit;
    private uint _lastEnergy;
    private bool _hasLast;
    private readonly object _sync = new();
    private readonly System.Diagnostics.Stopwatch _sw = new();

    public bool Available => IsValidHandle(_handle);

    public RaplPowerReader() => EnsureDevice();

    /// <summary>自上次采样以来的平均封装功耗（W）；首次采样返回 null（需要两个采样点）。</summary>
    public float? Sample()
    {
        lock (_sync)
        {
            if (!Available) return null;
            if (!ReadMsr(MsrEnergy, out uint e)) return null;
            if (!_hasLast)
            {
                _lastEnergy = e;
                _hasLast = true;
                _sw.Restart();
                return null;
            }

            double seconds = _sw.Elapsed.TotalSeconds;
            _sw.Restart();
            uint delta = unchecked(e - _lastEnergy); // 32 位自然回绕
            _lastEnergy = e;
            if (seconds < 0.01) return null;

            double watts = delta * _energyUnit / seconds;
            if (double.IsNaN(watts) || watts < 0 || watts > 1000) return null;
            return (float)watts;
        }
    }

    private void EnsureDevice()
    {
        try
        {
            _handle = OpenDevice();
            if (!Available && TryStartService())
            {
                // 服务刚启动，设备句柄可能需要短暂等待
                for (int i = 0; i < 10 && !Available; i++)
                {
                    System.Threading.Thread.Sleep(50);
                    _handle = OpenDevice();
                }
            }
            if (!Available)
            {
                _handle = IntPtr.Zero;
                return;
            }

            if (!ReadMsr(MsrUnit, out uint unit) || unit == 0)
            {
                Close();
                return;
            }
            int esu = (int)((unit >> 8) & 0x1F);
            _energyUnit = Math.Pow(2, -esu);
        }
        catch
        {
            Close();
        }
    }

    private static IntPtr OpenDevice() =>
        CreateFileW(DevicePath, 0xC0000000, 3 /*FILE_SHARE_READ|WRITE*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);

    private bool ReadMsr(uint index, out uint value)
    {
        value = 0;
        byte[] input = BitConverter.GetBytes(index);
        byte[] output = new byte[8];
        if (!DeviceIoControl(_handle, MsrRead, input, (uint)input.Length, output, (uint)output.Length, out _, IntPtr.Zero))
            return false;
        value = BitConverter.ToUInt32(output, 0);
        return true;
    }

    private static bool TryStartService()
    {
        IntPtr scm = OpenSCManagerW(null, null, 0xF003F);
        if (scm == IntPtr.Zero) return false;
        try
        {
            IntPtr svc = OpenServiceW(scm, ServiceName, 0x0010 /*SERVICE_START*/);
            if (svc == IntPtr.Zero)
            {
                string sys = Path.Combine(AppContext.BaseDirectory, "Resources", "WinRing0x64.sys");
                if (!File.Exists(sys))
                    sys = Path.Combine(AppContext.BaseDirectory, "WinRing0x64.sys"); // 兼容根目录部署
                if (!File.Exists(sys)) return false;
                svc = CreateServiceW(scm, ServiceName, ServiceName, 0xF01FF, 1 /*KERNEL_DRIVER*/,
                    3 /*DEMAND_START*/, 1 /*ERROR_NORMAL*/, sys, null, IntPtr.Zero, null, null, null);
                if (svc == IntPtr.Zero) return false;
            }
            try
            {
                return StartServiceW(svc, 0, IntPtr.Zero);
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private void Close()
    {
        if (IsValidHandle(_handle)) { try { CloseHandle(_handle); } catch { } }
        _handle = IntPtr.Zero;
    }

    public void Dispose() => Close();

    private static bool IsValidHandle(IntPtr h) => h != IntPtr.Zero && h != new IntPtr(-1);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(IntPtr h, uint code, byte[] input, uint inSize, byte[] output, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? db, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateServiceW(IntPtr scm, string name, string display, uint access, uint type,
        uint start, uint error, string binPath, string? orderGroup, IntPtr tagId, string? dependencies,
        string? account, string? password);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(IntPtr svc, int argc, IntPtr argv);

    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr h);
}
