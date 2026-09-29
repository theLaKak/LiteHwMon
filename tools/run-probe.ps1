# Elevated runner for AmdProbe. ASCII only.
$dir = Resolve-Path (Join-Path $PSScriptRoot '..\src\AmdProbe\bin\Release\net8.0-windows')
$out = Join-Path $PSScriptRoot 'probe-out.txt'
Push-Location $dir
& .\AmdProbe.exe *> $out
$code = $LASTEXITCODE
Pop-Location
Write-Host "exit: $code"
