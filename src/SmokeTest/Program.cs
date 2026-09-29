using System.Text;
using LiteHwMon.Core;

// 控制台冒烟测试：验证监控引擎在本机能否读到数据（建议以管理员权限运行）
Console.OutputEncoding = Encoding.UTF8;

using var engine = new MonitorEngine();
int polls = 0;
using var done = new ManualResetEvent(false);
engine.PollCompleted += () => { if (Interlocked.Increment(ref polls) >= 8) done.Set(); };

var t0 = DateTime.Now;
Console.WriteLine("Initializing hardware monitor...");
engine.Start(1000);

if (!done.WaitOne(45000))
{
    Console.WriteLine("(waiting for samples timed out)");
    Console.WriteLine($"startup phase: {engine.StartupPhase}");
    Console.WriteLine($"last hardware being updated when timed out: {engine.LastUpdatingHardware}");
    Console.WriteLine($"last error: {engine.LastError ?? "(none)"}");
}

Console.WriteLine($"\nCPU: {engine.CpuName}");
Console.WriteLine($"GPU: {string.Join(" | ", engine.GpuNames)}");
Console.WriteLine($"DISK: {string.Join(" | ", engine.DiskNames)}");
Console.WriteLine($"DRIVER: {engine.DriverStatus}");
Console.WriteLine($"MEM: {engine.Sys.MemoryDetailText}   FREQ: {engine.Sys.MemoryFrequencyText}");
Console.WriteLine($"PERF: open={engine.OpenMs:0}ms firstPoll={engine.FirstPollMs:0}ms lastPoll={engine.LastPollMs:0}ms polls={engine.PollCount} in {(DateTime.Now - t0).TotalSeconds:0.0}s");
Console.WriteLine($"BREAKDOWN: cpu={engine.LhmCpuMs:0}ms gpu={engine.LhmGpuMs:0}ms storage={engine.LhmStorageMs:0}ms board={engine.LhmBoardMs:0}ms sys={engine.SysMs:0}ms");
Console.WriteLine($"FALLBACK: gpuAdapters={engine.Sys.GpuAdapters.Count} physicalDisks={engine.Sys.Disks.Count} cpuFreqEst={engine.Sys.CpuFreqMhz:0}");
Console.WriteLine();

foreach (var s in engine.Specs)
{
    string v = s.Value is null ? "N/A" : s.FormatParts(s.Value.Value).Num + " " + s.FormatParts(s.Value.Value).Unit;
    string src = s.Sensor != null ? "LHM" : (s.IsStatic ? "STC" : "FB");
    Console.WriteLine($"[{s.Device,-7}] [{src,-3}] {s.LegendName,-28} = {v}");
}

engine.Dispose();
Console.WriteLine("\nDone.");

