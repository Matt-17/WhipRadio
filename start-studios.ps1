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

function Get-VolumeDestination($Volume) {
    if (-not $Volume) { return $null }
    if ($Volume -match ':(/[^:]+)(?::[^:]*)?$') { return $Matches[1] }
    return $null
}

function Test-ContainerHasVolumes($Name, $Volumes) {
    $required = @($Volumes | Where-Object { $_ } | ForEach-Object { Get-VolumeDestination $_ } | Where-Object { $_ })
    if ($required.Count -eq 0) { return $true }

    $mounts = docker inspect --format "{{range .Mounts}}{{.Destination}};{{end}}" $Name 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $mounts) { return $false }

    foreach ($destination in $required) {
        if ($mounts -notmatch "(^|;)$([regex]::Escape($destination))(;|$)") {
            return $false
        }
    }

    return $true
}

function Test-ContainerHasEnvironment($Name, $Environment) {
    $required = @($Environment | Where-Object { $_ })
    if ($required.Count -eq 0) { return $true }

    $actual = @(docker inspect --format "{{range .Config.Env}}{{println .}}{{end}}" $Name 2>$null)
    if ($LASTEXITCODE -ne 0 -or $actual.Count -eq 0) { return $false }

    foreach ($entry in $required) {
        if ($actual -notcontains $entry) {
            return $false
        }
    }

    return $true
}

function Test-ContainerMatchesRuntimeConfig($Name, $Volumes, $Environment) {
    return (Test-ContainerHasVolumes $Name $Volumes) -and (Test-ContainerHasEnvironment $Name $Environment)
}

function Ensure-Container($Name, $Image, $HostPort, $TargetPort, $Volumes, [bool]$UseGpu = $false, $Environment = @()) {
    $volumeList = @($Volumes | Where-Object { $_ })
    $environmentList = @($Environment | Where-Object { $_ })
    $running = docker ps --filter "name=^$Name$" --format "{{.Names}}"
    if ($running) {
        if (-not (Test-ContainerMatchesRuntimeConfig $Name $volumeList $environmentList)) {
            Write-Host "  $Name uses older runtime settings; recreating container and keeping mounted volumes" -ForegroundColor Yellow
            docker rm -f $Name | Out-Null
        } else {
            Write-Host "  $Name -> http://localhost:$HostPort (already running)"
            return
        }
    }

    $existing = docker ps -a --filter "name=^$Name$" --format "{{.Names}}"
    if ($existing) {
        if (-not (Test-ContainerMatchesRuntimeConfig $Name $volumeList $environmentList)) {
            Write-Host "  $Name uses older runtime settings; recreating container and keeping mounted volumes" -ForegroundColor Yellow
            docker rm -f $Name | Out-Null
        } else {
            docker start $Name | Out-Null
            Write-Host "  $Name -> http://localhost:$HostPort (existing container started)"
            return
        }
    }

    $args = @("run", "-d", "--name", $Name, "--restart", "unless-stopped",
              "-p", "$HostPort`:$TargetPort")
    foreach ($volume in $volumeList) { $args += @("-v", $volume) }
    foreach ($entry in $environmentList) { $args += @("-e", $entry) }
    if ($UseGpu) { $args += @("--gpus", "all") }
    $args += $Image

    docker @args | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  $Name FAILED to start" -ForegroundColor Red
        return
    }
    Write-Host "  $Name -> http://localhost:$HostPort (created)"
}

function Format-DockerImageId($Id) {
    if (-not $Id) { return "(none)" }

    $normalized = $Id -replace "^sha256:", ""
    if ($normalized.Length -gt 12) {
        return "sha256:$($normalized.Substring(0, 12))"
    }

    return $Id
}

function Get-DockerImageInfo($Image) {
    $raw = docker image inspect --format "{{.Id}}`t{{.Created}}`t{{range .RepoDigests}}{{.}} {{end}}" $Image 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $raw) { return $null }

    $parts = $raw -split "`t", 3
    $id = $parts[0].Trim()
    $created = if ($parts.Count -gt 1) { $parts[1].Trim() } else { "" }
    $digests = if ($parts.Count -gt 2) { $parts[2].Trim() } else { "" }
    $shortId = Format-DockerImageId $id

    return [pscustomobject]@{
        Id = $id
        ShortId = $shortId
        Created = $created
        Digests = $digests
    }
}

function Write-DockerImageInfo($Label, $Info) {
    if ($null -eq $Info) {
        Write-Host "  ${Label}: not present locally"
        return
    }

    Write-Host "  ${Label}: $($Info.ShortId)"
    if ($Info.Digests) { Write-Host "    digest: $($Info.Digests)" }
    if ($Info.Created) { Write-Host "    created: $($Info.Created)" }
}

function Remove-DockerImageIfUnused($ImageId, $Label) {
    if (-not $ImageId) { return }

    $shortId = Format-DockerImageId $ImageId
    Write-Host "  removing $Label $shortId if unused..."
    docker image rm $ImageId *> $null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  removed $Label $shortId"
    } else {
        Write-Host "  $Label $shortId is still referenced; leaving it in Docker cache" -ForegroundColor Yellow
    }
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

function Refresh-ContainerImage($Name, $Image, $StateDescription) {
    $existing = docker ps -a --filter "name=^$Name$" --format "{{.Names}}"
    if (-not $existing) { return }

    $containerImage = docker inspect --format "{{.Image}}" $Name 2>$null
    $latestImage = docker image inspect --format "{{.Id}}" $Image 2>$null
    if ($containerImage -and $latestImage -and $containerImage.Trim() -ne $latestImage.Trim()) {
        Write-Host "  $Name uses an older image; recreating container and keeping $StateDescription"
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
$dataDir = Join-Path $root "data"
if (-not (Test-Path $dataDir)) { New-Item -ItemType Directory -Force $dataDir | Out-Null }

if (-not $SkipWriterRoom) {
    Write-Host ""
    Write-Host "Writer Room:" -ForegroundColor Cyan
    $ollamaImage = "ollama/ollama:latest"
    $existingImage = Get-DockerImageInfo $ollamaImage
    Write-DockerImageInfo "existing image" $existingImage

    Write-Host "  checking $ollamaImage..."
    docker pull $ollamaImage | Out-Null
    $pullSucceeded = $LASTEXITCODE -eq 0
    $currentImage = Get-DockerImageInfo $ollamaImage
    if ($pullSucceeded) {
        Write-DockerImageInfo "current image" $currentImage
        if ($existingImage -and $currentImage) {
            if ($existingImage.Id -eq $currentImage.Id) {
                Write-Host "  image unchanged after registry check"
            } else {
                Write-Host "  image updated: $($existingImage.ShortId) -> $($currentImage.ShortId)" -ForegroundColor Green
            }
        } elseif ($currentImage) {
            Write-Host "  image downloaded: $($currentImage.ShortId)" -ForegroundColor Green
        }
    } else {
        Write-Host "  image pull failed; trying the local image cache" -ForegroundColor Yellow
        Write-DockerImageInfo "cached image" $currentImage
    }

    $legacyOllama = docker ps --format "{{.Names}}" |
        Where-Object { $_ -like "ollama-*" -and $_ -ne "whip-writer-room-ollama" }
    if ($legacyOllama) {
        Write-Host "  old Aspire Ollama container still running: $($legacyOllama -join ', ')" -ForegroundColor Yellow
        Write-Host "  stop/remove it after WhipRadio is stopped if you want to free GPU/RAM."
    }

    Refresh-OllamaContainer "whip-writer-room-ollama" $ollamaImage
    if ($pullSucceeded -and $existingImage -and $currentImage -and $existingImage.Id -ne $currentImage.Id) {
        Remove-DockerImageIfUnused $existingImage.Id "previous image"
    }

    Ensure-Container "whip-writer-room-ollama" $ollamaImage $OllamaPort 11434 "ollama-models:/root/.ollama" $gpu
    if (Wait-Http "http://localhost:$OllamaPort/api/version" 90) {
        Ensure-OllamaModel "http://localhost:$OllamaPort" $OllamaModel
    } else {
        Write-Host "  Ollama did not become ready on http://localhost:$OllamaPort" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "Recording studios:" -ForegroundColor Cyan
# ACE-Step supervises its own liveness — no AppHost/orchestrator needed, so this
# survives a published deployment where neither is present:
#   * The image's CMD is whip_watchdog.py (PID 1); it spawns api_server and polls
#     /v1/stats. If jobs are pending but none reach a terminal state for
#     STUCK_TIMEOUT, it kills the server and `--restart unless-stopped` (see
#     Ensure-Container) brings up a fresh one — recovering a wedged queue.
#   * GENERATION_TIMEOUT caps a single song: a generation that exceeds it is
#     aborted by the sidecar (job -> failed, a terminal state), so a slow song
#     never looks like a wedge.
# INVARIANT: STUCK must stay > GENERATION_TIMEOUT, or the watchdog would kill a
# legitimately long-running generation. Keep that gap when tuning these.
$aceStepEnv = @(
    "ACESTEP_GENERATION_TIMEOUT=600",      # 10 min hard cap for one song
    "ACESTEP_STUCK_TIMEOUT_SECONDS=1200"   # 20 min wedge -> auto-restart (> generation)
)
for ($i = 1; $i -le $Count; $i++) {
    Refresh-ContainerImage "whip-studio-acestep-$i" "whipradio-acestep:local" "acestep-models"
    Ensure-Container "whip-studio-acestep-$i" "whipradio-acestep:local" (8100 + $i) 8002 @("acestep-models:/models", "$dataDir`:/app/data:ro") $gpu $aceStepEnv
}
if ($IncludeMusicGen) {
    Refresh-ContainerImage "whip-studio-musicgen-1" "whipradio-musicgen:local" "hf-cache"
    Ensure-Container "whip-studio-musicgen-1" "whipradio-musicgen:local" 8111 8002 "hf-cache:/models" $gpu
}

Write-Host ""
Write-Host "Voice booths:" -ForegroundColor Cyan
Refresh-ContainerImage "whip-booth-tts-1" "whipradio-tts:local" "hf-cache"
Ensure-Container "whip-booth-tts-1" "whipradio-tts:local" 8201 8001 "hf-cache:/models" $gpu

Write-Host ""
Write-Host "Analysis:" -ForegroundColor Cyan
Refresh-ContainerImage "whip-analysis" "whipradio-analysis:local" "the mounted data folder"
Ensure-Container "whip-analysis" "whipradio-analysis:local" 8301 8301 "$dataDir`:/data:ro" $false

Write-Host ""
Write-Host "Studios are warming up (model loads can take minutes)." -ForegroundColor Green
Write-Host "Expected local endpoints:"
Write-Host "  Writer Room  http://localhost:$OllamaPort  (Ollama / $OllamaModel)"
Write-Host "  Studio #1    http://localhost:8101       (ACE-Step)"
Write-Host "  Booth #1     http://localhost:8201       (TTS)"
Write-Host "  Analysis     http://localhost:8301       (audio analysis)"
