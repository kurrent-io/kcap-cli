from __future__ import annotations

import subprocess
import sys
import time
from pathlib import Path

from harness.base import Adapter, AskResult, HookInfo, Session
from lib.isolation import Sandbox
from lib.probe_skill import NO_SKILL, TOKEN_RE


class FakeSession(Session):
    def __init__(self, adapter: "FakeAdapter", sb: Sandbox) -> None:
        self.adapter = adapter
        self.sb = sb
        # A frozen catalogue is what a vendor that indexes once looks like; /reload refreshes it.
        self.frozen: list[str] | None = None if adapter.live_catalogue else adapter.catalogue(sb)

    def ask(self, prompt: str) -> AskResult:
        lines = self.frozen if self.frozen is not None else self.adapter.catalogue(self.sb)
        return self.adapter.reply(lines, ["fake", "--session"])

    def reload(self) -> str | None:
        if self.frozen is not None:
            self.frozen = self.adapter.catalogue(self.sb)
        return "/reload"


class FakeAdapter(Adapter):
    entry = "fake"
    harness = "fake"
    binary = "sh"
    lever = "FAKE_HOME"
    native_root = ".fake/skills"
    documented_roots = frozenset({".fake/skills", ".agents/skills"})
    modes = ("print", "daemon", "tui")
    can_resume = True
    passthrough_env = ("KCAP_FAKE_TUI_BARE",)
    # A screen the fake never writes to is waited on for this long, so the suite stays quick.
    turn_timeout = 15.0
    tui_reload = "/reload"
    tui_ready = 0.3
    # What the fake vendor actually loads, which a subclass keeps while narrowing what it documents.
    read_roots = (".agents/skills", ".fake/skills")
    live_catalogue = True

    def __init__(self) -> None:
        # Keyed by sandbox root: a real vendor's hook lives in that sandbox's own config, and one
        # adapter instance now runs every scenario's arms, each in its own sandbox in turn.
        self._hooks: dict[Path, Path] = {}

    def version(self, env: dict | None = None) -> str:
        return "1.0"

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        self._hooks[sb.root] = script
        return HookInfo(mechanism="fake-startup", config_path=str(script))

    def catalogue(self, sb: Sandbox) -> list[str]:
        lines = []
        for root in self.read_roots:
            for skill_md in sorted((sb.cwd / root).glob("kcap-probe-*/SKILL.md")):
                m = TOKEN_RE.search(skill_md.read_text())
                if m:
                    lines.append(f"{skill_md.parent.name}=PROBE-BODY-{m.group(1)}")
        return lines

    def reply(self, lines: list[str], argv: list[str]) -> AskResult:
        started = time.time()
        text = "\n".join(lines) if lines else NO_SKILL
        return AskResult(reply_text=text, raw=text, argv=argv, started_at=started,
                         first_request_at=started, stderr_path=None, exit_code=0)

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        hook = self._hooks.get(sb.root)
        if hook is not None:
            subprocess.run([str(hook)], input="{}", capture_output=True, text=True, timeout=10)
        return self.reply(self.catalogue(sb), ["fake"])

    def open_session(self, sb: Sandbox, mode: str) -> Session | None:
        if mode == "daemon":
            return FakeSession(self, sb)
        return super().open_session(sb, mode)

    def session_id(self, res: AskResult) -> str | None:
        return "fake-session"

    def resume(self, sb: Sandbox, session_id: str, prompt: str) -> AskResult | None:
        return self.reply(self.catalogue(sb), ["fake", "--resume", session_id])

    def tui_argv(self, sb: Sandbox) -> list[str] | None:
        servers = Path(__file__).resolve().parent.parent / "selftest_servers.py"
        hook = self._hooks.get(sb.root)
        if hook is not None:
            sb.env["KCAP_FAKE_HOOK"] = str(hook)
        if not self.live_catalogue:
            sb.env["KCAP_FAKE_TUI_FROZEN"] = "1"
        return [sys.executable, str(servers), "tui"]
