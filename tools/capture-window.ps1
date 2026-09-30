# PrintWindow-based per-window capture (works even if covered by other windows).
# MUST run elevated: the app runs elevated and UIPI blocks PrintWindow from a
# filtered-token process. ASCII only.
param([string]$OutDir = "")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sig = @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class PwCap {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    public delegate bool EnumProc(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
Add-Type -TypeDefinition $sig
[PwCap]::SetProcessDPIAware() | Out-Null

$dir = $PSScriptRoot
if ($OutDir -eq "") { $OutDir = (Resolve-Path (Join-Path $dir '..')).Path + '\docs' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$log = Join-Path $dir 'capture-window.log'

$setTitle = 'OSD ' + (-join @([char]0x8BBE, [char]0x7F6E))   # OSD settings window title

$proc = Get-Process LiteHwMon -ErrorAction Stop
$found = @{}
$cb = {
    param($h, $lp)
    $wpid = 0
    [PwCap]::GetWindowThreadProcessId($h, [ref]$wpid) | Out-Null
    if ($wpid -eq $proc.Id -and [PwCap]::IsWindowVisible($h)) {
        $sb = New-Object System.Text.StringBuilder 256
        [PwCap]::GetWindowText($h, $sb, 256) | Out-Null
        $t = $sb.ToString()
        if ($t.Length -gt 0 -and -not $found.ContainsKey($t)) { $found[$t] = $h }
    }
    return $true
}
$null = [PwCap]::EnumWindows($cb, [IntPtr]::Zero)
[PwCap]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null   # second pass in case first raced

$targets = @()
foreach ($k in $found.Keys) {
    if ($k.Contains('LiteHwMon')) { $targets += @(@{ h = $found[$k]; name = 'screenshot-main.png' }) }
    if ($k -eq $setTitle)         { $targets += @(@{ h = $found[$k]; name = 'screenshot-osd-settings.png' }) }
}

function Capture-Window([IntPtr]$hwnd, [string]$name) {
    $wr = New-Object PwCap+RECT
    [PwCap]::GetWindowRect($hwnd, [ref]$wr) | Out-Null
    $w = $wr.R - $wr.L; $h = $wr.B - $wr.T
    if ($w -le 0 -or $h -le 0) { throw "bad rect for $name" }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $PW_RENDERFULLCONTENT = 2
    $ok = [PwCap]::PrintWindow($hwnd, $hdc, $PW_RENDERFULLCONTENT)
    $g.ReleaseHdc($hdc)
    $g.Dispose()

    # tight-crop to DWM extended frame bounds (drops invisible resize borders)
    $ex = New-Object PwCap+RECT
    if ([PwCap]::DwmGetWindowAttribute($hwnd, 9, [ref]$ex, 16) -eq 0) {
        $ox = [Math]::Max(0, $ex.L - $wr.L); $oy = [Math]::Max(0, $ex.T - $wr.T)
        $cw = [Math]::Min($w - $ox, $ex.R - $ex.L); $ch = [Math]::Min($h - $oy, $ex.B - $ex.T)
        if ($ox -gt 0 -or $oy -gt 0 -or $cw -ne $w -or $ch -ne $h) {
            $tight = $bmp.Clone((New-Object System.Drawing.Rectangle $ox, $oy, $cw, $ch), $bmp.PixelFormat)
            $bmp.Dispose(); $bmp = $tight
        }
    }
    $out = Join-Path $OutDir $name
    $fw = $bmp.Width; $fh = $bmp.Height
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Add-Content $log ("{0}: printwindow ok={1} {2}x{3} -> {4}" -f (Get-Date -Format 'HH:mm:ss'), $ok, $fw, $fh, $out)
}
foreach ($t in $targets) { Capture-Window $t.h $t.name }
if ($targets.Count -eq 0) { Add-Content $log ("{0}: no target windows found" -f (Get-Date -Format 'HH:mm:ss')) }
