#!/usr/bin/env bash
#
# Build the iOS app, boot a simulator, install and launch it, and stream its logs.
#
#   ./ios/run.sh [debug|release] [simulator-name]
#
# Defaults to an iPhone 17 Pro (a Dynamic Island device, so the Live Activity can be seen — IOS-3).
set -euo pipefail

CONFIG="${1:-debug}"
DEVICE_NAME="${2:-iPhone 17 Pro}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP="$ROOT/ios/build/BabyMonitor.app"
BUNDLE_ID="com.bluzi.babymonitor"

"$ROOT/ios/build.sh" "$CONFIG"

# Find (or boot) a simulator by name. simctl boots are idempotent — booting an already-booted device
# is a no-op.
UDID="$(xcrun simctl list devices available | grep -m1 "$DEVICE_NAME (" | grep -oE '[0-9A-F-]{36}' || true)"
if [ -z "$UDID" ]; then
  echo "!! No available simulator named '$DEVICE_NAME'. Available:" >&2
  xcrun simctl list devices available | grep -i iphone >&2
  exit 1
fi

echo "==> Booting $DEVICE_NAME ($UDID)"
xcrun simctl boot "$UDID" 2>/dev/null || true
open -a Simulator --args -CurrentDeviceUDID "$UDID" 2>/dev/null || true

echo "==> Installing"
xcrun simctl install "$UDID" "$APP"

echo "==> Launching. Logs (Ctrl-C to stop):"
echo "    log stream --predicate 'subsystem == \"com.bluzi.babymonitor\"'"
xcrun simctl launch --console-pty "$UDID" "$BUNDLE_ID"
