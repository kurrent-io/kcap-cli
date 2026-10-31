#!/usr/bin/env bash
# Fails unless every file carries an Authenticode signature that passes the default policy, is
# timestamped, and was issued to AZURE_TRUSTED_SIGNING_PUBLISHER. A Trusted Signing certificate is
# valid for three days, so a signature without a timestamp stops verifying shortly after the release.
# The publisher check matters because Velopack leaves an already-signed file alone, and anyone's valid
# signature passes /pa.
# Usage: AZURE_TRUSTED_SIGNING_PUBLISHER=<subject CN> verify-windows-signatures.sh <file>...
set -euo pipefail
[ "$#" -gt 0 ] || { echo "usage: verify-windows-signatures.sh <file>..." >&2; exit 1; }
publisher="${AZURE_TRUSTED_SIGNING_PUBLISHER:-}"
[ -n "$publisher" ] || { echo "::error::AZURE_TRUSTED_SIGNING_PUBLISHER is not set"; exit 1; }

signtool="$(command -v signtool.exe || true)"
if [ -z "$signtool" ]; then
  signtool="$(ls "/c/Program Files (x86)/Windows Kits/10/bin/"10.*/x64/signtool.exe 2>/dev/null | sort -V | tail -1 || true)"
fi
[ -n "$signtool" ] || { echo "::error::signtool.exe is not on PATH or in the Windows SDK"; exit 1; }

# The leaf is the last "Issued to:" of the signing chain, before the timestamp chain starts.
signer_of() {
  tr -d '\r' | awk '
    /^Signing Certificate Chain:/ { chain = 1; next }
    /^The signature is timestamped|^Timestamp Verified by|^File is not timestamped/ { chain = 0 }
    chain && /^[[:space:]]*Issued to:/ { sub(/^[[:space:]]*Issued to:[[:space:]]*/, ""); leaf = $0 }
    END { print leaf }'
}

# MSYS would rewrite signtool's /switches as paths.
export MSYS_NO_PATHCONV=1
failed=0
for f in "$@"; do
  [ -f "$f" ] || { echo "::error::$f does not exist"; failed=1; continue; }
  out="$("$signtool" verify /pa /v "$(cygpath -w "$f")" 2>&1)" && rc=0 || rc=$?
  signer="$(signer_of <<<"$out")"
  if [ "$rc" -eq 0 ] && grep -q 'The signature is timestamped' <<<"$out" && [ "$signer" = "$publisher" ]; then
    echo "signed by $signer and timestamped: $f"
  else
    echo "::error::$f is not signed, does not verify, carries no timestamp, or is signed by '$signer' rather than '$publisher'"
    echo "$out"
    failed=1
  fi
done
exit "$failed"
