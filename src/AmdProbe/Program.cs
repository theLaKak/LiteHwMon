using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using static Native;

// Probe: extract WinRing0x64.sys, install/start driver service, verify SMN temperature + RAPL power access.
Console.OutputEncoding = Encoding.UTF8;

string nugetDll = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".nuget", "packages", "librehardwaremonitorlib", "0.9.4", "lib", "netstandard2.0", "LibreHardwareMonitorLib.dll");
string outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LiteHwMon", "Resources");
outDir = Path.GetFullPath(outDir);

// ---- 1. extract driver (resource = FF prefix + gzip) ----
string sysFile = Path.Combine(outDir, "WinRing0x64.sys");
if (!File.Exists(sysFile) || new FileInfo(sysFile).Length != 14544)
{
    var asm = System.Reflection.Assembly.LoadFrom(nugetDll);
    using (var res = asm.GetManifestResourceStream("LibreHardwareMonitor.Resources.WinRing0x64.gz")
           ?? throw new Exception("resource missing"))
    {
        var all = new byte[res.Length];
        res.ReadExactly(all);
        using var ms = new MemoryStream(all, 1, all.Length - 1); // skip FF prefix
        using var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress);
        Directory.CreateDirectory(outDir);
        using var f = File.Create(sysFile);
        gz.CopyTo(f);
    }
}
Console.WriteLine($"driver file: {new FileInfo(sysFile).Length} bytes");

// ---- 2. install + start service ----
string sysPath = Path.Combine(outDir, "WinRing0x64.sys");
const string serviceName = "WinRing0";
using (var h = OpenService(serviceName))
{
    if (h.IsInvalid)
    {
        if (!CreateService(serviceName, sysPath))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Console.WriteLine("service created");
    }
    else Console.WriteLine("service already exists");
}
try
{
    if (!StartService(serviceName))
        Console.WriteLine($"StartService: {new Win32Exception(Marshal.GetLastWin32Error()).Message} (may already run)");
    else Console.WriteLine("service started");
}
catch (Exception ex) { Console.WriteLine("start: " + ex.Message); }
Thread.Sleep(500);

// ---- 3. open device (WinRing0 1.2.0 hardcodes device name) ----
IntPtr hDev = IntPtr.Zero;
foreach (var name in new[] { "WinRing0_1_2_0", "WinRing0", "WinRing0x64" })
{
    IntPtr h = CreateFileW(@$"\\.\{name}", 0xC0000000 /*GENERIC_READ|WRITE*/, 0, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);
    Console.WriteLine($"device \\..\\{name}: {(h != IntPtr.Zero && h != new IntPtr(-1) ? "OPEN" : Marshal.GetLastWin32Error().ToString())}");
    if (h != IntPtr.Zero && h != new IntPtr(-1)) { hDev = h; break; }
}
if (hDev == IntPtr.Zero) { Console.WriteLine("no device -> abort"); return; }

// ---- 3.5 validate IOCTL codes: driver version ----
uint ver = IoCtrl(hDev, 0x9C402004 /*GET_DRIVER_VERSION*/, new byte[4], 4, 4);
Console.WriteLine($"driver version ioctl = 0x{ver:X8}");

// ---- 4. validate PCI IOCTL: vendor id of 0:0:0 ----
uint pciAddr0 = 0; // bus0 dev0 fn0
uint vendor = ReadPciConfig(hDev, pciAddr0, 0x00);
Console.WriteLine($"PCI 0:0:0 vendor/device = 0x{vendor:X8} (vendor=0x{vendor & 0xFFFF:X4}, AMD=0x1022)");
if ((vendor & 0xFFFF) != 0x1022)
{
    // 扫描整个功能空间，找到能读出 PCI vendor 0x1022 的 IOCTL
    Console.WriteLine("scanning IOCTL space for PCI read...");
    for (uint func = 0x800; func <= 0x8FF; func++)
    foreach (uint method in new uint[] { 0, 1 })
    {
        uint code = 0x9C400000 | 0 | (func << 2) | method;
        byte[] input = pack(0, 0x00);
        byte[] output = new byte[4];
        bool ok = DeviceIoControl(hDev, code, input, 8, output, 4, out uint ret, IntPtr.Zero);
        uint val = BitConverter.ToUInt32(output, 0);
        if (ok && (val & 0xFFFF) == 0x1022)
            Console.WriteLine($"  MATCH code=0x{code:X8} func=0x{func:X} method={method} val=0x{val:X8}");
    }
    Console.WriteLine("scan done");

    // 同时扫描 WRITE_PCI_CONFIG:用“读改写同地址验证”无法安全扫描,改为仅列出缓冲式可用的功能号
    Console.WriteLine("scanning for functions that accept our buffer (non-error)...");
    var working = new System.Collections.Generic.List<uint>();
    for (uint func = 0x800; func <= 0x8FF; func++)
    foreach (uint method in new uint[] { 0, 1 })
    {
        uint code = 0x9C400000 | 0 | (func << 2) | method;
        byte[] input = pack(0, 0x00);
        if (DeviceIoControl(hDev, code, input, 8, input, 8, out uint ret, IntPtr.Zero))
            working.Add(code);
    }
    Console.WriteLine("working codes: " + string.Join(", ", working.Select(c => $"0x{c:X8}")));
}

// ---- 5. SMN temperature candidates ----
foreach (uint addr in new uint[] { 0x59800, 0x59808, 0x59900, 0x59940 })
{
    WritePciConfig(hDev, pciAddr0, 0x60, addr);
    uint raw = ReadPciConfig(hDev, pciAddr0, 0x64);
    float temp = ((raw >> 21) & 0x7FF) * 0.125f - 49f;
    Console.WriteLine($"SMN 0x{addr:X}: raw=0x{raw:X8} decoded={temp:0.0} C");
}

// ---- 6. RAPL power ----
uint unit = ReadMsr(hDev, 0xC0010299);
Console.WriteLine($"RAPL unit MSR 0xC0010299 = 0x{unit:X8}");
int esu = (int)((unit >> 8) & 0x1F);
Console.WriteLine($"energy unit = 2^-{esu} J");
ulong e1 = ReadMsr64(hDev, 0xC001029B);
Console.WriteLine($"energy t0 = {e1}");
Thread.Sleep(1000);
ulong e2 = ReadMsr64(hDev, 0xC001029B);
double deltaRaw = (double)(e2 - e1) % (1UL << 32);
double powerW = deltaRaw * Math.Pow(2, -esu);
Console.WriteLine($"energy t1 = {e2}  -> power = {powerW:0.00} W");

CloseHandle(hDev);
Console.WriteLine("done");

// ================= helpers =================
// WinRing0 IOCTLs: device type 0x9C40 (OLS_TYPE=40000)
//   GET_DRIVER_VERSION  = 0x9C402004  (buffered)
//   READ_MSR            = 0x9C402084  (buffered: in=DWORD index, out=UINT64)
//   WRITE_MSR           = 0x9C402088
//   READ_PCI_CONFIG     = 0x9C402104  (buffered: in={pci,reg}, out=data)
//   WRITE_PCI_CONFIG    = 0x9C402108  (buffered: in={pci,reg,data})

static uint ReadPciConfig(IntPtr h, uint pciAddr, uint reg) =>
    IoCtrl(h, 0x9C402104, pack(pciAddr, reg), 8, 4);

static void WritePciConfig(IntPtr h, uint pciAddr, uint reg, uint value)
{
    byte[] input = new byte[12];
    BitConverter.GetBytes(pciAddr).CopyTo(input, 0);
    BitConverter.GetBytes(reg).CopyTo(input, 4);
    BitConverter.GetBytes(value).CopyTo(input, 8);
    DeviceIoControl(h, 0x9C402108, input, (uint)input.Length, input, (uint)input.Length, out _, IntPtr.Zero);
}

static ulong ReadMsr64(IntPtr h, uint index)
{
    byte[] output = new byte[8];
    if (!DeviceIoControl(h, 0x9C402084 /*READ_MSR*/, BitConverter.GetBytes(index), 4, output, 8, out _, IntPtr.Zero))
        return 0xFFFFFFFFFFFFFFFF;
    return BitConverter.ToUInt64(output, 0);
}

static uint ReadMsr(IntPtr h, uint index) => (uint)(ReadMsr64(h, index) & 0xFFFFFFFF);

static byte[] pack(uint a, uint b)
{
    byte[] r = new byte[8];
    BitConverter.GetBytes(a).CopyTo(r, 0);
    BitConverter.GetBytes(b).CopyTo(r, 4);
    return r;
}

static uint IoCtrl(IntPtr h, uint code, byte[] input, uint inLen, uint outLen)
{
    byte[] output = new byte[outLen];
    if (!DeviceIoControl(h, code, input, (uint)input.Length, output, outLen, out uint ret, IntPtr.Zero))
        return 0xFFFFFFFF;
    return BitConverter.ToUInt32(output, 0);
}

static ServiceHandle OpenService(string name)
{
    var scm = OpenSCManagerW(null, null, 0xF003F /*ALL_ACCESS*/);
    if (scm == IntPtr.Zero) throw new Win32Exception();
    var svc = OpenServiceW(scm, name, 0xF01FF);
    CloseServiceHandle(scm);
    return new ServiceHandle(svc);
}

static bool CreateService(string name, string binPath)
{
    var scm = OpenSCManagerW(null, null, 0xF003F);
    if (scm == IntPtr.Zero) throw new Win32Exception();
    var svc = CreateServiceW(scm, name, name, 0xF01FF, 0x1 /*KERNEL_DRIVER*/, 3 /*DEMAND_START*/,
        1 /*ERROR_NORMAL*/, binPath, null, IntPtr.Zero, null, null, null);
    bool ok = svc != IntPtr.Zero;
    if (!ok) Console.WriteLine($"CreateService err {Marshal.GetLastWin32Error()}");
    if (svc != IntPtr.Zero) CloseServiceHandle(svc);
    CloseServiceHandle(scm);
    return ok;
}

static bool StartService(string name)
{
    var scm = OpenSCManagerW(null, null, 0xF003F);
    var svc = OpenServiceW(scm, name, 0x10 /*START*/);
    if (svc == IntPtr.Zero) return false;
    bool ok = StartServiceW(svc, 0, IntPtr.Zero);
    CloseServiceHandle(svc);
    CloseServiceHandle(scm);
    return ok;
}

class ServiceHandle : IDisposable
{
    public IntPtr Handle { get; }
    public bool IsInvalid => Handle == IntPtr.Zero;
    public ServiceHandle(IntPtr h) => Handle = h;
    public void Dispose() { if (Handle != IntPtr.Zero) CloseServiceHandle(Handle); }
}

internal static class Native
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(IntPtr h, uint code, byte[] input, uint inSize, byte[] output, uint outSize, out uint ret, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(IntPtr h, uint code, byte[] input, uint inSize, IntPtr output, uint outSize, out uint ret, IntPtr overlapped);

    [DllImport("kernel32.dll")]
    internal static extern bool CloseHandle(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr OpenSCManagerW(string? machine, string? db, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateServiceW(IntPtr scm, string name, string display, uint access, uint type,
        uint start, uint error, string binPath, string? orderGroup, IntPtr tagId, string? dependencies,
        string? account, string? password);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool StartServiceW(IntPtr svc, int argc, IntPtr argv);

    [DllImport("advapi32.dll")]
    internal static extern bool CloseServiceHandle(IntPtr h);
}
