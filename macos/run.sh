#!/usr/bin/env bash
#
# Build and run the Mac app, with its logs in the terminal.
#
#   ./macos/run.sh
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
"$ROOT/macos/build.sh" debug

# Kill a previous instance — a menu bar app gives no other clue that two are running.
pkill -x BabyMonitor 2>/dev/null || true

echo "==> Running (Ctrl-C to stop). Logs also via:"
echo "    log stream --predicate 'subsystem == \"com.bluzi.babymonitor\"'"
exec "$ROOT/macos/build/BabyMonitor.app/Contents/MacOS/BabyMonitor"
