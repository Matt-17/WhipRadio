# Restarts the operator-owned AI services. Pass the same options you use for
# start-studios.ps1 when scaling ACE-Step or enabling MusicGen.
param(
    [int]$Count = 1,
    [switch]$IncludeMusicGen,
    [switch]$SkipWriterRoom,
    [string]$OllamaModel = "gemma4:e4b",
    [int]$OllamaPort = 8001,
    [switch]$RecreateWriterRoom
)

$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if ($RecreateWriterRoom) {
    docker rm -f whip-writer-room-ollama *> $null
}

& (Join-Path $root "stop-studios.ps1")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$startParams = @{
    Count = $Count
    OllamaModel = $OllamaModel
    OllamaPort = $OllamaPort
}
if ($IncludeMusicGen) { $startParams.IncludeMusicGen = $true }
if ($SkipWriterRoom) { $startParams.SkipWriterRoom = $true }

& (Join-Path $root "start-studios.ps1") @startParams
exit $LASTEXITCODE
