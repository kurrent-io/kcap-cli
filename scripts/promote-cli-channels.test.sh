#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/promote-cli-channels.sh"

fail=0
assert() { # <label> <candidate> <latest> <beta> <want-output> <want-rc>
  local got rc; set +e; got="$(bash "$sh" "$2" "$3" "$4" 2>/dev/null)"; rc=$?; set -e
  if [ "$got" != "$5" ] || [ "$rc" != "$6" ]; then echo "FAIL: $1 -> out='$got' rc=$rc (want '$5' rc=$6)"; fail=1; fi
}

assert "first stable creates both channels"    "1.0.0"        ""      ""             $'beta\nlatest' 0
assert "first beta creates beta only"          "1.1.0-beta.1" ""      ""             "beta"          0
assert "newer beta moves beta only"            "1.1.0-beta.2" "1.0.0" "1.1.0-beta.1" "beta"          0
assert "stable above the beta moves both"      "1.1.0"        "1.0.0" "1.1.0-beta.2" $'beta\nlatest' 0
assert "stable patch below a beta: latest"     "1.0.1"        "1.0.0" "1.1.0-beta.1" "latest"        0
assert "older stable published late: nothing"  "0.9.9"        "1.0.0" "1.0.0"        ""              0
assert "re-run of the current version heals"   "1.0.0"        "1.0.0" "1.0.0"        $'beta\nlatest' 0
assert "malformed current version fails"       "1.0.0"        "x.y"   ""             "beta"          1

[ "$fail" -eq 0 ] && echo "ok" || exit 1
