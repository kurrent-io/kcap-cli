#!/usr/bin/env bash
# Puts the built-in embedding model set a local-edition bundle pins into the bucket, at
# models/<model_set>/<name> under $DEST (the bucket's local/ prefix, where the CLI reads
# $BaseUrl/models/<model_set>/<name>). Each file is checked against the size and digest the bundle
# pins, and the set id against the files, before anything is uploaded. An object already stored is
# never replaced: it must verify, and one that does not stops the run. A missing one is downloaded
# from the pinned upstream URL, verified and uploaded. An archive with no model publishes nothing.
# Run it before the bundle's manifest is published; a re-run from the top is safe.
# Usage: publish-local-model.sh <bundle-archive>
#   $R2 and $DEST as for r2-fetch.sh.
set -euo pipefail
usage="usage: publish-local-model.sh <bundle-archive>"
archive="${1:?$usage}"
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/hash.sh
source "$here/lib/hash.sh"
# shellcheck source=lib/embedding-model.sh
source "$here/lib/embedding-model.sh"
[ -f "$archive" ] || { echo "no archive at $archive" >&2; exit 1; }

member="$(embedding_model_member "$archive")" || { echo "could not list $archive" >&2; exit 1; }
[ "$member" = present ] || { echo "$archive carries no embedding model; nothing to publish"; exit 0; }

tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
tar -xOf "$archive" ./embedding-model.json > "$tmp/model.json"

if ! embedding_model_valid "$tmp/model.json" \
   || ! jq -e 'all(.files[]; .source_url | type == "string" and startswith("https://"))' "$tmp/model.json" >/dev/null; then
  echo "embedding-model.json in $archive is malformed" >&2; exit 1
fi

# The set id hashes the files in ordinal name order.
set_id="$(jq -r .model_set "$tmp/model.json")"
jq -j '"model=\(.model_id)\ncontract=\(.contract)\n" + ([.files | sort_by(.name)[] | "\(.name)=\(.sha256)\n"] | join(""))' "$tmp/model.json" > "$tmp/id-input"
computed="$(sha256_of "$tmp/id-input")"
[ "$computed" = "$set_id" ] || { echo "model_set $set_id does not match its files (they hash to $computed)" >&2; exit 1; }

# Returns 0 when the object exists and 1 when it does not; exits on any other failure.
exists() { # <key> <file>
  local rc; set +e; bash "$here/r2-fetch.sh" "$1" "$2"; rc=$?; set -e
  case "$rc" in 0) return 0 ;; 44) return 1 ;; *) exit 1 ;; esac
}
matches() { # <file> <sha256> <size>
  [ "$(wc -c <"$1" | tr -d ' ')" = "$3" ] && [ "$(sha256_of "$1")" = "$2" ]
}

while IFS=$'\t' read -r name sha size url; do
  key="models/$set_id/$name"
  if exists "$key" "$tmp/stored"; then
    matches "$tmp/stored" "$sha" "$size" \
      || { echo "local/$key is stored with bytes that do not match the pinned digest. Delete local/$key by hand, or pin the bytes it holds." >&2; exit 1; }
    echo "local/$key is already stored and verified"
    continue
  fi
  curl -fsSL --retry 3 -o "$tmp/upstream" "$url" || { echo "could not download $url" >&2; exit 1; }
  matches "$tmp/upstream" "$sha" "$size" \
    || { echo "$url does not match the pinned size and digest of $name; nothing was uploaded" >&2; exit 1; }
  # shellcheck disable=SC2086 # $R2 is a flag list
  aws s3 cp $R2 "$tmp/upstream" "$DEST/$key" --content-type application/octet-stream --cache-control "public, max-age=31536000, immutable"
  echo "uploaded local/$key"
done < <(jq -r '.files | sort_by(.name)[] | [.name, .sha256, .size, .source_url] | @tsv' "$tmp/model.json")
