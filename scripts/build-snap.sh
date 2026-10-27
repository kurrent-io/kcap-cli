#!/usr/bin/env bash
# Usage: build-snap.sh [version]
# Publishes the desktop app, kcap and kcap-daemon for this machine's architecture into .publish/snap,
# adds the Claude Code plugin, and packs the snap. Packing runs in LXD unless snapcraft is told otherwise.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
version="${1:-0.0.0-local}"
[[ "$version" =~ ^[0-9A-Za-z.+~-]{1,32}$ ]] || { echo "invalid snap version: $version" >&2; exit 1; }

case "$(uname -m)" in
  x86_64)  rid=linux-x64 ;;
  aarch64) rid=linux-arm64 ;;
  *) echo "unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

stage="$root/.publish/snap"
app="$root/.publish/snap-app"
rm -rf "$stage" "$app"
mkdir -p "$stage/bin"

publish() { dotnet publish "$root/$1" -c Release -r "$rid" -p:MinVerVersionOverride="$version" -o "$2" "${@:3}"; }
publish src/Capacitor.Cli/Capacitor.Cli.csproj "$stage/bin"
publish src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj "$stage/bin"
publish src/Capacitor.App/Capacitor.App.csproj "$app" --self-contained

# A snap command cannot contain a space, so the app's executable is renamed; its native libraries
# sit beside it, as they do in the publish output.
mv "$app/Kurrent Capacitor" "$stage/bin/kurrent-capacitor"
cp "$app"/*.so "$stage/bin/"
rm -rf "$app"
find "$stage/bin" -type f \( -name '*.dbg' -o -name '*.xml' -o -name '*.pdb' \) -delete

# The plugin goes beside bin/, not into it: on Linux bin/kcap is the CLI itself.
bash "$root/scripts/bundle-app-plugin.sh" "$stage" "$version"
echo "$version" > "$stage/VERSION"

cd "$root"
snapcraft pack
