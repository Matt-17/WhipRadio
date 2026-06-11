# Starts the studio containers OUTSIDE WhipRadio's lifecycle: they keep running
# across app restarts and can be scaled (run the script with -Count 2 to get a
# second ACE-Step studio on the next port). Connect them on the Studios page.
param(
    [int]$Count = 1,            # number of ACE-Step recording studios
    [switch]$IncludeMusicGen    # also start a MusicGen studio (port 8111)
)

$gpu = $false
try {
    nvidia-smi -L | Out-Null
    if ($LASTEXITCODE -eq 0) { $gpu = $true }
} catch {}

function Ensure-Container($name, $image, $hostPort, $targetPort, $volume) {
    $existing = docker ps -a --filter "name=^$name$" --format "{{.Names}}"
    if ($existing) {
        docker start $name | Out-Null
        Write-Host "  $name -> http://localhost:$hostPort (existing container started)"
        return
    }

    $args = @("run", "-d", "--name", $name, "--restart", "unless-stopped",
              "-p", "$hostPort`:$targetPort", "-v", $volume)
    if ($gpu) { $args += @("--gpus", "all") }
    $args += $image

    docker @args | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  $name FAILED to start" -ForegroundColor Red
        return
    }
    Write-Host "  $name -> http://localhost:$hostPort (created)"
}

Write-Host "Starting studios (GPU: $gpu)..." -ForegroundColor Cyan
Write-Host ""
Write-Host "Recording studios:" -ForegroundColor Cyan
for ($i = 1; $i -le $Count; $i++) {
    Ensure-Container "whip-studio-acestep-$i" "whipradio-acestep:local" (8100 + $i) 8002 "acestep-models:/models"
}
if ($IncludeMusicGen) {
    Ensure-Container "whip-studio-musicgen-1" "whipradio-musicgen:local" 8111 8002 "hf-cache:/models"
}

Write-Host ""
Write-Host "Voice booths:" -ForegroundColor Cyan
Ensure-Container "whip-booth-tts-1" "whipradio-tts:local" 8201 8001 "hf-cache:/models"

Write-Host ""
Write-Host "Studios are warming up (model loads can take minutes)." -ForegroundColor Green
Write-Host "Connect them on the Studios page; the seeded defaults already point at:"
Write-Host "  Studio #1  http://localhost:8101  (ACE-Step)"
Write-Host "  Booth #1   http://localhost:8201  (TTS)"
