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
mkdir "$tmp/plain"; printf 'x' > "$tmp/plain/kcap-server"
tar -czf "$tmp/dir/kcap-local-1.2.3-osx-arm64.tar.gz" -C "$tmp/plain" .

out="$(bash "$sh" 1.2.3 osx-arm64 "$tmp/dir/kcap-local-1.2.3-osx-arm64.tar.gz" "$tmp/k.pub")"
check "schema"              "1" "$(jq -r .schema <<<"$out")"
check "version"             "1.2.3" "$(jq -r .version <<<"$out")"
check "rid"                 "osx-arm64" "$(jq -r .rid <<<"$out")"
check "archive is relative" "kcap-local-1.2.3-osx-arm64.tar.gz" "$(jq -r .archive <<<"$out")"
check "sha256 of the file"  "$(sha256_of "$tmp/dir/kcap-local-1.2.3-osx-arm64.tar.gz")" "$(jq -r .sha256 <<<"$out")"
check "size"                "$(wc -c <"$tmp/dir/kcap-local-1.2.3-osx-arm64.tar.gz" | tr -d ' ')" "$(jq -r .size <<<"$out")"
check "key id is the SPKI digest" "$(sha256_of "$tmp/k.der")" "$(jq -r .key_id <<<"$out")"
check "deterministic"       "$out" "$(bash "$sh" 1.2.3 osx-arm64 "$tmp/dir/kcap-local-1.2.3-osx-arm64.tar.gz" "$tmp/k.pub")"

h64="$(printf 'a%.0s' $(seq 64))"
model='{"model_id":"m","contract":1,"model_set":"'"$h64"'","files":[{"name":"model.onnx","sha256":"'"$h64"'","size":3,"source_url":"https://u/x"}]}'
check "no model, no key" "false" "$(jq 'has("embedding_model")' <<<"$out")"
mkdir "$tmp/stage"; printf 'x' > "$tmp/stage/kcap-server"; printf '%s' "$model" > "$tmp/stage/embedding-model.json"
tar -czf "$tmp/dir/with-model.tar.gz" -C "$tmp/stage" .
withm="$(bash "$sh" 1.2.3 osx-arm64 "$tmp/dir/with-model.tar.gz" "$tmp/k.pub")"
check "model carried verbatim" "$(jq -S . <<<"$model")" "$(jq -S .embedding_model <<<"$withm")"
check "model key keeps the rest" "1" "$(jq -r .schema <<<"$withm")"
check "deterministic with a model" "$withm" "$(bash "$sh" 1.2.3 osx-arm64 "$tmp/dir/with-model.tar.gz" "$tmp/k.pub")"

set +e; bash "$sh" 1.2.3 osx-arm64 "$tmp/missing.tar.gz" "$tmp/k.pub" 2>/dev/null; rc=$?; set -e
check "a missing archive fails" "1" "$rc"

printf '%s' "$model" | jq '.contract = 0' > "$tmp/stage/embedding-model.json"
tar -czf "$tmp/dir/bad-model.tar.gz" -C "$tmp/stage" .
set +e; bash "$sh" 1.2.3 osx-arm64 "$tmp/dir/bad-model.tar.gz" "$tmp/k.pub" >/dev/null 2>&1; rc=$?; set -e
check "a model entry kcap rejects fails" "1" "$rc"

printf 'not a tar archive' > "$tmp/dir/notar.tar.gz"
set +e; bash "$sh" 1.2.3 osx-arm64 "$tmp/dir/notar.tar.gz" "$tmp/k.pub" >/dev/null 2>&1; rc=$?; set -e
check "an archive tar cannot list fails" "1" "$rc"

[ "$fail" -eq 0 ] && echo "ok" || exit 1
