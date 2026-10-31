#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/verify-windows-signatures.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
mkdir "$tmp/bin"
printf '#!/usr/bin/env bash\nprintf "%%s\n" "$FAKE_SIGNTOOL_OUTPUT"\nexit "${FAKE_SIGNTOOL_RC:-0}"\n' > "$tmp/bin/signtool.exe"
printf '#!/usr/bin/env bash\nprintf "%%s\n" "$2"\n' > "$tmp/bin/cygpath"
# The fake only answers for the file it was handed.
printf '#!/usr/bin/env bash\n[ "$KCAP_SIGNED_FILE" = "$FAKE_EXPECTED_FILE" ] || exit 9\nprintf "%%s" "$FAKE_EKUS"\nexit "${FAKE_PS_RC:-0}"\n' > "$tmp/bin/powershell.exe"
chmod +x "$tmp/bin/signtool.exe" "$tmp/bin/cygpath" "$tmp/bin/powershell.exe"
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

ours="1.3.6.1.4.1.311.97.990309390.766961637.194916062.941502583"
theirs="1.3.6.1.4.1.311.97.123456789.987654321.111111111.222222222"
# PowerShell prints CRLF line endings; every Public Trust certificate also carries 311.97.1.0.
ekus() { printf '%s\r\n' "1.3.6.1.5.5.7.3.3" "1.3.6.1.4.1.311.97.1.0" "$@"; }

fail=0
# assert <name> <signtool output> <signtool rc> <publisher> <want rc> [<EKUs printed> [<pinned EKU> [<powershell rc>]]]
assert() {
  local name="$1" output="$2" rc_in="$3" publisher="$4" want_rc="$5"
  local printed="${6-$(ekus "$ours")}" pinned="${7-$ours}" ps_rc="${8:-0}"
  local rc
  set +e
  PATH="$tmp/bin:$PATH" FAKE_SIGNTOOL_OUTPUT="$output" FAKE_SIGNTOOL_RC="$rc_in" \
    FAKE_EKUS="$printed" FAKE_PS_RC="$ps_rc" FAKE_EXPECTED_FILE="$tmp/app.exe" \
    AZURE_TRUSTED_SIGNING_PUBLISHER="$publisher" AZURE_TRUSTED_SIGNING_EKU="$pinned" \
    bash "$sh" "$tmp/app.exe" >/dev/null 2>&1
  rc=$?
  set -e
  if [ "$rc" != "$want_rc" ]; then echo "FAIL: $name -> rc=$rc (want $want_rc)"; fail=1; fi
}
signed="$(verbose "Kurrent, Inc." yes)"
assert "expected publisher and profile, timestamped" "$signed" 0 "Kurrent, Inc." 0
assert "another publisher's valid signature"  "$(verbose "Velopack Ltd" yes)"  0 "Kurrent, Inc." 1
assert "intermediate CA name as publisher"    "$signed" 0 "Microsoft ID Verified CS EOC CA 01" 1
assert "timestamp chain leaf as publisher"    "$signed" 0 "Microsoft Time-Stamp Service" 1
assert "not timestamped"                      "$(verbose "Kurrent, Inc." no)"  0 "Kurrent, Inc." 1
assert "signtool rejects the signature"       "$signed" 1 "Kurrent, Inc." 1
assert "no signature at all"                  "SignTool Error: No signature found." 1 "Kurrent, Inc." 1
assert "publisher not configured"             "$signed" 0 "" 1
assert "same common name, another profile"    "$signed" 0 "Kurrent, Inc." 1 "$(ekus "$theirs")"
assert "only the shared Public Trust EKUs"    "$signed" 0 "Kurrent, Inc." 1 "$(ekus)"
assert "pinned EKU is a prefix of the signer's" "$signed" 0 "Kurrent, Inc." 1 "$(ekus "$ours.1")"
assert "signer's EKU is a prefix of the pinned" "$signed" 0 "Kurrent, Inc." 1 "$(ekus "$ours")" "$ours.1"
assert "EKU not configured"                   "$signed" 0 "Kurrent, Inc." 1 "$(ekus "$ours")" ""
assert "PowerShell fails after printing it"   "$signed" 0 "Kurrent, Inc." 1 "$(ekus "$ours")" "$ours" 1
assert "no signer certificate"                "SignTool Error: No signature found." 1 "Kurrent, Inc." 1 ""

rc=0
PATH="$tmp/bin:$PATH" FAKE_SIGNTOOL_OUTPUT="$signed" FAKE_EKUS="$(ekus "$ours")" FAKE_EXPECTED_FILE="$tmp/app.exe" \
  AZURE_TRUSTED_SIGNING_PUBLISHER="Kurrent, Inc." AZURE_TRUSTED_SIGNING_EKU="$ours" \
  bash "$sh" "$tmp/app.exe" "$tmp/missing.exe" >/dev/null 2>&1 || rc=$?
[ "$rc" -ne 0 ] || { echo "FAIL: a missing file among signed ones -> rc=0 (want non-zero)"; fail=1; }

[ "$fail" -eq 0 ] && echo "ok" || exit 1
