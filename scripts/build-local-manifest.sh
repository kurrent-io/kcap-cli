#!/usr/bin/env bash
# Prints the manifest for one local-edition bundle archive. The archive is named relative to the
# manifest's own location, so the manifest does not depend on the host serving it. key_id is the
# SHA-256 of the signing key's SubjectPublicKeyInfo, which lets a verifier holding several keys
# pick one. No timestamp: the same archive must produce the same bytes.
# Usage: build-local-manifest.sh <version> <rid> <archive> <public-key-pem>
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/hash.sh
source "$here/lib/hash.sh"

usage="usage: build-local-manifest.sh <version> <rid> <archive> <public-key-pem>"
version="${1:?$usage}"; rid="${2:?$usage}"; archive="${3:?$usage}"; pub="${4:?$usage}"
[ -f "$archive" ] || { echo "no archive at $archive" >&2; exit 1; }

spki="$(mktemp)"; trap 'rm -f "$spki"' EXIT
openssl pkey -pubin -in "$pub" -outform DER -out "$spki"
size="$(wc -c <"$archive" | tr -d ' ')"

jq -S -n --arg v "$version" --arg rid "$rid" --arg name "$(basename "$archive")" \
  --arg sha "$(sha256_of "$archive")" --argjson size "$size" --arg key "$(sha256_of "$spki")" \
  '{schema: 1, version: $v, rid: $rid, archive: $name, sha256: $sha, size: $size, key_id: $key}'
