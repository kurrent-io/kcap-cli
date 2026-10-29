#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
source "$here/macho.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

fail=0
expect() { # <label> <want-rc> <dir>
  local rc; set +e; contained_symlinks "$3" 2>"$tmp/err"; rc=$?; set -e
  if [ "$rc" != "$2" ]; then echo "FAIL: $1 -> rc=$rc (want $2): $(cat "$tmp/err")"; fail=1; fi
}

mkdir -p "$tmp/b/lib" "$tmp/outside"
printf 'x' > "$tmp/b/lib/libpq.5.dylib"; printf 'x' > "$tmp/outside/evil"
ln -s libpq.5.dylib "$tmp/b/lib/libpq.dylib"
expect "a link inside the bundle" 0 "$tmp/b"

ln -s ../../outside/evil "$tmp/b/lib/escape"
expect "a relative link out of the bundle" 1 "$tmp/b"
grep -q "points outside the bundle" "$tmp/err" || { echo "FAIL: names the escape: $(cat "$tmp/err")"; fail=1; }
rm "$tmp/b/lib/escape"

ln -s "$tmp/outside/evil" "$tmp/b/abs"
expect "an absolute link out of the bundle" 1 "$tmp/b"
rm "$tmp/b/abs"

ln -s missing "$tmp/b/dangling"
expect "a dangling link" 1 "$tmp/b"

[ "$fail" -eq 0 ] && echo "ok" || exit 1
