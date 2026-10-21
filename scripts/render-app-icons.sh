#!/usr/bin/env bash
# Renders the committed app icons from src/Capacitor.App/Assets: kcap-icon.svg into kcap-icon.png
# (512 px), kcap-icon.icns and kcap-icon.ico, and kcap-tray-icon.svg into kcap-tray-icon.png.
# Sizes of 32 px and below come from the tray SVG, whose heavier strokes stay legible there.
# Needs rsvg-convert (brew install librsvg), python3 and macOS's iconutil; CI never runs it.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
assets="$here/../src/Capacitor.App/Assets"
svg="$assets/kcap-icon.svg"
small_svg="$assets/kcap-tray-icon.svg"

command -v rsvg-convert >/dev/null || { echo "rsvg-convert not found (brew install librsvg)" >&2; exit 1; }
command -v iconutil >/dev/null || { echo "iconutil not found (macOS only)" >&2; exit 1; }
command -v python3 >/dev/null || { echo "python3 not found" >&2; exit 1; }

render() {
  local size=$1 out=$2 src="$svg"
  [ "$size" -le 32 ] && src="$small_svg"
  rsvg-convert -w "$size" -h "$size" "$src" -o "$out"
}

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

render 512 "$assets/kcap-icon.png"
rsvg-convert -w 64 -h 64 "$small_svg" -o "$assets/kcap-tray-icon.png"

iconset="$work/kcap-icon.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  render "$size" "$iconset/icon_${size}x${size}.png"
  render $((size * 2)) "$iconset/icon_${size}x${size}@2x.png"
done
iconutil -c icns "$iconset" -o "$assets/kcap-icon.icns"

ico_pngs=()
for size in 16 20 24 32 40 48 64 128 256; do
  render "$size" "$work/ico-$size.png"
  ico_pngs+=("$work/ico-$size.png")
done
python3 -I - "$assets/kcap-icon.ico" "${ico_pngs[@]}" <<'PY'
import struct, sys
out, paths = sys.argv[1], sys.argv[2:]
images = [open(p, "rb").read() for p in paths]
header = struct.pack("<HHH", 0, 1, len(images))
offset = len(header) + 16 * len(images)
entries = b""
for data in images:
    width, height = struct.unpack(">II", data[16:24])
    entries += struct.pack("<BBBBHHII", width % 256, height % 256, 0, 0, 1, 32, len(data), offset)
    offset += len(data)
with open(out, "wb") as f:
    f.write(header + entries + b"".join(images))
PY

echo "rendered kcap-icon.png, kcap-icon.icns, kcap-icon.ico and kcap-tray-icon.png in $assets"
