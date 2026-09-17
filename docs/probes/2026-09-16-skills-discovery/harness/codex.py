from __future__ import annotations

import json
import subprocess
from pathlib import Path

from harness.base import Adapter, AskResult, ClassifiedSession, HookInfo, Session
from lib.appserver_driver import AppServerSession, appserver_ask
from lib.isolation import Sandbox
from lib.print_driver import print_ask

class CodexAdapter(Adapter):
    entry = "codex"
    harness = "codex"
    binary = "codex"
    lever = "CODEX_HOME"
    credential_files = ("auth.json",)
    native_root = ".agents/skills"
    documented_roots = frozenset({".agents/skills", ".codex/skills"})
    modes = ("print", "daemon", "tui")
    can_resume = True
    # A linear transcript is easier to read back than an alternate-screen redraw.
    tui_exit = ("\x03", "\x03", "\x04")

    def real_root(self) -> Path | None:
        return Path.home() / ".codex"

    def prepare(self, sb: Sandbox) -> None:
        (sb.config_root / "config.toml").write_text(
            f'[projects.{json.dumps(str(sb.repo))}]\ntrust_level = "trusted"\n')

    def check_auth(self, sb: Sandbox) -> bool | None:
        out = subprocess.run([self.binary_path() or self.binary, "login", "status"], env=sb.env,
                             capture_output=True, text=True, timeout=60)
        return out.returncode == 0 and "Logged in" in (out.stdout + out.stderr)

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        hooks = sb.config_root / "hooks.json"
        hooks.write_text(json.dumps({"hooks": {"SessionStart": [
            {"hooks": [{"type": "command", "command": str(script), "timeout": 30}]}
        ]}}, indent=2) + "\n")
        return HookInfo(mechanism="hooks.json SessionStart", config_path=str(hooks))

    def tui_argv(self, sb: Sandbox) -> list[str] | None:
        return [self.binary_path() or self.binary, "--sandbox", "read-only", "-a", "never", "--no-alt-screen"]

    def open_session(self, sb: Sandbox, mode: str) -> Session | None:
        if mode != "daemon":
            return super().open_session(sb, mode)
        inner = AppServerSession(self.binary_path() or self.binary, sb.cwd, sb.env,
                                 sb.root / "codex-appserver.stderr.log", self.turn_timeout)
        inner.start()
        return ClassifiedSession(inner, lambda r: classify_tool_items(appserver_items(r.raw)))

    def session_id(self, res: AskResult) -> str | None:
        for line in res.raw.splitlines():
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                continue
            if obj.get("thread_id"):
                return obj["thread_id"]
        return None

    def resume(self, sb: Sandbox, session_id: str, prompt: str) -> AskResult | None:
        return self._exec(sb, prompt, resume=session_id)

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        binary = self.binary_path() or self.binary
        if mode == "daemon":
            res = appserver_ask(binary, sb.cwd, sb.env, prompt, sb.root / "codex-appserver.stderr.log",
                                self.turn_timeout)
            # The driver's flat tool count is replaced by the classification that knows a listed read.
            generic = " ".join(n for n in res.notes.split() if not n.startswith("tools_used="))
            res.notes = (generic + " " + classify_tool_items(appserver_items(res.raw))).strip()
            return res
        return self._exec(sb, prompt)

    def _exec(self, sb: Sandbox, prompt: str, resume: str | None = None) -> AskResult:
        binary = self.binary_path() or self.binary
        last = sb.root / "last-message.txt"
        # A file left by an earlier turn would be read as this turn's answer.
        last.unlink(missing_ok=True)
        head = [binary, "exec", *(["resume", resume] if resume else [])]
        argv = head + ["--json", "--skip-git-repo-check", "--sandbox", "read-only", "--color", "never",
                       "--dangerously-bypass-hook-trust", "--output-last-message", str(last), "-"]

        def extract(raw: str) -> str:
            if last.exists():
                return last.read_text().strip()
            texts = []
            for line in raw.splitlines():
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError:
                    continue
                item = obj.get("item") or {}
                if obj.get("type") == "item.completed" and item.get("type") == "agent_message":
                    texts.append(item.get("text", ""))
            return "\n".join(texts)

        res = print_ask(argv, sb.cwd, sb.env, sb.root / "codex.stderr.log", self.turn_timeout,
                        stdin_text=prompt, extract=extract)
        res.notes = (res.notes + " " + classify_tool_items(exec_items(res.raw))).strip()
        return res


SEARCH_MARKERS = ("find ", "grep", "rg ", "ls ", "locate ", "*", "tree ")


def exec_items(raw: str) -> list[tuple[str, str]]:
    """(item type, command) for every completed item in `codex exec --json` output."""
    items = []
    for line in raw.splitlines():
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            continue
        item = obj.get("item") or {}
        if obj.get("type") == "item.completed":
            items.append((item.get("type") or "", item.get("command") or ""))
    return items


def appserver_items(raw: str) -> list[tuple[str, str]]:
    """(item type, command) for every item/completed notification in an app-server frame log."""
    items = []
    try:
        frames = json.loads(raw)
    except json.JSONDecodeError:
        return items
    for f in frames:
        fr = f.get("frame") or {}
        if fr.get("method") == "item/completed":
            item = (fr.get("params") or {}).get("item") or {}
            items.append((item.get("type") or "", item.get("command") or ""))
    return items


def classify_tool_items(items: list[tuple[str, str]]) -> str:
    """Codex loads a listed skill by reading its file, so a `cat` of a SKILL.md path is the native
    mechanism at work; a search command is what would make a sighting inconclusive."""
    skill_reads = searches = other = 0
    for kind, command in items:
        if kind in ("agent_message", "agentMessage", "reasoning", "userMessage", "user_message", "error", ""):
            continue
        if any(m in command for m in SEARCH_MARKERS):
            searches += 1
        elif "SKILL.md" in command:
            skill_reads += 1
        else:
            other += 1
    return f"tools_used={searches + other} skill_reads={skill_reads} searches={searches}"
