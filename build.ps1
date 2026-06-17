# Builds the full WhipRadio solution.
param(
    [string]$Configuration = "Debug"
)

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $root "WhipRadio.slnx"

Write-Host "Building WhipRadio solution ($Configuration)..." -ForegroundColor Cyan
dotnet build $solution --configuration $Configuration

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "BUILD FAILED." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Build passed." -ForegroundColor Green
