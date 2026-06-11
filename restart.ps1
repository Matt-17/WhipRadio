# Restarts WhipRadio: stop, then build + start. See stop.ps1 / start.ps1.
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

& (Join-Path $root "stop.ps1")
& (Join-Path $root "start.ps1")
