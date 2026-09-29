# 发布脚本 (ASCII only)
# Usage:
#   powershell -ExecutionPolicy Bypass -File publish.ps1                 # framework-dependent single exe (needs .NET 8 Desktop Runtime)
#   powershell -ExecutionPolicy Bypass -File publish.ps1 -SelfContained  # self-contained, zero install (~150MB)
param([switch]$SelfContained)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dotnet = Join-Path $env:USERPROFILE "dotnet-sdk8\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

$out = Join-Path $root "publish\LiteHwMon"
$argList = @(
    "publish", (Join-Path $root "src\LiteHwMon\LiteHwMon.csproj"),
    "-c", "Release", "-r", "win-x64",
    "/p:PublishSingleFile=true",
    "/p:IncludeNativeLibrariesForSelfExtract=true",
    "/p:DebugType=none"
)
if ($SelfContained) { $argList += @("--self-contained", "true") } else { $argList += @("--self-contained", "false") }
$argList += "-o", $out

& $dotnet @argList
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Write-Host ""
Write-Host "Output: $out"
Get-ChildItem $out | Format-Table Name, @{N="MB"; E={"{0:N1}" -f ($_.Length / 1MB)}} -AutoSize
