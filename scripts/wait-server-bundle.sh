#!/usr/bin/env bash
# Downloads one asset of a kcap-server release into <out-dir>, waiting for it first: the server's
# tag is pushed after the CLI's, and its bundle is built on the tag, so the release and the asset
# both appear well after this workflow starts. Checks the bytes against GitHub's digest when the
# API reports one.
# Usage: wait-server-bundle.sh <tag> <asset> <out-dir>
# GH_TOKEN must read kurrent-io/kcap-server. SERVER_WAIT_TIMEOUT (seconds, default 3300, inside the hour a GitHub App token lasts) and
# SERVER_WAIT_INTERVAL (seconds, default 60) tune the poll.
set -euo pipefail
usage="usage: wait-server-bundle.sh <tag> <asset> <out-dir>"
tag="${1:?$usage}"; asset="${2:?$usage}"; out="${3:?$usage}"
repo="${SERVER_REPO:-kurrent-io/kcap-server}"
timeout="${SERVER_WAIT_TIMEOUT:-3300}"
interval="${SERVER_WAIT_INTERVAL:-60}"
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/hash.sh
source "$here/lib/hash.sh"
deadline=$(( SECONDS + timeout ))
err="$(mktemp)"; trap 'rm -f "$err"' EXIT

while :; do
  # A missing release and a transient API failure look alike here; both are worth another poll.
  found="$(gh api "repos/$repo/releases/tags/$tag" \
    --jq ".assets[] | select(.name == \"$asset\" and .state == \"uploaded\") | \"\(.id) \(.digest // \"\")\"" 2>"$err")" || found=""
  [ -n "$found" ] && break
  if [ "$SECONDS" -ge "$deadline" ]; then
    reason="$(sed -n '/./{p;q;}' "$err")"
    echo "$repo release $tag has no uploaded $asset after ${timeout}s${reason:+: $reason}" >&2
    exit 1
  fi
  sleep "$interval"
done

read -r id digest <<<"$found"
mkdir -p "$out"
gh api -H 'Accept: application/octet-stream' "repos/$repo/releases/assets/$id" > "$out/$asset"
if [ -n "${digest:-}" ]; then
  actual="sha256:$(sha256_of "$out/$asset")"
  [ "$actual" = "$digest" ] || { echo "$asset downloaded as $actual, but the release reports $digest" >&2; exit 1; }
fi
echo "$out/$asset"
