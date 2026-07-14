#!/usr/bin/env bash
#
# Regenerates the app icon for every platform from brand/icon.swift — the one description of the
# mark (UI-3).
#
# The generated files are committed, so a normal build never runs this. Run it after changing
# icon.swift, and commit what it writes:
#
#   macos/Resources/AppIcon.icns
#   android/src/main/res/drawable/ic_launcher_{background,foreground,monochrome}.xml
#   android/src/main/res/mipmap-anydpi-v26/ic_launcher.xml
#   windows/src/BabyMonitor.App/Assets/{BabyMonitor.ico,BabyMonitor.png,tray-*.ico}
#
#   ./brand/build.sh [--preview]
#
# Windows needs ImageMagick (`brew install imagemagick`). Its icons are packed from the SAME pixels
# the Mac's .icns is packed from — the mark is never redrawn per platform (UI-3).
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

ICONSET="$WORK/AppIcon.iconset"
PREVIEW_ARGS=()
if [ "${1:-}" = "--preview" ]; then
  PREVIEW_ARGS=(--preview "$ROOT/brand/preview.png")
fi

echo "==> Drawing the mark"
# The `+` form is not a flourish: macOS ships bash 3.2, where `set -u` and an empty array is an error.
swift "$ROOT/brand/icon.swift" \
  --macos "$ICONSET" \
  --android "$ROOT/android/src/main/res" \
  ${PREVIEW_ARGS[@]+"${PREVIEW_ARGS[@]}"}

echo "==> Packing the Mac's .icns"
iconutil --convert icns "$ICONSET" --output "$ROOT/macos/Resources/AppIcon.icns"

echo "==> Packing the PC's .ico"
ASSETS="$ROOT/windows/src/BabyMonitor.App/Assets"
mkdir -p "$ASSETS"
BASE="$ICONSET/icon_256x256.png"

# The app's own icon, wherever Windows shows one: the taskbar, Alt-Tab, the tray, Explorer (WIN-17).
magick "$BASE" -define icon:auto-resize=256,128,64,48,32,16 "$ASSETS/BabyMonitor.ico"
magick "$ICONSET/icon_128x128.png" "$ASSETS/BabyMonitor.png"

# WIN-1: the tray icon shows the feed state at a glance. Same mark, four states — the way the Mac's
# menu bar item changes, and for the same reason: a parent must be able to read the monitor without
# opening it.
#
#   live      the mark, as it is everywhere else
#   stopped   drained of colour — the quietest failure there is, and it must not look normal
#   warning   an amber badge: connecting, reconnecting, an expired session, a monitor that failed
#   alarm     a red badge: unmistakable, even on a PC with its speakers off
badge() { # badge <colour> <out>
  magick "$BASE" \
    \( -size 256x256 xc:none \
       -fill white -draw 'circle 186,186 186,250' \
       -fill "$1" -draw 'circle 186,186 186,244' \) \
    -composite -define icon:auto-resize=48,32,24,20,16 "$2"
}

magick "$BASE" -define icon:auto-resize=48,32,24,20,16 "$ASSETS/tray-live.ico"
magick "$BASE" -colorspace Gray -brightness-contrast -12 \
  -define icon:auto-resize=48,32,24,20,16 "$ASSETS/tray-stopped.ico"
badge '#F5A524' "$ASSETS/tray-warning.ico"
badge '#E53935' "$ASSETS/tray-alarm.ico"

echo "==> $ROOT/macos/Resources/AppIcon.icns"
echo "==> $ROOT/android/src/main/res/{drawable,mipmap-anydpi-v26}"
echo "==> $ASSETS/{BabyMonitor.ico,BabyMonitor.png,tray-*.ico}"
