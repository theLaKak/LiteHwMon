# Non-elevated comparison run
$dir = Resolve-Path (Join-Path $PSScriptRoot '..\src\SmokeTest\bin\Release\net8.0-windows')
$out = Join-Path $PSScriptRoot 'smoke-out2.txt'
Push-Location $dir
& .\SmokeTest.exe *> $out
Pop-Location
