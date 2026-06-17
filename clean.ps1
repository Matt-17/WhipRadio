# Cleans WhipRadio build and test artifacts without touching data or model caches.
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $root "WhipRadio.slnx"
$rootFull = [System.IO.Path]::GetFullPath($root)
$rootPrefix = $rootFull.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar

function Remove-ArtifactDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "Skipping path outside repository: $fullPath" -ForegroundColor Yellow
        return
    }

    if (Test-Path -LiteralPath $fullPath) {
        Write-Host "Removing $fullPath"
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

Write-Host "Running dotnet clean..." -ForegroundColor Cyan
dotnet clean $solution
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "dotnet clean failed." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Removing bin, obj, and TestResults directories..." -ForegroundColor Cyan
$artifactDirectories = Get-ChildItem -LiteralPath $root -Recurse -Directory -Force |
    Where-Object { $_.Name -in @("bin", "obj", "TestResults") } |
    Sort-Object { $_.FullName.Length } -Descending

$artifactDirectories | ForEach-Object { Remove-ArtifactDirectory $_.FullName }

Write-Host ""
Write-Host "Clean complete." -ForegroundColor Green
