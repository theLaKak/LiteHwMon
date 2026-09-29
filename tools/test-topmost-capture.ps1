# Test: set the MAIN window topmost, capture, then restore. ASCII only.
$sig = @"
using System;
using System.Runtime.InteropServices;
public static class ZTest {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
}
"@
Add-Type -TypeDefinition $sig
$proc = Get-Process LiteHwMon -ErrorAction SilentlyContinue
if (-not $proc) { Write-Host "not running"; exit }
$main = $proc.MainWindowHandle
# HWND_TOPMOST = -1, SWP_NOMOVE|SWP_NOSIZE = 0x3
[ZTest]::SetWindowPos($main, [IntPtr](-1), 0, 0, 0, 0, 0x3) | Out-Null
Write-Host "main window set topmost"
Start-Sleep -Milliseconds 800
& "$PSScriptRoot\capture-osd.ps1"
Start-Sleep -Milliseconds 300
# restore NOTOPMOST (-2)
[ZTest]::SetWindowPos($main, [IntPtr](-2), 0, 0, 0, 0, 0x3) | Out-Null
Write-Host "restored"
