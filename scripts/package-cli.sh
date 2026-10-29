#!/usr/bin/env bash
# Packs the published CLI and daemon for one RID twice: into npm/<npm-package>/bin for the platform
# package, and into kcap-<rid>.tar.gz (kcap-<rid>.zip on Windows) for the GitHub Release. Refuses a
# daemon whose bytes differ from the digest the CLI embeds, since the CLI would refuse to launch it.
# Run from the repository root, after publish/cli and publish/daemon exist.
# Usage: package-cli.sh <rid> <version> <npm-package> <daemon-digest>
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/hash.sh
source "$here/lib/hash.sh"
usage="usage: package-cli.sh <rid> <version> <npm-package> <daemon-digest>"
rid="${1:?$usage}"; version="${2:?$usage}"; npm_package="${3:?$usage}"; digest="${4:?$usage}"
exe=""; [[ "$rid" == win-* ]] && exe=".exe"
cli="publish/cli/kcap$exe"; daemon="publish/daemon/kcap-daemon$exe"

[ "$(sha256_of "$daemon")" = "$digest" ] || { echo "::error::$daemon does not match the digest the CLI embeds"; exit 1; }

copy_binaries() { # <dir>
  mkdir -p "$1"
  cp "$cli" "$daemon" "$1/"
  for ext in so dylib; do
    if [ -f "publish/daemon/libpty_shim.$ext" ]; then cp "publish/daemon/libpty_shim.$ext" "$1/"; fi
  done
}

copy_binaries "npm/$npm_package/bin"

# Binaries under bin/: the plugin directory is also named kcap and cannot sit beside the kcap
# binary. SetupCommand.ResolvePluginPath finds it at <bin>/../kcap.
copy_binaries archive/bin
# The Windows service ships with the archive the desktop bundle is built from, not with npm: an npm
# install has no elevated helper to register it and keeps the Scheduled Task.
if [[ "$rid" == win-* ]]; then
  [ -f publish/service/kcap-service.exe ] || { echo "::error::publish/service/kcap-service.exe is missing"; exit 1; }
  cp publish/service/kcap-service.exe archive/bin/
fi
if [[ "$rid" == osx-* ]]; then
  echo "$digest" > archive/bin/daemon.sha256
  sha256_of "$cli" > archive/bin/kcap.sha256
fi
# Claude Code reads a payload as new only when the plugin version moves.
bash "$here/bundle-app-plugin.sh" archive "$version"
plugin=archive/kcap/.claude-plugin/plugin.json
grep -qF "\"version\": \"${version%%+*}\"" "$plugin" || { echo "::error::could not stamp the plugin version into $plugin"; exit 1; }
cp LICENSE.md archive/

# Bare names: the archive's entries are bin/..., kcap/... and LICENSE.md, which installers expect.
cd archive
# shellcheck disable=SC2035
if [[ "$rid" == win-* ]]; then
  7z a "../kcap-$rid.zip" *
else
  tar czf "../kcap-$rid.tar.gz" *
fi
