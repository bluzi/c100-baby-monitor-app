#!/usr/bin/env bash
#
# Creates the self-signed code-signing certificate the Mac app is signed with. Run once per
# machine. Idempotent — it does nothing if the certificate is already there and usable.
#
#   ./macos/make-signing-cert.sh
#
# WHY THIS EXISTS
#
# An ad-hoc signature (`codesign -s -`) is a hash of the binary, so it changes with every build.
# macOS binds a Keychain item's access rule to the signing identity of whatever wrote it — so with
# ad-hoc signing, EVERY update is a different app in the Keychain's eyes, and the user is asked for
# their login password before the app can read its own stored session. That prompt blocks startup:
# after an overnight auto-update you would come back to a monitor that is not running, and a
# password box nobody was awake to answer.
#
# A certificate is a stable identity, so every future build satisfies the same rule. No prompt.
#
# This is not about Gatekeeper. A self-signed app still needs one right-click → Open the first time
# it is installed. A real Apple Developer ID would fix that too; this fixes, for free, the one that
# actually matters at 3am.
set -euo pipefail

NAME="${BM_MACOS_IDENTITY:-Baby Monitor Self-Signed}"

usable() {
  security find-identity -v -p codesigning 2>/dev/null | grep -qF "$NAME"
}

if usable; then
  echo "==> '$NAME' is already set up."
  security find-identity -v -p codesigning | grep -F "$NAME"
  exit 0
fi

# A previous run may have left an untrusted certificate behind; start clean.
if security find-identity -p codesigning 2>/dev/null | grep -qF "$NAME"; then
  echo "==> Removing a previous, untrusted '$NAME'"
  security delete-identity -c "$NAME" >/dev/null 2>&1 || true
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "==> Creating a self-signed code-signing certificate: $NAME"

cat > "$TMP/openssl.cnf" <<EOF
[ req ]
distinguished_name = dn
x509_extensions = ext
prompt = no

[ dn ]
CN = $NAME

[ ext ]
basicConstraints = critical,CA:false
keyUsage = critical,digitalSignature
extendedKeyUsage = critical,codeSigning
EOF

openssl req -x509 -newkey rsa:2048 -nodes -days 3650 \
  -keyout "$TMP/key.pem" -out "$TMP/cert.pem" -config "$TMP/openssl.cnf" 2>/dev/null

# Legacy PBE on purpose: OpenSSL 3 defaults to AES-256/SHA-256, and macOS's `security import`
# cannot read that container ("MAC verification failed"). These are the algorithms it understands.
openssl pkcs12 -export -inkey "$TMP/key.pem" -in "$TMP/cert.pem" \
  -out "$TMP/identity.p12" -passout pass:babymonitor -name "$NAME" \
  -keypbe PBE-SHA1-3DES -certpbe PBE-SHA1-3DES -macalg sha1 2>/dev/null

# -T /usr/bin/codesign: codesign may use this key without asking for the login password.
security import "$TMP/identity.p12" -k ~/Library/Keychains/login.keychain-db \
  -P babymonitor -T /usr/bin/codesign -A >/dev/null

# codesign will not use an identity it does not trust for code signing. This is the step that
# needs your permission — macOS will ask for your login password once.
echo "==> Trusting it for code signing (macOS will ask for your password once)"
security add-trusted-cert -r trustRoot -p codeSign \
  -k ~/Library/Keychains/login.keychain-db "$TMP/cert.pem"

# Otherwise the Keychain asks for permission every single time codesign uses the key.
security set-key-partition-list -S apple-tool:,apple:,codesign: \
  -s -k "" ~/Library/Keychains/login.keychain-db >/dev/null 2>&1 || true

if ! usable; then
  echo "!! The certificate exists but codesign still will not use it." >&2
  echo "   Open Keychain Access → login → Certificates → '$NAME' → Get Info → Trust →" >&2
  echo "   set 'Code Signing' to 'Always Trust'." >&2
  exit 1
fi

echo "==> Done:"
security find-identity -v -p codesigning | grep -F "$NAME"
echo
echo "Rebuild with:  ./macos/build.sh"
