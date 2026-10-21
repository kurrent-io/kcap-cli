#!/usr/bin/env bash
# A prerelease must sort below its release, or apt never upgrades a beta to the final: dpkg reads a
# hyphen as the start of a Debian revision and a tilde as "before".
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/app"; printf '#!/bin/sh\n' > "$tmp/app/kcap-desktop"; chmod +x "$tmp/app/kcap-desktop"
bash "$here/build-deb.sh" "$tmp/app" "1.4.0-beta.2+abc" "$tmp/out.deb" >/dev/null
version="$(dpkg-deb -f "$tmp/out.deb" Version)"
[ "$version" = "1.4.0~beta.2" ] && dpkg --compare-versions "$version" lt 1.4.0 \
  && echo ok || { echo "FAIL: prerelease version is $version (want 1.4.0~beta.2, below 1.4.0)"; exit 1; }
