#!/usr/bin/env bash
# Usage: build-deb.sh <app-directory> <version> <output.deb>
# Packages a published Linux app directory (the app, plus bin/ and the kcap/ plugin) as a .deb that
# installs under /usr/lib/kurrent-capacitor. Velopack does not manage this install: apt or a newer
# .deb updates it, and the app hides its own update item when Velopack is absent.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
app="${1:?app directory required}"
version="${2:?version required}"
out="${3:?output .deb required}"
[[ "$version" =~ ^[0-9A-Za-z.+-]+$ ]] || { echo "invalid version" >&2; exit 1; }
[ -x "$app/Kurrent Capacitor" ] || { echo "no app executable in $app" >&2; exit 1; }

# A hyphen would split off a Debian revision; a tilde keeps a prerelease below its release.
deb_version="${version%%+*}"
deb_version="${deb_version//-/\~}"

root="$(mktemp -d)"
trap 'rm -rf "$root"' EXIT
chmod 0755 "$root"
lib="$root/usr/lib/kurrent-capacitor"
mkdir -p "$lib" "$root/usr/bin" "$root/usr/share/applications" "$root/usr/share/icons/hicolor/512x512/apps" "$root/DEBIAN"
cp -a "$app/." "$lib/"
chmod -R go-w,a+rX "$lib"
ln -s "../lib/kurrent-capacitor/Kurrent Capacitor" "$root/usr/bin/kurrent-capacitor"
cp "$here/../src/Capacitor.App/Assets/kcap-icon.png" "$root/usr/share/icons/hicolor/512x512/apps/kurrent-capacitor.png"

cat > "$root/usr/share/applications/kurrent-capacitor.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Kurrent Capacitor
Comment=Run and watch coding agents
Exec=kurrent-capacitor
Icon=kurrent-capacitor
Terminal=false
Categories=Development;
EOF

cat > "$root/DEBIAN/control" <<EOF
Package: kurrent-capacitor
Version: $deb_version
Architecture: amd64
Maintainer: Kurrent <team@kcap.ai>
Installed-Size: $(du -sk "$root/usr" | cut -f1)
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libicu78 | libicu76 | libicu74 | libicu72 | libicu70 | libicu67, libssl3 | libssl1.1, libx11-6, libice6, libsm6, libfontconfig1
Section: devel
Priority: optional
Homepage: https://www.kurrent.io
Description: Kurrent Capacitor desktop app
 Runs hosted coding agents through the kcap daemon and shows their sessions.
EOF

dpkg-deb --root-owner-group --build "$root" "$out" >/dev/null
echo "built $out ($deb_version)"
