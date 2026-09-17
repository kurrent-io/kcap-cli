from __future__ import annotations

import json
import os
import subprocess
from pathlib import Path

from harness.base import AskResult, HookInfo
from harness.opencode_v1 import OpenCodeV1Adapter, classify_acp_tools
from lib.acp_driver import acp_ask
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
    # `run` takes a private server; `acp` rejects the flag and always uses the background service,
    # which is started under the sandbox's config and stopped after the turn.
    extra_argv = ("--standalone",)

    def binary_path(self) -> str | None:
        p = Path(os.environ.get("KCAP_OPENCODE_V2_PATH") or (Path.home() / ".local" / "opencode-v2" / "bin" / "opencode"))
        return str(p) if p.exists() else None

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        if mode != "daemon":
            return super().ask(sb, mode, prompt)
        binary = self.binary_path() or self.binary
        try:
            res = acp_ask([binary, "acp"], sb.cwd, sb.env, prompt, sb.root / "opencode-acp.stderr.log",
                          self.turn_timeout)
        finally:
            subprocess.run([binary, "service", "stop"], env=sb.env, capture_output=True, text=True, timeout=60)
        generic = " ".join(n for n in res.notes.split() if not n.startswith("tools_used="))
        res.notes = (generic + " " + classify_acp_tools(res.raw)).strip()
        return res

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
