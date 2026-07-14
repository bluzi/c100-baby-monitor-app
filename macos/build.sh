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
  # A trusted timestamp is what keeps a signature valid after the certificate expires. It is worth
  # a round-trip to Apple's server for something people install; it is not worth one for a build
  # that exists for ninety seconds. (And on a machine without a route to timestamp.apple.com, the
  # signing step does not fail — it *hangs*, which is a far worse way to spend an afternoon.)
  TIMESTAMP_FLAG="--timestamp"
else
  GRADLE_TASK="linkDebugFrameworkMacosArm64"
  FRAMEWORK_DIR="$ROOT/core/build/bin/macosArm64/debugFramework"
  SWIFT_FLAGS="-Onone -g"
  TIMESTAMP_FLAG="--timestamp=none"
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

# MACOS-17: the icon. Committed rather than generated at build time — regenerate it with
# ./macos/make-icon.sh after changing tools/make-icon.swift.
if [ -f "$ROOT/macos/Resources/AppIcon.icns" ]; then
  cp "$ROOT/macos/Resources/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
else
  echo "!! macos/Resources/AppIcon.icns is missing — run ./macos/make-icon.sh" >&2
fi

# The Kotlin framework is static, so nothing to embed — the binary carries the monitor, libopus and
# all. Verified below, because a missing dylib would only show up at 3am on someone else's Mac.
if otool -L "$APP/Contents/MacOS/BabyMonitor" | grep -qi "BabyMonitorCore\|libopus"; then
  echo "!! The binary links a dynamic BabyMonitorCore or libopus — it must be static." >&2
  otool -L "$APP/Contents/MacOS/BabyMonitor" >&2
  exit 1
fi

# Signing. This is NOT cosmetic, and it is not mainly about Gatekeeper.
#
# macOS guards a Keychain item against the exact binary that wrote it. Every build is a different
# binary, so the app is asked for the login password before it can read a session token it wrote
# itself — and that prompt blocks startup. Three tiers, best first:
#
#  1. Developer ID + the provisioning profile. This is the only combination that removes the prompt
#     entirely: the profile grants keychain-access-groups, which lets the app use the
#     data-protection Keychain — the one that identifies an app by its IDENTITY rather than by the
#     bytes of its binary. An update is then still the same app, and it reads its own token in
#     silence. Nothing less works: a team-based signature is not enough, because the login
#     Keychain's access list binds to the binary no matter who signed it (measured, twice).
#  2. Developer ID alone, or a self-signed certificate: ONE password prompt after each update.
#     Survivable only because an update never applies while monitoring is running (UPD-5), so the
#     parent is at the machine when it appears. That is AUTH-6m in the spec.
#  3. Ad-hoc: a prompt after every single build. Development only.
#
# The entitlement is attached ONLY alongside the profile. Claim it without one and the kernel
# SIGKILLs the process at exec — Developer ID or not.
DEV_ID="$(security find-identity -v -p codesigning 2>/dev/null \
  | grep -m1 "Developer ID Application" | sed -E 's/.*"(.*)"/\1/' || true)"

# BM_SIGN=adhoc: sign ad-hoc and never touch the Keychain. For an agent, a CI runner, or any shell
# with no way to answer a Keychain prompt — where signing with a real identity does not fail, it
# *blocks*, and the build simply never finishes. The app still runs; it will just ask for the login
# password when it wants its session back, which a throwaway build has no business having anyway.
if [ "${BM_SIGN:-}" = "adhoc" ]; then
  DEV_ID=""
  SKIP_SELF_SIGNED=1
fi
# The team id is the parenthesised part of the identity — "Developer ID Application: Name (TEAMID)".
# Taken from the certificate rather than hard-coded, so nothing drifts if the team ever changes.
TEAM_ID="${BM_TEAM_ID:-$(printf '%s' "$DEV_ID" | sed -nE 's/.*\(([A-Z0-9]{10})\)$/\1/p')}"
SELF_SIGNED="${BM_MACOS_IDENTITY:-Baby Monitor Self-Signed}"

PROFILE="$ROOT/macos/Resources/BabyMonitor.provisionprofile"

if [ -n "$DEV_ID" ] && [ -f "$PROFILE" ]; then
  # The provisioning profile is what lets the entitlement exist at all. Without it the kernel
  # SIGKILLs the process at exec for claiming keychain-access-groups — Developer ID or not.
  cp "$PROFILE" "$APP/Contents/embedded.provisionprofile"

  ENTITLEMENTS="$OUT/BabyMonitor.entitlements"
  cat > "$ENTITLEMENTS" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>com.apple.application-identifier</key>
    <string>$TEAM_ID.com.bluzi.babymonitor</string>
    <key>com.apple.developer.team-identifier</key>
    <string>$TEAM_ID</string>
    <key>keychain-access-groups</key>
    <array>
        <string>$TEAM_ID.com.bluzi.babymonitor</string>
    </array>
</dict>
</plist>
EOF
  codesign --force --deep $TIMESTAMP_FLAG --options runtime \
    --entitlements "$ENTITLEMENTS" --sign "$DEV_ID" "$APP"
  echo "==> Signed with '$DEV_ID' + entitlement (data-protection Keychain — no password prompts)"
elif [ -n "$DEV_ID" ]; then
  codesign --force --deep $TIMESTAMP_FLAG --options runtime --sign "$DEV_ID" "$APP"
  echo "==> Signed with '$DEV_ID' (no provisioning profile — one Keychain prompt per update)"
elif [ -z "${SKIP_SELF_SIGNED:-}" ] && security find-identity -v -p codesigning 2>/dev/null | grep -qF "$SELF_SIGNED"; then
  codesign --force --deep --sign "$SELF_SIGNED" "$APP"
  echo "==> Signed with '$SELF_SIGNED'"
  echo "   (macOS will ask for your login password once after each update — see AUTH-6m)"
else
  codesign --force --deep --sign - "$APP" 2>/dev/null || true
  echo "!! No signing identity — signed ad-hoc." >&2
  echo "!! macOS will ask for your login password after EVERY build." >&2
  echo "!! Run ./macos/make-signing-cert.sh once to reduce that to once per update." >&2
fi

echo "==> $APP"
