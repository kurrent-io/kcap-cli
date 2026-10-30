#!/usr/bin/env bash
# Fails unless every file carries an Authenticode signature that passes the default policy and is
# timestamped. A Trusted Signing certificate is valid for three days, so a signature without a
# timestamp stops verifying shortly after the release.
# Usage: verify-windows-signatures.sh <file>...
set -euo pipefail
[ "$#" -gt 0 ] || { echo "usage: verify-windows-signatures.sh <file>..." >&2; exit 1; }

signtool="$(command -v signtool.exe || true)"
if [ -z "$signtool" ]; then
  signtool="$(ls "/c/Program Files (x86)/Windows Kits/10/bin/"10.*/x64/signtool.exe 2>/dev/null | sort -V | tail -1 || true)"
fi
[ -n "$signtool" ] || { echo "::error::signtool.exe is not on PATH or in the Windows SDK"; exit 1; }

# MSYS would rewrite signtool's /switches as paths.
export MSYS_NO_PATHCONV=1
failed=0
for f in "$@"; do
  [ -f "$f" ] || { echo "::error::$f does not exist"; failed=1; continue; }
  out="$("$signtool" verify /pa /v "$(cygpath -w "$f")" 2>&1)" && rc=0 || rc=$?
  if [ "$rc" -eq 0 ] && grep -q 'The signature is timestamped' <<<"$out"; then
    echo "signed and timestamped: $f"
  else
    echo "::error::$f is not signed, does not verify, or carries no timestamp"
    echo "$out"
    failed=1
  fi
done
exit "$failed"
