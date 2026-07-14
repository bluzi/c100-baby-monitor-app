#!/usr/bin/env bash
#
# Builds macos/BabyMonitor.app.
#
# No Xcode project on purpose. The app is a menu bar item, a few windows and a picture; everything
# hard is in the shared core. A .pbxproj would be the largest and least reviewable file in the
# repo, and CI would need to parse it. swiftc and a bundle layout are the whole build.
#
#   ./macos/build.sh [debug|release]
#
set -euo pipefail

CONFIG="${1:-debug}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/macos/build"
APP="$OUT/BabyMonitor.app"

VERSION_CODE="${BM_VERSION_CODE:-$(git -C "$ROOT" rev-list --count HEAD 2>/dev/null || echo 1)}"
VERSION_NAME="${BM_VERSION_NAME:-0.1.$VERSION_CODE}"

if [ "$CONFIG" = "release" ]; then
  GRADLE_TASK="linkReleaseFrameworkMacosArm64"
  FRAMEWORK_DIR="$ROOT/core/build/bin/macosArm64/releaseFramework"
  SWIFT_FLAGS="-O"
else
  GRADLE_TASK="linkDebugFrameworkMacosArm64"
  FRAMEWORK_DIR="$ROOT/core/build/bin/macosArm64/debugFramework"
  SWIFT_FLAGS="-Onone -g"
fi

echo "==> Building the shared monitor ($CONFIG)"
"$ROOT/gradlew" -p "$ROOT" ":core:$GRADLE_TASK" --console=plain -q

echo "==> Compiling the macOS shell (v$VERSION_NAME)"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

# shellcheck disable=SC2086
swiftc $SWIFT_FLAGS \
  -target arm64-apple-macos13.0 \
  -swift-version 5 \
  -F "$FRAMEWORK_DIR" \
  -framework BabyMonitorCore \
  -Xlinker -rpath -Xlinker @executable_path/../Frameworks \
  -o "$APP/Contents/MacOS/BabyMonitor" \
  "$ROOT"/macos/Sources/*.swift

sed -e "s/__VERSION_NAME__/$VERSION_NAME/" -e "s/__VERSION_CODE__/$VERSION_CODE/" \
  "$ROOT/macos/Resources/Info.plist" > "$APP/Contents/Info.plist"

# The Kotlin framework is static, so nothing to embed — the binary carries the monitor, libopus and
# all. Verified below, because a missing dylib would only show up at 3am on someone else's Mac.
if otool -L "$APP/Contents/MacOS/BabyMonitor" | grep -qi "BabyMonitorCore\|libopus"; then
  echo "!! The binary links a dynamic BabyMonitorCore or libopus — it must be static." >&2
  otool -L "$APP/Contents/MacOS/BabyMonitor" >&2
  exit 1
fi

# Ad-hoc signature. Without an Apple Developer ID the first launch needs one right-click → Open;
# after that the self-updater swaps the bundle in place and macOS is content, because we download
# it ourselves and it is never quarantined.
codesign --force --deep --sign - "$APP" 2>/dev/null || true

echo "==> $APP"
