from __future__ import annotations

import json
import os
import subprocess
import tempfile
from pathlib import Path

from harness.base import Adapter, AskResult, HookInfo
from lib.isolation import Sandbox
from lib.print_driver import print_ask


class ClaudeAdapter(Adapter):
    entry = "claude"
    harness = "claude"
    binary = "claude"
    lever = "CLAUDE_CONFIG_DIR"
    credential_files = (".credentials.json",)
    native_root = ".claude/skills"
    documented_roots = frozenset({".claude/skills"})
    modes = ("print", "tui")
    can_resume = True
    # An arrow and its Enter are typed a redraw apart; the browser dialog's default keeps the
    # browser tools off, which is what the probe wants.
    tui_dialogs = ((r"Yes, I trust this folder", "\x1b[B\r"), (r"keep browser tools off", "\r"))
    tui_exit = ("/exit\r", "\x03", "\x03")
    tui_ready = 5.0

    def __init__(self) -> None:
        forced = os.environ.get("KCAP_PROBE_CLAUDE_REAL_CONFIG")
        self.real_config = forced == "1" if forced in ("0", "1") else not self._isolated_login_works()

    def real_root(self) -> Path | None:
        return Path(os.environ.get("CLAUDE_CONFIG_DIR") or (Path.home() / ".claude"))

    def _auth_status(self, env: dict) -> bool:
        binary = self.binary_path()
        if binary is None:
            return False
        try:
            out = subprocess.run([binary, "auth", "status"], env=env, capture_output=True, text=True, timeout=60)
            return bool(json.loads(out.stdout).get("loggedIn"))
        except (OSError, subprocess.SubprocessError, json.JSONDecodeError, AttributeError):
            return False

    def _isolated_login_works(self) -> bool:
        """A keychain-held login is keyed by the config root it was minted under, so a private
        root can report logged out; that decides the fallback once per adapter instance."""
        if self.binary_path() is None:
            return True
        with tempfile.TemporaryDirectory(prefix="skprobe-claude-") as d:
            env = {k: v for k, v in os.environ.items() if k in ("PATH", "HOME", "TERM", "USER", "LOGNAME")}
            env["CLAUDE_CONFIG_DIR"] = d
            return self._auth_status(env)

    def prepare(self, sb: Sandbox) -> None:
        settings = sb.config_root / "settings.json"
        if not settings.exists():
            settings.write_text("{}\n")
        if self.real_config:
            # The keychain entry is keyed by the config root, and naming the default root
            # explicitly counts as a different one: the variable must be absent, not defaulted.
            # The hooks still come from the sandbox's settings file, passed with --settings;
            # KCAP_SKIP stands kcap's own hooks down should any user-level one still load.
            sb.env.pop("CLAUDE_CONFIG_DIR", None)
            sb.env["KCAP_SKIP"] = "1"
            return
        (sb.config_root / ".claude.json").write_text(json.dumps({
            "hasCompletedOnboarding": True,
            "projects": {str(sb.repo): {"hasTrustDialogAccepted": True}},
        }, indent=2) + "\n")

    def check_auth(self, sb: Sandbox) -> bool | None:
        return self._auth_status(sb.env)

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        settings = sb.config_root / "settings.json"
        data = json.loads(settings.read_text() or "{}")
        data.setdefault("hooks", {})["SessionStart"] = [
            {"hooks": [{"type": "command", "command": str(script), "timeout": 10}]}
        ]
        settings.write_text(json.dumps(data, indent=2) + "\n")
        where = "--settings file over the real config root" if self.real_config else "isolated settings.json"
        return HookInfo(mechanism=f"{where} hooks.SessionStart", config_path=str(settings))

    def _settings_argv(self, sb: Sandbox) -> list[str]:
        if self.real_config:
            return ["--setting-sources", "project", "--settings", str(sb.config_root / "settings.json")]
        return ["--setting-sources", "user"]

    def print_argv(self, sb: Sandbox, prompt: str) -> list[str]:
        return [self.binary_path() or self.binary, "-p", prompt, "--output-format", "json", "--max-turns", "4",
                "--strict-mcp-config", "--allowedTools", "Skill"] + self._settings_argv(sb)

    def tui_argv(self, sb: Sandbox) -> list[str] | None:
        return [self.binary_path() or self.binary, "--strict-mcp-config",
                "--allowedTools", "Skill"] + self._settings_argv(sb)

    def session_id(self, res: AskResult) -> str | None:
        try:
            return json.loads(res.raw).get("session_id")
        except (json.JSONDecodeError, AttributeError):
            return None

    def resume(self, sb: Sandbox, session_id: str, prompt: str) -> AskResult | None:
        return self._run(sb, self.print_argv(sb, prompt) + ["--resume", session_id])

    def _run(self, sb: Sandbox, argv: list[str]) -> AskResult:
        def extract(raw: str) -> str:
            obj = json.loads(raw)
            if obj.get("is_error"):
                raise ValueError(f"is_error: {json.dumps(obj)[:300]}")
            return obj.get("result") or ""

        res = print_ask(argv, sb.cwd, sb.env, sb.root / "claude.stderr.log", self.turn_timeout, extract=extract)
        res.notes = (res.notes + f" config={'real' if self.real_config else 'isolated'}").strip()
        return res

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        return self._run(sb, self.print_argv(sb, prompt))
