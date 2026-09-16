from __future__ import annotations

import json
import subprocess
from pathlib import Path

from harness.base import Adapter, AskResult, HookInfo
from lib.acp_driver import acp_ask
from lib.isolation import Sandbox
from lib.print_driver import print_ask


class CursorAdapter(Adapter):
    entry = "cursor"
    harness = "cursor"
    binary = "cursor-agent"
    lever = "HOME"
    credential_files = (".config/cursor-agent/auth.json", ".cursor/auth.json", ".cursor/cli-config.json")
    native_root = ".cursor/skills"
    documented_roots = frozenset({".cursor/skills", ".agents/skills", ".claude/skills", ".codex/skills"})

    def real_root(self) -> Path | None:
        return Path.home()

    def check_auth(self, sb: Sandbox) -> bool | None:
        out = subprocess.run([self.binary_path() or self.binary, "status"], env=sb.env, capture_output=True,
                             text=True, timeout=60)
        return out.returncode == 0 and "Logged in" in (out.stdout + out.stderr)

    def _hooks(self, sb: Sandbox) -> Path:
        d = sb.config_root / ".cursor"
        d.mkdir(parents=True, exist_ok=True)
        return d / "hooks.json"

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        self._hooks(sb).write_text(json.dumps({"version": 1, "hooks": {"sessionStart": [{"command": str(script)}]}},
                                              indent=2) + "\n")
        return HookInfo(mechanism="hooks.json sessionStart", config_path=str(self._hooks(sb)))

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
        return HookInfo(mechanism="hooks.json workspaceOpen pluginPaths", config_path=str(self._hooks(sb)))

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
                for key in ("result", "text", "content"):
                    if isinstance(obj.get(key), str):
                        texts.append(obj[key])
            return "\n".join(texts) if texts else raw

        argv = [binary, "-p", "--output-format", "json", "--trust", "--force", prompt]
        return print_ask(argv, sb.repo, sb.env, sb.root / "cursor.stderr.log", self.turn_timeout, extract=extract)
