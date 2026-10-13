#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/verify-desktop-immutables.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
v="0.12.0-beta.2"
local_dir="$tmp/local"; remote="$tmp/remote"; mkdir -p "$local_dir" "$remote"
printf 'full' > "$local_dir/KurrentCapacitor-$v-osx-arm64-full.nupkg"
printf 'dmg'  > "$local_dir/Kurrent-Capacitor-$v-osx-arm64.dmg"
fetch="$tmp/fetch.sh"
printf '#!/usr/bin/env bash\n[ -f "%s/$1" ] || exit 44\ncp "%s/$1" "$2"\n' "$remote" "$remote" > "$fetch"; chmod +x "$fetch"

fail=0
assert() { local rc; set +e; bash "$sh" "$local_dir" "$v" "$fetch" >/dev/null 2>&1; rc=$?; set -e
  if [ "$rc" != "$2" ]; then echo "FAIL: $1 -> rc=$rc (want $2)"; fail=1; fi; }

assert "nothing published yet passes" 0
cp "$local_dir/KurrentCapacitor-$v-osx-arm64-full.nupkg" "$remote/"
assert "identical bytes already published passes (retry)" 0
printf 'other-bytes' > "$remote/Kurrent-Capacitor-$v-osx-arm64.dmg"
assert "different bytes already published fails" 1

error_fetch="$tmp/error-fetch.sh"
printf '#!/usr/bin/env bash\necho "transient failure" >&2\nexit 7\n' > "$error_fetch"; chmod +x "$error_fetch"
rc=1; set +e; bash "$sh" "$local_dir" "$v" "$error_fetch" >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -eq 1 ] || { echo "FAIL: a fetch error (exit 7) must refuse to publish -> rc=$rc (want 1)"; fail=1; }

# The channel argument names the Windows objects, Setup.exe among them.
win_local="$tmp/win-local"; win_remote="$tmp/win-remote"; mkdir -p "$win_local" "$win_remote"
printf 'setup' > "$win_local/Kurrent-Capacitor-$v-win-x64-Setup.exe"
printf 'other' > "$win_remote/Kurrent-Capacitor-$v-win-x64-Setup.exe"
win_fetch="$tmp/win-fetch.sh"
printf '#!/usr/bin/env bash\n[ -f "%s/$1" ] || exit 44\ncp "%s/$1" "$2"\n' "$win_remote" "$win_remote" > "$win_fetch"; chmod +x "$win_fetch"
rc=0; set +e; bash "$sh" "$win_local" "$v" "$win_fetch" win-x64 >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -eq 1 ] || { echo "FAIL: a republished win-x64 Setup.exe with different bytes must refuse -> rc=$rc (want 1)"; fail=1; }
rc=0; set +e; bash "$sh" "$win_local" "$v" "$win_fetch" >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -eq 0 ] || { echo "FAIL: without the channel only osx-arm64 names are checked -> rc=$rc (want 0)"; fail=1; }

[ "$fail" -eq 0 ] && echo "ok" || exit 1
