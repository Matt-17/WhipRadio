# Builds all sidecar images and tags them whipradio-*:local - the tags the
# AppHost runs by default (no startup builds). Run this after changing anything
# under sidecars\. GPU torch wheels are used when nvidia-smi reports a GPU.
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$torchIndex = "cpu"
try {
    nvidia-smi -L | Out-Null
    if ($LASTEXITCODE -eq 0) { $torchIndex = "cu121" }
} catch {}
Write-Host "TORCH_INDEX = $torchIndex" -ForegroundColor Cyan

$builds = @(
    @{ Tag = "whipradio-tts:local";      Path = "sidecars\tts";      TorchArg = $true  },
    @{ Tag = "whipradio-musicgen:local"; Path = "sidecars\musicgen"; TorchArg = $true  },
    @{ Tag = "whipradio-acestep:local";  Path = "sidecars\acestep";  TorchArg = $false }
)

$failed = $false
foreach ($b in $builds) {
    Write-Host ""
    Write-Host "=== $($b.Tag) ===" -ForegroundColor Cyan
    $context = Join-Path $root $b.Path
    if ($b.TorchArg) {
        docker build -t $b.Tag --build-arg "TORCH_INDEX=$torchIndex" $context
    } else {
        docker build -t $b.Tag $context
    }
    if ($LASTEXITCODE -ne 0) { $failed = $true }
}

Write-Host ""
if ($failed) {
    Write-Host "SIDECAR BUILD FAILED." -ForegroundColor Red
    exit 1
}
Write-Host "All sidecar images tagged :local - restart to run them." -ForegroundColor Green
