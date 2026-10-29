#!/usr/bin/env bash
set -euo pipefail
if [ "$(uname -s)" != Darwin ]; then echo "skipped (macOS bundle assembly)"; exit 0; fi
here="$(cd "$(dirname "$0")" && pwd)"
source "$here/lib/stub-kcap.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
mkdir "$tmp/publish"
for binary in "kcap-desktop" kcap-daemon libpty_shim.dylib; do
  printf '%s' "$binary" > "$tmp/publish/$binary"
done
write_stub_kcap "$tmp/publish/kcap"
bundle="$tmp/kcap.app"
bash "$here/assemble-app-bundle.sh" "$tmp/publish" "$bundle" 1.3.0-beta.7 >/dev/null
cmp "$tmp/publish/kcap" "$bundle/Contents/MacOS/kcap"
cmp "$tmp/publish/kcap-desktop" "$bundle/Contents/MacOS/kcap-desktop"
[[ "$(/usr/libexec/PlistBuddy -c 'Print CFBundleExecutable' "$bundle/Contents/Info.plist")" == kcap-desktop ]]
[[ "$(/usr/libexec/PlistBuddy -c 'Print CFBundleDisplayName' "$bundle/Contents/Info.plist")" == kcap ]]
[[ "$(/usr/libexec/PlistBuddy -c 'Print CFBundleIdentifier' "$bundle/Contents/Info.plist")" == io.kurrent.kcap ]]
test -f "$bundle/Contents/Resources/kcap-icon.icns"
bash "$here/assert-app-plugin.sh" "$bundle" 1.3.0-beta.7 >/dev/null
if bash "$here/assemble-app-bundle.sh" "$tmp/publish" "$bundle" 1.3.0-beta.8 >/dev/null 2>&1; then
  echo "FAIL: overwrote an existing bundle"; exit 1
fi
bash "$here/assert-app-plugin.sh" "$bundle" 1.3.0-beta.7 >/dev/null
echo "ok"
