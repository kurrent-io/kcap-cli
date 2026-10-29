#!/usr/bin/env bash
# Signs every Mach-O in an extracted local-edition bundle, deepest first. Only the single-file
# .NET host gets an entitlement: it JITs, and without allow-jit the hardened runtime stops CoreCLR
# from starting.
# Usage: sign-local-bundle.sh <identity> <keychain> <bundle-dir>
set -euo pipefail
usage="usage: sign-local-bundle.sh <identity> <keychain> <bundle-dir>"
identity="${1:?$usage}"; keychain="${2:?$usage}"; dir="${3:?$usage}"
dir="${dir%/}"
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/macho.sh
source "$here/lib/macho.sh"

for f in kcap-server postgres/bin/postgres; do
  [ -f "$dir/$f" ] && [ ! -L "$dir/$f" ] && [ -x "$dir/$f" ] || { echo "$dir/$f is not an executable regular file" >&2; exit 1; }
done
contained_symlinks "$dir"

others=()
while IFS= read -r f; do
  [ "$f" = "$dir/kcap-server" ] || others+=("$f")
done < <(macho_files "$dir")
[ "${#others[@]}" -gt 0 ] || { echo "no Mach-O besides kcap-server under $dir" >&2; exit 1; }

bash "$here/sign-macos.sh" "$identity" "$keychain" "$here/local-bundle/none.entitlements" "${others[@]}"
bash "$here/sign-macos.sh" "$identity" "$keychain" "$here/local-bundle/kcap-server.entitlements" "$dir/kcap-server"
echo "signed $(( ${#others[@]} + 1 )) Mach-O files" >&2
