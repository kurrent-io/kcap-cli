#!/usr/bin/env bash
# Fails unless every file carries an Authenticode signature that passes the default policy, is
# timestamped, was issued to AZURE_TRUSTED_SIGNING_PUBLISHER, and carries the certificate profile's
# own EKU, AZURE_TRUSTED_SIGNING_EKU. A Trusted Signing certificate is valid for three days, so a
# signature without a timestamp stops verifying shortly after the release.
# The signer checks matter because Velopack leaves an already-signed file alone, and anyone's valid
# signature passes /pa. The common name alone is not enough: another publisher can hold a trusted
# certificate with the same one, while the profile EKU is unique to our profile and survives renewals.
# Usage: AZURE_TRUSTED_SIGNING_PUBLISHER=<subject CN> AZURE_TRUSTED_SIGNING_EKU=<OID> verify-windows-signatures.sh <file>...
set -euo pipefail
[ "$#" -gt 0 ] || { echo "usage: verify-windows-signatures.sh <file>..." >&2; exit 1; }
publisher="${AZURE_TRUSTED_SIGNING_PUBLISHER:-}"
[ -n "$publisher" ] || { echo "::error::AZURE_TRUSTED_SIGNING_PUBLISHER is not set"; exit 1; }
profile_eku="${AZURE_TRUSTED_SIGNING_EKU:-}"
[ -n "$profile_eku" ] || { echo "::error::AZURE_TRUSTED_SIGNING_EKU is not set"; exit 1; }

signtool="$(command -v signtool.exe || true)"
if [ -z "$signtool" ]; then
  signtool="$(ls "/c/Program Files (x86)/Windows Kits/10/bin/"10.*/x64/signtool.exe 2>/dev/null | sort -V | tail -1 || true)"
fi
[ -n "$signtool" ] || { echo "::error::signtool.exe is not on PATH or in the Windows SDK"; exit 1; }
powershell="$(command -v powershell.exe || true)"
[ -n "$powershell" ] || { echo "::error::powershell.exe is not on PATH"; exit 1; }

# The leaf is the last "Issued to:" of the signing chain, before the timestamp chain starts.
signer_of() {
  tr -d '\r' | awk '
    /^Signing Certificate Chain:/ { chain = 1; next }
    /^The signature is timestamped|^Timestamp Verified by|^File is not timestamped/ { chain = 0 }
    chain && /^[[:space:]]*Issued to:/ { sub(/^[[:space:]]*Issued to:[[:space:]]*/, ""); leaf = $0 }
    END { print leaf }'
}

# signtool /v does not print EKUs. One OID per line, from the primary signature's signer certificate.
# shellcheck disable=SC2016
ekus_ps='$ErrorActionPreference = "Stop"
$c = (Get-AuthenticodeSignature -LiteralPath $env:KCAP_SIGNED_FILE).SignerCertificate
if ($c) { foreach ($e in $c.Extensions) {
  if ($e -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
    foreach ($u in $e.EnhancedKeyUsages) { $u.Value } } } }'

# MSYS would rewrite signtool's /switches as paths.
export MSYS_NO_PATHCONV=1
failed=0
for f in "$@"; do
  [ -f "$f" ] || { echo "::error::$f does not exist"; failed=1; continue; }
  win="$(cygpath -w "$f")"
  out="$("$signtool" verify /pa /v "$win" 2>&1)" && rc=0 || rc=$?
  signer="$(signer_of <<<"$out")"
  ekus="$(KCAP_SIGNED_FILE="$win" "$powershell" -NoProfile -NonInteractive -Command "$ekus_ps" 2>&1 | tr -d '\r')" && eku_rc=0 || eku_rc=$?
  if [ "$rc" -eq 0 ] && grep -q 'The signature is timestamped' <<<"$out" && [ "$signer" = "$publisher" ] \
     && [ "$eku_rc" -eq 0 ] && grep -qxF "$profile_eku" <<<"$ekus"; then
    echo "signed by $signer under profile EKU $profile_eku and timestamped: $f"
  else
    echo "::error::$f is not signed, does not verify, carries no timestamp, is signed by '$signer' rather than '$publisher', or lacks the profile EKU $profile_eku"
    echo "$out"
    echo "Signer certificate EKUs:"
    echo "$ekus"
    failed=1
  fi
done
exit "$failed"
