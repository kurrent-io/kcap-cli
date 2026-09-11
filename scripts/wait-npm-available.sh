#!/usr/bin/env bash
# Blocks until every <package>@<version> resolves from the npm registry, or fails at the deadline.
# npm publish returns while the registry is still processing the tarball, for minutes on a large one.
# Usage: wait-npm-available.sh <version> <package>...
# NPM_WAIT_TIMEOUT (seconds, default 1800) and NPM_WAIT_INTERVAL (seconds, default 15) tune the poll.
set -euo pipefail
usage="usage: wait-npm-available.sh <version> <package>..."
version="${1:?$usage}"; shift
[ "$#" -gt 0 ] || { echo "$usage" >&2; exit 2; }
timeout="${NPM_WAIT_TIMEOUT:-1800}"
interval="${NPM_WAIT_INTERVAL:-15}"
deadline=$(( SECONDS + timeout ))
err="$(mktemp)"; trap 'rm -f "$err"' EXIT

for pkg in "$@"; do
  while :; do
    # --prefer-online: npm caches a packument for five minutes, so a plain re-poll re-reads the miss.
    if [ "$(npm view "$pkg@$version" version --prefer-online --registry https://registry.npmjs.org 2>"$err")" = "$version" ]; then
      echo "$pkg@$version is available"
      break
    fi
    if [ "$SECONDS" -ge "$deadline" ]; then
      reason="$(sed -n '/complete log/d; /npm error code /d; /./{p;q;}' "$err")"
      echo "$pkg@$version is still unavailable after ${timeout}s: ${reason:-<no npm output>}" >&2
      exit 1
    fi
    sleep "$interval"
  done
done
