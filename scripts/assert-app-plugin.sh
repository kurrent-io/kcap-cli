#!/usr/bin/env bash
# Usage: assert-app-plugin.sh <bundle.app-or-app-directory> <version>
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# Absolute, because the smoke step runs the CLI from a scratch directory.
bundle="$(cd "${1:?bundle required}" && pwd)"
version="${2:?version required}"
source_dir="$here/../kcap"
if [ -d "$bundle/Contents/Resources" ]; then
  plugin="$bundle/Contents/Resources/kcap"
  cli="$bundle/Contents/MacOS/kcap"
elif [ -d "$bundle/bin" ]; then
  plugin="$bundle/kcap"
  cli="$bundle/bin/kcap"
else
  plugin="$bundle/kcap"
  cli="$bundle/kcap.exe"
fi
test -f "$plugin/.claude-plugin/plugin.json"
grep -qF '"version": "'"$version"'"' "$plugin/.claude-plugin/plugin.json"
while IFS= read -r -d '' file; do
  relative="${file#"$source_dir/"}"
  test -f "$plugin/$relative" || { echo "missing bundled plugin file: $relative" >&2; exit 1; }
  if [ "$relative" != .claude-plugin/plugin.json ]; then
    cmp "$file" "$plugin/$relative"
  fi
done < <(find "$source_dir" -type f -print0)
echo "bundled plugin payload is complete and stamped $version"
# Carrying the payload is not enough: the bundled CLI must also resolve it, for both installs the
# onboarding wizard runs. A successful install also writes the git hook into the home's git config,
# so the home is a scratch one too, and the vendor overrides are cleared so they resolve under it.
scratch="$(mktemp -d)"; trap 'rm -rf "$scratch"' EXIT
mkdir "$scratch/home"
native="$scratch"
if command -v cygpath >/dev/null 2>&1; then native="$(cygpath -m "$scratch")"; fi
install() {
  (cd "$scratch" && env -u KCAP_PLUGIN_DIR -u CODEX_HOME -u CLAUDE_CONFIG_DIR HOME="$native/home" KCAP_CONFIG_DIR="$native/config" DO_NOT_TRACK=1 \
    "$cli" plugin install "$@" --project --no-update-check)
}
install
test -f "$scratch/.claude/settings.local.json"
install --codex --skip-codex-network-access
grep -qF 'kcap hook --codex' "$scratch/.codex/hooks.json"
echo "bundled CLI resolves the bundled plugin for Claude and Codex"
