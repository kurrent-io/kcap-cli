#!/usr/bin/env bash
set -euo pipefail
if [ "$(uname -s)" != Darwin ]; then echo "skipped (macOS bundle assembly)"; exit 0; fi
here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
mkdir "$tmp/publish"
for binary in "Kurrent Capacitor" kcap kcap-daemon libpty_shim.dylib; do
  printf '%s' "$binary" > "$tmp/publish/$binary"
done
bundle="$tmp/Kurrent Capacitor.app"
bash "$here/assemble-app-bundle.sh" "$tmp/publish" "$bundle" 1.3.0-beta.7 >/dev/null
cmp "$tmp/publish/kcap" "$bundle/Contents/MacOS/kcap"
test -f "$bundle/Contents/Resources/kcap-icon.icns"
bash "$here/assert-app-plugin.sh" "$bundle" 1.3.0-beta.7 >/dev/null
if bash "$here/assemble-app-bundle.sh" "$tmp/publish" "$bundle" 1.3.0-beta.8 >/dev/null 2>&1; then
  echo "FAIL: overwrote an existing bundle"; exit 1
fi
bash "$here/assert-app-plugin.sh" "$bundle" 1.3.0-beta.7 >/dev/null
echo "ok"
