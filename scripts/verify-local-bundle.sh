#!/usr/bin/env bash
# Checks every Mach-O in an extracted local-edition bundle: a strict signature with the hardened
# runtime, all from one team, and with --notarized a notarization Apple's service vouches for.
# spctl cannot stand in for the last check: it rejects any executable outside an app bundle.
# Usage: verify-local-bundle.sh <bundle-dir> [--notarized]
set -euo pipefail
usage="usage: verify-local-bundle.sh <bundle-dir> [--notarized]"
dir="${1:?$usage}"; notarized="${2:-}"
dir="${dir%/}"
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/macho.sh
source "$here/lib/macho.sh"

contained_symlinks "$dir"
count=0; teams=""
while IFS= read -r f; do
  codesign --verify --strict "$f"
  info="$(codesign -dv "$f" 2>&1)"
  grep -qE '^CodeDirectory .*flags=0x[0-9a-f]*\(.*runtime' <<<"$info" || { echo "$f is not signed with the hardened runtime" >&2; exit 1; }
  team="$(sed -n 's/^TeamIdentifier=//p' <<<"$info")"
  case "$team" in ""|"not set") echo "$f carries no team identifier" >&2; exit 1 ;; esac
  teams="$(printf '%s\n%s' "$teams" "$team" | sed '/^$/d' | sort -u)"
  if [ "$notarized" = "--notarized" ]; then
    codesign --verify --check-notarization -R=notarized "$f" || { echo "$f is not notarized" >&2; exit 1; }
  fi
  count=$(( count + 1 ))
done < <(macho_files "$dir")

[ "$count" -gt 0 ] || { echo "no Mach-O files under $dir" >&2; exit 1; }
[ "$(wc -l <<<"$teams" | tr -d ' ')" = 1 ] || { echo "Mach-Os are signed by more than one team: $teams" >&2; exit 1; }
echo "$count Mach-O files verified (team $teams${notarized:+, notarized})"
