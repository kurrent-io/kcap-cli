from __future__ import annotations

import json
import subprocess
from pathlib import Path

from harness.base import Adapter, AskResult, HookInfo, Session
from lib.acp_driver import AcpSession, acp_ask
from lib.isolation import Sandbox
from lib.print_driver import print_ask


class GeminiAdapter(Adapter):
    entry = "gemini"
    harness = "gemini"
    binary = "gemini"
    lever = "GEMINI_CLI_HOME"
    credential_files = (".gemini/oauth_creds.json", ".gemini/google_accounts.json", ".gemini/installation_id")
    passthrough_env = ("GEMINI_API_KEY",)
    native_root = ".gemini/skills"
    documented_roots = frozenset({".gemini/skills", ".agents/skills"})
    modes = ("print", "daemon", "tui")
    can_resume = True
    tui_exit = ("/quit\r", "\x03", "\x03")

    def real_root(self) -> Path | None:
        return Path.home()

    def _settings(self, sb: Sandbox) -> Path:
        return sb.config_root / ".gemini" / "settings.json"

    def prepare(self, sb: Sandbox) -> None:
        self._settings(sb).parent.mkdir(parents=True, exist_ok=True)
        if not self._settings(sb).exists():
            # A credential is only used once the settings name it as the auth method; an API key
            # in the environment wins over the copied OAuth files.
            auth = "gemini-api-key" if sb.env.get("GEMINI_API_KEY") else "oauth-personal"
            self._settings(sb).write_text(json.dumps({"security": {
                "auth": {"selectedType": auth},
                "folderTrust": {"enabled": False},
            }}, indent=2) + "\n")
        (sb.config_root / ".gemini" / "trustedFolders.json").write_text(json.dumps({str(sb.repo): "TRUST_FOLDER"}) + "\n")

    def check_auth(self, sb: Sandbox) -> bool | None:
        return (sb.config_root / ".gemini" / "oauth_creds.json").exists() or None

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        data = json.loads(self._settings(sb).read_text() or "{}")
        data.setdefault("hooks", {})["SessionStart"] = [
            {"hooks": [{"name": "probe", "type": "command", "command": str(script), "timeout": 30000}]}
        ]
        self._settings(sb).write_text(json.dumps(data, indent=2) + "\n")
        return HookInfo(mechanism="settings.json hooks.SessionStart", config_path=str(self._settings(sb)))

    def acp_argv(self) -> list[str]:
        return [self.binary_path() or self.binary, "--experimental-acp", "--skip-trust", "--approval-mode", "yolo"]

    def tui_argv(self, sb: Sandbox) -> list[str] | None:
        return [self.binary_path() or self.binary, "--approval-mode", "yolo"]

    def open_session(self, sb: Sandbox, mode: str) -> Session | None:
        if mode != "daemon":
            return super().open_session(sb, mode)
        session = AcpSession(self.acp_argv(), sb.cwd, sb.env, sb.root / "gemini-acp.stderr.log", self.turn_timeout)
        session.start()
        return session

    def session_id(self, res: AskResult) -> str | None:
        try:
            sid = json.loads(res.raw).get("sessionId") or json.loads(res.raw).get("session_id")
        except (json.JSONDecodeError, AttributeError):
            sid = None
        # The resume flag also takes "latest", which is the session this sandbox just created.
        return sid or "latest"

    def resume(self, sb: Sandbox, session_id: str, prompt: str) -> AskResult | None:
        return self._print(sb, prompt, ["--resume", session_id])

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        if mode == "daemon":
            return acp_ask(self.acp_argv(), sb.cwd, sb.env, prompt, sb.root / "gemini-acp.stderr.log",
                           self.turn_timeout)
        return self._print(sb, prompt)

    def _print(self, sb: Sandbox, prompt: str, extra: list[str] = ()) -> AskResult:
        argv = [self.binary_path() or self.binary, "-p", prompt, "-o", "json", "--approval-mode", "yolo", *extra]
        return print_ask(argv, sb.cwd, sb.env, sb.root / "gemini.stderr.log", self.turn_timeout,
                         extract=lambda raw: json.loads(raw).get("response") or "")

    def list_catalogue(self, sb: Sandbox) -> str | None:
        out = subprocess.run([self.binary_path() or self.binary, "skills", "list"], cwd=str(sb.repo), env=sb.env,
                             capture_output=True, text=True, timeout=120)
        return out.stdout
