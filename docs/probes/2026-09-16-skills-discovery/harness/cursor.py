from __future__ import annotations

import json
import os
import subprocess
from pathlib import Path

from harness.base import Adapter, AskResult, ClassifiedSession, HookInfo, Session
from lib.acp_driver import AcpSession, acp_ask
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
    modes = ("print", "daemon", "tui")
    can_resume = True
    tui_exit = ("/exit\r", "\x03", "\x03")

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
        return HookInfo(mechanism="project .cursor/hooks.json workspaceOpen pluginPaths",
                        config_path=str(self._hooks(sb)), target=target)

    def acp_argv(self) -> list[str]:
        return [self.binary_path() or self.binary, "acp", "--trust"]

    def tui_argv(self, sb: Sandbox) -> list[str] | None:
        return [self.binary_path() or self.binary, "--trust"]

    def open_session(self, sb: Sandbox, mode: str) -> Session | None:
        if mode != "daemon":
            return super().open_session(sb, mode)
        inner = AcpSession(self.acp_argv(), sb.cwd, sb.env, sb.root / "cursor-acp.stderr.log", self.turn_timeout)
        inner.start()
        return ClassifiedSession(inner, lambda r: classify_cursor_tools(r.raw))

    def session_id(self, res: AskResult) -> str | None:
        for line in res.raw.splitlines():
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                continue
            if isinstance(obj.get("session_id"), str):
                return obj["session_id"]
        return None

    def resume(self, sb: Sandbox, session_id: str, prompt: str) -> AskResult | None:
        return self._print(sb, prompt, [f"--resume={session_id}"])

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        if mode == "daemon":
            res = acp_ask(self.acp_argv(), sb.cwd, sb.env, prompt, sb.root / "cursor-acp.stderr.log",
                          self.turn_timeout)
            generic = " ".join(n for n in res.notes.split() if not n.startswith("tools_used="))
            res.notes = (generic + " " + classify_cursor_tools(res.raw)).strip()
            return res
        return self._print(sb, prompt)

    def _print(self, sb: Sandbox, prompt: str, extra: list[str] = ()) -> AskResult:
        binary = self.binary_path() or self.binary

        def extract(raw: str) -> str:
            texts = []
            for line in raw.splitlines():
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if obj.get("type") == "assistant":
                    for part in (obj.get("message") or {}).get("content") or []:
                        if isinstance(part, dict) and isinstance(part.get("text"), str):
                            texts.append(part["text"])
                elif obj.get("type") == "result" and isinstance(obj.get("result"), str):
                    texts.append(obj["result"])
            return "\n".join(texts)

        # Print mode ends every tool-using turn with "WritableIterable is closed" and exit 1; the
        # json and text formats then print nothing, while stream-json has already streamed the
        # answer, so the answer is read from the stream and the exit code is recorded beside it.
        argv = [binary, "-p", "--output-format", "stream-json", "--trust", "--force", *extra, prompt]
        res = print_ask(argv, sb.cwd, sb.env, sb.root / "cursor.stderr.log", self.turn_timeout, extract=extract)
        res.notes = (res.notes + " " + classify_cursor_stream(res.raw)).strip()
        return res


class CursorUserHooksAdapter(CursorAdapter):
    """The user-level `~/.cursor/hooks.json`, where kcap installs its own hooks: the probe entry is
    merged into the real file for one turn and the original file is restored afterwards."""

    entry = "cursor-userhooks"

    def __init__(self) -> None:
        self._backup: bytes | None = None
        self._touched = False

    def _user_hooks(self) -> Path:
        return Path.home() / ".cursor" / "hooks.json"

    def _merge(self, sb: Sandbox, event: str, command: str) -> HookInfo:
        path = self._user_hooks()
        path.parent.mkdir(parents=True, exist_ok=True)
        self._backup = path.read_bytes() if path.exists() else None
        self._touched = True
        try:
            data = json.loads(self._backup.decode()) if self._backup else {}
        except json.JSONDecodeError:
            data = {}
        data.setdefault("version", 1)
        data.setdefault("hooks", {}).setdefault(event, []).append({"command": command})
        path.write_text(json.dumps(data, indent=2) + "\n")
        return HookInfo(mechanism=f"user ~/.cursor/hooks.json {event}", config_path=str(path))

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        return self._merge(sb, "sessionStart", str(script))

    def install_registration(self, sb: Sandbox, skill_file: Path, body: str) -> HookInfo | None:
        info = super().install_registration(sb, skill_file, body)
        script = json.loads(Path(info.config_path).read_text())["hooks"]["workspaceOpen"][0]["command"]
        Path(info.config_path).unlink()
        return self._merge(sb, "workspaceOpen", script)

    def cleanup_hook(self, sb: Sandbox) -> None:
        if not self._touched:
            return
        path = self._user_hooks()
        if self._backup is None:
            path.unlink(missing_ok=True)
        else:
            path.write_bytes(self._backup)
        self._touched = False

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        try:
            return super().ask(sb, mode, prompt)
        finally:
            self.cleanup_hook(sb)


def classify_cursor_stream(raw: str) -> str:
    """The stream-json events of print mode: a tool_call whose input names a SKILL.md path is the
    native load; any shell or search tool call makes a sighting inconclusive."""
    skill_reads = searches = other = 0
    for line in raw.splitlines():
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            continue
        if obj.get("type") != "tool_call" or obj.get("subtype") not in (None, "started"):
            continue
        call = json.dumps(obj.get("tool_call") or obj)
        if "SKILL.md" in call and ("read" in call.lower()):
            skill_reads += 1
        elif any(k in call for k in ("shellToolCall", "grep", "glob", "find", "ls")):
            searches += 1
        else:
            other += 1
    return f"tools_used={searches + other} skill_reads={skill_reads} searches={searches}"


def classify_cursor_tools(raw: str) -> str:
    """Cursor lists a skill with its path and the model reads that file, so a read of a SKILL.md
    path is the native mechanism; a shell or search tool is what makes a sighting inconclusive."""
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
        call = calls.setdefault(upd.get("toolCallId") or "", {"kind": "", "path": ""})
        if upd.get("kind"):
            call["kind"] = upd["kind"]
        path = (upd.get("rawInput") or {}).get("path")
        if isinstance(path, str):
            call["path"] = path
    skill_reads = searches = other = 0
    for call in calls.values():
        if call["kind"] == "read" and call["path"].endswith("SKILL.md"):
            skill_reads += 1
        elif call["kind"] in ("execute", "search", "fetch"):
            searches += 1
        else:
            other += 1
    return f"tools_used={searches + other} skill_reads={skill_reads} searches={searches}"
