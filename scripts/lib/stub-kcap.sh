#!/usr/bin/env bash
# A bundled-CLI stand-in for assert-app-plugin.sh's smoke step. It refuses any call the real
# assertion would not make, and a HOME outside its working directory, which would let a real
# install write the caller's git config.
write_stub_kcap() {
  cat > "$1" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
[ "$*" = "plugin install --project --no-update-check" ] || { echo "stub kcap: unexpected args: $*" >&2; exit 2; }
[ -z "${KCAP_PLUGIN_DIR+set}" ] || { echo "stub kcap: KCAP_PLUGIN_DIR leaked in" >&2; exit 2; }
cwd="$PWD"
if command -v cygpath >/dev/null 2>&1; then cwd="$(cygpath -m "$PWD")"; fi
case "$HOME" in "$cwd"/*) ;; *) echo "stub kcap: HOME is not isolated: $HOME" >&2; exit 2 ;; esac
mkdir -p .claude
printf '{}\n' > .claude/settings.local.json
STUB
  chmod +x "$1"
}
