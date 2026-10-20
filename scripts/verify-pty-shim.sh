#!/usr/bin/env bash
# The daemon hard-imports libpty_shim on every RID but win-x64, so a missing, wrong-arch or
# unloadable shim ships a daemon that crashes on first PTY use. Checks the shim in publish/daemon/
# exists, matches the RID's machine type, and loads and spawns through the production pty_spawn
# path. Run from the repository root.
# Usage: verify-pty-shim.sh <rid> <version>
set -euo pipefail
usage="usage: verify-pty-shim.sh <rid> <version>"
rid="${1:?$usage}"; version="${2:?$usage}"

ext="so"; [[ "$rid" == osx-* ]] && ext="dylib"
shim="publish/daemon/libpty_shim.$ext"
if [ ! -f "$shim" ]; then
  echo "::error::$shim missing on $rid"; ls -la publish/daemon/; exit 1
fi

case "$rid" in
  *-arm64) want_arch="arm64|aarch64" ;;
  *-x64)   want_arch="x86-64|x86_64" ;;
  *)       echo "::error::no machine type known for $rid"; exit 1 ;;
esac
if [[ "$ext" == "dylib" ]]; then
  actual=$(lipo -info "$shim" | grep -oE 'arm64|x86_64' | head -1)
else
  actual=$(readelf -h "$shim" | grep -i "Machine:" | grep -oE 'X86-64|AArch64' | tr '[:upper:]' '[:lower:]')
fi
if ! grep -qEi "$want_arch" <<<"$actual"; then
  echo "::error::libpty_shim machine type ($actual) does not match $rid (expected $want_arch)"; exit 1
fi

# musl build output cannot execute on a glibc host, so a musl RID outside its Alpine container
# gets the machine-type check only.
if [[ "$rid" == *musl* && ! -f /etc/alpine-release ]]; then
  echo "Skipping load-and-call smoke: musl build output cannot execute on a glibc host"
  exit 0
fi

# Built first: a cold `dotnet run` takes far longer than the wait below allows.
dotnet build test/Capacitor.Cli.Tests.Unit.NativeTestHost -r "$rid" -p:MinVerVersionOverride="$version"
out="$(mktemp)"; trap 'rm -f "$out"' EXIT
dotnet run --no-build --project test/Capacitor.Cli.Tests.Unit.NativeTestHost -r "$rid" -- spawn-dummy > "$out" 2>&1 &
host_pid=$!

# spawn-dummy blocks forever by design: poll for its PID= line, then tear it down with a SIGKILL
# fallback so this never hangs on a host that ignores SIGTERM.
ok=0
for _ in $(seq 1 60); do
  if grep -q '^PID=' "$out" 2>/dev/null; then ok=1; break; fi
  if ! kill -0 "$host_pid" 2>/dev/null; then break; fi
  sleep 1
done
pkill -TERM -P "$host_pid" 2>/dev/null || true
kill "$host_pid" 2>/dev/null || true
sleep 1
kill -9 "$host_pid" 2>/dev/null || true
wait "$host_pid" 2>/dev/null || true
cat "$out"
if [[ "$ok" -ne 1 ]]; then
  echo "::error::load-and-call smoke saw no PID= line from spawn-dummy on $rid"; exit 1
fi
