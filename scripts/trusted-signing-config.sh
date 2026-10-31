#!/usr/bin/env bash
# Fails a release that switched Windows signing on with an incomplete or malformed configuration,
# instead of letting it fail deep inside signtool. Reads the repository variables from the
# environment under their own names, and writes the metadata file signtool's Trusted Signing dlib
# takes when given a path.
# Usage: trusted-signing-config.sh [<metadata.json>]
set -euo pipefail

missing=()
for v in AZURE_TRUSTED_SIGNING_ENDPOINT AZURE_TRUSTED_SIGNING_ACCOUNT AZURE_TRUSTED_SIGNING_PROFILE \
         AZURE_TRUSTED_SIGNING_CLIENT_ID AZURE_TRUSTED_SIGNING_TENANT_ID AZURE_TRUSTED_SIGNING_PUBLISHER; do
  [ -n "${!v:-}" ] || missing+=("$v")
done
if [ "${#missing[@]}" -gt 0 ]; then
  echo "::error::Windows signing is partly configured; set the repository variables ${missing[*]}"
  exit 1
fi

endpoint="${AZURE_TRUSTED_SIGNING_ENDPOINT%/}"
[[ "$endpoint" =~ ^https://[a-z0-9]+\.codesigning\.azure\.net$ ]] || {
  echo "::error::AZURE_TRUSTED_SIGNING_ENDPOINT must be the signing account's regional endpoint, https://<region>.codesigning.azure.net"
  exit 1
}
# Also what keeps the names safe to write into the JSON below unescaped.
for v in AZURE_TRUSTED_SIGNING_ACCOUNT AZURE_TRUSTED_SIGNING_PROFILE; do
  [[ "${!v}" =~ ^[A-Za-z0-9-]+$ ]] || { echo "::error::$v holds characters a Trusted Signing name cannot"; exit 1; }
done

echo "Windows signing with $AZURE_TRUSTED_SIGNING_ACCOUNT/$AZURE_TRUSTED_SIGNING_PROFILE at $endpoint"

if [ -n "${1:-}" ]; then
  # azure/login leaves an Azure CLI session; the credentials excluded here would only add probes.
  printf '{"Endpoint":"%s","CodeSigningAccountName":"%s","CertificateProfileName":"%s","ExcludeCredentials":["ManagedIdentityCredential","EnvironmentCredential"]}\n' \
    "$endpoint" "$AZURE_TRUSTED_SIGNING_ACCOUNT" "$AZURE_TRUSTED_SIGNING_PROFILE" > "$1"
fi
