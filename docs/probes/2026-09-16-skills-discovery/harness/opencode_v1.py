from __future__ import annotations

import json
import os
import shutil
from pathlib import Path

from harness.base import Adapter, AskResult, HookInfo
from lib.acp_driver import acp_ask
from lib.hook_script import stamp_path, write_hook_script
from lib.isolation import Sandbox
from lib.print_driver import print_ask

V1_EVENT_PLUGIN = """import {{ execFileSync }} from "node:child_process"

export const ProbePlugin = async () => ({{
  event: async ({{ event }}: any) => {{
    if (event?.type === "session.created") {{
      try {{ execFileSync({script}, {{ stdio: "ignore" }}) }} catch {{}}
    }}
  }},
}})
"""

V1_TRANSFORM_PLUGIN = """import {{ execFileSync }} from "node:child_process"

export const ProbeRegisterPlugin = async () => ({{
  "experimental.chat.system.transform": async () => {{
    try {{ execFileSync({script}, {{ stdio: "ignore" }}) }} catch {{}}
  }},
}})
"""


class OpenCodeV1Adapter(Adapter):
    entry = "opencode-v1"
    harness = "opencode"
    binary = "opencode"
    lever = "OPENCODE_CONFIG_DIR"
    native_root = ".opencode/skills"
    documented_roots = frozenset({".opencode/skills", ".claude/skills", ".agents/skills"})
    extra_argv: tuple[str, ...] = ()

    def real_root(self) -> Path | None:
        return Path.home() / ".config" / "opencode"

    def default_model(self) -> str | None:
        if os.environ.get("KCAP_PROBE_OPENCODE_MODEL"):
            return os.environ["KCAP_PROBE_OPENCODE_MODEL"]
        cfg = (self.real_root() or Path()) / "opencode.json"
        if cfg.exists():
            try:
                return json.loads(cfg.read_text()).get("model")
            except json.JSONDecodeError:
                return None
        return None

    def prepare(self, sb: Sandbox) -> None:
        data = sb.root / "data"
        (data / "opencode").mkdir(parents=True, exist_ok=True)
        sb.env["XDG_DATA_HOME"] = str(data)
        auth = Path.home() / ".local" / "share" / "opencode" / "auth.json"
        if auth.exists():
            shutil.copy2(auth, data / "opencode" / "auth.json")
        cfg = {"$schema": "https://opencode.ai/config.json"}
        if self.default_model():
            cfg["model"] = self.default_model()
        (sb.config_root / "opencode.json").write_text(json.dumps(cfg, indent=2) + "\n")

    def check_auth(self, sb: Sandbox) -> bool | None:
        return (Path(sb.env["XDG_DATA_HOME"]) / "opencode" / "auth.json").exists()

    def _plugins(self, sb: Sandbox) -> Path:
        d = sb.config_root / "plugins"
        d.mkdir(parents=True, exist_ok=True)
        return d

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        path = self._plugins(sb) / "probe.ts"
        path.write_text(V1_EVENT_PLUGIN.format(script=json.dumps(str(script))))
        return HookInfo(mechanism="plugin event session.created", config_path=str(path))

    def install_registration(self, sb: Sandbox, skill_file: Path, body: str) -> HookInfo | None:
        script = write_hook_script(sb.config_root, skill_file, body, stamp_path(sb.config_root))
        path = self._plugins(sb) / "probe-register.ts"
        path.write_text(V1_TRANSFORM_PLUGIN.format(script=json.dumps(str(script))))
        return HookInfo(mechanism="plugin experimental.chat.system.transform", config_path=str(path))

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        binary = self.binary_path() or self.binary
        if mode == "daemon":
            return acp_ask([binary, *self.extra_argv, "acp"], sb.repo, sb.env, prompt,
                           sb.root / "opencode-acp.stderr.log", self.turn_timeout)

        def extract(raw: str) -> str:
            texts = []
            for line in raw.splitlines():
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError:
                    continue
                part = obj.get("part") if isinstance(obj.get("part"), dict) else obj
                if part.get("type") == "text" and isinstance(part.get("text"), str):
                    texts.append(part["text"])
            return "\n".join(texts) if texts else raw

        argv = [binary, *self.extra_argv, "run", "--format", "json", prompt]
        return print_ask(argv, sb.repo, sb.env, sb.root / "opencode.stderr.log", self.turn_timeout, extract=extract)
