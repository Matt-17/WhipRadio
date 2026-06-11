# Runs all WhipRadio unit tests. Safe while the station is running:
# the test projects only build Core and Infrastructure, never the app exes.
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$failed = $false

foreach ($project in @("WhipRadio.Core.Tests", "WhipRadio.Infrastructure.Tests")) {
    Write-Host ""
    Write-Host "=== $project ===" -ForegroundColor Cyan
    dotnet test (Join-Path $root "tests\$project")
    if ($LASTEXITCODE -ne 0) { $failed = $true }
}

Write-Host ""
if ($failed) {
    Write-Host "TESTS FAILED." -ForegroundColor Red
    exit 1
}
Write-Host "All tests passed." -ForegroundColor Green
