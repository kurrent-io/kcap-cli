#!/usr/bin/env bash
# Usage: assert-app-plugin.sh <bundle.app> <version>
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
bundle="${1:?bundle required}"
version="${2:?version required}"
source_dir="$here/../kcap"
plugin="$bundle/Contents/Resources/kcap"
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
