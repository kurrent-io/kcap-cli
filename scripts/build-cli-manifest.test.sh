#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/build-cli-manifest.sh"
source "$here/lib/hash.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

fail=0
check() { # <label> <want> <got>
  if [ "$2" != "$3" ]; then echo "FAIL: $1 -> '$3' (want '$2')"; fail=1; fi
}

mkdir "$tmp/a"
printf 'mac' > "$tmp/a/kcap-osx-arm64.tar.gz"
printf 'windows' > "$tmp/a/kcap-win-x64.zip"
printf 'sqlite' > "$tmp/a/libe_sqlite3-osx-arm64.dylib"

out="$(bash "$sh" 1.2.3 abc123 "$tmp/a" https://example.test/dl/)"
check "version"                "1.2.3" "$(jq -r .version <<<"$out")"
check "commit"                 "abc123" "$(jq -r .commit <<<"$out")"
check "only kcap archives"     "osx-arm64 win-x64" "$(jq -r '.platforms | keys | join(" ")' <<<"$out")"
check "url, no double slash"   "https://example.test/dl/kcap-osx-arm64.tar.gz" "$(jq -r '.platforms["osx-arm64"].url' <<<"$out")"
check "zip rid"                "https://example.test/dl/kcap-win-x64.zip" "$(jq -r '.platforms["win-x64"].url' <<<"$out")"
check "sha256 of the file"     "$(sha256_of "$tmp/a/kcap-win-x64.zip")" "$(jq -r '.platforms["win-x64"].sha256' <<<"$out")"
check "size"                   "7" "$(jq -r '.platforms["win-x64"].size' <<<"$out")"
check "deterministic"          "$out" "$(bash "$sh" 1.2.3 abc123 "$tmp/a" https://example.test/dl)"

mkdir "$tmp/empty"
set +e; bash "$sh" 1.2.3 abc "$tmp/empty" https://x 2>/dev/null; rc=$?; set -e
check "no archives fails" "1" "$rc"

[ "$fail" -eq 0 ] && echo "ok" || exit 1
