from __future__ import annotations

import json
import subprocess
from pathlib import Path

from harness.base import Adapter, AskResult, HookInfo
from lib.acp_driver import acp_ask
from lib.isolation import Sandbox
from lib.print_driver import print_ask


class CopilotAdapter(Adapter):
    entry = "copilot"
    harness = "copilot"
    binary = "copilot"
    lever = "COPILOT_HOME"
    native_root = ".github/skills"
    documented_roots = frozenset({".github/skills", ".agents/skills", ".claude/skills"})

    def __init__(self) -> None:
        try:
            out = subprocess.run(["gh", "auth", "token"], capture_output=True, text=True, timeout=30)
            self.token = out.stdout.strip() if out.returncode == 0 else ""
        except (OSError, subprocess.SubprocessError):
            self.token = ""

    def real_root(self) -> Path | None:
        return Path.home() / ".copilot"

    def prepare(self, sb: Sandbox) -> None:
        if self.token:
            sb.env["COPILOT_GITHUB_TOKEN"] = self.token

    def check_auth(self, sb: Sandbox) -> bool | None:
        return bool(self.token)

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        hooks = sb.config_root / "hooks"
        hooks.mkdir(parents=True, exist_ok=True)
        path = hooks / "probe.json"
        path.write_text(json.dumps({"version": 1, "hooks": {"sessionStart": [
            {"type": "command", "command": str(script), "timeoutSec": 30}
        ]}}, indent=2) + "\n")
        return HookInfo(mechanism="hooks/*.json sessionStart", config_path=str(path))

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        binary = self.binary_path() or self.binary
        if mode == "daemon":
            res = acp_ask([binary, "--acp", "--stdio"], sb.repo, sb.env, prompt, sb.root / "copilot-acp.stderr.log",
                          self.turn_timeout)
            generic = " ".join(n for n in res.notes.split() if not n.startswith("tools_used="))
            res.notes = (generic + " " + classify_copilot_tools(acp_tool_calls(res.raw))).strip()
            return res
        argv = [binary, "-p", prompt, "--allow-all-tools", "--output-format", "json"]

        def extract(raw: str) -> str:
            texts = []
            for line in raw.splitlines():
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if obj.get("type") == "assistant.message":
                    content = (obj.get("data") or {}).get("content")
                    if isinstance(content, str) and content.strip():
                        texts.append(content)
            return "\n".join(texts)

        res = print_ask(argv, sb.repo, sb.env, sb.root / "copilot.stderr.log", self.turn_timeout, extract=extract)
        res.notes = (res.notes + " " + classify_copilot_tools(print_tool_calls(res.raw))).strip()
        return res


SEARCH_TOOLS = ("bash", "shell", "grep", "glob", "find", "view", "read", "ls")


def print_tool_calls(raw: str) -> list[tuple[str, str]]:
    """(tool name, title) for every tool.execution_start event in `copilot --output-format json`."""
    calls = []
    for line in raw.splitlines():
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            continue
        if obj.get("type") == "tool.execution_start":
            data = obj.get("data") or {}
            calls.append((data.get("toolName") or "", json.dumps(data.get("arguments") or {})))
    return calls


def acp_tool_calls(raw: str) -> list[tuple[str, str]]:
    """(kind, title) for every tool_call update in an ACP frame log."""
    calls = []
    try:
        frames = json.loads(raw)
    except json.JSONDecodeError:
        return calls
    for f in frames:
        fr = f.get("frame") or {}
        if fr.get("method") != "session/update":
            continue
        upd = (fr.get("params") or {}).get("update") or {}
        if upd.get("sessionUpdate") == "tool_call":
            title = upd.get("title") or ""
            name = "skill" if title.startswith("Using skill:") else (upd.get("kind") or "")
            calls.append((name, title))
    return calls


def classify_copilot_tools(calls: list[tuple[str, str]]) -> str:
    """Copilot loads a listed skill through its own `skill` tool; any other tool is either a
    search for the file or unrelated work, and both make a sighting inconclusive."""
    skill_loads = searches = other = 0
    for name, _ in calls:
        if name == "skill":
            skill_loads += 1
        elif name in SEARCH_TOOLS:
            searches += 1
        else:
            other += 1
    return f"tools_used={searches + other} skill_loads={skill_loads} searches={searches}"

    def list_catalogue(self, sb: Sandbox) -> str | None:
        out = subprocess.run([self.binary_path() or self.binary, "skill", "list", "--json"], cwd=str(sb.repo),
                             env=sb.env, capture_output=True, text=True, timeout=120)
        return out.stdout
