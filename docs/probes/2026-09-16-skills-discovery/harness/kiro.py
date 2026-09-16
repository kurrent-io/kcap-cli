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
    # None keeps whatever the cloned default agent declares; a tuple replaces it.
    agent_resources: tuple[str, ...] | None = None

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
        """A custom agent cloned from the default one, as kcap's installer does: a minimal agent
        definition has no tools, and a model that cannot read the listed SKILL.md never loads it."""
        agents = sb.config_root / "agents"
        agents.mkdir(parents=True, exist_ok=True)
        path = agents / f"{name}.json"
        data: dict = {}
        if self.binary_path():
            env = dict(sb.env, EDITOR="true", VISUAL="true")
            subprocess.run([self.binary_path(), "agent", "create", name, "--from", "kiro_default"], env=env,
                           capture_output=True, text=True, timeout=120)
            if path.exists():
                try:
                    data = json.loads(path.read_text())
                except json.JSONDecodeError:
                    data = {}
        if not data:
            data = {"name": name, "description": "probe agent", "tools": ["*"]}
        data["name"] = name
        if self.agent_resources is not None:
            data["resources"] = list(self.agent_resources)
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
            res = acp_ask([binary, "acp", "--trust-all-tools", *agent], sb.repo, sb.env, prompt,
                          sb.root / "kiro-acp.stderr.log", self.turn_timeout)
            generic = " ".join(n for n in res.notes.split() if not n.startswith("tools_used="))
            res.notes = (generic + " " + classify_kiro_tools(res.raw)).strip()
            return res
        argv = [binary, "chat", "--no-interactive", "--trust-all-tools", *agent, prompt]
        res = print_ask(argv, sb.repo, sb.env, sb.root / "kiro.stderr.log", self.turn_timeout)
        # Plain chat output carries the answer only, so tool use is unobservable in this mode.
        res.notes = (res.notes + " tools=unobserved").strip()
        return res


def classify_kiro_tools(raw: str) -> str:
    """Kiro lists a skill with its path and the model reads that file, so a read whose location
    is a SKILL.md path is the native mechanism; a shell or search tool makes a sighting
    inconclusive."""
    calls: dict[str, dict] = {}
    try:
        frames = json.loads(raw)
    except json.JSONDecodeError:
        frames = []
    for f in frames:
        fr = f.get("frame") or {}
        if fr.get("method") != "session/update":
            continue
        upd = (fr.get("params") or {}).get("update") or {}
        if not upd.get("sessionUpdate", "").startswith("tool_call"):
            continue
        call = calls.setdefault(upd.get("toolCallId") or "", {"kind": "", "paths": []})
        if upd.get("kind"):
            call["kind"] = upd["kind"]
        call["paths"] += [loc.get("path", "") for loc in upd.get("locations") or [] if isinstance(loc, dict)]
    skill_reads = searches = other = 0
    for call in calls.values():
        if call["kind"] == "read" and any(p.endswith("SKILL.md") for p in call["paths"]):
            skill_reads += 1
        elif call["kind"] in ("execute", "search", "fetch"):
            searches += 1
        else:
            other += 1
    return f"tools_used={searches + other} skill_reads={skill_reads} searches={searches}"


class KiroAgentBareAdapter(KiroAdapter):
    """A cloned default agent with its resources emptied: does it still inherit the skills?"""

    entry = "kiro-agent-bare"
    agent_name = "probe-bare"
    agent_resources = ()


class KiroAgentSkillsAdapter(KiroAdapter):
    """A cloned default agent declaring the skills resource explicitly."""

    entry = "kiro-agent-skills"
    agent_name = "probe-skills"
    agent_resources = ("skill://.kiro/skills/*/SKILL.md",)
