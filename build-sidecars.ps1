# Builds all sidecar images and tags them whipradio-*:local - the tags the
# AppHost runs by default (no startup builds). Run this after changing anything
# under sidecars\. GPU torch wheels are used when nvidia-smi reports a GPU.
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$hasGpu = $false
try {
    nvidia-smi -L | Out-Null
    if ($LASTEXITCODE -eq 0) { $hasGpu = $true }
} catch {}
Write-Host "GPU wheels: $hasGpu" -ForegroundColor Cyan

# Each image pins its own torch, so the CUDA wheel index differs per image:
# tts pins torch 2.8 (cu128); musicgen's audiocraft pins torch 2.1 (cu121 tops
# out where 2.1 still exists). A single shared index would break one of them.
$builds = @(
    @{ Tag = "whipradio-tts:local";      Path = "sidecars\tts";      GpuIndex = "cu128" },
    @{ Tag = "whipradio-musicgen:local"; Path = "sidecars\musicgen"; GpuIndex = "cu121" },
    @{ Tag = "whipradio-acestep:local";  Path = "sidecars\acestep";  GpuIndex = $null   },
    @{ Tag = "whipradio-analysis:local"; Path = "sidecars\analysis"; GpuIndex = $null   }
)

$failed = $false
foreach ($b in $builds) {
    Write-Host ""
    Write-Host "=== $($b.Tag) ===" -ForegroundColor Cyan
    $context = Join-Path $root $b.Path
    if ($b.GpuIndex) {
        $torchIndex = if ($hasGpu) { $b.GpuIndex } else { "cpu" }
        Write-Host "  TORCH_INDEX = $torchIndex"
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
