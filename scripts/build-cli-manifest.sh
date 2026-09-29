#!/usr/bin/env bash
# Prints the install manifest for one CLI release: per RID, the archive's download URL, sha256
# and size. Built from the archive files themselves, so a checksum can never describe other bytes.
# No timestamp: re-running over the same archives must produce the same bytes, which is what
# lets the publish job tell a re-run from a re-cut.
# Usage: build-cli-manifest.sh <version> <commit> <archive-dir> <base-url>
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/hash.sh
source "$here/lib/hash.sh"

usage="usage: build-cli-manifest.sh <version> <commit> <archive-dir> <base-url>"
version="${1:?$usage}"; commit="${2:?$usage}"; dir="${3:?$usage}"; base="${4:?$usage}"
base="${base%/}"

shopt -s nullglob
archives=("$dir"/kcap-*.tar.gz "$dir"/kcap-*.zip)
shopt -u nullglob
[ "${#archives[@]}" -gt 0 ] || { echo "no kcap-<rid> archives in $dir" >&2; exit 1; }

platforms='{}'
for f in "${archives[@]}"; do
  name="$(basename "$f")"
  rid="${name#kcap-}"; rid="${rid%.tar.gz}"; rid="${rid%.zip}"
  size="$(wc -c <"$f" | tr -d ' ')"
  platforms="$(jq -c --arg rid "$rid" --arg url "$base/$name" --arg sha "$(sha256_of "$f")" --argjson size "$size" \
    '. + {($rid): {url: $url, sha256: $sha, size: $size}}' <<<"$platforms")"
done

jq -S -n --arg v "$version" --arg c "$commit" --argjson p "$platforms" \
  '{version: $v, commit: $c, platforms: $p}'
