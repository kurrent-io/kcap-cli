#!/usr/bin/env bash
# Signs and verifies local-edition bundle manifests: ECDSA P-256 over SHA-256, the signature DER
# encoded and then base64, so the CLI verifies it with the platform's ECDsa and no extra package.
# The private key is the PEM in LOCAL_BUNDLE_MANIFEST_KEY and must match <public-key-pem>, the key
# the CLI embeds; a mismatch fails before anything is signed.
# Usage: local-manifest-key.sh check  <public-key-pem>
#        local-manifest-key.sh sign   <public-key-pem> <manifest> <signature-out>
#        local-manifest-key.sh verify <public-key-pem> <manifest> <signature>
set -euo pipefail
usage="usage: local-manifest-key.sh check|sign|verify <public-key-pem> [<manifest> <signature>]"
cmd="${1:?$usage}"; pub="${2:?$usage}"
[ -f "$pub" ] || { echo "no public key at $pub" >&2; exit 1; }
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

load_private() {
  if [ -z "${LOCAL_BUNDLE_MANIFEST_KEY:-}" ]; then
    echo "LOCAL_BUNDLE_MANIFEST_KEY is not set. Create it with scripts/new-local-manifest-key.sh, which stores the private key as that repository secret and writes the public key to $pub." >&2
    exit 1
  fi
  (umask 077; printf '%s\n' "$LOCAL_BUNDLE_MANIFEST_KEY" > "$tmp/key.pem")
  openssl pkey -in "$tmp/key.pem" -noout 2>/dev/null || { echo "LOCAL_BUNDLE_MANIFEST_KEY is not a PEM private key" >&2; exit 1; }
  openssl pkey -in "$tmp/key.pem" -pubout -outform DER -out "$tmp/derived.der"
  openssl pkey -pubin -in "$pub" -outform DER -out "$tmp/embedded.der"
  cmp -s "$tmp/derived.der" "$tmp/embedded.der" || {
    echo "LOCAL_BUNDLE_MANIFEST_KEY does not match the public key in $pub: the CLI built from this commit could not verify what it signs" >&2
    exit 1
  }
}

verify() { # <manifest> <signature>
  base64 --decode < "$2" > "$tmp/sig.der" 2>/dev/null || base64 -D < "$2" > "$tmp/sig.der"
  openssl dgst -sha256 -verify "$pub" -signature "$tmp/sig.der" "$1" >/dev/null
}

case "$cmd" in
  check)
    load_private
    echo "LOCAL_BUNDLE_MANIFEST_KEY matches $pub" ;;
  sign)
    manifest="${3:?$usage}"; out="${4:?$usage}"
    load_private
    openssl dgst -sha256 -sign "$tmp/key.pem" -out "$tmp/sig.der" "$manifest"
    base64 < "$tmp/sig.der" | tr -d '\n' > "$out"
    verify "$manifest" "$out" || { echo "the signature just written does not verify" >&2; exit 1; } ;;
  verify)
    manifest="${3:?$usage}"; sig="${4:?$usage}"
    verify "$manifest" "$sig" || { echo "$sig is not a valid signature of $manifest" >&2; exit 1; } ;;
  *)
    echo "$usage" >&2; exit 2 ;;
esac
