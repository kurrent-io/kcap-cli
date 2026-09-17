from __future__ import annotations

import json
import subprocess
from pathlib import Path

from harness.base import Adapter, AskResult, ClassifiedSession, HookInfo, Session
from lib.hook_script import stamp_path, write_hook_script
from lib.isolation import Sandbox
from lib.pirpc_driver import PiRpcSession, pirpc_ask
from lib.print_driver import print_ask

HOOK_EXT = """import {{ execFileSync }} from "node:child_process";

export default function (pi: any) {{
  pi.on("session_start", async () => {{
    try {{ execFileSync({script}, {{ stdio: "ignore" }}); }} catch {{}}
  }});
}}
"""

REGISTER_EXT = """import {{ execFileSync }} from "node:child_process";

export default function (pi: any) {{
  pi.on("session_start", async () => {{
    try {{ execFileSync({script}, {{ stdio: "ignore" }}); }} catch {{}}
  }});
  pi.on("resources_discover", async () => {{
    return {{ skillPaths: [{root}] }};
  }});
}}
"""


class PiAdapter(Adapter):
    entry = "pi"
    harness = "pi"
    binary = "pi"
    lever = "PI_CODING_AGENT_DIR"
    credential_files = ("auth.json", "models.json", "settings.json")
    native_root = ".pi/skills"
    documented_roots = frozenset({".pi/skills", ".agents/skills"})
    modes = ("print", "daemon", "tui")
    can_resume = True
    tui_exit = ("/quit\r", "\x03", "\x04")
    # A private config root downloads its own fd and ripgrep before the prompt appears.
    tui_ready = 6.0

    def real_root(self) -> Path | None:
        return Path.home() / ".pi" / "agent"

    def check_auth(self, sb: Sandbox) -> bool | None:
        auth = sb.config_root / "auth.json"
        if not auth.exists():
            return False
        for provider in json.loads(auth.read_text()).keys():
            out = subprocess.run([self.binary_path() or self.binary, "auth", "check", "--provider", provider, "--json"],
                                 env=sb.env, capture_output=True, text=True, timeout=60)
            if out.returncode == 0:
                return True
        return False

    def _ext_dir(self, sb: Sandbox) -> Path:
        d = sb.config_root / "extensions"
        d.mkdir(parents=True, exist_ok=True)
        return d

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        ext = self._ext_dir(sb) / "probe.ts"
        ext.write_text(HOOK_EXT.format(script=json.dumps(str(script))))
        return HookInfo(mechanism="extension session_start", config_path=str(ext))

    def install_registration(self, sb: Sandbox, skill_file: Path, body: str) -> HookInfo | None:
        script = write_hook_script(sb.config_root, skill_file, body, stamp_path(sb.config_root))
        root = skill_file.parent.parent
        ext = self._ext_dir(sb) / "probe-register.ts"
        ext.write_text(REGISTER_EXT.format(script=json.dumps(str(script)), root=json.dumps(str(root))))
        return HookInfo(mechanism="extension session_start + resources_discover skillPaths", config_path=str(ext))

    def rpc_argv(self) -> list[str]:
        return [self.binary_path() or self.binary, "--mode", "rpc", "--approve"]

    def tui_argv(self, sb: Sandbox) -> list[str] | None:
        return [self.binary_path() or self.binary, "--approve"]

    def open_session(self, sb: Sandbox, mode: str) -> Session | None:
        if mode != "daemon":
            return super().open_session(sb, mode)
        inner = PiRpcSession(self.rpc_argv(), sb.cwd, sb.env, sb.root / "pi-rpc.stderr.log", self.turn_timeout)
        inner.start()
        return ClassifiedSession(inner, lambda r: classify_pi_tools(frame_events(r.raw)))

    def session_id(self, res: AskResult) -> str | None:
        for msg in json_lines(res.raw):
            if msg.get("type") == "session" and msg.get("id"):
                return msg["id"]
        return None

    def resume(self, sb: Sandbox, session_id: str, prompt: str) -> AskResult | None:
        return self._print(sb, prompt, ["--session", session_id])

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        if mode == "daemon":
            res = pirpc_ask(self.rpc_argv(), sb.cwd, sb.env, prompt,
                            sb.root / "pi-rpc.stderr.log", self.turn_timeout)
            generic = " ".join(n for n in res.notes.split() if not n.startswith("tools_used="))
            res.notes = (generic + " " + classify_pi_tools(frame_events(res.raw))).strip()
            return res
        return self._print(sb, prompt)

    def _print(self, sb: Sandbox, prompt: str, extra: list[str] = ()) -> AskResult:
        binary = self.binary_path() or self.binary

        def extract(raw: str) -> str:
            texts = []
            for line in raw.splitlines():
                try:
                    msg = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if msg.get("type") == "message_end" and (msg.get("message") or {}).get("role") == "assistant":
                    texts += [p.get("text", "") for p in msg["message"].get("content") or []
                              if isinstance(p, dict) and p.get("type") == "text"]
            return "\n".join(texts)

        argv = [binary, "-p", "--mode", "json", "--approve", *extra, "--", prompt]
        res = print_ask(argv, sb.cwd, sb.env, sb.root / "pi.stderr.log", self.turn_timeout, extract=extract)
        res.notes = (res.notes + " " + classify_pi_tools(json_lines(res.raw))).strip()
        return res


SEARCH_TOOLS = ("bash", "grep", "find", "glob", "ls")


def json_lines(raw: str) -> list[dict]:
    out = []
    for line in raw.splitlines():
        try:
            out.append(json.loads(line))
        except json.JSONDecodeError:
            continue
    return out


def frame_events(raw: str) -> list[dict]:
    try:
        return [f.get("frame") or {} for f in json.loads(raw)]
    except (json.JSONDecodeError, TypeError, AttributeError):
        return []


def classify_pi_tools(events: list[dict]) -> str:
    """Pi lists a skill with its path and the model reads that file, so a `read` of a SKILL.md
    path is the native mechanism; a shell or search tool is what makes a sighting inconclusive."""
    skill_reads = searches = other = 0
    for msg in events:
        if msg.get("type") != "tool_execution_start":
            continue
        name = msg.get("toolName") or ""
        args = msg.get("args") or {}
        if name in SEARCH_TOOLS:
            searches += 1
        elif name == "read" and str(args.get("path", "")).endswith("SKILL.md"):
            skill_reads += 1
        else:
            other += 1
    return f"tools_used={searches + other} skill_reads={skill_reads} searches={searches}"
