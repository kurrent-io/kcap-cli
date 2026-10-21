#!/usr/bin/env bash
# Uploads one local-edition bundle to $DEST (the bucket's local/<rid>/ prefix): the archive, then
# the signature, then the manifest. The manifest is the publication: written last and only once,
# so a reader that finds it finds everything it vouches for. Every object is served as immutable,
# so one already there is never replaced: an archive must match byte for byte, and a signature must
# verify over this manifest (ECDSA signs differently each time, so a re-run keeps the stored one).
# Usage: publish-local-bundle.sh <dir> <version> <rid> <public-key-pem>
#   $R2 and $DEST as for r2-fetch.sh.
set -euo pipefail
usage="usage: publish-local-bundle.sh <dir> <version> <rid> <public-key-pem>"
dir="${1:?$usage}"; version="${2:?$usage}"; rid="${3:?$usage}"; pub="${4:?$usage}"
here="$(cd "$(dirname "$0")" && pwd)"
name="kcap-local-$version-$rid"
for f in "$name.tar.gz" "$name.json" "$name.json.sig"; do
  [ -f "$dir/$f" ] || { echo "$dir/$f is missing" >&2; exit 1; }
done

tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
# Returns 0 when the object exists and 1 when it does not; exits on any other failure. Not for
# use inside $(...), where that exit would leave only the subshell.
exists() { # <key> <file>
  local rc; set +e; bash "$here/r2-fetch.sh" "$1" "$2"; rc=$?; set -e
  case "$rc" in 0) return 0 ;; 44) return 1 ;; *) exit 1 ;; esac
}
refuse() {
  echo "$1 Delete local/$rid/$name.* if no manifest refers to it, or cut a new version." >&2
  exit 1
}

# Each check answers "stored", "absent", or refuses a stored object that is not this build's.
archive_state() {
  exists "$name.tar.gz" "$tmp/archive" || { echo absent; return; }
  cmp -s "$tmp/archive" "$dir/$name.tar.gz" || refuse "$name.tar.gz is already uploaded with other bytes."
  echo stored
}
signature_state() {
  exists "$name.json.sig" "$tmp/sig" || { echo absent; return; }
  bash "$here/local-manifest-key.sh" verify "$pub" "$dir/$name.json" "$tmp/sig" >/dev/null 2>&1 \
    || refuse "$name.json.sig is already uploaded and does not sign this manifest."
  echo stored
}

if exists "$name.json" "$tmp/published.json"; then
  cmp -s "$tmp/published.json" "$dir/$name.json" || {
    echo "$name.json is already published from another build. A published version is never replaced: cut a new version." >&2
    exit 1; }
  # Called directly, not in $(...), so a refusal or an R2 failure ends the script.
  archive_state > "$tmp/a"; signature_state > "$tmp/s"
  [ "$(cat "$tmp/a") $(cat "$tmp/s")" = "stored stored" ] || {
    echo "$name.json is published but its archive or signature is missing from local/$rid/: restore them by hand." >&2
    exit 1; }
  echo "$name is already published with these bytes"
  exit 0
fi

# shellcheck disable=SC2086 # $R2 is a flag list
put() { aws s3 cp $R2 "$1" "$DEST/$(basename "$1")" --content-type "$2" --cache-control "public, max-age=31536000, immutable"; }

archive_state > "$tmp/a"
if [ "$(cat "$tmp/a")" = stored ]; then echo "$name.tar.gz is already uploaded with these bytes"
else put "$dir/$name.tar.gz" application/gzip; fi

signature_state > "$tmp/s"
if [ "$(cat "$tmp/s")" = stored ]; then echo "$name.json.sig is already uploaded and signs this manifest"
else put "$dir/$name.json.sig" text/plain; fi

put "$dir/$name.json" application/json
echo "published $name to $DEST"
