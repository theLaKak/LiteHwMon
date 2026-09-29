using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LiteHwMon.Core;

/// <summary>
/// 应用内的 PawnIO 后端安装器。
///
/// 背景：LibreHardwareMonitor 0.9.5 起把硬件访问后端换成了 PawnIO（上游 PR #1857），
/// 没有它就读不到 AMD 的 SMN（Tctl/Tdie 温度）、主板 SuperIO/EC 等数据。
/// 本程序已经自带 WinRing0 只用于 RAPL 功耗回退，**不会**用它去碰 PCI/SMN。
///
/// 这里不静默安装任何东西：由用户点击按钮触发，且下载后必须通过
/// WinVerifyTrust 验签（必须是 namazso 的 PawnIO 签名）才会执行安装。
/// </summary>
public static class PawnIoInstaller
{
    public const string DownloadUrl = "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";
    public const string HomeUrl = "https://pawnio.eu/";

    /// <summary>预期签名者（证书主题包含该字符串才认为可信）。</summary>
    private const string ExpectedSigner = "namazso";

    public sealed record Result(bool Ok, string Message);

    /// <summary>
    /// 下载 → 验签 → 以 <c>-install -silent</c> 安装。整个过程是阻塞的，调用方应放到后台线程。
    /// </summary>
    public static Result Install(IProgress<string>? progress = null)
    {
        string path = Path.Combine(Path.GetTempPath(), "LiteHwMon", "PawnIO_setup.exe");
        try
        {
            progress?.Report("正在下载 PawnIO…");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("LiteHwMon");
                byte[] data = http.GetByteArrayAsync(DownloadUrl).GetAwaiter().GetResult();
                if (data.Length < 100_000)
                    return new Result(false, "下载内容异常（文件过小），已中止。");
                File.WriteAllBytes(path, data);
            }

            progress?.Report("正在校验数字签名…");
            if (!VerifySignature(path, out string signer, out string why))
                return new Result(false, $"签名校验失败，拒绝安装。（{why}；签名者：{signer}）");

            progress?.Report("正在安装 PawnIO 驱动…");
            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = "-install -silent",
                UseShellExecute = true, // 需要提权时由系统弹出 UAC
                Verb = "runas",
            };
            using (var p = Process.Start(psi))
            {
                if (p == null) return new Result(false, "无法启动安装程序。");
                p.WaitForExit(180_000);
                if (!p.HasExited)
                    return new Result(false, "安装程序超时未结束，请手动完成安装。");
            }

            progress?.Report("正在确认安装结果…");
            for (int i = 0; i < 10 && !PrivilegedBackend.PawnIoInstalled; i++)
                Thread.Sleep(500);

            return PrivilegedBackend.PawnIoInstalled
                ? new Result(true, "PawnIO 安装完成。重启本程序后即可读取 CPU 温度。")
                : new Result(false, "安装程序已结束，但未检测到 PawnIO，请手动确认。");
        }
        catch (Exception ex)
        {
            return new Result(false, "安装失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 校验 Authenticode 签名。策略分两级：
    /// 1) 先用 WinVerifyTrust 做完整签名链校验（允许联网构建链，不做吊销检查以免卡住）；
    /// 2) 若仅因“无法在本地构建信任链”（离线 / 中间证书未缓存，返回 CERT_E_* / TRUST_E_*），
    ///    退回到“文件确实带 Authenticode 签名，且签名者主题匹配官方发布者”。
    /// 只要签名者不匹配就一律拒绝。
    /// </summary>
    private static bool VerifySignature(string file, out string signer, out string why)
    {
        signer = "(未知)";
        why = "";
        try
        {
            var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(file);
            signer = cert.Subject;
            if (!signer.Contains(ExpectedSigner, StringComparison.OrdinalIgnoreCase))
            {
                why = "签名者不是官方发布者";
                return false;
            }

            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = file,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };
            IntPtr pFile = Marshal.AllocHGlobal(fileInfo.cbStruct);
            try
            {
                Marshal.StructureToPtr(fileInfo, pFile, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = 2,           // WTD_UI_NONE
                    fdwRevocationChecks = 0,  // WTD_REVOKE_NONE
                    dwUnionChoice = 1,        // WTD_CHOICE_FILE
                    pFile = pFile,
                    dwStateAction = 0,
                    dwProvFlags = 0,          // 允许联网取中间证书/根证书
                };
                var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
                int hr = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                data.dwStateAction = 2; // WTD_STATEACTION_CLOSE
                _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

                if (hr == 0) return true;

                // 0x800Bxxxx = CERT_E_* / TRUST_E_*：签名存在但信任链未能本地验证 ⇒ 放行（已确认签名者）
                bool trustChainOnly = (hr & unchecked((int)0xFFFF0000)) == unchecked((int)0x800B0000);
                if (!trustChainOnly)
                {
                    why = $"WinVerifyTrust 返回 0x{hr:X8}";
                    return false;
                }
                return true;
            }
            finally { Marshal.FreeHGlobal(pFile); }
        }
        catch (Exception ex)
        {
            why = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    // ------------------------------------------------------------ Win32

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public int cbStruct;
        // 必须显式声明为宽字符：默认按 ANSI 编组会把非 ASCII 路径（如中文目录）写坏，
        // WinVerifyTrust 随后返回 CRYPT_E_FILE_ERROR (0x80092003)。
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public int cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public int dwUIChoice;
        public int fdwRevocationChecks;
        public int dwUnionChoice;
        public IntPtr pFile;
        public int dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public int dwProvFlags;
        public int dwUIContext;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WINTRUST_DATA data);
}
