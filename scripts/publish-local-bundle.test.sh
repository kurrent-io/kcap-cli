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
openssl ecparam -name prime256v1 -genkey -noout 2>/dev/null | openssl pkey -out "$tmp/k.pem"
openssl pkey -in "$tmp/k.pem" -pubout -out "$tmp/k.pub"
sign() { LOCAL_BUNDLE_MANIFEST_KEY="$(cat "$tmp/k.pem")" bash "$here/local-manifest-key.sh" sign "$tmp/k.pub" "$1" "$2"; }
printf 'archive' > "$art/$n.tar.gz"; printf '{"v":1}\n' > "$art/$n.json"; sign "$art/$n.json" "$art/$n.json.sig"
B="$tmp/bucket/local/osx-arm64"

fail=0
run() { # <label> <want-rc>
  local rc
  set +e
  BUCKET="$tmp/bucket" PATH="$tmp/bin:$PATH" R2="--endpoint-url https://r2.test" DEST="s3://bucket/local/osx-arm64" \
    bash "$sh" "$art" 1.0.0 osx-arm64 "$tmp/k.pub" >"$tmp/out" 2>&1
  rc=$?
  set -e
  if [ "$rc" != "$2" ]; then echo "FAIL: $1 -> rc=$rc (want $2): $(cat "$tmp/out")"; fail=1; fi
}
log() { cat "$tmp/bucket/.log" 2>/dev/null | sed 's|local/osx-arm64/||' | tr '\n' ' '; }
fresh() { rm -rf "$tmp/bucket"; mkdir -p "$B"; }

fresh
run "first publication" 0
[ "$(log)" = "$n.tar.gz $n.json.sig $n.json " ] \
  || { echo "FAIL: uploads the archive, then the signature, then the manifest: $(log)"; fail=1; }

rm "$tmp/bucket/.log"
run "a finished re-run uploads nothing" 0
[ -z "$(log)" ] || { echo "FAIL: re-run uploaded $(log)"; fail=1; }

printf '{"v":2}\n' > "$B/$n.json"; rm -f "$tmp/bucket/.log"
run "another build's manifest refuses" 1
grep -q "already published from another build" "$tmp/out" || { echo "FAIL: refusal message: $(cat "$tmp/out")"; fail=1; }
[ -z "$(log)" ] || { echo "FAIL: a refused run uploaded $(log)"; fail=1; }

fresh; cp "$art/$n.tar.gz" "$B/"
run "an identical unpublished archive is kept" 0
[ "$(log)" = "$n.json.sig $n.json " ] || { echo "FAIL: re-uploaded the archive: $(log)"; fail=1; }

fresh; printf 'other build' > "$B/$n.tar.gz"
run "an archive with other bytes refuses" 1
grep -q "already uploaded with other bytes" "$tmp/out" || { echo "FAIL: archive refusal: $(cat "$tmp/out")"; fail=1; }
[ -z "$(log)" ] || { echo "FAIL: uploaded past a differing archive: $(log)"; fail=1; }

fresh; cp "$art/$n.tar.gz" "$B/"; sign "$art/$n.json" "$B/$n.json.sig"
cmp -s "$B/$n.json.sig" "$art/$n.json.sig" && { echo "FAIL: precondition: a second signature should differ"; fail=1; }
run "a stored signature of this manifest is kept" 0
[ "$(log)" = "$n.json " ] || { echo "FAIL: replaced a valid stored signature: $(log)"; fail=1; }
bash "$here/local-manifest-key.sh" verify "$tmp/k.pub" "$B/$n.json" "$B/$n.json.sig" \
  || { echo "FAIL: the published manifest and signature disagree"; fail=1; }

fresh; printf '{"v":0}\n' > "$tmp/old.json"; sign "$tmp/old.json" "$B/$n.json.sig"
run "a stored signature of another manifest refuses" 1
grep -q "does not sign this manifest" "$tmp/out" || { echo "FAIL: signature refusal: $(cat "$tmp/out")"; fail=1; }
[ "$(log)" = "$n.tar.gz " ] || { echo "FAIL: got past a foreign signature: $(log)"; fail=1; }

for broken in "rm $n.tar.gz" "rm $n.json.sig" "printf x > $n.tar.gz"; do
  fresh; cp "$art/$n.tar.gz" "$art/$n.json" "$art/$n.json.sig" "$B/"; (cd "$B" && eval "$broken")
  run "a published manifest with a broken set: $broken" 1
  [ -z "$(log)" ] || { echo "FAIL: uploaded over a published manifest: $(log)"; fail=1; }
done

fresh; touch "$tmp/bucket/.broken"
run "an R2 failure is never read as unpublished" 1

[ "$fail" -eq 0 ] && echo "ok" || exit 1
