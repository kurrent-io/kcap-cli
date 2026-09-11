#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/wait-npm-available.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

# Fake npm: a package resolves from its Nth poll on (N in $STATE/<key>.after, or "never"). It refuses a
# poll without --prefer-online, which would re-read npm's cached packument instead of the registry.
mkdir -p "$tmp/bin"
cat > "$tmp/bin/npm" <<'EOF'
#!/usr/bin/env bash
spec="$2"; key="$(printf '%s' "${spec%@*}" | tr '/@' '__')"
case " $* " in *" --prefer-online "*) ;; *) echo "npm error polled without --prefer-online" >&2; exit 1 ;; esac
n=$(( $(cat "$STATE/$key.calls" 2>/dev/null || echo 0) + 1 )); echo "$n" > "$STATE/$key.calls"
after="$(cat "$STATE/$key.after")"
if [ "$after" != never ] && [ "$n" -ge "$after" ]; then echo "${spec##*@}"; exit 0; fi
echo "npm error code E404" >&2
echo "npm error 404 No match found for version ${spec##*@}" >&2
echo "npm error A complete log of this run can be found in: /x/debug-0.log" >&2
exit 1
EOF
chmod +x "$tmp/bin/npm"

fail=0
run() { # <name> <want-rc> <timeout> <package=after>...
  local name="$1" want="$2" timeout="$3" kv rc; shift 3
  local pkgs=()
  rm -rf "$tmp/state"; mkdir -p "$tmp/state"
  for kv in "$@"; do
    pkgs+=("${kv%=*}")
    printf '%s' "${kv#*=}" > "$tmp/state/$(printf '%s' "${kv%=*}" | tr '/@' '__').after"
  done
  set +e
  STATE="$tmp/state" PATH="$tmp/bin:$PATH" NPM_WAIT_TIMEOUT="$timeout" NPM_WAIT_INTERVAL=0 \
    bash "$sh" 1.0.2 "${pkgs[@]}" >"$tmp/out" 2>&1
  rc=$?
  set -e
  if [ "$rc" != "$want" ]; then echo "FAIL: $name -> rc=$rc (want $want): $(cat "$tmp/out")"; fail=1; fi
}

run "visible on the first poll"       0 30 "@kurrent/a=1"
run "visible after retries"           0 30 "@kurrent/a=3"
run "waits for every package"         0 30 "@kurrent/a=1" "@kurrent/b=3"
run "fails when one never appears"    1 0  "@kurrent/a=1" "@kurrent/b=never"
if ! grep -q "@kurrent/b@1.0.2 is still unavailable after 0s: npm error 404 No match found for version 1.0.2" "$tmp/out"; then
  echo "FAIL: deadline message names the package and npm's reason: $(cat "$tmp/out")"; fail=1
fi
[ "$fail" -eq 0 ] && echo "ok" || exit 1
