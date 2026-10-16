#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/publish-local-bundle.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

# Fake aws: s3://bucket/<key> lives at $BUCKET/<key>. "aws s3 cp" from a missing key fails with a
# 404; $BUCKET/.broken makes every call fail the way an R2 outage does. Uploads are logged in order.
mkdir -p "$tmp/bin"
cat > "$tmp/bin/aws" <<'AWS'
#!/usr/bin/env bash
[ -e "$BUCKET/.broken" ] && { echo "Could not connect to the endpoint URL" >&2; exit 255; }
args=(); for a in "$@"; do case "$a" in s3|cp|--endpoint-url|https://r2.test) ;; *) args+=("$a") ;; esac; done
src="${args[0]}"; dst="${args[1]}"
case "$src" in
  s3://*) key="${src#s3://bucket/}"; [ -f "$BUCKET/$key" ] || { echo "fatal error: An error occurred (404) when calling the HeadObject operation: Not Found" >&2; exit 1; }
          cp "$BUCKET/$key" "$dst" ;;
  *)      key="${dst#s3://bucket/}"; mkdir -p "$BUCKET/$(dirname "$key")"; cp "$src" "$BUCKET/$key"; echo "$key" >> "$BUCKET/.log" ;;
esac
AWS
chmod +x "$tmp/bin/aws"

art="$tmp/art"; mkdir -p "$art"
n=kcap-local-1.0.0-osx-arm64
printf 'archive' > "$art/$n.tar.gz"; printf '{"v":1}\n' > "$art/$n.json"; printf 'sig' > "$art/$n.json.sig"

fail=0
run() { # <label> <want-rc>
  local rc
  set +e
  BUCKET="$tmp/bucket" PATH="$tmp/bin:$PATH" R2="--endpoint-url https://r2.test" DEST="s3://bucket/local/osx-arm64" \
    bash "$sh" "$art" 1.0.0 osx-arm64 >"$tmp/out" 2>&1
  rc=$?
  set -e
  if [ "$rc" != "$2" ]; then echo "FAIL: $1 -> rc=$rc (want $2): $(cat "$tmp/out")"; fail=1; fi
}
log() { cat "$tmp/bucket/.log" 2>/dev/null | tr '\n' ' '; }

mkdir -p "$tmp/bucket"
run "first publication" 0
[ "$(log)" = "local/osx-arm64/$n.tar.gz local/osx-arm64/$n.json.sig local/osx-arm64/$n.json " ] \
  || { echo "FAIL: uploads the archive, then the signature, then the manifest: $(log)"; fail=1; }

rm "$tmp/bucket/.log"
run "a finished re-run uploads nothing" 0
[ -z "$(log)" ] || { echo "FAIL: re-run uploaded $(log)"; fail=1; }

printf '{"v":2}\n' > "$tmp/bucket/local/osx-arm64/$n.json"
run "another build's manifest refuses" 1
grep -q "already published from another build" "$tmp/out" || { echo "FAIL: refusal message: $(cat "$tmp/out")"; fail=1; }
[ -z "$(log)" ] || { echo "FAIL: a refused run uploaded $(log)"; fail=1; }

rm -rf "$tmp/bucket"; mkdir -p "$tmp/bucket/local/osx-arm64"
printf 'orphan' > "$tmp/bucket/local/osx-arm64/$n.tar.gz"
run "an archive no manifest vouches for is replaced" 0
cmp -s "$tmp/bucket/local/osx-arm64/$n.tar.gz" "$art/$n.tar.gz" || { echo "FAIL: orphan archive kept"; fail=1; }

rm -rf "$tmp/bucket"; mkdir -p "$tmp/bucket"; touch "$tmp/bucket/.broken"
run "an R2 failure is never read as unpublished" 1

[ "$fail" -eq 0 ] && echo "ok" || exit 1
