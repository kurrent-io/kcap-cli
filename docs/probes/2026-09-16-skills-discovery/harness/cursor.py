from __future__ import annotations

import json
import os
import subprocess
from pathlib import Path

from harness.base import Adapter, AskResult, HookInfo
from lib.acp_driver import acp_ask
from lib.isolation import Sandbox
from lib.print_driver import print_ask


class CursorAdapter(Adapter):
    """Cursor CLI. Its login is keyring-held and not found from a private HOME, so the CLI runs
    against the real home with kcap's hooks stood down; the probe hook is the sandbox repository's
    own `.cursor/hooks.json`, which Cursor reads beside the user-level one."""

    entry = "cursor"
    harness = "cursor"
    binary = "cursor-agent"
    lever = "CURSOR_PROBE_UNUSED"
    native_root = ".cursor/skills"
    documented_roots = frozenset({".cursor/skills", ".agents/skills", ".claude/skills", ".codex/skills"})

    def real_root(self) -> Path | None:
        return Path.home()

    def prepare(self, sb: Sandbox) -> None:
        sb.env.pop(self.lever, None)
        sb.env["HOME"] = os.environ.get("HOME", str(Path.home()))
        sb.env["KCAP_SKIP"] = "1"

    def check_auth(self, sb: Sandbox) -> bool | None:
        out = subprocess.run([self.binary_path() or self.binary, "status"], env=sb.env, capture_output=True,
                             text=True, timeout=60)
        return out.returncode == 0 and "Logged in" in (out.stdout + out.stderr)

    def _hooks(self, sb: Sandbox) -> Path:
        d = sb.repo / ".cursor"
        d.mkdir(parents=True, exist_ok=True)
        return d / "hooks.json"

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        self._hooks(sb).write_text(json.dumps({"version": 1, "hooks": {"sessionStart": [{"command": str(script)}]}},
                                              indent=2) + "\n")
        return HookInfo(mechanism="project .cursor/hooks.json sessionStart", config_path=str(self._hooks(sb)))

    def install_registration(self, sb: Sandbox, skill_file: Path, body: str) -> HookInfo | None:
        plugin = sb.root / "plugin"
        (plugin / ".cursor-plugin").mkdir(parents=True, exist_ok=True)
        (plugin / ".cursor-plugin" / "plugin.json").write_text(json.dumps({"name": "kcap-probe", "version": "1.0.0"}))
        target = plugin / "skills" / skill_file.parent.name / "SKILL.md"
        if body and not body.endswith("\n"):
            body += "\n"
        script = sb.config_root / "probe-workspace-open.sh"
        script.write_text(
            "#!/bin/sh\nset -eu\n"
            f"mkdir -p '{target.parent}'\n"
            f"cat > '{target}' <<'KCAP_PROBE_EOF'\n{body}KCAP_PROBE_EOF\n"
            f"printf '{{\"fired_at\": %s}}\\n' \"$(date +%s)\" > '{sb.config_root / 'probe-hook-fired.json'}'\n"
            f"printf '%s\\n' '{json.dumps({'pluginPaths': [str(plugin)]})}'\n")
        script.chmod(0o755)
        self._hooks(sb).write_text(json.dumps({"version": 1, "hooks": {"workspaceOpen": [{"command": str(script)}]}},
                                              indent=2) + "\n")
        return HookInfo(mechanism="project .cursor/hooks.json workspaceOpen pluginPaths", config_path=str(self._hooks(sb)))

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        binary = self.binary_path() or self.binary
        if mode == "daemon":
            return acp_ask([binary, "acp", "--trust"], sb.repo, sb.env, prompt, sb.root / "cursor-acp.stderr.log",
                           self.turn_timeout)

        def extract(raw: str) -> str:
            texts = []
            for line in raw.splitlines():
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if obj.get("type") == "result" and isinstance(obj.get("result"), str):
                    texts.append(obj["result"])
                elif obj.get("type") == "assistant":
                    msg = obj.get("message") or {}
                    for part in msg.get("content") or []:
                        if isinstance(part, dict) and isinstance(part.get("text"), str):
                            texts.append(part["text"])
            return "\n".join(texts)

        argv = [binary, "-p", "--output-format", "json", "--trust", "--force", prompt]
        return print_ask(argv, sb.repo, sb.env, sb.root / "cursor.stderr.log", self.turn_timeout, extract=extract)
