#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
fail=0

# A stub npm answering `npm view <spec> <field>` from $tmp/<field>: "value:<v>", "404", or "down".
mkdir -p "$tmp/bin"
cat > "$tmp/bin/npm" <<'EOF'
#!/usr/bin/env bash
answer="$(cat "$STUB/$3" 2>/dev/null || echo 404)"
case "$answer" in
  value:*) echo "${answer#value:}" ;;
  404)     echo "npm error code E404" >&2; exit 1 ;;
  down)    echo "npm error network ECONNRESET" >&2; exit 1 ;;
esac
EOF
chmod +x "$tmp/bin/npm"

check() { # <name> <kcapReleaseCommit> <gitHead> <want-output> <want-rc>
  local stub="$tmp/$1"; mkdir -p "$stub"
  echo "$2" > "$stub/kcapReleaseCommit"; echo "$3" > "$stub/gitHead"
  local out rc=0
  out="$(PATH="$tmp/bin:$PATH" STUB="$stub" bash "$here/npm-release-commit.sh" @kurrent/kcap 1.2.3 2>/dev/null)" || rc=$?
  [ "$out" = "$4" ] && [ "$rc" -eq "$5" ] || { echo "FAIL: $1 -> '$out' rc=$rc (want '$4' rc=$5)"; fail=1; }
}

check "stamped commit"        "value:abc123" "value:old"   "abc123" 0
check "pre-stamp falls back"  "value:"       "value:def456" "def456" 0
check "not published"         "404"          "404"          ""       0
check "registry down"         "down"         "value:x"      ""       1

[ "$fail" -eq 0 ] && echo "ok" || exit 1
