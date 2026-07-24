#!/usr/bin/env bash
# Decides whether a release build should proceed, given the commit this
# build is for and the commit npm already has on record for the exact
# version being released (empty string if the version has never been
# published). Pure decision logic only — network/registry lookups and any
# CI-specific error formatting live in the caller (release.yml).
#
# Exit 0 + "ok"        -> version never published, proceed.
# Exit 1 + "republish" -> already published from the SAME commit: npm
#                         forbids republishing a version even unchanged.
# Exit 1 + "moved"     -> already published from a DIFFERENT commit: the
#                         release tag was moved/re-cut after that version's
#                         npm artifact was published (the AI-1514 failure
#                         mode) — refuse.
set -euo pipefail
this_sha="${1:?usage: verify-release-not-recut.sh <this-sha> <published-sha-or-empty>}"
published_sha="${2:-}"

if [ -z "$published_sha" ]; then
  echo "ok"
  exit 0
fi

if [ "$published_sha" = "$this_sha" ]; then
  echo "republish"
  exit 1
fi

echo "moved"
exit 1
