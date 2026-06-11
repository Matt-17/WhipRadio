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

    Write-Host "WhipRadio stopped." -ForegroundColor Green
}
