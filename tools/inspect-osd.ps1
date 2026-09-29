# Dump all windows of LiteHwMon process with styles. ASCII only.
$sig = @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class WinSpy2 {
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    public delegate bool EnumProc(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
}
"@
Add-Type -TypeDefinition $sig

$proc = Get-Process LiteHwMon -ErrorAction SilentlyContinue
if (-not $proc) { Write-Host "not running"; exit }
Write-Host ("pid={0} mainWindowHandle={1}" -f $proc.Id, $proc.MainWindowHandle)
$target = $proc.Id
$null = [WinSpy2]::EnumWindows({
    param($h, $lp)
    $pid2 = 0
    [WinSpy2]::GetWindowThreadProcessId($h, [ref]$pid2) | Out-Null
    if ($pid2 -eq $target) {
        $sb = New-Object System.Text.StringBuilder 256
        [WinSpy2]::GetWindowText($h, $sb, 256) | Out-Null
        $ex = [WinSpy2]::GetWindowLong($h, -20)
        $r = New-Object WinSpy2+RECT
        [WinSpy2]::GetWindowRect($h, [ref]$r) | Out-Null
        $vis = [WinSpy2]::IsWindowVisible($h)
        $cloaked = 0
        [WinSpy2]::DwmGetWindowAttribute($h, 14, [ref]$cloaked, 4) | Out-Null
        Write-Host ("hwnd={0} vis={1} cloaked={2} ex=0x{3:X8} rect=({4},{5})-({6},{7}) len={8} main={9}" -f $h, $vis, $cloaked, $ex, $r.L, $r.T, $r.R, $r.B, $sb.Length, ($h -eq $proc.MainWindowHandle))
    }
    return $true
}, [IntPtr]::Zero)
Write-Host "done"
