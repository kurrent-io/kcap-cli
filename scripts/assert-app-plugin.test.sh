#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
source "$here/lib/stub-kcap.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
bundle="$tmp/Capacitor.app"
mkdir -p "$bundle/Contents/Resources" "$bundle/Contents/MacOS"
cp -R "$here/../kcap" "$bundle/Contents/Resources/"
write_stub_kcap "$bundle/Contents/MacOS/kcap"
plugin="$bundle/Contents/Resources/kcap"
version="$(sed -nE 's/.*"version": *"([^"]+)".*/\1/p' "$plugin/.claude-plugin/plugin.json")"
bash "$here/assert-app-plugin.sh" "$bundle" "$version" >/dev/null
(cd "$tmp" && bash "$here/assert-app-plugin.sh" Capacitor.app "$version" >/dev/null)
if bash "$here/assert-app-plugin.sh" "$bundle" 0.0.0 >/dev/null 2>&1; then
  echo "FAIL: accepted a stale plugin version"; exit 1
fi
mv "$plugin/skills/guided-tour/SKILL.md" "$tmp/guided-tour"
if bash "$here/assert-app-plugin.sh" "$bundle" "$version" >/dev/null 2>&1; then
  echo "FAIL: accepted a missing skill"; exit 1
fi
mv "$tmp/guided-tour" "$plugin/skills/guided-tour/SKILL.md"
mv "$bundle/Contents/MacOS/kcap" "$tmp/kcap"
printf '#!/usr/bin/env bash\nexit 1\n' > "$bundle/Contents/MacOS/kcap"
chmod +x "$bundle/Contents/MacOS/kcap"
if bash "$here/assert-app-plugin.sh" "$bundle" "$version" >/dev/null 2>&1; then
  echo "FAIL: accepted a CLI that cannot install its plugin"; exit 1
fi
printf '#!/usr/bin/env bash\ncase "$*" in *--codex*) exit 1 ;; esac\nmkdir -p .claude && : > .claude/settings.local.json\n' \
  > "$bundle/Contents/MacOS/kcap"
if bash "$here/assert-app-plugin.sh" "$bundle" "$version" >/dev/null 2>&1; then
  echo "FAIL: accepted a CLI that cannot install the Codex plugin"; exit 1
fi
mv "$tmp/kcap" "$bundle/Contents/MacOS/kcap"
printf 'broken hook' > "$plugin/hooks/hooks.json"
if bash "$here/assert-app-plugin.sh" "$bundle" "$version" >/dev/null 2>&1; then
  echo "FAIL: accepted a modified hook"; exit 1
fi
linux="$tmp/linux"
mkdir -p "$linux/bin"
cp -R "$here/../kcap" "$linux/"
write_stub_kcap "$linux/bin/kcap"
bash "$here/assert-app-plugin.sh" "$linux" "$version" >/dev/null
echo "ok"
