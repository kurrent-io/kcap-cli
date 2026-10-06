#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
bundle="$tmp/Capacitor.app"
mkdir -p "$bundle/Contents/Resources"
cp -R "$here/../kcap" "$bundle/Contents/Resources/"
plugin="$bundle/Contents/Resources/kcap"
version="$(sed -nE 's/.*"version": *"([^"]+)".*/\1/p' "$plugin/.claude-plugin/plugin.json")"
bash "$here/assert-app-plugin.sh" "$bundle" "$version" >/dev/null
if bash "$here/assert-app-plugin.sh" "$bundle" 0.0.0 >/dev/null 2>&1; then
  echo "FAIL: accepted a stale plugin version"; exit 1
fi
mv "$plugin/skills/guided-tour/SKILL.md" "$tmp/guided-tour"
if bash "$here/assert-app-plugin.sh" "$bundle" "$version" >/dev/null 2>&1; then
  echo "FAIL: accepted a missing skill"; exit 1
fi
mv "$tmp/guided-tour" "$plugin/skills/guided-tour/SKILL.md"
printf 'broken hook' > "$plugin/hooks/hooks.json"
if bash "$here/assert-app-plugin.sh" "$bundle" "$version" >/dev/null 2>&1; then
  echo "FAIL: accepted a modified hook"; exit 1
fi
echo "ok"
