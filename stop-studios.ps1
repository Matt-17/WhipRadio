# Stops the operator-owned AI service containers without deleting model/data volumes.
# This does not stop the WhipRadio AppHost or Icecast; use .\stop.ps1 for those.

docker version --format "{{.Server.Version}}" *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Docker is not reachable. Start Docker Desktop first." -ForegroundColor Red
    exit 1
}

$names = docker ps --format "{{.Names}}" |
    Where-Object {
        $_ -eq "whip-writer-room-ollama" -or
        $_ -like "whip-studio-*" -or
        $_ -like "whip-booth-*" -or
        $_ -eq "whip-analysis"
    }

if (-not $names) {
    Write-Host "No running studio containers found." -ForegroundColor Yellow
    exit 0
}

Write-Host "Stopping studio containers..." -ForegroundColor Cyan
foreach ($name in $names) {
    Write-Host "  stopping $name"
    docker stop $name | Out-Null
}

Write-Host "Studios stopped." -ForegroundColor Green
