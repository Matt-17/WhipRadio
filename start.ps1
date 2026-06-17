# Starts WhipRadio: builds the solution and launches the Aspire AppHost
# in a minimized window so this console stays free. Use .\stop.ps1 to shut down.
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$appHost = Join-Path $root "src\WhipRadio.AppHost"

if (Get-Process -Name "WhipRadio.AppHost" -ErrorAction SilentlyContinue) {
    Write-Host "WhipRadio is already running. Run .\stop.ps1 first." -ForegroundColor Yellow
    exit 1
}

try {
    Invoke-RestMethod -Uri "http://localhost:11434/api/version" -TimeoutSec 2 | Out-Null
}
catch {
    Write-Host "Writer Room is not reachable at http://localhost:11434. Run .\start-studios.ps1 first." -ForegroundColor Yellow
}

Write-Host "Building..." -ForegroundColor Cyan
dotnet build $appHost
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed - not starting." -ForegroundColor Red
    exit 1
}

Start-Process dotnet -ArgumentList "run --project `"$appHost`" --no-build" -WindowStyle Minimized
Write-Host ""
Write-Host "WhipRadio is starting..." -ForegroundColor Green
Write-Host "  Web app:          http://localhost:5084"
Write-Host "  Aspire dashboard: https://localhost:17005 (login link in the AppHost window)"
Write-Host "  Stream:           http://localhost:8000/radio.mp3"
