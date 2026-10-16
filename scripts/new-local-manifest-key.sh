#!/usr/bin/env bash
# Creates the local-edition manifest signing key: stores the private half as the repository secret
# LOCAL_BUNDLE_MANIFEST_KEY and writes the public half to the resource the CLI embeds. The private
# key never touches the working tree and is deleted once the secret is stored.
# Rotating is running this again and committing the new public key: a CLI only ever installs the
# bundle of its own version, which the release that built it signed, so no key needs to overlap.
# Usage: new-local-manifest-key.sh [<repo>]   (default kurrent-io/kcap-cli; needs gh with admin)
set -euo pipefail
repo="${1:-kurrent-io/kcap-cli}"
here="$(cd "$(dirname "$0")" && pwd)"
pub="$here/../src/Capacitor.Cli.Core/Resources/local-bundle-manifest-key.pem"

tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
(umask 077; openssl ecparam -name prime256v1 -genkey -noout 2>/dev/null | openssl pkey -out "$tmp/key.pem")
openssl pkey -in "$tmp/key.pem" -pubout -out "$tmp/key.pub"
gh secret set LOCAL_BUNDLE_MANIFEST_KEY --repo "$repo" < "$tmp/key.pem"
cp "$tmp/key.pub" "$pub"
echo "Stored LOCAL_BUNDLE_MANIFEST_KEY on $repo and wrote the public key to ${pub#"$here/../"}. Commit it."
