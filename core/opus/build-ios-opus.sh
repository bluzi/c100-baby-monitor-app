#!/usr/bin/env bash
#
# Cross-compiles libopus as a static library for iOS — device and simulator, both arm64.
#
#   ./core/opus/build-ios-opus.sh
#
# Why this exists, and why it is not `brew install`:
#
# The camera speaks Opus, and no Apple framework decodes it — so libopus is linked statically into
# the Kotlin/Native framework (see core/build.gradle.kts), exactly as the macOS build does with
# Homebrew's libopus.a. But Homebrew's is built for *macOS* arm64, and an iOS binary cannot link it:
# the simulator and the device are their own platforms, each with its own SDK and its own load
# command in the Mach-O, and the linker refuses to mix them. So the one thing Homebrew cannot give
# us we build ourselves, once, and cache under core/build (which is gitignored — this is an output).
#
# It is a documented prerequisite of the iOS build, the same way `brew install opus` is of the macOS
# one. Runs in a couple of minutes and then no-ops until the outputs are deleted.
set -euo pipefail

VER="1.5.2"
SHA256="65c1d2f78b9f2fb20082c38cbe47c951ad5839345876e46941612ee87f9a7ce1"
# The library's minimum-OS load command. Kept low on purpose: a lib whose minimum is at or below the
# framework that links it never triggers the linker's "built for a newer OS than being linked"
# warning, whatever deployment target Kotlin/Native happens to pick.
MIN_IOS="14.0"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT="$ROOT/core/build/opus-ios"
CACHE="$OUT/cache"
SRC_TGZ="$CACHE/opus-$VER.tar.gz"

mkdir -p "$CACHE"

# --- source, verified ---------------------------------------------------------------------------
if [ ! -f "$SRC_TGZ" ]; then
  echo "==> Downloading opus $VER"
  curl -fsSL -o "$SRC_TGZ" "https://downloads.xiph.org/releases/opus/opus-$VER.tar.gz"
fi
ACTUAL="$(shasum -a 256 "$SRC_TGZ" | awk '{print $1}')"
if [ "$ACTUAL" != "$SHA256" ]; then
  echo "!! opus-$VER.tar.gz checksum mismatch" >&2
  echo "!!   expected $SHA256" >&2
  echo "!!   got      $ACTUAL" >&2
  echo "!! Refusing to build unverified source. Delete $SRC_TGZ to re-download." >&2
  exit 1
fi

# --- one slice ----------------------------------------------------------------------------------
# $1 = label (device|sim), $2 = xcrun sdk name, $3 = clang -target triple
build_slice() {
  local label="$1" sdk="$2" triple="$3"
  local prefix="$OUT/$label"
  local work="$OUT/build-$label"

  if [ -f "$prefix/lib/libopus.a" ] && [ -f "$prefix/include/opus/opus.h" ]; then
    echo "==> $label: already built ($prefix) — skipping"
    return
  fi

  echo "==> $label: configuring + building opus for $triple"
  rm -rf "$work"
  mkdir -p "$work"
  tar -xzf "$SRC_TGZ" -C "$work"
  cd "$work/opus-$VER"

  local sdkpath
  sdkpath="$(xcrun --sdk "$sdk" --show-sdk-path)"
  local clang
  clang="$(xcrun --sdk "$sdk" --find clang)"

  # -target carries the platform (…-simulator vs not), which is what stamps the right load command
  # into every .o and therefore into libopus.a; the sysroot in both CC and LDFLAGS makes configure's
  # own compile+link probes use the iOS SDK rather than the host's.
  export CC="$clang"
  export CFLAGS="-target $triple -isysroot $sdkpath -O2 -fPIC"
  export LDFLAGS="-target $triple -isysroot $sdkpath"
  # An iOS/simulator binary cannot be executed on the build Mac, so configure's run-tests would all
  # fail. Force cross mode: --build must differ from --host. On an Apple-silicon Mac the real build
  # triple canonicalises to aarch64-apple-darwin — the *same* as the host we want — so autoconf would
  # decide it is a native build and try to run the probes. Claiming an x86_64 build host is the
  # standard trick to force cross mode while keeping the host honestly aarch64.
  #
  # host=aarch64 matters: told it was 32-bit `arm`, opus reaches for its ARMv7 hand-assembly
  # (`.syntax unified`, `@`-comments) that the arm64 assembler cannot parse. We sidestep the whole
  # question — `--disable-asm --disable-intrinsics --disable-rtcd` builds the portable C decoder,
  # which is bit-identical to the vectorised one (Opus decoding is normative) and vastly more than
  # fast enough for one mono 48 kHz stream. Reliability over cleverness.
  export cross_compiling=yes

  ./configure \
    --build=x86_64-apple-darwin \
    --host=aarch64-apple-darwin \
    --prefix="$prefix" \
    --disable-shared --enable-static \
    --disable-doc --disable-extra-programs \
    --disable-asm --disable-intrinsics --disable-rtcd \
    >/dev/null

  make -j"$(sysctl -n hw.ncpu)" >/dev/null
  make install >/dev/null
  unset CC CFLAGS LDFLAGS cross_compiling

  # Prove the slice is what it claims to be, because a wrong-platform .a would only surface as a
  # baffling link error against the Kotlin framework much later.
  echo "    $(lipo -archs "$prefix/lib/libopus.a" 2>/dev/null) — $(otool -l "$prefix/lib/libopus.a" 2>/dev/null | grep -m1 -A3 LC_BUILD_VERSION | grep -m1 platform | awk '{print "platform "$2}')"
  cd "$ROOT"
  rm -rf "$work"
}

build_slice sim    iphonesimulator arm64-apple-ios${MIN_IOS}-simulator
build_slice device iphoneos        arm64-apple-ios${MIN_IOS}

echo "==> Done."
echo "    simulator: $OUT/sim/lib/libopus.a"
echo "    device:    $OUT/device/lib/libopus.a"
