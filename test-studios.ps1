# Probes the expected local studio endpoints and verifies the Writer Room model.
param(
    [int]$Count = 1,
    [switch]$IncludeMusicGen,
    [string]$OllamaModel = "gemma4:e4b",
    [int]$OllamaPort = 11434,
    [switch]$SkipWriterRoomChat
)

$failures = 0

function Pass($Message) {
    Write-Host "  OK  $Message" -ForegroundColor Green
}

function Fail($Message) {
    $script:failures++
    Write-Host "  ERR $Message" -ForegroundColor Red
}

function Test-Json($Name, $Url, [int]$TimeoutSec = 10) {
    try {
        $result = Invoke-RestMethod -Uri $Url -TimeoutSec $TimeoutSec
        Pass $Name
        return $result
    } catch {
        Fail "$Name - $($_.Exception.Message)"
        return $null
    }
}

Write-Host "Testing studios..." -ForegroundColor Cyan

Write-Host ""
Write-Host "Writer Room:" -ForegroundColor Cyan
$ollamaBase = "http://localhost:$OllamaPort"
$version = Test-Json "Ollama /api/version" "$ollamaBase/api/version"
if ($version -and $version.version) {
    Write-Host "      version $($version.version)"
}

$tags = Test-Json "Ollama /api/tags" "$ollamaBase/api/tags"
$modelNames = @()
if ($tags -and $tags.models) {
    $modelNames = @($tags.models | ForEach-Object { $_.name })
}

if ($modelNames -contains $OllamaModel) {
    Pass "model $OllamaModel is installed"
} else {
    $installed = if ($modelNames.Count -gt 0) { $modelNames -join ", " } else { "(none)" }
    Fail "model $OllamaModel is missing; installed models: $installed"
}

if (-not $SkipWriterRoomChat -and ($modelNames -contains $OllamaModel)) {
    Write-Host "      running a small chat completion; first load can take a minute"
    $body = @{
        model = $OllamaModel
        stream = $false
        messages = @(@{ role = "user"; content = "Reply with exactly OK." })
        options = @{ num_ctx = 2048 }
    } | ConvertTo-Json -Depth 8

    try {
        $chat = Invoke-RestMethod -Method Post -Uri "$ollamaBase/api/chat" -ContentType "application/json" -Body $body -TimeoutSec 600
        $content = $chat.message.content
        if ([string]::IsNullOrWhiteSpace($content)) {
            Fail "Ollama chat returned an empty response"
        } else {
            Pass "Ollama chat generated: $content"
        }
    } catch {
        Fail "Ollama chat failed - $($_.Exception.Message)"
    }
}

Write-Host ""
Write-Host "Recording studios:" -ForegroundColor Cyan
for ($i = 1; $i -le $Count; $i++) {
    $port = 8100 + $i
    Test-Json "ACE-Step studio #$i /health" "http://localhost:$port/health" | Out-Null
}
if ($IncludeMusicGen) {
    Test-Json "MusicGen studio /health" "http://localhost:8111/health" | Out-Null
}

Write-Host ""
Write-Host "Voice booths:" -ForegroundColor Cyan
$voices = Test-Json "TTS booth /voices" "http://localhost:8201/voices"
if ($voices -ne $null) {
    Write-Host "      voices $(@($voices).Count)"
}

Write-Host ""
Write-Host "Analysis:" -ForegroundColor Cyan
Test-Json "Analysis /health" "http://localhost:8301/health" | Out-Null

Write-Host ""
if ($failures -gt 0) {
    Write-Host "$failures studio check(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host "All studio checks passed." -ForegroundColor Green
