#!/usr/bin/env bash
# Renders scripts/dmg/background.svg into the committed scripts/dmg/background.tiff, a 1x + 2x
# pair Finder picks from by display density. Needs rsvg-convert (brew install librsvg) and macOS's
# tiffutil; CI never runs it.
set -euo pipefail
dir="$(cd "$(dirname "$0")" && pwd)/dmg"

command -v rsvg-convert >/dev/null || { echo "rsvg-convert not found (brew install librsvg)" >&2; exit 1; }
command -v tiffutil >/dev/null || { echo "tiffutil not found (macOS only)" >&2; exit 1; }

work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
rsvg-convert -w 640 -h 480 "$dir/background.svg" -o "$work/background.png"
rsvg-convert -w 1280 -h 960 "$dir/background.svg" -o "$work/background@2x.png"
tiffutil -cathidpicheck "$work/background.png" "$work/background@2x.png" -out "$dir/background.tiff" 2>/dev/null
echo "rendered $dir/background.tiff"
