#!/usr/bin/env bash
# Fails when the channel's full package for this version is already in the bucket, and when the
# bucket cannot say either way: a transient R2 error must never read as "not published".
set -uo pipefail
usage="usage: refuse-published-desktop.sh <channel> <version>"
channel="${1:?$usage}"; version="${2:?$usage}"
key="desktop/$channel/kcap-$version-$channel-full.nupkg"

err="$(aws s3api head-object --endpoint-url "${R2_ENDPOINT:?}" --bucket "${R2_BUCKET:?}" --key "$key" 2>&1 >/dev/null)"
rc=$?
if [ "$rc" -eq 0 ]; then
  echo "::error::$key is already published. A published desktop version is never rebuilt, so cut a new version."
  exit 1
elif grep -qiE '404|Not Found' <<<"$err"; then
  echo "$key is not published yet"
  exit 0
fi
echo "::error::could not determine whether $key is published (aws exit $rc): $err"
exit 1
