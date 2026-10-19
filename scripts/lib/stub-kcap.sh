#!/usr/bin/env bash
# A bundled-CLI stand-in for assert-app-plugin.sh's smoke step. It refuses any call the real
# assertion would not make, and a HOME outside its working directory, which would let a real
# install write the caller's git config.
write_stub_kcap() {
  cat > "$1" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
for var in KCAP_PLUGIN_DIR CODEX_HOME CLAUDE_CONFIG_DIR; do
  [ -z "${!var+set}" ] || { echo "stub kcap: $var leaked in" >&2; exit 2; }
done
cwd="$PWD"
if command -v cygpath >/dev/null 2>&1; then cwd="$(cygpath -m "$PWD")"; fi
case "$HOME" in "$cwd"/*) ;; *) echo "stub kcap: HOME is not isolated: $HOME" >&2; exit 2 ;; esac
case "$*" in
  "plugin install --project --no-update-check")
    mkdir -p .claude
    printf '{}\n' > .claude/settings.local.json ;;
  "plugin install --codex --skip-codex-network-access --project --no-update-check")
    mkdir -p .codex
    printf '{"hooks":{"SessionStart":[{"hooks":[{"type":"command","command":"kcap hook --codex"}]}]}}\n' > .codex/hooks.json ;;
  *) echo "stub kcap: unexpected args: $*" >&2; exit 2 ;;
esac
STUB
  chmod +x "$1"
}
