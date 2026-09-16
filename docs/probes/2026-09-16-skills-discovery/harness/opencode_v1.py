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

SEARCH_TOOLS = ("bash", "grep", "glob", "list", "read", "webfetch")


class OpenCodeV1Adapter(Adapter):
    entry = "opencode-v1"
    harness = "opencode"
    binary = "opencode"
    lever = "OPENCODE_CONFIG_DIR"
    native_root = ".opencode/skills"
    documented_roots = frozenset({".opencode/skills", ".claude/skills", ".agents/skills"})
    # Flags a version accepts only after its subcommand.
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
            res = acp_ask([binary, "acp", *self.extra_argv], sb.repo, sb.env, prompt,
                          sb.root / "opencode-acp.stderr.log", self.turn_timeout)
            generic = " ".join(n for n in res.notes.split() if not n.startswith("tools_used="))
            res.notes = (generic + " " + classify_acp_tools(res.raw)).strip()
            return res

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

        argv = [binary, "run", *self.extra_argv, "--format", "json", prompt]
        res = print_ask(argv, sb.repo, sb.env, sb.root / "opencode.stderr.log", self.turn_timeout, extract=extract)
        if "USAGE" in res.reply_text and "FLAGS" in res.reply_text:
            # The CLI printed its usage instead of running: an invocation error, not an answer.
            res.reply_text = ""
            res.exit_code = res.exit_code or 2
            res.notes = (res.notes + " usage printed").strip()
        res.notes = (res.notes + " " + classify_print_tools(res.raw)).strip()
        return res


def classify_acp_tools(raw: str) -> str:
    """OpenCode loads a listed skill through its own `skill` tool over ACP."""
    skill_loads = searches = other = 0
    try:
        frames = json.loads(raw)
    except json.JSONDecodeError:
        frames = []
    for f in frames:
        fr = f.get("frame") or {}
        if fr.get("method") != "session/update":
            continue
        upd = (fr.get("params") or {}).get("update") or {}
        if upd.get("sessionUpdate") != "tool_call":
            continue
        title = (upd.get("title") or "").lower()
        if title == "skill" or title.startswith("loaded skill"):
            skill_loads += 1
        elif any(title.startswith(t) for t in SEARCH_TOOLS) or upd.get("kind") in ("execute", "search"):
            searches += 1
        else:
            other += 1
    return f"tools_used={searches + other} skill_loads={skill_loads} searches={searches}"


def classify_print_tools(raw: str) -> str:
    """The `run --format json` events name each tool part by its tool name."""
    skill_loads = searches = other = 0
    for line in raw.splitlines():
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            continue
        part = obj.get("part") if isinstance(obj.get("part"), dict) else obj
        if part.get("type") != "tool":
            continue
        tool = (part.get("tool") or "").lower()
        if tool == "skill":
            skill_loads += 1
        elif tool in SEARCH_TOOLS:
            searches += 1
        else:
            other += 1
    return f"tools_used={searches + other} skill_loads={skill_loads} searches={searches}"
