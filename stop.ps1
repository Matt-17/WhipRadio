# Stops WhipRadio: kills the AppHost, Orchestrator, Web and any leftover
# Aspire dcp orchestration processes so no file locks block the next build.
$procs = Get-Process | Where-Object { $_.ProcessName -like "WhipRadio*" }

if (-not $procs) {
    Write-Host "WhipRadio is not running." -ForegroundColor Yellow
}
else {
    $procs | ForEach-Object { Write-Host "Stopping $($_.ProcessName) (PID $($_.Id))" }
    $procs | Stop-Process -Force -Confirm:$false
    Start-Sleep -Seconds 2

    # dcp is Aspire's orchestrator; orphans keep ports and file handles open.
    $dcp = Get-Process -Name "dcp" -ErrorAction SilentlyContinue
    if ($dcp) {
        $dcp | Stop-Process -Force -Confirm:$false
        Write-Host "Stopped $($dcp.Count) leftover dcp process(es)."
    }

    # Windows does NOT kill child processes with their parent: an orphaned
    # encoder ffmpeg can keep pushing stale audio to the Icecast mount and
    # fight the next session (weird sounds after restart). Kill ONLY ffmpeg
    # processes that talk to Icecast or decode from our data folder.
    $orphans = @()
    try {
        $orphans = Get-CimInstance Win32_Process -Filter "Name='ffmpeg.exe'" -ErrorAction Stop |
            Where-Object { $_.CommandLine -match 'icecast://' -or $_.CommandLine -match 'WhipRadio' }
    }
    catch {
        Write-Host "Skipping ffmpeg orphan check: $($_.Exception.Message)" -ForegroundColor Yellow
    }

    if ($orphans) {
        $orphans | ForEach-Object {
            Write-Host "Stopping orphaned ffmpeg (PID $($_.ProcessId))"
            try { Stop-Process -Id $_.ProcessId -Force -Confirm:$false -ErrorAction Stop } catch {}
        }
    }

    Write-Host "WhipRadio stopped." -ForegroundColor Green
}
