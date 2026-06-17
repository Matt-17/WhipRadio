# Builds the full solution and runs all WhipRadio .NET tests.
param(
    [string]$Configuration = "Debug"
)

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $root "WhipRadio.slnx"
$buildScript = Join-Path $root "build.ps1"

Write-Host ""
& $buildScript -Configuration $Configuration
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Running full WhipRadio test suite ($Configuration)..." -ForegroundColor Cyan
dotnet test $solution --configuration $Configuration --no-build

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "TESTS FAILED." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "All tests passed." -ForegroundColor Green
