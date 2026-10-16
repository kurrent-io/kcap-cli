#!/usr/bin/env bash
# Uploads one local-edition bundle to $DEST (the bucket's local/<rid>/ prefix): the archive, then
# the signature, then the manifest. The manifest is the publication: written last and only once,
# so a reader that finds it finds everything it vouches for. A manifest already there with the same
# bytes is a re-run that finished; with other bytes, the version was published from another build.
# An archive with no manifest vouches for nothing, so a re-run replaces it.
# Usage: publish-local-bundle.sh <dir> <version> <rid>   ($R2 and $DEST as for r2-fetch.sh)
set -euo pipefail
usage="usage: publish-local-bundle.sh <dir> <version> <rid>"
dir="${1:?$usage}"; version="${2:?$usage}"; rid="${3:?$usage}"
here="$(cd "$(dirname "$0")" && pwd)"
name="kcap-local-$version-$rid"
for f in "$name.tar.gz" "$name.json" "$name.json.sig"; do
  [ -f "$dir/$f" ] || { echo "$dir/$f is missing" >&2; exit 1; }
done

tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
set +e; bash "$here/r2-fetch.sh" "$name.json" "$tmp/published.json"; rc=$?; set -e
case "$rc" in
  0)  if cmp -s "$tmp/published.json" "$dir/$name.json"; then
        echo "$name is already published with these bytes"; exit 0
      fi
      echo "$name.json is already published from another build. A published version is never replaced: cut a new version." >&2
      exit 1 ;;
  44) ;;
  *)  exit 1 ;;
esac

immutable="public, max-age=31536000, immutable"
# shellcheck disable=SC2086 # $R2 is a flag list
aws s3 cp $R2 "$dir/$name.tar.gz"   "$DEST/$name.tar.gz"   --content-type application/gzip --cache-control "$immutable"
# shellcheck disable=SC2086
aws s3 cp $R2 "$dir/$name.json.sig" "$DEST/$name.json.sig" --content-type text/plain --cache-control "$immutable"
# shellcheck disable=SC2086
aws s3 cp $R2 "$dir/$name.json"     "$DEST/$name.json"     --content-type application/json --cache-control "$immutable"
echo "published $name to $DEST"
