from __future__ import annotations

import subprocess
import time
from pathlib import Path

from harness.base import Adapter, AskResult, HookInfo
from lib.isolation import Sandbox
from lib.probe_skill import NO_SKILL, TOKEN_RE


class FakeAdapter(Adapter):
    entry = "fake"
    harness = "fake"
    binary = "sh"
    lever = "FAKE_HOME"
    native_root = ".fake/skills"
    documented_roots = frozenset({".fake/skills", ".agents/skills"})

    def __init__(self) -> None:
        self._hook: Path | None = None

    def version(self) -> str:
        return "1.0"

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        self._hook = script
        return HookInfo(mechanism="fake-startup", config_path=str(script))

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        started = time.time()
        if self._hook is not None:
            subprocess.run([str(self._hook)], input="{}", capture_output=True, text=True, timeout=10)
        lines = []
        for root in sorted(self.documented_roots):
            for skill_md in sorted((sb.repo / root).glob("kcap-probe-*/SKILL.md")):
                m = TOKEN_RE.search(skill_md.read_text())
                if m:
                    lines.append(f"{skill_md.parent.name}=PROBE-BODY-{m.group(1)}")
        text = "\n".join(lines) if lines else NO_SKILL
        return AskResult(reply_text=text, raw=text, argv=["fake"], started_at=started,
                         first_request_at=started, stderr_path=None, exit_code=0)
