#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/build-local-manifest.sh"
source "$here/lib/hash.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

fail=0
check() { # <label> <want> <got>
  if [ "$2" != "$3" ]; then echo "FAIL: $1 -> '$3' (want '$2')"; fail=1; fi
}

openssl ecparam -name prime256v1 -genkey -noout -out "$tmp/k.key" 2>/dev/null
openssl pkey -in "$tmp/k.key" -pubout -out "$tmp/k.pub"
openssl pkey -pubin -in "$tmp/k.pub" -outform DER -out "$tmp/k.der"
mkdir "$tmp/dir"
printf 'bundle' > "$tmp/dir/kcap-local-1.2.3-osx-arm64.tar.gz"

out="$(bash "$sh" 1.2.3 osx-arm64 "$tmp/dir/kcap-local-1.2.3-osx-arm64.tar.gz" "$tmp/k.pub")"
check "schema"              "1" "$(jq -r .schema <<<"$out")"
check "version"             "1.2.3" "$(jq -r .version <<<"$out")"
check "rid"                 "osx-arm64" "$(jq -r .rid <<<"$out")"
check "archive is relative" "kcap-local-1.2.3-osx-arm64.tar.gz" "$(jq -r .archive <<<"$out")"
check "sha256 of the file"  "$(sha256_of "$tmp/dir/kcap-local-1.2.3-osx-arm64.tar.gz")" "$(jq -r .sha256 <<<"$out")"
check "size"                "6" "$(jq -r .size <<<"$out")"
check "key id is the SPKI digest" "$(sha256_of "$tmp/k.der")" "$(jq -r .key_id <<<"$out")"
check "deterministic"       "$out" "$(bash "$sh" 1.2.3 osx-arm64 "$tmp/dir/kcap-local-1.2.3-osx-arm64.tar.gz" "$tmp/k.pub")"

set +e; bash "$sh" 1.2.3 osx-arm64 "$tmp/missing.tar.gz" "$tmp/k.pub" 2>/dev/null; rc=$?; set -e
check "a missing archive fails" "1" "$rc"

[ "$fail" -eq 0 ] && echo "ok" || exit 1
