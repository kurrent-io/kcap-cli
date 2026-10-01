#!/usr/bin/env bash
# Copies $DEST/<key> to <file>, with the aws flags in $R2. Exits 44 when the object does not
# exist and 1 on any other failure, so a transient R2 error is never read as "not published".
set -uo pipefail
usage="usage: r2-fetch.sh <key> <file>"
key="${1:?$usage}"; file="${2:?$usage}"

# shellcheck disable=SC2086 # $R2 is a flag list
err="$(aws s3 cp ${R2:?} "${DEST:?}/$key" "$file" 2>&1 >/dev/null)"
rc=$?
if [ "$rc" -eq 0 ]; then
  exit 0
elif grep -qiE '404|Not Found' <<<"$err"; then
  exit 44
fi
echo "$err" >&2
exit 1
