# Elevated runner for SmokeTest (ASCII only; path with Chinese resolved via $PSScriptRoot)
$ErrorActionPreference = 'Continue'
$dir = Resolve-Path (Join-Path $PSScriptRoot '..\src\SmokeTest\bin\Release\net8.0-windows')
$out = Join-Path $PSScriptRoot 'smoke-out.txt'
Push-Location $dir
& .\SmokeTest.exe *> $out
$code = $LASTEXITCODE
Pop-Location
Write-Host "exit: $code"
