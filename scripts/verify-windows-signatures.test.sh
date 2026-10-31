#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/verify-windows-signatures.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
mkdir "$tmp/bin"
printf '#!/usr/bin/env bash\nprintf "%%s\\n" "$FAKE_SIGNTOOL_OUTPUT"\nexit "${FAKE_SIGNTOOL_RC:-0}"\n' > "$tmp/bin/signtool.exe"
printf '#!/usr/bin/env bash\nprintf "%%s\\n" "$2"\n' > "$tmp/bin/cygpath"
chmod +x "$tmp/bin/signtool.exe" "$tmp/bin/cygpath"
touch "$tmp/app.exe"

# signtool prints CRLF line endings.
verbose() {
  local leaf="$1" stamp="$2"
  printf '%s\r\n' \
    "Verifying: app.exe" "" "Signature Index: 0 (Primary Signature)" "" \
    "Signing Certificate Chain:" \
    "    Issued to: Microsoft Identity Verification Root Certificate Authority 2020" \
    "    Issued by: Microsoft Identity Verification Root Certificate Authority 2020" "" \
    "        Issued to: Microsoft ID Verified CS EOC CA 01" \
    "        Issued by: Microsoft Identity Verification Root Certificate Authority 2020" "" \
    "            Issued to: $leaf" \
    "            Issued by: Microsoft ID Verified CS EOC CA 01" ""
  if [ "$stamp" = yes ]; then
    printf '%s\r\n' "The signature is timestamped: Wed Nov 19 16:44:11 2025" "Timestamp Verified by:" \
      "    Issued to: Microsoft Time-Stamp Service" ""
  else
    printf '%s\r\n' "File is not timestamped." ""
  fi
  printf '%s\r\n' "Successfully verified: app.exe"
}

fail=0
assert() {
  local name="$1" output="$2" rc_in="$3" publisher="$4" want_rc="$5"
  local rc
  set +e
  PATH="$tmp/bin:$PATH" FAKE_SIGNTOOL_OUTPUT="$output" FAKE_SIGNTOOL_RC="$rc_in" \
    AZURE_TRUSTED_SIGNING_PUBLISHER="$publisher" bash "$sh" "$tmp/app.exe" >/dev/null 2>&1
  rc=$?
  set -e
  if [ "$rc" != "$want_rc" ]; then echo "FAIL: $name -> rc=$rc (want $want_rc)"; fail=1; fi
}
assert "expected publisher, timestamped"     "$(verbose "Kurrent, Inc." yes)" 0 "Kurrent, Inc." 0
assert "another publisher's valid signature" "$(verbose "Velopack Ltd" yes)"  0 "Kurrent, Inc." 1
assert "intermediate CA name as publisher"   "$(verbose "Kurrent, Inc." yes)" 0 "Microsoft ID Verified CS EOC CA 01" 1
assert "timestamp chain leaf as publisher"   "$(verbose "Kurrent, Inc." yes)" 0 "Microsoft Time-Stamp Service" 1
assert "not timestamped"                     "$(verbose "Kurrent, Inc." no)"  0 "Kurrent, Inc." 1
assert "signtool rejects the signature"      "$(verbose "Kurrent, Inc." yes)" 1 "Kurrent, Inc." 1
assert "no signature at all"                 "SignTool Error: No signature found." 1 "Kurrent, Inc." 1
assert "publisher not configured"            "$(verbose "Kurrent, Inc." yes)" 0 "" 1

rc=0
PATH="$tmp/bin:$PATH" FAKE_SIGNTOOL_OUTPUT="$(verbose "Kurrent, Inc." yes)" AZURE_TRUSTED_SIGNING_PUBLISHER="Kurrent, Inc." \
  bash "$sh" "$tmp/app.exe" "$tmp/missing.exe" >/dev/null 2>&1 || rc=$?
[ "$rc" -ne 0 ] || { echo "FAIL: a missing file among signed ones -> rc=0 (want non-zero)"; fail=1; }

[ "$fail" -eq 0 ] && echo "ok" || exit 1
