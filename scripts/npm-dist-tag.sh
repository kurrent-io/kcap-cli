#!/usr/bin/env bash
# Prints the npm dist-tag for a version string: `beta` for a SemVer prerelease
# (a hyphen in the core, ignoring +build metadata), else `latest`.
set -euo pipefail
version="${1:?usage: npm-dist-tag.sh <version>}"
core="${version%%+*}"                 # strip +build metadata
if [[ "$core" == *-* ]]; then echo beta; else echo latest; fi
