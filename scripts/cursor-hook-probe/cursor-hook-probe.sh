#!/bin/sh
# cursor-hook-probe.sh — capture one Cursor hook invocation to disk.
#
# Cursor invokes this script per hook event. The event name is the first argv,
# the JSON payload arrives on stdin. We dump:
#   - stdin (Cursor's JSON payload, verbatim)     → <ts>-<event>-<pid>.stdin.json
#   - argv, cwd, filtered env                     → <ts>-<event>-<pid>.meta.txt
#
# Output dir: $HOME/kcap-cursor-hook-probe/
#
# Exit 0 always — never block Cursor on probe issues.

set -u

DIR="${HOME}/kcap-cursor-hook-probe"
mkdir -p "$DIR" 2>/dev/null || exit 0

EVENT="${1:-unknown}"
TS=$(date -u +%Y%m%dT%H%M%SZ 2>/dev/null || echo "no-date")
PID=$$
BASE="$DIR/${TS}-${EVENT}-${PID}"

# Stdin first (Cursor's payload — the most valuable thing to capture).
cat > "${BASE}.stdin.json" 2>/dev/null || true

# Sidecar metadata: argv, cwd, env filtered to relevant prefixes.
{
    echo "# event"
    echo "  ${EVENT}"
    echo
    echo "# argv (\$0..\$#)"
    echo "  \$0: $0"
    i=1
    for a in "$@"; do
        printf '  $%d: %s\n' "$i" "$a"
        i=$((i + 1))
    done
    echo
    echo "# cwd"
    echo "  $(pwd 2>/dev/null || echo '?')"
    echo
    echo "# env (filtered)"
    env 2>/dev/null \
        | grep -E '^(CURSOR_|KCAP_|HOME=|PATH=|PWD=|SHELL=|USER=|LOGNAME=|TERM=)' \
        | sort \
        | sed 's/^/  /'
} > "${BASE}.meta.txt" 2>/dev/null || true

exit 0
