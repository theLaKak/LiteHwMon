# Generate app.ico: dark rounded square + monitoring curves (pure ASCII to avoid PS5.1 encoding issues)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$size = 256
$bmp = New-Object System.Drawing.Bitmap -ArgumentList $size, $size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

$r = 52
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc(0, 0, $r, $r, 180, 90)
$path.AddArc(($size - $r), 0, $r, $r, 270, 90)
$path.AddArc(($size - $r), ($size - $r), $r, $r, 0, 90)
$path.AddArc(0, ($size - $r), $r, $r, 90, 90)
$path.CloseFigure()
$g.FillPath((New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(255, 30, 32, 38))), $path)
$g.DrawPath((New-Object System.Drawing.Pen -ArgumentList ([System.Drawing.Color]::FromArgb(255, 76, 141, 255)), 8), $path)

$blue = New-Object System.Drawing.Pen -ArgumentList ([System.Drawing.Color]::FromArgb(255, 76, 141, 255)), 13
$green = New-Object System.Drawing.Pen -ArgumentList ([System.Drawing.Color]::FromArgb(255, 46, 210, 143)), 13

$pts1 = @( (48,168), (84,120), (118,140), (152,86), (186,104), (208,64) ) | ForEach-Object {
    New-Object System.Drawing.Point -ArgumentList $_[0], $_[1] }
$pts2 = @( (48,200), (88,178), (124,188), (160,150), (196,164), (208,140) ) | ForEach-Object {
    New-Object System.Drawing.Point -ArgumentList $_[0], $_[1] }
$g.DrawLines($blue, $pts1)
$g.DrawLines($green, $pts2)
$g.Dispose()

# PNG -> temp file -> ICO (PNG-in-ICO, Vista+)
$tmp = Join-Path $PSScriptRoot 'icon.tmp.png'
$bmp.Save($tmp, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
$png = [System.IO.File]::ReadAllBytes($tmp)
Remove-Item $tmp -ErrorAction SilentlyContinue
Write-Host "PNG bytes: $($png.Length)"

$ico = New-Object System.Collections.Generic.List[byte]
$ico.AddRange([byte[]]@(0, 0, 1, 0, 1, 0))                 # ICONDIR + count=1
$ico.AddRange([byte[]]@(0, 0, 0, 0, 1, 0, 32, 0))          # 256x256(0), planes=1, bpp=32
$ico.AddRange([BitConverter]::GetBytes([UInt32]$png.Length))
$ico.AddRange([BitConverter]::GetBytes([UInt32]22))        # data offset = 6 + 16
$ico.AddRange($png)

$out = Join-Path $PSScriptRoot '..\src\LiteHwMon\app.ico'
[System.IO.File]::WriteAllBytes($out, $ico.ToArray())
Write-Host "icon written: $((Get-Item $out).Length) bytes"
