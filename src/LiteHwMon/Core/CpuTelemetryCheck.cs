namespace LiteHwMon.Core;

/// <summary>
/// CPU 遥测可信度自检。
///
/// 为什么需要它：本机（Ryzen 7 7735H / Rembrandt）实测发现，即使装了 PawnIO，
/// AMD SMU 给出的读数在物理上不成立——
///   * 16 线程 100% 负载、频率 4071→4360 MHz 时，"Tctl/Tdie" 反而从 71.4 ℃ 掉到 66.3 ℃；
///   * 同时在 22% 与 100% 负载下，封装功耗恒为 35.0 W（RAPL MSR 与 SMU 两条独立通路都是）。
/// 这种"负载大范围变化、封装功耗纹丝不动"的组合，说明该平台的功耗/温度遥测没有真正工作。
///
/// 判据刻意保守：必须在**观测到足够大的负载跨度**之后，功耗读数依然几乎不动，才判为可疑。
/// 因此正常平台不会误报（真实 CPU 在轻载与满载之间功耗必然变化）。
/// </summary>
public sealed class CpuTelemetryCheck
{
    private const int WindowSize = 30;
    private const double MinLoadSpreadPct = 45.0;   // 至少见过 45 个百分点的负载跨度
    private const double MaxPowerSpreadW = 0.8;     // 功耗读数跨度小于 0.8 W 视为"钉死"
    private const int MinSamples = 12;

    private readonly double[] _load = new double[WindowSize];
    private readonly double[] _power = new double[WindowSize];
    private int _count;
    private int _head;

    /// <summary>遥测可疑（功耗在负载大范围变化时仍恒定）。</summary>
    public bool Suspect { get; private set; }

    /// <summary>用于向用户解释的证据文本。</summary>
    public string Evidence { get; private set; } = "";

    public void Add(double cpuLoadPct, float? packagePowerW)
    {
        if (Suspect || double.IsNaN(cpuLoadPct)) return;
        if (packagePowerW is not { } p || p <= 0) return; // 功耗不可用时无法判断

        _load[_head] = cpuLoadPct;
        _power[_head] = p;
        _head = (_head + 1) % WindowSize;
        if (_count < WindowSize) _count++;
        if (_count < MinSamples) return;

        double minLoad = double.MaxValue, maxLoad = double.MinValue;
        double minPower = double.MaxValue, maxPower = double.MinValue;
        for (int i = 0; i < _count; i++)
        {
            if (_load[i] < minLoad) minLoad = _load[i];
            if (_load[i] > maxLoad) maxLoad = _load[i];
            if (_power[i] < minPower) minPower = _power[i];
            if (_power[i] > maxPower) maxPower = _power[i];
        }

        if (maxLoad - minLoad < MinLoadSpreadPct) return;   // 负载跨度不够，不下结论
        if (maxPower - minPower >= MaxPowerSpreadW) return; // 功耗会变 → 正常

        Suspect = true;
        Evidence = $"CPU 负载在 {minLoad:0}%–{maxLoad:0}% 之间变化，但封装功耗始终为 {minPower:0.0} W —— 该平台的功耗/温度遥测未正常工作";
    }
}
