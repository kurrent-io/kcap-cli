#!/usr/bin/env bash
# Prints the commit npm has on record for <package>@<version>: kcapReleaseCommit, or gitHead for a
# version published before that stamp existed. Prints nothing when the version is not published.
# Exits 1 when the registry cannot say either way after retries, which a caller must never read
# as "not published".
set -uo pipefail
usage="usage: npm-release-commit.sh <package> <version>"
pkg="${1:?$usage}"; version="${2:?$usage}"

# Prints "FOUND:<value>" (the value may be empty) or "NOTFOUND"; returns 1 when every attempt failed.
view() {
  local field="$1" attempts=3 i out rc errfile err=""
  for ((i = 1; i <= attempts; i++)); do
    errfile="$(mktemp)"
    out="$(npm view "$pkg@$version" "$field" --registry https://registry.npmjs.org 2>"$errfile")"
    rc=$?
    err="$(cat "$errfile")"; rm -f "$errfile"
    if [ "$rc" -eq 0 ]; then echo "FOUND:$out"; return 0; fi
    # npm versions disagree on the exit code for a missing version, but all of them say E404.
    if grep -qiE '(^|[^0-9])E404([^0-9]|$)|404 Not Found' <<<"$err"; then echo "NOTFOUND"; return 0; fi
    echo "npm view $pkg@$version $field: attempt $i/$attempts failed (exit $rc): ${err:-<no output>}" >&2
    [ "$i" -lt "$attempts" ] && sleep 3
  done
  return 1
}

result="$(view kcapReleaseCommit)" || exit 1
[ "$result" = NOTFOUND ] && exit 0
commit="${result#FOUND:}"
if [ -z "$commit" ]; then
  result="$(view gitHead)" || exit 1
  [ "$result" = NOTFOUND ] && exit 0
  commit="${result#FOUND:}"
fi
echo "$commit"
