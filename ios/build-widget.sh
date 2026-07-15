#!/usr/bin/env bash
#
# Builds the WidgetKit extension — the monitoring Live Activity (BG-2i/3i) — into
# BabyMonitor.app/PlugIns/BabyMonitorWidget.appex. Invoked by ios/build.sh; not run on its own.
#
# The extension links no framework: it draws state and posts a Darwin notification for Stop. The
# shared ActivityAttributes + Stop intent come from ios/Shared, compiled into both the app and here so
# both sides agree on the one type that crosses between them.
set -euo pipefail

CONFIG="${1:-debug}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP="$ROOT/ios/build/BabyMonitor.app"
APPEX="$APP/PlugIns/BabyMonitorWidget.appex"

VERSION_CODE="${BM_VERSION_CODE:-$(git -C "$ROOT" rev-list --count HEAD 2>/dev/null || echo 1)}"
VERSION_NAME="${BM_VERSION_NAME:-0.1.$VERSION_CODE}"

SWIFT_FLAGS="-Onone -g"
[ "$CONFIG" = "release" ] && SWIFT_FLAGS="-O"

SDKPATH="$(xcrun --sdk iphonesimulator --show-sdk-path)"

echo "==> Compiling the Live Activity widget"
rm -rf "$APPEX"
mkdir -p "$APPEX"

# shellcheck disable=SC2086
xcrun -sdk iphonesimulator swiftc $SWIFT_FLAGS \
  -target arm64-apple-ios26.0-simulator \
  -sdk "$SDKPATH" \
  -swift-version 5 \
  -parse-as-library \
  -application-extension \
  -o "$APPEX/BabyMonitorWidget" \
  "$ROOT"/ios/Widget/*.swift "$ROOT"/ios/Shared/*.swift

sed -e "s/__VERSION_NAME__/$VERSION_NAME/" -e "s/__VERSION_CODE__/$VERSION_CODE/" \
  "$ROOT/ios/Widget/Info.plist" > "$APPEX/Info.plist"

# Sign the extension first; the app is signed after (inside-out), which seals this into it.
codesign --force --sign - "$APPEX"
echo "==> $APPEX"
