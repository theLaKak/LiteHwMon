using LiteHwMon.Core;

namespace LiteHwMon.Ui;

/// <summary>
/// 指标说明文案。主窗口磁贴上的 ? 图标与 OSD 设置里的 ? 图标共用同一份内容，
/// 避免同一个参数在两处说法不一致。
/// </summary>
public static class MetricHelp
{
    /// <summary>硬盘活动率：概念不直观，且常被误当成“磁盘速度”。</summary>
    public const string DiskActivity =
        "硬盘活动率（%）\n" +
        "\n" +
        "是什么：采样周期内磁盘处于忙碌状态的时间占比。100% 表示该周期内磁盘一直在处理 I/O 请求，0% 表示完全空闲。\n" +
        "\n" +
        "数值含义：它衡量的是“忙不忙”，不是“快不快”。活动率与吞吐量没有固定关系 —— 大量小文件随机读写可能长时间 100% 但速度很低；单个大文件顺序读写也可能只占 20% 就跑满带宽。\n" +
        "\n" +
        "数据来源：优先读取硬盘 SMART 的 Activity 传感器（LibreHardwareMonitor）；读取不到时回退到 Windows 性能计数器 PhysicalDisk\\% Idle Time，按“100 − 空闲率”换算。\n" +
        "\n" +
        "适用场景：判断卡顿是否由磁盘引起；观察后台更新、杀毒扫描、索引服务对磁盘的持续占用；确认 SSD 是否被长时间写满。";

    /// <summary>返回该指标的说明；null 表示不需要 ? 图标。</summary>
    public static string? For(ReadingSpec spec) =>
        spec.Device == DeviceKind.Disk && spec.Metric == MetricKind.Load ? DiskActivity : null;
}
