#!/usr/bin/env bash
# The CLI downloads the e_sqlite3 native on demand from its release's assets and verifies it against
# the SHA-256 SqliteNativeResolver pins per RID. Stages the publish output's native under the name
# the resolver expects, <basename>-<rid>.<ext>, refusing bytes that do not match the pin: every
# cold-cache OpenCode import would fail to verify them.
# Usage: stage-sqlite-native.sh <rid> <cli-publish-dir> <out-dir>
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/hash.sh
source "$here/lib/hash.sh"
usage="usage: stage-sqlite-native.sh <rid> <cli-publish-dir> <out-dir>"
rid="${1:?$usage}"; publish="${2:?$usage}"; out="${3:?$usage}"

shopt -s nullglob
matches=("$publish"/*e_sqlite3*)
shopt -u nullglob
if [ "${#matches[@]}" -ne 1 ]; then
  echo "::error::expected exactly one e_sqlite3 native in $publish for $rid, found ${#matches[@]}"
  ls -la "$publish" || true
  exit 1
fi
src="${matches[0]}"

pin=$(grep -E "\[\"$rid\"\]" "$here/../src/Capacitor.Cli/Commands/SqliteNativeResolver.cs" \
      | grep -oE '[0-9a-f]{64}' | head -1 || true)
[ -n "$pin" ] || { echo "::error::no SqliteNativeResolver pin found for $rid"; exit 1; }
actual="$(sha256_of "$src")"
[ "$actual" = "$pin" ] || { echo "::error::e_sqlite3 native for $rid ($actual) does not match resolver pin ($pin)"; exit 1; }

mkdir -p "$out"
base=$(basename "$src"); name="${base%.*}"; ext="${base##*.}"
cp "$src" "$out/${name}-${rid}.${ext}"
ls -la "$out"
