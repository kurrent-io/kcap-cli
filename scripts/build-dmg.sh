#!/usr/bin/env bash
# A drag-to-Applications DMG: the stapled bundle beside an Applications symlink, over the
# background in scripts/dmg. dmgbuild writes the Finder layout itself, so no Finder session is needed.
# Usage: build-dmg.sh <bundle.app> <out.dmg>   (dmgbuild on PATH: pip install -r scripts/dmg/requirements.txt)
set -euo pipefail
bundle="${1:?usage: build-dmg.sh <bundle.app> <out.dmg>}"
out="${2:?usage: build-dmg.sh <bundle.app> <out.dmg>}"
here="$(cd "$(dirname "$0")" && pwd)/dmg"
command -v dmgbuild >/dev/null || { echo "dmgbuild not found (pip install --require-hashes -r scripts/dmg/requirements.txt)" >&2; exit 1; }
rm -f "$out"
dmgbuild -s "$here/settings.py" -D app="$bundle" -D here="$here" "Kurrent Capacitor" "$out"
echo "built $out"
