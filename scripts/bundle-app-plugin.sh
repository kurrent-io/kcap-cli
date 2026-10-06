#!/usr/bin/env bash
# Usage: bundle-app-plugin.sh <resources-or-app-directory> <version>
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
destination="${1:?destination required}"
version="${2:?version required}"
[[ "$version" =~ ^[0-9A-Za-z.+-]+$ ]] || { echo "invalid version" >&2; exit 1; }
test ! -e "$destination/kcap"
mkdir -p "$destination"
cp -R "$here/../kcap" "$destination/kcap"
manifest="$destination/kcap/.claude-plugin/plugin.json"
sed -i.bak -E 's/"version": *"[^"]*"/"version": "'"${version%%+*}"'"/' "$manifest"
rm "$manifest.bak"
