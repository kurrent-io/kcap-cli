#!/usr/bin/env bash
# Usage: assemble-app-bundle.sh <publish-dir> <bundle.app> <version>
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
published="${1:?publish directory required}"
bundle="${2:?bundle destination required}"
version="${3:?version required}"
[[ "$bundle" == *.app ]] || { echo "destination must end in .app" >&2; exit 1; }
[[ "$version" =~ ^[0-9A-Za-z.+-]+$ ]] || { echo "invalid version" >&2; exit 1; }
for binary in "Kurrent Capacitor" kcap kcap-daemon libpty_shim.dylib; do
  test -f "$published/$binary"
done
mkdir -p "$(dirname "$bundle")"
mkdir "$bundle"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
cp -R "$published/." "$bundle/Contents/MacOS/"
bash "$here/bundle-app-plugin.sh" "$bundle/Contents/Resources" "$version"
cp "$here/../src/Capacitor.App/Assets/kcap-icon.icns" "$bundle/Contents/Resources/"
chmod +x "$bundle/Contents/MacOS/kcap" "$bundle/Contents/MacOS/kcap-daemon" "$bundle/Contents/MacOS/Kurrent Capacitor"
bash "$here/render-info-plist.sh" "$version" "$bundle/Contents/Info.plist"
bash "$here/assert-app-plugin.sh" "$bundle" "${version%%+*}"
