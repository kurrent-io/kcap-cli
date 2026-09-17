from __future__ import annotations

import json
from pathlib import Path


def stamp_path(config_root: Path) -> Path:
    return config_root / "probe-hook-fired.json"


def write_hook_script(config_root: Path, skill_file: Path, body: str, stamp: Path, delete: bool = False) -> Path:
    if body and not body.endswith("\n"):
        body += "\n"  # the heredoc terminator must start its own line
    if delete:
        action = f"rm -rf '{skill_file.parent}'\n"
    else:
        action = (
            f"mkdir -p '{skill_file.parent}'\n"
            f"cat > '{skill_file}' <<'KCAP_PROBE_EOF'\n{body}KCAP_PROBE_EOF\n"
        )
    script = config_root / "probe-hook.sh"
    script.write_text(
        "#!/bin/sh\n"
        "set -eu\n"
        f"{action}"
        f"printf '{{\"fired_at\": %s, \"pid\": %s}}\\n' \"$(date +%s)\" \"$$\" > '{stamp}'\n"
        # A vendor that never closes the hook's stdin must not wedge the launch: capture briefly.
        # Backgrounded commands get /dev/null on fd 0 unless explicitly redirected, so the real
        # stdin is saved to fd 3 first and handed to the background reader from there. A vendor
        # that hands the hook no stdin at all must not abort the script under set -e. A terminal
        # is never read: its input is the interactive UI's, and a hook that consumes it leaves the
        # vendor waiting for a keystroke it will never see.
        "if [ ! -t 0 ]; then\n"
        "  { exec 3<&0; } 2>/dev/null || exec 3</dev/null\n"
        f"  ( cat <&3 > '{stamp}.stdin' ) & cat_pid=$!\n"
        "  sleep 2\n"
        "  kill $cat_pid 2>/dev/null || true\n"
        "fi\n"
        "exit 0\n"
    )
    script.chmod(0o755)
    return script


def read_stamp(stamp: Path) -> dict | None:
    if not stamp.exists():
        return None
    data = json.loads(stamp.read_text())
    # `date +%s` resolves to the second, which is too coarse to order the hook against the first
    # request; the stamp file's own mtime is not.
    data["fired_at_mtime"] = stamp.stat().st_mtime
    return data
