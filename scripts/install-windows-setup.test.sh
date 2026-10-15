#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/install-windows-setup.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

layout=(
  "Local/KurrentCapacitor/Update.exe"
  "Local/KurrentCapacitor/current/Kurrent Capacitor.exe"
  "Local/KurrentCapacitor/current/kcap.exe"
  "Local/KurrentCapacitor/current/kcap-daemon.exe"
  "Roaming/Microsoft/Windows/Start Menu/Programs/Kurrent Capacitor.lnk"
)

fail=0
# Lays out every file except <skip> as if Setup.exe had installed it, then runs the script against a
# fake Setup.exe that exits <setup_rc>.
assert() {
  local name="$1" skip="$2" setup_rc="$3" want_rc="$4" rc=0 f
  local profile="$tmp/$name"
  for f in "${layout[@]}"; do
    [ "$f" = "$skip" ] && continue
    mkdir -p "$(dirname "$profile/$f")"; : > "$profile/$f"
  done
  printf '#!/usr/bin/env bash\nexit %s\n' "$setup_rc" > "$tmp/$name-setup"; chmod +x "$tmp/$name-setup"
  LOCALAPPDATA="$profile/Local" APPDATA="$profile/Roaming" bash "$sh" "$tmp/$name-setup" >/dev/null 2>&1 || rc=$?
  if [ "$rc" != "$want_rc" ]; then echo "FAIL: $name -> rc=$rc (want $want_rc)"; fail=1; fi
}

assert complete      ""              0 0
assert setup-failed  ""              1 1
assert no-update-exe "${layout[0]}"  0 1
assert no-daemon     "${layout[3]}"  0 1
assert no-shortcut   "${layout[4]}"  0 1

[ "$fail" -eq 0 ] && echo "ok" || exit 1
