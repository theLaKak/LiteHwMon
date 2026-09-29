using System.Runtime.InteropServices;

namespace LiteHwMon.Core;

/// <summary>
/// NVML 直连回退：NVIDIA 驱动自带 nvml.dll（System32，无需管理员权限）。
/// 当 LibreHardwareMonitor 未提供显存占用/总量时用读取。
/// </summary>
public static class Nvml
{
    private static bool _initTried;
    private static bool _ok;
    private static readonly object Sync = new();

    [DllImport("nvml.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int nvmlInit_v2();

    [DllImport("nvml.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int nvmlShutdown();

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [DllImport("nvml.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensorType, out uint temp);

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [DllImport("nvml.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int nvmlDeviceGetClockInfo(IntPtr device, int clockType, out uint clockMhz);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);

    private const int NvmlTemperatureGpu = 0;
    private const int NvmlClockGraphics = 0;

    /// <summary>GPU 核心温度（°C）。</summary>
    public static bool TryGetTemperature(uint index, out float celsius)
    {
        celsius = float.NaN;
        if (!EnsureInit()) return false;
        try
        {
            if (nvmlDeviceGetHandleByIndex_v2(index, out IntPtr dev) != 0) return false;
            if (nvmlDeviceGetTemperature(dev, NvmlTemperatureGpu, out uint t) != 0) return false;
            if (t is 0 or > 150) return false;
            celsius = t;
            return true;
        }
        catch { return false; }
    }

    /// <summary>GPU 核心占用率（%）。</summary>
    public static bool TryGetUtilization(uint index, out float pct)
    {
        pct = float.NaN;
        if (!EnsureInit()) return false;
        try
        {
            if (nvmlDeviceGetHandleByIndex_v2(index, out IntPtr dev) != 0) return false;
            if (nvmlDeviceGetUtilizationRates(dev, out var u) != 0) return false;
            pct = Math.Clamp(u.Gpu, 0u, 100u);
            return true;
        }
        catch { return false; }
    }

    /// <summary>GPU 图形核心频率（MHz）。</summary>
    public static bool TryGetClock(uint index, out float mhz)
    {
        mhz = float.NaN;
        if (!EnsureInit()) return false;
        try
        {
            if (nvmlDeviceGetHandleByIndex_v2(index, out IntPtr dev) != 0) return false;
            if (nvmlDeviceGetClockInfo(dev, NvmlClockGraphics, out uint c) != 0) return false;
            if (c == 0) return false;
            mhz = c;
            return true;
        }
        catch { return false; }
    }

    /// <summary>GPU 功耗（W）。</summary>
    public static bool TryGetPower(uint index, out float watts)
    {
        watts = float.NaN;
        if (!EnsureInit()) return false;
        try
        {
            if (nvmlDeviceGetHandleByIndex_v2(index, out IntPtr dev) != 0) return false;
            if (nvmlDeviceGetPowerUsage(dev, out uint mw) != 0) return false;
            if (mw == 0) return false;
            watts = mw / 1000f;
            return true;
        }
        catch { return false; }
    }

    private static bool EnsureInit()
    {
        if (_initTried) return _ok;
        lock (Sync)
        {
            if (_initTried) return _ok;
            _initTried = true;
            try
            {
                _ok = nvmlInit_v2() == 0;
            }
            catch (DllNotFoundException) { _ok = false; }
            catch (EntryPointNotFoundException) { _ok = false; }
            return _ok;
        }
    }

    /// <summary>按设备序号取显存 used/total（GB）；不可用返回 false。</summary>
    public static bool TryGetMemory(uint index, out float usedGB, out float totalGB)
    {
        usedGB = totalGB = float.NaN;
        if (!EnsureInit()) return false;
        try
        {
            if (nvmlDeviceGetHandleByIndex_v2(index, out IntPtr dev) != 0) return false;
            if (nvmlDeviceGetMemoryInfo(dev, out var mem) != 0) return false;
            const double gb = 1024.0 * 1024 * 1024;
            usedGB = (float)(mem.Used / gb);
            totalGB = (float)(mem.Total / gb);
            return totalGB > 0;
        }
        catch { return false; }
    }

    public static void Shutdown()
    {
        if (!_ok) return;
        try { nvmlShutdown(); } catch { }
        _ok = false;
        _initTried = true;
    }
}
