#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/npm-dist-tag.sh"

fail=0
assert() {
  local got; got="$(bash "$sh" "$1")"
  if [ "$got" != "$2" ]; then echo "FAIL: '$1' -> '$got' (want '$2')"; fail=1; fi
}
assert "0.7.0"            latest
assert "0.7.0-beta.1"     beta
assert "0.7.0-beta.10"    beta
assert "1.2.3+build.5"    latest
assert "1.2.3-rc.1+build" beta
[ "$fail" -eq 0 ] && echo "ok" || exit 1
