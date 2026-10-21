#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/wait-server-bundle.sh"
source "$here/lib/hash.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

# Fake gh: the release lists the asset from the Nth poll on ($STATE/after, or "never"), in the
# state $STATE/state, with the digest in $STATE/digest. Asset downloads print $STATE/bytes.
mkdir -p "$tmp/bin"
cat > "$tmp/bin/gh" <<'GH'
#!/usr/bin/env bash
case "$*" in
  *"releases/assets/"*) cat "$STATE/bytes"; exit 0 ;;
esac
n=$(( $(cat "$STATE/calls" 2>/dev/null || echo 0) + 1 )); echo "$n" > "$STATE/calls"
after="$(cat "$STATE/after")"
if [ "$after" = never ] || [ "$n" -lt "$after" ]; then echo "gh: Not Found (HTTP 404)" >&2; exit 1; fi
jq_expr="${!#}"
jq -r "$jq_expr" <<JSON
{"assets":[{"id":7,"name":"other.tar.gz","state":"uploaded"},
           {"id":42,"name":"kcap-local-1.0.0-osx-arm64.tar.gz","state":"$(cat "$STATE/state")","digest":$(cat "$STATE/digest")}]}
JSON
GH
chmod +x "$tmp/bin/gh"

fail=0
asset=kcap-local-1.0.0-osx-arm64.tar.gz
run() { # <name> <want-rc> <timeout> <after> <state> <digest-json>
  local name="$1" want="$2" rc
  rm -rf "$tmp/state" "$tmp/out"; mkdir -p "$tmp/state"
  printf '%s' "$4" > "$tmp/state/after"; printf '%s' "$5" > "$tmp/state/state"; printf '%s' "$6" > "$tmp/state/digest"
  printf 'bundle-bytes' > "$tmp/state/bytes"
  set +e
  STATE="$tmp/state" PATH="$tmp/bin:$PATH" SERVER_WAIT_TIMEOUT="$3" SERVER_WAIT_INTERVAL=0 \
    bash "$sh" v1.0.0 "$asset" "$tmp/out" >"$tmp/stdout" 2>"$tmp/stderr"
  rc=$?
  set -e
  if [ "$rc" != "$want" ]; then echo "FAIL: $name -> rc=$rc (want $want): $(cat "$tmp/stderr")"; fail=1; fi
}

printf 'bundle-bytes' > "$tmp/expected"
good="\"sha256:$(sha256_of "$tmp/expected")\""

run "present on the first poll"     0 30 1 uploaded "$good"
[ "$(cat "$tmp/stdout")" = "$tmp/out/$asset" ] || { echo "FAIL: prints the downloaded path: $(cat "$tmp/stdout")"; fail=1; }
cmp -s "$tmp/out/$asset" "$tmp/expected" || { echo "FAIL: downloads the asset's bytes"; fail=1; }
run "waits for the release"         0 30 3 uploaded "$good"
run "no digest reported"            0 30 1 uploaded null
run "digest mismatch fails"         1 30 1 uploaded '"sha256:0000"'
grep -q "but the release reports sha256:0000" "$tmp/stderr" || { echo "FAIL: mismatch message: $(cat "$tmp/stderr")"; fail=1; }
run "an asset still uploading is not ready" 1 0 1 starter "$good"
run "never appears"                 1 0 never uploaded "$good"
grep -q "kurrent-io/kcap-server release v1.0.0 has no uploaded $asset after 0s: gh: Not Found (HTTP 404)" "$tmp/stderr" \
  || { echo "FAIL: deadline message names the release and gh's reason: $(cat "$tmp/stderr")"; fail=1; }

[ "$fail" -eq 0 ] && echo "ok" || exit 1
