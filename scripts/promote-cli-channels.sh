#!/usr/bin/env bash
# Decides which install channels a just-published CLI version may take: "beta" when it is at
# least the beta channel's version (any kind), "latest" when it is stable and at least the latest
# channel's version. An empty current version means the channel does not exist yet. Equal
# promotes, so a re-run heals a channel an earlier attempt failed to write; lower never does, so
# an older tag published late cannot regress a channel.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/semver.sh
source "$here/lib/semver.sh"

usage="usage: promote-cli-channels.sh <candidate> <current-latest|\"\"> <current-beta|\"\">"
candidate="${1:?$usage}"; latest="${2-}"; beta="${3-}"

at_least() { # <candidate> <current>
  [ -z "$2" ] && return 0
  local c; c="$(semver_cmp "$1" "$2")" || exit 1
  [ "$c" -ge 0 ]
}

if at_least "$candidate" "$beta"; then echo beta; fi
if ! semver_is_prerelease "$candidate" && at_least "$candidate" "$latest"; then echo latest; fi
exit 0
