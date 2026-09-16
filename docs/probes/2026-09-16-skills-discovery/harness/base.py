from __future__ import annotations

import shutil
import subprocess
from dataclasses import dataclass, field
from pathlib import Path

from lib.isolation import Sandbox


@dataclass
class HookInfo:
    mechanism: str
    config_path: str


@dataclass
class AskResult:
    reply_text: str
    raw: str
    argv: list[str]
    started_at: float
    first_request_at: float
    stderr_path: str | None
    exit_code: int | None
    notes: str = ""


class Adapter:
    entry: str = ""
    harness: str = ""
    binary: str = ""
    lever: str = ""
    credential_files: list[str] = []
    passthrough_env: list[str] = []
    extra_env: dict[str, str] = {}
    native_root: str = ""
    documented_roots: frozenset[str] = frozenset()
    flat_skill_layout: bool = False
    modes: tuple[str, ...] = ("print", "daemon")
    turn_timeout: float = 180.0

    def binary_path(self) -> str | None:
        return shutil.which(self.binary)

    def version(self) -> str:
        path = self.binary_path()
        if path is None:
            return "not-installed"
        out = subprocess.run([path, "--version"], capture_output=True, text=True, timeout=60)
        return (out.stdout or out.stderr).strip().splitlines()[0] if (out.stdout or out.stderr).strip() else "unknown"

    def real_root(self) -> Path | None:
        return None

    def prepare(self, sb: Sandbox) -> None:
        return None

    def check_auth(self, sb: Sandbox) -> bool | None:
        return None

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        raise NotImplementedError

    def install_registration(self, sb: Sandbox, skill_file: Path, body: str) -> HookInfo | None:
        return None

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        raise NotImplementedError

    def list_catalogue(self, sb: Sandbox) -> str | None:
        return None

    def skill_dir(self, sb: Sandbox, root: str, name: str) -> Path:
        return sb.repo / root if self.flat_skill_layout else sb.repo / root / name

    def skill_file(self, sb: Sandbox, root: str, name: str) -> Path:
        if self.flat_skill_layout:
            return sb.repo / root / f"{name}.md"
        return sb.repo / root / name / "SKILL.md"
