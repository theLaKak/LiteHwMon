# Launch app elevated, wait, screenshot. ASCII only.
param(
    [int]$WaitSeconds = 30,
    [string]$OutName = "screen"
)
$ErrorActionPreference = 'Continue'
$root = 'C:\Users\Administrator\Desktop\codx'
$proj = Join-Path $root ([char]0x8F7B + [char]0x91CF + [char]0x5316)  # placeholder, not used
# Build full paths using the literal Chinese folder name via script location
$dir = $PSScriptRoot
$exe = Join-Path $dir '..\src\LiteHwMon\bin\Release\net8.0-windows\LiteHwMon.exe'
$exe = (Resolve-Path $exe).Path

# kill existing (elevated)
try {
    $p = Get-Process LiteHwMon -ErrorAction SilentlyContinue
    if ($p) { Start-Process cmd -Verb RunAs -WindowStyle Hidden -ArgumentList '/c', 'taskkill /IM LiteHwMon.exe /F' | Out-Null; Start-Sleep -Seconds 2 }
} catch {}

Start-Process -FilePath $exe -Verb RunAs -WindowStyle Normal | Out-Null
Write-Host "launched"
Start-Sleep -Seconds $WaitSeconds

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$g.Dispose()
$out = Join-Path $dir "$OutName.png"
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "saved: $out"
