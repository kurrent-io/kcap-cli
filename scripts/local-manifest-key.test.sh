#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/local-manifest-key.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

openssl ecparam -name prime256v1 -genkey -noout -out "$tmp/a.key" 2>/dev/null
openssl pkey -in "$tmp/a.key" -pubout -out "$tmp/a.pub"
openssl ecparam -name prime256v1 -genkey -noout -out "$tmp/b.key" 2>/dev/null
openssl pkey -in "$tmp/b.key" -pubout -out "$tmp/b.pub"
printf '{"version":"1.0.0"}\n' > "$tmp/manifest.json"

fail=0
expect() { # <label> <want-rc> <key-file-or-empty> <args...>
  local label="$1" want="$2" key="$3" rc; shift 3
  set +e
  if [ -n "$key" ]; then LOCAL_BUNDLE_MANIFEST_KEY="$(cat "$key")" bash "$sh" "$@" >"$tmp/out" 2>&1
  else env -u LOCAL_BUNDLE_MANIFEST_KEY bash "$sh" "$@" >"$tmp/out" 2>&1; fi
  rc=$?
  set -e
  if [ "$rc" != "$want" ]; then echo "FAIL: $label -> rc=$rc (want $want): $(cat "$tmp/out")"; fail=1; fi
}

expect "check: matching key"           0 "$tmp/a.key" check "$tmp/a.pub"
expect "check: unset secret"           1 ""           check "$tmp/a.pub"
grep -q "LOCAL_BUNDLE_MANIFEST_KEY is not set" "$tmp/out" || { echo "FAIL: unset message names the secret"; fail=1; }
expect "check: another key"            1 "$tmp/b.key" check "$tmp/a.pub"
grep -q "does not match the public key" "$tmp/out" || { echo "FAIL: mismatch message"; fail=1; }
printf 'not a key' > "$tmp/junk"
expect "check: not a PEM"              1 "$tmp/junk"  check "$tmp/a.pub"
grep -q "not a PEM private key" "$tmp/out" || { echo "FAIL: junk message"; fail=1; }
grep -q "not a key" "$tmp/out" && { echo "FAIL: the secret's value was echoed"; fail=1; }

expect "sign"                          0 "$tmp/a.key" sign "$tmp/a.pub" "$tmp/manifest.json" "$tmp/manifest.json.sig"
expect "sign: mismatched key refuses"  1 "$tmp/b.key" sign "$tmp/a.pub" "$tmp/manifest.json" "$tmp/other.sig"
[ -e "$tmp/other.sig" ] && { echo "FAIL: a mismatched key wrote a signature"; fail=1; }
expect "verify: own signature"         0 "" verify "$tmp/a.pub" "$tmp/manifest.json" "$tmp/manifest.json.sig"
expect "verify: another key"           1 "" verify "$tmp/b.pub" "$tmp/manifest.json" "$tmp/manifest.json.sig"
printf '{"version":"1.0.1"}\n' > "$tmp/tampered.json"
expect "verify: tampered manifest"     1 "" verify "$tmp/a.pub" "$tmp/tampered.json" "$tmp/manifest.json.sig"
grep -q '^[A-Za-z0-9+/=]*$' "$tmp/manifest.json.sig" || { echo "FAIL: the signature is one line of base64"; fail=1; }

[ "$fail" -eq 0 ] && echo "ok" || exit 1
