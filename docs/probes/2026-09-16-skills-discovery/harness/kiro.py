from __future__ import annotations

import json
import re
import subprocess
from pathlib import Path

from harness.base import Adapter, AskResult, HookInfo
from lib.acp_driver import acp_ask
from lib.isolation import Sandbox
from lib.print_driver import print_ask


class KiroAdapter(Adapter):
    entry = "kiro"
    harness = "kiro"
    binary = "kiro-cli"
    lever = "KIRO_HOME"
    passthrough_env = ("KIRO_API_KEY",)
    native_root = ".kiro/skills"
    documented_roots = frozenset({".kiro/skills"})
    agent_name: str | None = None
    agent_resources: tuple[str, ...] = ()

    def real_root(self) -> Path | None:
        return Path.home() / ".kiro"

    def check_auth(self, sb: Sandbox) -> bool | None:
        # An unauthenticated `chat` opens a browser login instead of answering; whoami says first.
        out = subprocess.run([self.binary_path() or self.binary, "whoami"], env=sb.env, capture_output=True,
                             text=True, timeout=60)
        text = (out.stdout + out.stderr).lower()
        return out.returncode == 0 and "not logged in" not in text and "log in" not in text

    def major(self) -> int:
        m = re.search(r"(\d+)\.\d+", self.version())
        return int(m.group(1)) if m else 0

    def _settings(self, sb: Sandbox) -> Path:
        d = sb.config_root / "settings"
        d.mkdir(parents=True, exist_ok=True)
        return d / "cli.json"

    def _agent_file(self, sb: Sandbox, name: str, hooks: dict | None) -> Path:
        agents = sb.config_root / "agents"
        agents.mkdir(parents=True, exist_ok=True)
        path = agents / f"{name}.json"
        data = {"name": name, "description": "probe agent", "resources": list(self.agent_resources)}
        if hooks:
            data["hooks"] = hooks
        path.write_text(json.dumps(data, indent=2) + "\n")
        return path

    def prepare(self, sb: Sandbox) -> None:
        if self.agent_name:
            self._agent_file(sb, self.agent_name, None)

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        if self.major() >= 3:
            hooks = sb.config_root / "hooks"
            hooks.mkdir(parents=True, exist_ok=True)
            path = hooks / "probe.json"
            path.write_text(json.dumps({"version": "v1", "hooks": [{
                "name": "probe", "trigger": "SessionStart",
                "action": {"type": "command", "command": str(script)}, "timeout": 30, "enabled": True,
            }]}, indent=2) + "\n")
            return HookInfo(mechanism="hooks/*.json SessionStart (cli 3.x)", config_path=str(path))
        # Hooks fire only for the active agent, so the hook rides a custom agent made the default.
        name = self.agent_name or "probe"
        path = self._agent_file(sb, name, {"agentSpawn": [{"command": str(script)}]})
        self._settings(sb).write_text(json.dumps({"chat.defaultAgent": name}) + "\n")
        return HookInfo(mechanism="agent hooks.agentSpawn (cli 2.x)", config_path=str(path))

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        binary = self.binary_path() or self.binary
        agent = ["--agent", self.agent_name] if self.agent_name else []
        if mode == "daemon":
            return acp_ask([binary, "acp", "--trust-all-tools", *agent], sb.repo, sb.env, prompt,
                           sb.root / "kiro-acp.stderr.log", self.turn_timeout)
        argv = [binary, "chat", "--no-interactive", "--trust-all-tools", *agent, prompt]
        return print_ask(argv, sb.repo, sb.env, sb.root / "kiro.stderr.log", self.turn_timeout)


class KiroAgentBareAdapter(KiroAdapter):
    entry = "kiro-agent-bare"
    agent_name = "probe-bare"


class KiroAgentSkillsAdapter(KiroAdapter):
    entry = "kiro-agent-skills"
    agent_name = "probe-skills"
    agent_resources = ("skill://.kiro/skills/*/SKILL.md",)
