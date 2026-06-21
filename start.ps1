# Starts WhipRadio: builds the solution and launches the Aspire AppHost
# hidden in the background so this console stays free. Use .\stop.ps1 to shut down.
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$appHost = Join-Path $root "src\WhipRadio.AppHost\WhipRadio.AppHost.csproj"
$appHostExe = Join-Path $root "src\WhipRadio.AppHost\bin\Debug\net10.0\WhipRadio.AppHost.exe"
$stdoutLog = Join-Path $root "apphost-run.log"
$stderrLog = Join-Path $root "apphost-run.err.log"
$projects = @(
    $appHost,
    (Join-Path $root "src\WhipRadio.Orchestrator\WhipRadio.Orchestrator.csproj"),
    (Join-Path $root "src\WhipRadio.Web\WhipRadio.Web.csproj")
)

if (Get-Process -Name "WhipRadio.AppHost" -ErrorAction SilentlyContinue) {
    Write-Host "WhipRadio is already running. Run .\stop.ps1 first." -ForegroundColor Yellow
    exit 1
}

$envFile = Join-Path $root ".env"
if (-not (Test-Path -LiteralPath $envFile)) {
    Write-Host ".env not found - copying from .env.example. Edit it and set real Icecast passwords." -ForegroundColor Yellow
    Copy-Item (Join-Path $root ".env.example") $envFile
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

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:DOTNET_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://localhost:15044"
$env:ASPIRE_ALLOW_UNSECURED_TRANSPORT = "true"
$env:ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL = "http://localhost:19293"
$env:ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL = "http://localhost:20145"

Remove-Item -LiteralPath $stdoutLog, $stderrLog -Force -ErrorAction SilentlyContinue

$process = Start-Process $appHostExe `
    -WorkingDirectory $root `
    -WindowStyle Hidden `
    -RedirectStandardOutput $stdoutLog `
    -RedirectStandardError $stderrLog `
    -PassThru

$dashboardUrl = $null
$startupDeadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $startupDeadline) {
    if ($process.HasExited) {
        break
    }

    $dashboardUrl = @($stdoutLog, $stderrLog) |
        Where-Object { Test-Path -LiteralPath $_ } |
        ForEach-Object { Get-Content -LiteralPath $_ -ErrorAction SilentlyContinue } |
        Select-String -Pattern 'https?://localhost:15044/\S*|https?://127\.0\.0\.1:15044/\S*' |
        Select-Object -Last 1 |
        ForEach-Object { $_.Matches[0].Value.TrimEnd('.') }

    if ($dashboardUrl) {
        break
    }

    Start-Sleep -Milliseconds 500
}

if (-not $dashboardUrl) {
    $dashboardUrl = @($stdoutLog, $stderrLog) |
        Where-Object { Test-Path -LiteralPath $_ } |
        ForEach-Object { Get-Content -LiteralPath $_ -ErrorAction SilentlyContinue } |
        Select-String -Pattern 'https?://localhost:15044|https?://127\.0\.0\.1:15044' |
        Select-Object -Last 1 |
        ForEach-Object { $_.Matches[0].Value.TrimEnd('.') }
}

if (-not $dashboardUrl -and -not $process.HasExited) {
    $dashboardUrl = @($stdoutLog, $stderrLog) |
        Where-Object { Test-Path -LiteralPath $_ } |
        ForEach-Object { Get-Content -LiteralPath $_ -ErrorAction SilentlyContinue } |
        Select-String -Pattern 'https?://\S+/login\?t=\S+' |
            Select-Object -Last 1 |
            ForEach-Object { $_.Matches[0].Value.TrimEnd('.') }
}

Write-Host ""
if ($process.HasExited) {
    Write-Host "WhipRadio AppHost exited during startup (PID $($process.Id), exit code $($process.ExitCode))." -ForegroundColor Red
    if (Test-Path -LiteralPath $stdoutLog) {
        Get-Content -LiteralPath $stdoutLog -Tail 20
    }
    if (Test-Path -LiteralPath $stderrLog) {
        Get-Content -LiteralPath $stderrLog -Tail 20
    }
    exit 1
}

Write-Host "WhipRadio is starting in the background (PID $($process.Id))..." -ForegroundColor Green
Write-Host "  Web app:          http://localhost:5084"
if ($dashboardUrl) {
    Write-Host "  Aspire dashboard: $dashboardUrl"
}
else {
    Write-Host "  Aspire dashboard: http://localhost:15044"
    Write-Host "  Dashboard token was not found yet; check $stdoutLog." -ForegroundColor Yellow
}
Write-Host "  Stream:           http://localhost:8000/radio.mp3"
Write-Host "  Logs:             $stdoutLog"
Write-Host "  Stop with:        .\stop.ps1"
