#!/usr/bin/env bash
#
# Regenerates macos/Resources/AppIcon.icns from tools/make-icon.swift (MACOS-17).
#
# The .icns is committed, so a normal build never needs this — run it only after changing the
# drawing. Keeping the source of the icon as code, rather than a binary somebody exported once from
# an app they no longer have, is the same instinct as the rest of this repo.
#
#   ./macos/make-icon.sh
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

ICONSET="$WORK/AppIcon.iconset"
mkdir -p "$ICONSET"

echo "==> Drawing the icon"
swift "$ROOT/macos/tools/make-icon.swift" "$ICONSET"

echo "==> Packing the .icns"
iconutil --convert icns "$ICONSET" --output "$ROOT/macos/Resources/AppIcon.icns"

echo "==> $ROOT/macos/Resources/AppIcon.icns"
