from __future__ import annotations

import json
import os
from pathlib import Path

from harness.base import HookInfo
from harness.opencode_v1 import OpenCodeV1Adapter
from lib.hook_script import stamp_path, write_hook_script
from lib.isolation import Sandbox

V2_SETUP_PLUGIN = """import {{ execFileSync }} from "node:child_process"
import {{ Plugin }} from "@opencode/plugin"

export default Plugin.define({{
  id: "probe",
  async setup(ctx) {{
    try {{ execFileSync({script}, {{ stdio: "ignore" }}) }} catch {{}}
    {reload}
  }},
}})
"""

V2_PROMPT_PLUGIN = """import {{ execFileSync }} from "node:child_process"
import {{ Plugin }} from "@opencode/plugin"

export default Plugin.define({{
  id: "probe",
  async setup(ctx) {{
    await ctx.session.hook("prompt", async () => {{
      try {{ execFileSync({script}, {{ stdio: "ignore" }}) }} catch {{}}
      await ctx.skill.reload()
    }})
  }},
}})
"""


class OpenCodeV2Adapter(OpenCodeV1Adapter):
    entry = "opencode-v2"
    # A private server per run: the shared background service would outlive the sandbox.
    extra_argv = ("--standalone",)

    def binary_path(self) -> str | None:
        p = Path(os.environ.get("KCAP_OPENCODE_V2_PATH") or (Path.home() / ".local" / "opencode-v2" / "bin" / "opencode"))
        return str(p) if p.exists() else None

    def _plugin_file(self, sb: Sandbox) -> Path:
        d = self._plugins(sb) / "probe"
        d.mkdir(parents=True, exist_ok=True)
        return d / "index.ts"

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        path = self._plugin_file(sb)
        path.write_text(V2_SETUP_PLUGIN.format(script=json.dumps(str(script)), reload=""))
        return HookInfo(mechanism="plugin setup (no reload)", config_path=str(path))

    def install_registration(self, sb: Sandbox, skill_file: Path, body: str) -> HookInfo | None:
        script = write_hook_script(sb.config_root, skill_file, body, stamp_path(sb.config_root))
        path = self._plugin_file(sb)
        path.write_text(V2_SETUP_PLUGIN.format(script=json.dumps(str(script)), reload="await ctx.skill.reload()"))
        return HookInfo(mechanism="plugin setup + skill.reload", config_path=str(path))


class OpenCodeV2PromptHookAdapter(OpenCodeV2Adapter):
    entry = "opencode-v2-prompt"

    def install_registration(self, sb: Sandbox, skill_file: Path, body: str) -> HookInfo | None:
        script = write_hook_script(sb.config_root, skill_file, body, stamp_path(sb.config_root))
        path = self._plugin_file(sb)
        path.write_text(V2_PROMPT_PLUGIN.format(script=json.dumps(str(script))))
        return HookInfo(mechanism="plugin prompt hook + skill.reload", config_path=str(path))
