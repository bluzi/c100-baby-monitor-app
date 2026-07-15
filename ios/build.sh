#!/usr/bin/env bash
#
# Builds ios/build/BabyMonitor.app for the iOS Simulator.
#
# No Xcode project, for exactly the reasons the macOS app has none (see macos/build.sh): the app is a
# handful of SwiftUI screens over the shared monitor, and a .pbxproj would be the largest and least
# reviewable file in the repo, one CI would have to parse. swiftc, a bundle layout and simctl are the
# whole build.
#
# This targets the **simulator**, which is what we verify on. A real-device or App Store build needs
# an Apple Developer account, provisioning and App Store Connect — a separate pipeline. Updates on
# iOS are the App Store's job by design (UPD-2i), so there is no self-updater to build.
#
#   ./ios/build.sh [debug|release]
set -euo pipefail

CONFIG="${1:-debug}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/ios/build"
APP="$OUT/BabyMonitor.app"

VERSION_CODE="${BM_VERSION_CODE:-$(git -C "$ROOT" rev-list --count HEAD 2>/dev/null || echo 1)}"
VERSION_NAME="${BM_VERSION_NAME:-0.1.$VERSION_CODE}"

if [ "$CONFIG" = "release" ]; then
  GRADLE_TASK="linkReleaseFrameworkIosSimulatorArm64"
  FRAMEWORK_DIR="$ROOT/core/build/bin/iosSimulatorArm64/releaseFramework"
  SWIFT_FLAGS="-O"
else
  GRADLE_TASK="linkDebugFrameworkIosSimulatorArm64"
  FRAMEWORK_DIR="$ROOT/core/build/bin/iosSimulatorArm64/debugFramework"
  SWIFT_FLAGS="-Onone -g"
fi

# The SDK is part of what ships, not a property of the build machine (see macos/build.sh for the full
# argument). iOS 26 is where Liquid Glass lives; this app is designed against it and requires it.
SDK_VERSION="$(xcrun --sdk iphonesimulator --show-sdk-version 2>/dev/null || echo 0)"
SDK_MAJOR="${SDK_VERSION%%.*}"
if [ "${SDK_MAJOR:-0}" -lt 26 ]; then
  echo "!! iOS Simulator SDK $SDK_VERSION is too old — this app is designed against SDK 26 (Xcode 26)." >&2
  echo "!! Install Xcode 26 and select it: sudo xcode-select -s /Applications/Xcode_26.app" >&2
  exit 1
fi

# libopus for the simulator must exist or the framework link fails. Build it once if missing.
if [ ! -f "$ROOT/core/build/opus-ios/sim/lib/libopus.a" ]; then
  echo "==> Building libopus for iOS (one-time — see core/opus/build-ios-opus.sh)"
  "$ROOT/core/opus/build-ios-opus.sh"
fi

echo "==> Building the shared monitor ($CONFIG)  [iOS Simulator SDK $SDK_VERSION]"
"$ROOT/gradlew" -p "$ROOT" ":core:$GRADLE_TASK" --console=plain -q

echo "==> Compiling the iOS shell (v$VERSION_NAME)"
rm -rf "$APP"
mkdir -p "$APP"

SDKPATH="$(xcrun --sdk iphonesimulator --show-sdk-path)"

# shellcheck disable=SC2086
# -parse-as-library: the app's entry point is a SwiftUI `@main App`, not a main.swift full of
# top-level statements (the shape the macOS menu-bar app uses). Without this, swiftc treats the
# sources as a script and rejects `@main`.
# shellcheck disable=SC2086
xcrun -sdk iphonesimulator swiftc $SWIFT_FLAGS \
  -target arm64-apple-ios26.0-simulator \
  -sdk "$SDKPATH" \
  -swift-version 5 \
  -parse-as-library \
  -F "$FRAMEWORK_DIR" \
  -framework BabyMonitorCore \
  -o "$APP/BabyMonitor" \
  "$ROOT"/ios/Sources/*.swift "$ROOT"/ios/Shared/*.swift

sed -e "s/__VERSION_NAME__/$VERSION_NAME/" -e "s/__VERSION_CODE__/$VERSION_CODE/" \
  "$ROOT/ios/Resources/Info.plist" > "$APP/Info.plist"

printf 'APPL????' > "$APP/PkgInfo"

# Icon (IOS-1 / UI-3): compiled from the shared brand mark into Assets.car. Non-fatal if not yet
# generated — the app still launches, wearing the default icon.
if [ -d "$ROOT/ios/Assets.xcassets" ]; then
  xcrun actool "$ROOT/ios/Assets.xcassets" \
    --compile "$APP" \
    --app-icon AppIcon \
    --platform iphonesimulator \
    --minimum-deployment-target 26.0 \
    --output-partial-info-plist "$OUT/assetcatalog-info.plist" >/dev/null \
    || echo "!! actool could not compile the icon — run ./brand/build.sh" >&2
else
  echo "!! ios/Assets.xcassets is missing — run ./brand/build.sh (the app shows a default icon)" >&2
fi

# The Live Activity widget (BG-2i/3i), when present, is embedded as a PlugIn.
if [ -d "$ROOT/ios/Widget" ]; then
  "$ROOT/ios/build-widget.sh" "$CONFIG"
fi

# The Kotlin framework is static, so nothing to embed — the binary carries the monitor, libopus and
# all. Verified, because a stray dynamic dependency would only surface at launch on someone's phone.
if otool -L "$APP/BabyMonitor" | grep -qi "BabyMonitorCore\|libopus"; then
  echo "!! The binary links a dynamic BabyMonitorCore or libopus — it must be static." >&2
  otool -L "$APP/BabyMonitor" >&2
  exit 1
fi

# Ad-hoc sign for the simulator, no entitlements — and that is forced, not chosen. The iOS 26
# Simulator's AMFI SIGKILLs an ad-hoc-signed app at exec the instant it carries ANY restricted
# entitlement: the launch is denied ("denied by service delegate (SBMainWorkspace)"), with no crash
# report because the kernel kills it before main(). This is true of `get-task-allow`,
# `keychain-access-groups` and `application-identifier` alike, and signing with a real Apple
# Development identity does not change it without a provisioning profile to back the entitlement —
# which is the separate device pipeline noted at the top, not something the Simulator build has.
#
# The one thing this costs is the Keychain: with no keychain-access-groups the app has no keychain
# access group, so SecItemAdd returns errSecMissingEntitlement (-34018) and the session cannot be
# persisted on the Simulator at all (AUTH-13 makes the app say so instead of looping on login; a
# real provisioned device stores it fine). Everything else the app needs here — background audio,
# Live Activities, notifications — genuinely needs no entitlement.
codesign --force --sign - "$APP"

echo "==> $APP"
