# Elevate-and-run capture-window.ps1. ASCII only.
$dir = $PSScriptRoot
$p = Join-Path $dir 'capture-window.ps1'
$log = Join-Path $dir 'capture-window.log'
if (Test-Path $log) { Remove-Item $log -Force }
Start-Process powershell -Verb RunAs -Wait -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $p)
Get-Content $log
