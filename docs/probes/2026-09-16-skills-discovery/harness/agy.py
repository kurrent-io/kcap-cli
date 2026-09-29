from __future__ import annotations

import json
import os
import shutil
from pathlib import Path

from harness.base import Adapter, AskResult, HookInfo, Session
from lib.isolation import Sandbox
from lib.print_driver import print_ask
from lib.probe_skill import NAME_RE


class AgyAdapter(Adapter):
    """Antigravity CLI. The OAuth session lives in the OS keyring and is not found from a private
    HOME (each such launch starts a new sign-in), so agy runs against the real home with kcap's own
    hooks stood down, and the probe hook lives inside the sandbox repository as a workspace plugin."""

    entry = "agy"
    harness = "antigravity"
    binary = "agy"
    lever = "AGY_PROBE_UNUSED"
    native_root = ".agents/skills"
    documented_roots = frozenset({".agents/skills", ".agent/skills"})
    flat_skill_layout = True
    modes = ("print", "tui")
    can_resume = True
    tui_exit = ("/exit\r", "\x03", "\x03")

    def real_root(self) -> Path | None:
        return Path.home()

    def prepare(self, sb: Sandbox) -> None:
        sb.env.pop(self.lever, None)
        sb.env["HOME"] = os.environ.get("HOME", str(Path.home()))
        sb.env["KCAP_SKIP"] = "1"

    def check_auth(self, sb: Sandbox) -> bool | None:
        return (Path.home() / ".gemini" / "antigravity-cli" / "settings.json").exists() or None

    def hook_dir(self, sb: Sandbox) -> Path:
        return sb.repo / ".agents" / "plugins" / "probe"

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        plugin = self.hook_dir(sb)
        plugin.mkdir(parents=True, exist_ok=True)
        (plugin / "plugin.json").write_text(json.dumps({"name": "probe", "version": "1.0.0", "description": "probe"}, indent=2) + "\n")
        hooks = plugin / "hooks.json"
        hooks.write_text(json.dumps({"probe": {
            "PreInvocation": [{"type": "command", "command": str(script), "timeout": 15000}],
        }}, indent=2) + "\n")
        return HookInfo(mechanism="workspace .agents/plugins PreInvocation", config_path=str(hooks))

    def tui_argv(self, sb: Sandbox) -> list[str] | None:
        return [self.binary_path() or self.binary, "--add-dir", str(sb.cwd)]

    def open_session(self, sb: Sandbox, mode: str) -> Session | None:
        session = super().open_session(sb, mode)
        if session is None:
            return None
        # Interactive and print mode invoke a skill the same way, so their rows compare.
        return _SlashPromptSession(session, lambda: self.cleanup_hook(sb))

    def session_id(self, res: AskResult) -> str | None:
        for line in res.raw.splitlines():
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                continue
            if isinstance(obj.get("conversation_id"), str):
                return obj["conversation_id"]
        return None

    def resume(self, sb: Sandbox, session_id: str, prompt: str) -> AskResult | None:
        return self.ask(sb, "print", prompt, extra=["--conversation", session_id])

    def ask(self, sb: Sandbox, mode: str, prompt: str, extra: list[str] = ()) -> AskResult:
        def extract(raw: str) -> str:
            texts = []
            for line in raw.splitlines():
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if obj.get("event") == "step_update":
                    su = obj.get("step_update") or {}
                    if su.get("step_type", "agent_response") == "agent_response" and isinstance(su.get("text_delta"), str):
                        texts.append(su["text_delta"])
            return "".join(texts)

        # agy exposes skills as slash commands that expand in print mode: invoking the listed
        # skill by name is its native load, so the prompt leads with it when it names one.
        named = NAME_RE.search(prompt)
        text = slash_prefixed(prompt)
        # The current directory alone is not the workspace in print mode; --add-dir makes it one.
        argv = [self.binary_path() or self.binary, "-p", text, "--output-format", "stream-json",
                "--dangerously-skip-permissions", "--print-timeout", "180s", "--add-dir", str(sb.cwd), *extra]
        try:
            res = print_ask(argv, sb.cwd, sb.env, sb.root / "agy.stderr.log", self.turn_timeout, extract=extract)
        finally:
            self.cleanup_hook(sb)
        if named:
            res.notes = (res.notes + f" slash=/{named.group(0)}").strip()
        return res

    def cleanup_hook(self, sb: Sandbox) -> None:
        return None


def slash_prefixed(prompt: str) -> str:
    """agy exposes skills as slash commands that expand in place: invoking the listed skill by
    name is its native load, so the prompt leads with it when it names one."""
    named = NAME_RE.search(prompt)
    return f"/{named.group(0)} {prompt}" if named else prompt


class _SlashPromptSession(Session):
    def __init__(self, inner: Session, cleanup) -> None:
        self.inner = inner
        self.cleanup = cleanup

    def ask(self, prompt: str) -> AskResult:
        return self.inner.ask(slash_prefixed(prompt))

    def reload(self) -> str | None:
        return self.inner.reload()

    def close(self) -> None:
        try:
            self.inner.close()
        finally:
            self.cleanup()


class AgyDirLayoutAdapter(AgyAdapter):
    entry = "agy-dirlayout"
    flat_skill_layout = False


class AgyCliDirAdapter(AgyAdapter):
    """The CLI documentation's global plugin directory: written into the real home for the turn
    and removed right after, since no private home is available."""

    entry = "agy-clidir"
    flat_skill_layout = False

    def hook_dir(self, sb: Sandbox) -> Path:
        return Path.home() / ".gemini" / "antigravity-cli" / "plugins" / "kcap-probe"

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        info = super().install_startup_hook(sb, script)
        return HookInfo(mechanism="~/.gemini/antigravity-cli/plugins PreInvocation", config_path=info.config_path)

    def cleanup_hook(self, sb: Sandbox) -> None:
        shutil.rmtree(self.hook_dir(sb), ignore_errors=True)
