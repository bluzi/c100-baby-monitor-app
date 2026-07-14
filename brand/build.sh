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
#
#   ./brand/build.sh [--preview]
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
swift "$ROOT/brand/icon.swift" \
  --macos "$ICONSET" \
  --android "$ROOT/android/src/main/res" \
  "${PREVIEW_ARGS[@]}"

echo "==> Packing the Mac's .icns"
iconutil --convert icns "$ICONSET" --output "$ROOT/macos/Resources/AppIcon.icns"

echo "==> $ROOT/macos/Resources/AppIcon.icns"
echo "==> $ROOT/android/src/main/res/{drawable,mipmap-anydpi-v26}"
