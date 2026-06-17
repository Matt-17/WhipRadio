# Starts the operator-owned AI services outside WhipRadio's AppHost lifecycle.
# They keep running across app restarts and can be scaled independently.
param(
    [int]$Count = 1,                 # number of ACE-Step recording studios
    [switch]$IncludeMusicGen,        # also start a MusicGen studio on port 8111
    [switch]$SkipWriterRoom,         # skip the local Ollama Writer Room
    [string]$OllamaModel = "gemma4:e4b",
    [int]$OllamaPort = 11434
)

$root = Split-Path -Parent $MyInvocation.MyCommand.Path

docker version --format "{{.Server.Version}}" *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Docker is not reachable. Start Docker Desktop first." -ForegroundColor Red
    exit 1
}

$gpu = $false
try {
    nvidia-smi -L | Out-Null
    if ($LASTEXITCODE -eq 0) { $gpu = $true }
} catch {}

function Wait-Http($Url, [int]$Seconds = 60) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        try {
            Invoke-RestMethod -Uri $Url -TimeoutSec 5 | Out-Null
            return $true
        } catch {
            Start-Sleep -Seconds 2
        }
    }
    return $false
}

function Ensure-Container($Name, $Image, $HostPort, $TargetPort, $Volume, [bool]$UseGpu = $false) {
    $running = docker ps --filter "name=^$Name$" --format "{{.Names}}"
    if ($running) {
        Write-Host "  $Name -> http://localhost:$HostPort (already running)"
        return
    }

    $existing = docker ps -a --filter "name=^$Name$" --format "{{.Names}}"
    if ($existing) {
        docker start $Name | Out-Null
        Write-Host "  $Name -> http://localhost:$HostPort (existing container started)"
        return
    }

    $args = @("run", "-d", "--name", $Name, "--restart", "unless-stopped",
              "-p", "$HostPort`:$TargetPort")
    if ($Volume) { $args += @("-v", $Volume) }
    if ($UseGpu) { $args += @("--gpus", "all") }
    $args += $Image

    docker @args | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  $Name FAILED to start" -ForegroundColor Red
        return
    }
    Write-Host "  $Name -> http://localhost:$HostPort (created)"
}

function Refresh-OllamaContainer($Name, $Image) {
    $existing = docker ps -a --filter "name=^$Name$" --format "{{.Names}}"
    if (-not $existing) { return }

    $containerImage = docker inspect --format "{{.Image}}" $Name 2>$null
    $latestImage = docker image inspect --format "{{.Id}}" $Image 2>$null
    if ($containerImage -and $latestImage -and $containerImage.Trim() -ne $latestImage.Trim()) {
        Write-Host "  $Name uses an older image; recreating container and keeping ollama-models"
        docker rm -f $Name | Out-Null
    }
}

function Get-OllamaModels($BaseUrl) {
    try {
        $tags = Invoke-RestMethod -Uri "$BaseUrl/api/tags" -TimeoutSec 10
        return @($tags.models | ForEach-Object { $_.name })
    } catch {
        return @()
    }
}

function Ensure-OllamaModel($BaseUrl, $Model) {
    $models = @(Get-OllamaModels $BaseUrl)
    if ($models -contains $Model) {
        Write-Host "  model $Model is installed"
        return
    }

    Write-Host "  pulling model $Model (first run can take a long time)..."
    $body = @{ model = $Model; stream = $false } | ConvertTo-Json
    try {
        Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/pull" -ContentType "application/json" -Body $body -TimeoutSec 7200 | Out-Null
        Write-Host "  model $Model installed"
    } catch {
        Write-Host "  model $Model FAILED to pull: $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host "Starting studios (GPU: $gpu)..." -ForegroundColor Cyan

if (-not $SkipWriterRoom) {
    Write-Host ""
    Write-Host "Writer Room:" -ForegroundColor Cyan
    Write-Host "  pulling ollama/ollama:latest..."
    docker pull ollama/ollama:latest | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  image pull failed; trying the local image cache" -ForegroundColor Yellow
    }

    $legacyOllama = docker ps --format "{{.Names}}" |
        Where-Object { $_ -like "ollama-*" -and $_ -ne "whip-writer-room-ollama" }
    if ($legacyOllama) {
        Write-Host "  old Aspire Ollama container still running: $($legacyOllama -join ', ')" -ForegroundColor Yellow
        Write-Host "  stop/remove it after WhipRadio is stopped if you want to free GPU/RAM."
    }

    Refresh-OllamaContainer "whip-writer-room-ollama" "ollama/ollama:latest"
    Ensure-Container "whip-writer-room-ollama" "ollama/ollama:latest" $OllamaPort 11434 "ollama-models:/root/.ollama" $gpu
    if (Wait-Http "http://localhost:$OllamaPort/api/version" 90) {
        Ensure-OllamaModel "http://localhost:$OllamaPort" $OllamaModel
    } else {
        Write-Host "  Ollama did not become ready on http://localhost:$OllamaPort" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "Recording studios:" -ForegroundColor Cyan
for ($i = 1; $i -le $Count; $i++) {
    Ensure-Container "whip-studio-acestep-$i" "whipradio-acestep:local" (8100 + $i) 8002 "acestep-models:/models" $gpu
}
if ($IncludeMusicGen) {
    Ensure-Container "whip-studio-musicgen-1" "whipradio-musicgen:local" 8111 8002 "hf-cache:/models" $gpu
}

Write-Host ""
Write-Host "Voice booths:" -ForegroundColor Cyan
Ensure-Container "whip-booth-tts-1" "whipradio-tts:local" 8201 8001 "hf-cache:/models" $gpu

Write-Host ""
Write-Host "Analysis:" -ForegroundColor Cyan
$dataDir = Join-Path $root "data"
if (-not (Test-Path $dataDir)) { New-Item -ItemType Directory -Force $dataDir | Out-Null }
Ensure-Container "whip-analysis" "whipradio-analysis:local" 8301 8301 "$dataDir`:/data:ro" $false

Write-Host ""
Write-Host "Studios are warming up (model loads can take minutes)." -ForegroundColor Green
Write-Host "Expected local endpoints:"
Write-Host "  Writer Room  http://localhost:$OllamaPort  (Ollama / $OllamaModel)"
Write-Host "  Studio #1    http://localhost:8101       (ACE-Step)"
Write-Host "  Booth #1     http://localhost:8201       (TTS)"
Write-Host "  Analysis     http://localhost:8301       (audio analysis)"
