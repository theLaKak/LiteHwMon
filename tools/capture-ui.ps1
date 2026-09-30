# Capture UI screenshots for README: main window, game OSD, OSD settings window.
# Restarts the app elevated with --osd-settings, waits for chart history,
# stages the windows side by side, then crops each window from a full-screen
# BitBlt(CAPTUREBLT) shot. Run this script ELEVATED: the app runs elevated and
# UIPI blocks window staging (MoveWindow) from a filtered-token process.
# For covered windows prefer capture-window.ps1 (PrintWindow, overlay-proof).
# ASCII only (PS5.1 parses no-BOM UTF-8 as GBK).
param(
    [int]$WarmupSeconds = 30,     # data accumulation time before capture
    [int]$BurstAtSeconds = 0,     # run a short all-core CPU burst at this second of warmup (0 = off)
    [int]$BurstDurationSeconds = 20,
    [string]$OutDir = ""
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$sig = @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class CapUi {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    public delegate bool EnumProc(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int hh, IntPtr src, int sx, int sy, int rop);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
Add-Type -TypeDefinition $sig
[CapUi]::SetProcessDPIAware() | Out-Null   # window rects == screen pixels

$dir = $PSScriptRoot
$exe = (Resolve-Path (Join-Path $dir '..\src\LiteHwMon\bin\Release\net8.0-windows\LiteHwMon.exe')).Path
if ($OutDir -eq "") { $OutDir = (Resolve-Path (Join-Path $dir '..')).Path + '\docs' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# window titles (non-ASCII built from char codes)
$osdTitle = (-join @([char]0x6E38, [char]0x620F, [char]0x76D1, [char]0x63A7)) + ' OSD'   # game-monitor OSD
$setTitle = 'OSD ' + (-join @([char]0x8BBE, [char]0x7F6E))                                # OSD settings

# --- kill existing instance (elevated) ---
if (Get-Process LiteHwMon -ErrorAction SilentlyContinue) {
    Start-Process cmd -Verb RunAs -WindowStyle Hidden -ArgumentList '/c', 'taskkill /IM LiteHwMon.exe /F' | Out-Null
    Start-Sleep -Seconds 4
}

# --- launch elevated with --osd-settings (main window + OSD + settings all open) ---
Start-Process -FilePath $exe -Verb RunAs -ArgumentList '--osd-settings' | Out-Null
Write-Host "launched, warming up $WarmupSeconds s..."

# --- optional all-core busy burst so the charts show a visible bump ---
if ($BurstAtSeconds -gt 0) {
    $jobs = @()
    for ($s = 0; $s -lt $WarmupSeconds; $s++) {
        Start-Sleep -Seconds 1
        if ($s -eq $BurstAtSeconds) {
            Write-Host "CPU burst ${BurstDurationSeconds}s..."
            for ($i = 0; $i -lt 16; $i++) {
                $jobs += Start-Job -ScriptBlock {
                    $sw = [System.Diagnostics.Stopwatch]::StartNew()
                    while ($sw.Elapsed.TotalSeconds -lt $using:BurstDurationSeconds) { }
                }
            }
        }
    }
    $jobs | Stop-Job -ErrorAction SilentlyContinue
    $jobs | Remove-Job -Force -ErrorAction SilentlyContinue
} else {
    Start-Sleep -Seconds $WarmupSeconds
}

# --- find the three windows ---
$proc = Get-Process LiteHwMon -ErrorAction Stop
$found = @{}
$cb = {
    param($h, $lp)
    $wpid = 0
    [CapUi]::GetWindowThreadProcessId($h, [ref]$wpid) | Out-Null
    if ($wpid -eq $proc.Id -and [CapUi]::IsWindowVisible($h)) {
        $sb = New-Object System.Text.StringBuilder 256
        [CapUi]::GetWindowText($h, $sb, 256) | Out-Null
        $t = $sb.ToString()
        if ($t.Length -gt 0 -and -not $found.ContainsKey($t)) { $found[$t] = $h }
    }
    return $true
}
$null = [CapUi]::EnumWindows($cb, [IntPtr]::Zero)

$hMain = $found.Keys | Where-Object { $_.Contains('LiteHwMon') } | ForEach-Object { $found[$_] } | Select-Object -First 1
$hOsd = if ($found.ContainsKey($osdTitle)) { $found[$osdTitle] } else { [IntPtr]::Zero }
$hSet = if ($found.ContainsKey($setTitle)) { $found[$setTitle] } else { [IntPtr]::Zero }
Write-Host ("windows: main={0} osd={1} settings={2}" -f $hMain, $hOsd, $hSet)
if ($hMain -eq $null -or $hMain -eq [IntPtr]::Zero -or $hOsd -eq [IntPtr]::Zero -or $hSet -eq [IntPtr]::Zero) {
    Write-Host "FOUND TITLES:"; $found.Keys | ForEach-Object { Write-Host ("  '" + $_ + "'") }
    throw "missing window"
}

# --- stage windows: main left, settings right, OSD untouched (top edge) ---
$rM = New-Object CapUi+RECT; [CapUi]::GetWindowRect($hMain, [ref]$rM) | Out-Null
$rS = New-Object CapUi+RECT; [CapUi]::GetWindowRect($hSet, [ref]$rS) | Out-Null
$scr = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$mW = $rM.R - $rM.L; $mH = $rM.B - $rM.T
$sW = $rS.R - $rS.L
$xM = 40; $yM = 150
$xS = [Math]::Min($xM + $mW + 24, $scr.Width - $sW - 8); $yS = 150
[CapUi]::MoveWindow($hMain, $xM, $yM, $mW, $mH, $true) | Out-Null
[CapUi]::MoveWindow($hSet, $xS, $yS, $sW, ($rS.B - $rS.T), $true) | Out-Null
[CapUi]::SetForegroundWindow($hMain) | Out-Null
Start-Sleep -Seconds 3   # let WPF re-render

# --- full screen BitBlt with CAPTUREBLT (includes layered windows) ---
$bmp = New-Object System.Drawing.Bitmap $scr.Width, $scr.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$sdc = [CapUi]::GetDC([IntPtr]::Zero)
$CAPTUREBLT = 0x40CC0020
[CapUi]::BitBlt($hdc, 0, 0, $scr.Width, $scr.Height, $sdc, 0, 0, $CAPTUREBLT) | Out-Null
[CapUi]::ReleaseDC([IntPtr]::Zero, $sdc) | Out-Null
$g.ReleaseHdc($hdc)
$g.Dispose()

function Save-Crop([IntPtr]$hwnd, [string]$name, [int]$pad) {
    $r = New-Object CapUi+RECT
    if ([CapUi]::DwmGetWindowAttribute($hwnd, 9, [ref]$r, 16) -ne 0) {  # 9 = EXTENDED_FRAME_BOUNDS
        [CapUi]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    }
    $x = [Math]::Max(0, $r.L - $pad); $y = [Math]::Max(0, $r.T - $pad)
    $w = [Math]::Min($scr.Width - $x, ($r.R - $r.L) + 2 * $pad)
    $hh = [Math]::Min($scr.Height - $y, ($r.B - $r.T) + 2 * $pad)
    $crop = $bmp.Clone((New-Object System.Drawing.Rectangle $x, $y, $w, $hh), $bmp.PixelFormat)
    $out = Join-Path $OutDir $name
    $crop.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $crop.Dispose()
    Write-Host ("saved {0}  rect=({1},{2}) {3}x{4}" -f $out, $x, $y, $w, $hh)
}

Save-Crop $hMain 'screenshot-main.png' 0
Save-Crop $hOsd 'screenshot-osd.png' 10
Save-Crop $hSet 'screenshot-osd-settings.png' 0
$bmp.Dispose()
Write-Host "done"
