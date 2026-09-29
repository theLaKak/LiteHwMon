# Capture screen with CAPTUREBLT to include layered windows. ASCII only, gdi32 for BitBlt.
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$sig = @"
using System;
using System.Runtime.InteropServices;
public static class CapY {
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
}
"@
Add-Type -TypeDefinition $sig

$outDir = $PSScriptRoot
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$sdc = [CapY]::GetDC([IntPtr]::Zero)
$CAPTUREBLT_SRCCOPY = 0x40CC0020
[CapY]::BitBlt($hdc, 0, 0, $b.Width, $b.Height, $sdc, 0, 0, $CAPTUREBLT_SRCCOPY) | Out-Null
[CapY]::ReleaseDC([IntPtr]::Zero, $sdc) | Out-Null
$g.ReleaseHdc($hdc)
$g.Dispose()

$bmp.Save((Join-Path $outDir "capblt-full.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$rect = New-Object System.Drawing.Rectangle 1600, 100, 448, 400
$crop = New-Object System.Drawing.Bitmap 448, 400
$g2 = [System.Drawing.Graphics]::FromImage($crop)
$g2.DrawImage($bmp, (New-Object System.Drawing.Rectangle 0, 0, 448, 400), $rect, 'Pixel')
$g2.Dispose()
$crop.Save((Join-Path $outDir "osd-captureblt.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$crop.Dispose()
$bmp.Dispose()
Write-Host "saved"
