#!/usr/bin/env bash
# Usage: build-snap.sh [version]
# Stages the desktop app with kcap, kcap-daemon and the plugin in .publish/snap — the layout of the
# release's Linux app directory — and packs the snap. Packing runs in LXD unless snapcraft is told otherwise.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
version="${1:-0.0.0-local}"
[[ "$version" =~ ^[0-9A-Za-z.+-]{1,32}$ ]] || { echo "invalid snap version: $version" >&2; exit 1; }

# x64 only, like the Linux desktop release: snapcraft.yaml packs amd64 alone.
case "$(uname -m)" in
  x86_64) rid=linux-x64 ;;
  *) echo "unsupported architecture: $(uname -m); the snap is built for x86_64 only" >&2; exit 1 ;;
esac

stage="$root/.publish/snap"
rm -rf "$stage"
mkdir -p "$stage/bin"

publish() { dotnet publish "$root/$1" -c Release -r "$rid" -p:MinVerVersionOverride="$version" -o "$2" "${@:3}"; }
publish src/Capacitor.App/Capacitor.App.csproj "$stage" --self-contained
# The daemon first: the CLI embeds its SHA-256 and refuses a daemon that does not match, as release builds do.
publish src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj "$stage/bin"
daemon_digest="$(sha256sum "$stage/bin/kcap-daemon" | cut -d' ' -f1)"
publish src/Capacitor.Cli/Capacitor.Cli.csproj "$stage/bin" -p:KcapDaemonDigest="$daemon_digest"
find "$stage" -type f \( -name '*.dbg' -o -name '*.xml' -o -name '*.pdb' \) -delete

bash "$root/scripts/bundle-app-plugin.sh" "$stage" "$version"
echo "$version" > "$stage/VERSION"

cd "$root"
snapcraft pack
