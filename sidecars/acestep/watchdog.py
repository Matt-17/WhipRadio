"""Supervises the ACE-Step API server and restarts it when a job wedges.

The upstream server runs generations on worker threads; a hung thread (e.g.
a CPU VAE decode that never finishes) keeps /health green forever while the
single queue worker is bricked. This wrapper runs as PID 1, polls /v1/stats,
and exits when work is pending but no job has reached a terminal state for
ACESTEP_STUCK_TIMEOUT_SECONDS — the container's restart policy then brings
up a fresh server.
"""

from __future__ import annotations

import json
import os
import signal
import subprocess
import sys
import time
import urllib.error
import urllib.request

POLL_INTERVAL_SECONDS = 30
# Worst legitimate case is the first job after a cold start: checkpoint load
# plus generation. Anything beyond this is considered wedged.
STUCK_TIMEOUT_SECONDS = int(os.environ.get("ACESTEP_STUCK_TIMEOUT_SECONDS", "1200"))

HOST = os.environ.get("ACESTEP_API_HOST", "0.0.0.0")
PORT = os.environ.get("ACESTEP_API_PORT", "8002")
API_KEY = os.environ.get("ACESTEP_API_KEY", "")

STATS_URL = f"http://127.0.0.1:{PORT}/v1/stats"


def log(message: str) -> None:
    print(f"[whip-watchdog] {message}", flush=True)


def fetch_stats() -> dict | None:
    request = urllib.request.Request(STATS_URL)
    if API_KEY:
        request.add_header("Authorization", f"Bearer {API_KEY}")
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            payload = json.load(response)
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError, OSError):
        return None
    return (payload or {}).get("data")


def main() -> int:
    server = subprocess.Popen(
        [sys.executable, "-m", "acestep.api_server", "--host", HOST, "--port", PORT]
    )

    def forward_signal(signum: int, _frame) -> None:
        server.send_signal(signum)

    signal.signal(signal.SIGTERM, forward_signal)
    signal.signal(signal.SIGINT, forward_signal)

    log(
        f"supervising api_server pid={server.pid}; "
        f"stuck timeout {STUCK_TIMEOUT_SECONDS}s, poll every {POLL_INTERVAL_SECONDS}s"
    )

    last_terminal_count = -1
    pending_since: float | None = None

    while True:
        time.sleep(POLL_INTERVAL_SECONDS)

        exit_code = server.poll()
        if exit_code is not None:
            log(f"api_server exited with code {exit_code}")
            return exit_code

        stats = fetch_stats()
        if stats is None:
            # Server busy starting up or briefly unresponsive — the job-progress
            # timer below is the hang detector, not this probe.
            continue

        jobs = stats.get("jobs") or {}
        pending = int(jobs.get("queued", 0)) + int(jobs.get("running", 0))
        terminal = int(jobs.get("succeeded", 0)) + int(jobs.get("failed", 0))

        if pending == 0 or terminal != last_terminal_count:
            pending_since = None
            last_terminal_count = terminal
            continue

        if pending_since is None:
            pending_since = time.monotonic()
            continue

        stalled = time.monotonic() - pending_since
        if stalled < STUCK_TIMEOUT_SECONDS:
            continue

        log(
            f"STUCK: {pending} pending job(s) and no terminal-state change for "
            f"{stalled:.0f}s — killing api_server so the container restarts"
        )
        server.terminate()
        try:
            server.wait(timeout=15)
        except subprocess.TimeoutExpired:
            server.kill()
            server.wait()
        return 1


if __name__ == "__main__":
    sys.exit(main())
