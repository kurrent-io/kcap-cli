#!/usr/bin/env bash
# Installs a packed Windows Setup.exe silently for the current user and asserts the layout the app's
# gates and its update path rely on: Velopack's Update.exe at the root, the app, CLI and daemon in
# current/, and the Start Menu shortcut. A silent install never launches the app.
# Usage: install-windows-setup.sh <Setup.exe>
set -euo pipefail

setup="${1:?usage: install-windows-setup.sh <Setup.exe>}"
unix() { if command -v cygpath >/dev/null 2>&1; then cygpath -u "$1"; else printf '%s' "$1"; fi; }
root="$(unix "${LOCALAPPDATA:?LOCALAPPDATA is not set}")/KurrentCapacitor"
shortcut="$(unix "${APPDATA:?APPDATA is not set}")/Microsoft/Windows/Start Menu/Programs/Kurrent Capacitor.lnk"

"$setup" --silent || { echo "Setup.exe failed (exit $?)" >&2; exit 1; }

missing=0
for f in "$root/Update.exe" "$root/current/Kurrent Capacitor.exe" "$root/current/kcap.exe" "$root/current/kcap-daemon.exe" "$shortcut"; do
  [ -f "$f" ] || { echo "Setup.exe did not install $f" >&2; missing=1; }
done
[ "$missing" -eq 0 ] || exit 1
echo "installed to $root"
