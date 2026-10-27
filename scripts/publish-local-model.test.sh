#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/publish-local-model.sh"
source "$here/lib/hash.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

# Fake aws as in publish-local-bundle.test.sh; fake curl serves $UPSTREAM/<last URL segment> and logs the URL.
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
cat > "$tmp/bin/curl" <<'CURL'
#!/usr/bin/env bash
out=; url=
while [ $# -gt 0 ]; do case "$1" in -o) out="$2"; shift 2 ;; -*) shift ;; *) url="$1"; shift ;; esac; done
echo "$url" >> "$UPSTREAM/.requests"
f="$UPSTREAM/$(basename "$url")"; [ -f "$f" ] || exit 22
cp "$f" "$out"
CURL
chmod +x "$tmp/bin/aws" "$tmp/bin/curl"

up="$tmp/upstream"; mkdir -p "$up"
printf 'onnx bytes' > "$up/model.onnx"; printf 'vocab bytes' > "$up/vocab.txt"
msha="$(sha256_of "$up/model.onnx")"; vsha="$(sha256_of "$up/vocab.txt")"
set_id="$(printf 'model=m1\ncontract=1\nmodel.onnx=%s\nvocab.txt=%s\n' "$msha" "$vsha" > "$tmp/id"; sha256_of "$tmp/id")"
json() { # <model_set>
  jq -n --arg set "$1" --arg m "$msha" --arg v "$vsha" '{model_id:"m1",contract:1,model_set:$set,files:[
    {name:"model.onnx",sha256:$m,size:10,source_url:"https://up.test/a/model.onnx"},
    {name:"vocab.txt",sha256:$v,size:11,source_url:"https://up.test/a/vocab.txt"}]}'
}
bundle() { # <name> <model_set>   (an archive laid out as the server's: ./embedding-model.json beside the server)
  mkdir -p "$tmp/stage"; printf x > "$tmp/stage/kcap-server"; json "$2" > "$tmp/stage/embedding-model.json"
  tar -czf "$tmp/$1.tar.gz" -C "$tmp/stage" .
}
bundle good "$set_id"; bundle badid "$(printf 'f%.0s' $(seq 64))"
mkdir -p "$tmp/empty"; printf x > "$tmp/empty/kcap-server"; tar -czf "$tmp/nomodel.tar.gz" -C "$tmp/empty" .
printf 'not a tar archive' > "$tmp/notar.tar.gz"
reject() { # <name> <jq edit>   (an entry kcap rejects, under a set id that matches it, so only validation can refuse it)
  json x | jq "$2" > "$tmp/edit.json"
  jq -j '"model=\(.model_id)\ncontract=\(.contract)\n" + ([.files | sort_by(.name)[] | "\(.name)=\(.sha256)\n"] | join(""))' "$tmp/edit.json" > "$tmp/edit-id"
  mkdir -p "$tmp/stage"; jq --arg set "$(sha256_of "$tmp/edit-id")" '.model_set = $set' "$tmp/edit.json" > "$tmp/stage/embedding-model.json"
  tar -czf "$tmp/$1.tar.gz" -C "$tmp/stage" .
}
reject contract0 '.contract = 0'
reject contractneg '.contract = -1'
reject contractfrac '.contract = 1.5'
reject longname ".files[1].name = \"z$(printf 'a%.0s' $(seq 128))\""
reject casedup '.files[1].name = "MODEL.onnx"'

B="$tmp/bucket/local/models/$set_id"
fail=0
run() { # <label> <want-rc> <archive>
  local rc
  set +e
  BUCKET="$tmp/bucket" UPSTREAM="$up" PATH="$tmp/bin:$PATH" R2="--endpoint-url https://r2.test" DEST="s3://bucket/local" \
    bash "$sh" "$tmp/$3.tar.gz" >"$tmp/out" 2>&1
  rc=$?
  set -e
  if [ "$rc" != "$2" ]; then echo "FAIL: $1 -> rc=$rc (want $2): $(cat "$tmp/out")"; fail=1; fi
}
log() { cat "$tmp/bucket/.log" 2>/dev/null | sed 's|local/models/[0-9a-f]*/||' | tr '\n' ' '; }
fresh() { rm -rf "$tmp/bucket" "$up/.requests"; mkdir -p "$tmp/bucket/local"; }

fresh
run "first publication" 0 good
[ "$(log)" = "model.onnx vocab.txt " ] || { echo "FAIL: uploads each file under models/<set>/: $(log)"; fail=1; }
cmp -s "$B/model.onnx" "$up/model.onnx" || { echo "FAIL: stored bytes differ from upstream"; fail=1; }

rm "$tmp/bucket/.log" "$up/.requests"
run "a finished re-run" 0 good
[ -z "$(log)" ] && [ ! -e "$up/.requests" ] || { echo "FAIL: re-run uploaded $(log) / downloaded"; fail=1; }

fresh; mkdir -p "$B"; cp "$up/vocab.txt" "$B/"
run "a verified stored file is reused" 0 good
[ "$(log)" = "model.onnx " ] || { echo "FAIL: re-uploaded the stored file: $(log)"; fail=1; }
grep -q vocab "$up/.requests" && { echo "FAIL: downloaded a file that was stored"; fail=1; }

fresh; mkdir -p "$B"; printf 'wrong bytes' > "$B/vocab.txt"
run "a stored file that does not verify refuses" 1 good
grep -q "Delete local/models/$set_id/vocab.txt by hand" "$tmp/out" || { echo "FAIL: remove-by-hand message: $(cat "$tmp/out")"; fail=1; }
cmp -s "$B/vocab.txt" "$up/vocab.txt" && { echo "FAIL: replaced a stored object"; fail=1; }

fresh; printf 'tampered' > "$up/vocab.txt.bad"; cp "$up/vocab.txt" "$tmp/vocab.keep"; cp "$up/vocab.txt.bad" "$up/vocab.txt"
run "upstream bytes that do not verify are never uploaded" 1 good
[ ! -e "$B/vocab.txt" ] || { echo "FAIL: uploaded unverified upstream bytes"; fail=1; }
cp "$tmp/vocab.keep" "$up/vocab.txt"

fresh
run "a set id that does not match its files refuses" 1 badid
[ -z "$(log)" ] || { echo "FAIL: uploaded under a wrong set id"; fail=1; }

fresh
run "an archive without a model publishes nothing" 0 nomodel
grep -q "nothing to publish" "$tmp/out" || { echo "FAIL: no-model message: $(cat "$tmp/out")"; fail=1; }
[ -z "$(log)" ] && [ ! -e "$up/.requests" ] || { echo "FAIL: a model-less archive uploaded or downloaded"; fail=1; }

fresh
run "an archive tar cannot read refuses" 1 notar

for bad in contract0 contractneg contractfrac longname casedup; do
  fresh
  run "an entry kcap rejects refuses: $bad" 1 "$bad"
  grep -q "malformed" "$tmp/out" || { echo "FAIL: $bad refused for another reason: $(cat "$tmp/out")"; fail=1; }
  [ -z "$(log)" ] && [ ! -e "$up/.requests" ] || { echo "FAIL: $bad uploaded or downloaded before refusing"; fail=1; }
done

fresh; touch "$tmp/bucket/.broken"
run "an R2 failure is never read as absent" 1 good
[ ! -e "$up/.requests" ] || { echo "FAIL: downloaded during an R2 outage"; fail=1; }

[ "$fail" -eq 0 ] && echo "ok" || exit 1
