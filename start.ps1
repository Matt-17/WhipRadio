# Starts WhipRadio: builds the solution and launches the Aspire AppHost
# hidden in the background so this console stays free. Use .\stop.ps1 to shut down.
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$appHost = Join-Path $root "src\WhipRadio.AppHost\WhipRadio.AppHost.csproj"
$stdoutLog = Join-Path $root "apphost-run.log"
$stderrLog = Join-Path $root "apphost-run.err.log"
$projects = @(
    (Join-Path $root "src\WhipRadio.Orchestrator\WhipRadio.Orchestrator.csproj"),
    (Join-Path $root "src\WhipRadio.Web\WhipRadio.Web.csproj")
)

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
foreach ($project in $projects) {
    dotnet build $project --no-restore
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed - not starting." -ForegroundColor Red
        exit 1
    }
}

$command = "cd /d `"$root`" && dotnet run --project `"$appHost`" --no-build --no-restore > `"$stdoutLog`" 2> `"$stderrLog`""
$process = Start-Process "$env:ComSpec" -ArgumentList "/d", "/c", $command -WorkingDirectory $root -WindowStyle Hidden -PassThru
Write-Host ""
Write-Host "WhipRadio is starting in the background (PID $($process.Id))..." -ForegroundColor Green
Write-Host "  Web app:          http://localhost:5084"
Write-Host "  Aspire dashboard: https://localhost:17005"
Write-Host "  Stream:           http://localhost:8000/radio.mp3"
Write-Host "  Logs:             $stdoutLog"
Write-Host "                    $stderrLog"
Write-Host "  Stop with:        .\stop.ps1"
