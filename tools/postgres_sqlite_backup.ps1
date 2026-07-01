param(
    [string]$Out,
    [string]$DockerConfig,
    [string]$Container,
    [string]$Database = "radio",
    [string]$User = "postgres",
    [string]$Password,
    [switch]$Overwrite
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$tool = Join-Path $root "tools\sqlite_postgres_recovery.py"
if (-not (Test-Path -LiteralPath $tool)) {
    throw "Recovery tool not found: $tool"
}

if (-not $Out) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $Out = Join-Path $root "data\db\postgres-backup-$stamp.sqlite"
}

$outDir = Split-Path -Parent $Out
if ($outDir) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}

if (-not $DockerConfig) {
    $localDockerConfig = "D:\tmp\docker-test"
    if (Test-Path -LiteralPath $localDockerConfig) {
        $DockerConfig = $localDockerConfig
    }
}

$argsList = @($tool)
if ($DockerConfig) {
    $argsList += @("--docker-config", $DockerConfig)
}
if ($Container) {
    $argsList += @("--container", $Container)
}
if ($Database) {
    $argsList += @("--database", $Database)
}
if ($User) {
    $argsList += @("--user", $User)
}
if ($Password) {
    $argsList += @("--password", $Password)
}

$argsList += @("backup", "--out", $Out)
if ($Overwrite) {
    $argsList += "--overwrite"
}

Write-Host "Backing up PostgreSQL '$Database' to SQLite: $Out" -ForegroundColor Cyan
& python @argsList
if ($LASTEXITCODE -ne 0) {
    throw "PostgreSQL to SQLite backup failed with exit code $LASTEXITCODE."
}

Write-Host "Backup written: $Out" -ForegroundColor Green
