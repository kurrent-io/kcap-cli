#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
mkdir "$tmp/app"
printf 'exe' > "$tmp/app/kcap.exe"
bash "$here/bundle-app-plugin.sh" "$tmp/app" 1.3.0-beta.7+abc
bash "$here/assert-app-plugin.sh" "$tmp/app" 1.3.0-beta.7 >/dev/null
test "$(cat "$tmp/app/kcap.exe")" = exe
if bash "$here/bundle-app-plugin.sh" "$tmp/app" 1.3.0-beta.8 >/dev/null 2>&1; then
  echo "FAIL: overwrote an existing plugin"; exit 1
fi
bash "$here/assert-app-plugin.sh" "$tmp/app" 1.3.0-beta.7 >/dev/null
mv "$tmp/app/kcap/hooks/hooks.json" "$tmp/hooks.json"
if bash "$here/assert-app-plugin.sh" "$tmp/app" 1.3.0-beta.7 >/dev/null 2>&1; then
  echo "FAIL: accepted a missing Windows hook"; exit 1
fi
echo "ok"
