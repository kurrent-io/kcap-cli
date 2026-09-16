from __future__ import annotations

import json
from pathlib import Path

from harness.base import Adapter, AskResult, HookInfo
from lib.isolation import Sandbox
from lib.print_driver import print_ask


class AgyAdapter(Adapter):
    entry = "agy"
    harness = "antigravity"
    binary = "agy"
    lever = "HOME"
    credential_files = (".gemini/antigravity-cli/settings.json",)
    native_root = ".agents/skills"
    documented_roots = frozenset({".agents/skills", ".agent/skills"})
    flat_skill_layout = True
    modes = ("print",)
    plugin_parent = (".gemini", "config", "plugins")

    def real_root(self) -> Path | None:
        return Path.home()

    def prepare(self, sb: Sandbox) -> None:
        (sb.config_root / "tmp").mkdir(exist_ok=True)
        sb.env["TMPDIR"] = str(sb.config_root / "tmp")

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        plugin = sb.config_root.joinpath(*self.plugin_parent) / "probe"
        plugin.mkdir(parents=True, exist_ok=True)
        (plugin / "plugin.json").write_text(json.dumps({"name": "probe", "version": "1.0.0", "description": "probe"}, indent=2) + "\n")
        hooks = plugin / "hooks.json"
        hooks.write_text(json.dumps({"probe": {
            "PreInvocation": [{"type": "command", "command": str(script), "timeout": 15000}],
        }}, indent=2) + "\n")
        return HookInfo(mechanism=f"{'/'.join(self.plugin_parent)} PreInvocation", config_path=str(hooks))

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
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

        argv = [self.binary_path() or self.binary, "-p", prompt, "--output-format", "stream-json",
                "--dangerously-skip-permissions", "--print-timeout", "180s"]
        return print_ask(argv, sb.repo, sb.env, sb.root / "agy.stderr.log", self.turn_timeout, extract=extract)


class AgyDirLayoutAdapter(AgyAdapter):
    entry = "agy-dirlayout"
    flat_skill_layout = False


class AgyCliDirAdapter(AgyAdapter):
    entry = "agy-clidir"
    plugin_parent = (".gemini", "antigravity-cli", "plugins")
