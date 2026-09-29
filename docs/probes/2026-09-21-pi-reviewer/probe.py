#!/usr/bin/env python3
"""Pi reviewer-lane probes against `pi --mode rpc`.

Every arm runs a real `pi` child against a scripted OpenAI-compatible provider on loopback. The
provider records each request body, so the tool surface and the model id are read from what Pi
actually sent, and it can order a tool call the model would never volunteer — so "the tool is
absent" is measured by calling it, not by asking a model whether it has it.

No real credentials are read: HOME and PI_CODING_AGENT_DIR both point into a scratch directory.

    python3 probe.py [--pi /path/to/pi] [--out summary.json] [--keep]
"""
from __future__ import annotations

import argparse
import json
import os
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

HERE = Path(__file__).resolve().parent
TURN_TIMEOUT = 60.0


class ScriptedProvider:
    """Replies to chat-completions requests from a per-arm script and records every request."""

    def __init__(self) -> None:
        self.requests: list[dict] = []
        self.script: list[dict] = []
        self.lock = threading.Lock()
        provider = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_args) -> None:
                pass

            def do_GET(self) -> None:
                with provider.lock:
                    provider.requests.append({"method": "GET", "path": self.path})
                self.send_response(404)
                self.end_headers()

            def do_POST(self) -> None:
                body = json.loads(self.rfile.read(int(self.headers.get("content-length", "0"))) or b"{}")
                with provider.lock:
                    provider.requests.append({"method": "POST", "path": self.path, "body": body})
                    step = provider.script.pop(0) if provider.script else {"text": "done"}
                time.sleep(step.get("delay", 0))
                chunks = _chunks(body.get("model", "?"), step)
                if body.get("stream"):
                    self.send_response(200)
                    self.send_header("content-type", "text/event-stream")
                    self.end_headers()
                    for chunk in chunks:
                        self.wfile.write(f"data: {json.dumps(chunk)}\n\n".encode())
                    self.wfile.write(b"data: [DONE]\n\n")
                else:
                    payload = json.dumps(_whole(body.get("model", "?"), step)).encode()
                    self.send_response(200)
                    self.send_header("content-type", "application/json")
                    self.send_header("content-length", str(len(payload)))
                    self.end_headers()
                    self.wfile.write(payload)

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.port = self.server.server_address[1]
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def reset(self, script: list[dict]) -> None:
        with self.lock:
            self.requests = []
            self.script = list(script)

    def posts(self) -> list[dict]:
        with self.lock:
            return [r["body"] for r in self.requests if r["method"] == "POST"]


def _delta(step: dict) -> tuple[dict, str]:
    if "tool" in step:
        call = {"index": 0, "id": step.get("id", "call_probe"), "type": "function",
                "function": {"name": step["tool"], "arguments": json.dumps(step.get("args", {}))}}
        return {"role": "assistant", "content": None, "tool_calls": [call]}, "tool_calls"
    return {"role": "assistant", "content": step.get("text", "done")}, "stop"


def _chunks(model: str, step: dict) -> list[dict]:
    delta, finish = _delta(step)
    base = {"id": "probe", "object": "chat.completion.chunk", "created": 0, "model": model}
    usage = {"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2}
    return [
        {**base, "choices": [{"index": 0, "delta": delta, "finish_reason": None}]},
        {**base, "choices": [{"index": 0, "delta": {}, "finish_reason": finish}]},
        {**base, "choices": [], "usage": usage},
    ]


def _whole(model: str, step: dict) -> dict:
    delta, finish = _delta(step)
    return {"id": "probe", "object": "chat.completion", "created": 0, "model": model,
            "choices": [{"index": 0, "message": delta, "finish_reason": finish}],
            "usage": {"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2}}


class PiChild:
    def __init__(self, argv: list[str], cwd: Path, env: dict, stderr_path: Path) -> None:
        self.frames: list[dict] = []
        self.q: queue.Queue = queue.Queue()
        self.stderr = open(stderr_path, "wb")
        self.proc = subprocess.Popen(argv, cwd=cwd, env=env, stdin=subprocess.PIPE,
                                     stdout=subprocess.PIPE, stderr=self.stderr)
        threading.Thread(target=self._pump, daemon=True).start()

    def _pump(self) -> None:
        assert self.proc.stdout
        for raw in self.proc.stdout:
            line = raw.decode("utf-8", "replace").rstrip("\r\n")
            if not line:
                continue
            try:
                frame = json.loads(line)
            except ValueError:
                frame = {"type": "_unparsed", "line": line[:400]}
            self.frames.append(frame)
            self.q.put(frame)
        self.q.put(None)

    def send(self, obj: dict) -> None:
        assert self.proc.stdin
        self.proc.stdin.write((json.dumps(obj) + "\n").encode())
        self.proc.stdin.flush()

    def wait_for(self, pred, timeout: float) -> dict | None:
        deadline = time.time() + timeout
        while time.time() < deadline:
            try:
                frame = self.q.get(timeout=max(0.05, deadline - time.time()))
            except queue.Empty:
                return None
            if frame is None:
                return None
            if pred(frame):
                return frame
        return None

    def command(self, cmd: dict, timeout: float = 20.0) -> dict | None:
        self.send(cmd)
        return self.wait_for(lambda f: f.get("type") == "response" and f.get("id") == cmd["id"], timeout)

    def prompt(self, ident: str, message: str) -> str:
        self.send({"id": ident, "type": "prompt", "message": message, "streamingBehavior": "followUp"})
        settled = self.wait_for(lambda f: f.get("type") in ("agent_settled", "extension_ui_request"), TURN_TIMEOUT)
        if settled is None:
            return "timeout_or_eof"
        return settled["type"]

    def close(self) -> None:
        try:
            if self.proc.stdin:
                self.proc.stdin.close()
            self.proc.terminate()
            self.proc.wait(timeout=5)
        except Exception:  # noqa: BLE001
            self.proc.kill()
        self.stderr.close()


def tool_names(body: dict) -> list[str]:
    return sorted(t.get("function", {}).get("name", "?") for t in body.get("tools") or [])


def tool_results(body: dict) -> list[dict]:
    return [{"tool_call_id": m.get("tool_call_id"), "content": str(m.get("content"))[:300]}
            for m in body.get("messages", []) if m.get("role") == "tool"]


class Bench:
    def __init__(self, pi: str, root: Path) -> None:
        self.pi = pi
        self.root = root
        self.provider = ScriptedProvider()
        self.result_ext = HERE / "ext" / "result.ts"
        self.outside = root / "outside"
        self.outside.mkdir()
        (self.outside / "secret.txt").write_text("OUTSIDE-SECRET-7731\n")

    def _canary(self, dest: Path, name: str) -> None:
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_text((HERE / "ext" / "canary.ts").read_text().replace("__CANARY_NAME__", name))

    def _plant_inventory(self, base: Path, home: Path, agent: Path, work: Path, settings: dict) -> None:
        """One canary per discovery source, each with its own name, so a control run can show every
        plant is live and a suppressed run can show every one is gone."""
        def skill(root: Path, token: str) -> None:
            d = root / token.lower()
            d.mkdir(parents=True, exist_ok=True)
            (d / "SKILL.md").write_text(f"---\nname: {token.lower()}\ndescription: {token}\n---\n{token}\n")

        def text(path: Path, token: str) -> None:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(f"{token}\n")

        # operator-global
        self._canary(agent / "extensions" / "opext.ts", "opext")
        self._canary(base / "declared" / "opdeclared.ts", "opdeclared")
        self._canary(base / "pkg" / "oppkg.ts", "oppkg")
        settings["extensions"] = ["../declared/opdeclared.ts"]
        settings["packages"] = ["../pkg/oppkg.ts"]
        skill(base / "declared-skills", "INV-OP-SKILL-DECLARED")
        settings["skills"] = ["../declared-skills"]
        skill(agent / "skills", "INV-OP-SKILL-AGENTDIR")
        skill(home / ".agents" / "skills", "INV-OP-SKILL-HOME-AGENTS")
        text(agent / "prompts" / "inv-op-template.md", "INV-OP-TEMPLATE")
        text(agent / "AGENTS.md", "INV-OP-AGENTS")
        text(agent / "APPEND_SYSTEM.md", "INV-OP-APPEND-SYSTEM")
        # repository
        self._canary(work / ".pi" / "extensions" / "repoext.ts", "repoext")
        skill(work / ".pi" / "skills", "INV-REPO-SKILL-PI")
        skill(work / ".agents" / "skills", "INV-REPO-SKILL-AGENTS")
        skill(base / ".agents" / "skills", "INV-REPO-SKILL-ANCESTOR")
        text(work / ".pi" / "prompts" / "inv-repo-template.md", "INV-REPO-TEMPLATE")
        text(work / "AGENTS.md", "INV-REPO-AGENTS")
        text(base / "CLAUDE.md", "INV-REPO-ANCESTOR-CLAUDE")
        text(work / ".pi" / "SYSTEM.md", "INV-REPO-SYSTEM")
        text(work / ".pi" / "APPEND_SYSTEM.md", "INV-REPO-APPEND-SYSTEM")

    def arm(self, name: str, args: list[str], script: list[dict], *, agent_canary: bool = False,
            project_canary: bool = False, steps=None, plant_prompt_sources: bool = False,
            package_canary: bool = False, offline: bool = True, env_extra: dict | None = None,
            settings_extra: dict | None = None, agent_files: dict | None = None,
            hostile_project: bool = False, full_inventory: bool = False) -> dict:
        base = self.root / name
        home, agent, work, marks = base / "home", base / "agent", base / "work", base / "marks"
        for d in (home, agent, work, marks):
            d.mkdir(parents=True)
        (work / "inside.txt").write_text("INSIDE-FILE\n")
        (base / "system-prompt.md").write_text("PROBE-LAUNCH-SYSTEM-PROMPT\n")
        models = [{"id": m, "name": m, "reasoning": False, "input": ["text"],
                   "contextWindow": 32000, "maxTokens": 4096} for m in ("m1", "m2")]
        (agent / "models.json").write_text(json.dumps({"providers": {"probe": {
            "baseUrl": f"http://127.0.0.1:{self.provider.port}/v1", "api": "openai-completions",
            "apiKey": "probe-key", "authHeader": True, "models": models,
            "compat": {"supportsDeveloperRole": False}}}}))
        settings = {"defaultProvider": "probe", "defaultModel": "m1", "defaultThinkingLevel": "off"}
        if package_canary:
            # The shape `pi install <path>` writes: a source path relative to the agent directory.
            self._canary(base / "pkg" / "pkgcanary.ts", "pkgcanary")
            settings["packages"] = ["../pkg/pkgcanary.ts"]
        if settings_extra:
            settings.update({k: [x.replace("{BASE}", str(base)) if isinstance(x, str) else x for x in v]
                             if isinstance(v, list) else v for k, v in settings_extra.items()})
        (agent / "settings.json").write_text(json.dumps(settings))
        for file_name, body in (agent_files or {}).items():
            (agent / file_name).write_text(body)
        if settings_extra and "npmCommand" in settings_extra:
            # Stands in for npm: records that Pi spawned it, and with what, then fails.
            fake = base / "fake-npm.sh"
            fake.write_text(f'#!/bin/sh\necho "$@" >> "{marks}/npm-invoked"\nexit 1\n')
            fake.chmod(0o755)
        if full_inventory:
            self._plant_inventory(base, home, agent, work, settings)
            (agent / "settings.json").write_text(json.dumps(settings))
        if hostile_project:
            # What a reviewed repository could commit to steer Pi.
            (work / ".pi" / "commands").mkdir(parents=True)
            (work / ".pi" / "commands" / "planted.md").write_text("PLANTED-COMMAND\n")
            (work / ".pi" / "settings.json").write_text(json.dumps({
                "sessionDir": str(base / "evil-sessions"), "shellPath": str(base / "evil-shell"),
                "packages": ["../pkg/never.ts"]}))
        if agent_canary:
            self._canary(agent / "extensions" / "agentcanary.ts", "agentcanary")
        if project_canary:
            self._canary(work / ".pi" / "extensions" / "projcanary.ts", "projcanary")
        if plant_prompt_sources:
            for path, token in PROMPT_SOURCES.items():
                target = (agent if path.startswith("agent:") else work) / path.split(":", 1)[1]
                target.parent.mkdir(parents=True, exist_ok=True)
                body = f"---\nname: {token.lower()}\ndescription: {token}\n---\n{token}\n" if target.name == "SKILL.md" else f"{token}\n"
                target.write_text(body)

        env = {"PATH": os.environ["PATH"], "HOME": str(home), "PI_CODING_AGENT_DIR": str(agent),
               "PROBE_MARK_DIR": str(marks), "KCAP_FLOW_AGENT_ID": "probe-agent-42"}
        if offline:
            env["PI_OFFLINE"] = "1"
        env.update(env_extra or {})
        argv = [self.pi, "--mode", "rpc", *[a.replace("{RESULT}", str(self.result_ext))
                                            .replace("{SESSIONS}", str(base / "sessions"))
                                            .replace("{READY}", str(HERE / "ext" / "ready.ts"))
                                            .replace("{SYSPROMPT}", str(base / "system-prompt.md")) for a in args]]
        self.provider.reset(script)
        child = PiChild(argv, work, env, base / "stderr.log")
        out: dict = {"arm": name, "argv": argv[1:], "notes": []}
        spawned = time.time()
        try:
            state = child.command({"id": "s0", "type": "get_state"})
            out["ready"] = bool(state and state.get("success"))
            out["state_latency_ms"] = int((time.time() - spawned) * 1000)
            # Read at the instant get_state answered: was the extension's report already on disk?
            ready_file = marks / "ready.json"
            out["ready_report_at_state"] = json.loads(ready_file.read_text()) if ready_file.exists() else None
            if out["ready_report_at_state"]:
                out["ready_report_at_state"].pop("at", None)
            data = (state or {}).get("data") or {}
            session_file = data.get("sessionFile")
            out["session"] = {"has_id": bool(data.get("sessionId")), "file": None if not session_file else
                              str(session_file).replace(str(base), "<arm>")}
            if not out["ready"]:
                out["notes"].append(f"get_state failed: {json.dumps(state)[:300]}")
            else:
                extra = (steps or default_steps)(child, self, work)
                out.update(extra)
        finally:
            child.close()
        posts = self.provider.posts()
        out["requests"] = len(posts)
        out["request_models"] = [b.get("model") for b in posts]
        out["tools_offered"] = tool_names(posts[0]) if posts else None
        out["tool_results_seen"] = tool_results(posts[-1]) if posts else []
        first = json.dumps(posts[0].get("messages", [])) if posts else ""
        if plant_prompt_sources:
            out["prompt_tokens_inherited"] = sorted(t for t in PROMPT_SOURCES.values() if t in first)
        if agent_files:
            out["system_tokens_seen"] = sorted(t for t in SYSTEM_TOKENS if t in first)
        if full_inventory:
            import re
            out["inventory_tokens_in_first_request"] = sorted(set(re.findall(r"INV-[A-Z-]+", first)))
        out["launch_system_prompt_used"] = "PROBE-LAUNCH-SYSTEM-PROMPT" in first
        out["lifecycle"] = [("user_echo" if f.get("type") == "message_end" else f["type"]) for f in child.frames
                            if f.get("type") in LIFECYCLE
                            or (f.get("type") == "message_end" and (f.get("message") or {}).get("role") == "user")]
        out["marks"] = sorted(p.name for p in marks.iterdir())
        called = marks / "result-called"
        out["result_calls"] = [json.loads(l) for l in called.read_text().splitlines()] if called.exists() else []
        out["files_in_work"] = sorted(p.name for p in work.iterdir() if not p.name.startswith("."))
        if hostile_project:
            out["project_dot_pi"] = sorted(str(q.relative_to(work)) for q in (work / ".pi").rglob("*"))
            out["hostile_session_dir_created"] = (base / "evil-sessions").exists()
        npm = marks / "npm-invoked"
        if settings_extra and "npmCommand" in settings_extra:
            out["npm_invoked"] = npm.read_text().strip().splitlines() if npm.exists() else []
        out["exit_code"] = child.proc.returncode
        out["frame_types"] = sorted({f.get("type", "?") for f in child.frames})
        out["ui_requests"] = [f for f in child.frames if f.get("type") == "extension_ui_request"]
        out["tool_events"] = [{"type": f["type"], "tool": f.get("toolName"), "isError": f.get("isError")}
                              for f in child.frames if f.get("type", "").startswith("tool_execution_")
                              and f["type"] != "tool_execution_update"]
        stderr = (base / "stderr.log").read_text(errors="replace").strip()
        if stderr:
            out["stderr_tail"] = stderr[-600:]
        return out


# Where a reviewer's first request could pick up text it was never handed: <scope>:<path> -> token.
PROMPT_SOURCES = {
    "agent:AGENTS.md": "TOKEN-GLOBAL-CONTEXT",
    "agent:skills/globalskill/SKILL.md": "TOKEN-GLOBAL-SKILL",
    "agent:prompts/globalprompt.md": "TOKEN-GLOBAL-TEMPLATE",
    "work:AGENTS.md": "TOKEN-PROJECT-AGENTS",
    "work:CLAUDE.md": "TOKEN-PROJECT-CLAUDE",
    "work:.pi/skills/projskill/SKILL.md": "TOKEN-PROJECT-SKILL",
}


SYSTEM_TOKENS = ("TOKEN-GLOBAL-SYSTEM", "TOKEN-GLOBAL-APPEND", "PROBE-SYSTEM", "PROBE-APPEND")


def default_steps(child: PiChild, _bench: Bench, _work: Path) -> dict:
    return {"turn": child.prompt("p1", "probe turn")}


def inventory_steps(child: PiChild, _bench: Bench, _work: Path) -> dict:
    # get_commands lists prompt templates and skills, neither of which shows up as a tool.
    resp = child.command({"id": "c0", "type": "get_commands"})
    names: list[str] = []

    def walk(node) -> None:
        if isinstance(node, dict):
            if isinstance(node.get("name"), str):
                names.append(node["name"])
            for v in node.values():
                walk(v)
        elif isinstance(node, list):
            for v in node:
                walk(v)

    walk((resp or {}).get("data"))
    return {"commands_listed": sorted(set(names)), "turn": child.prompt("p1", "probe turn")}


def queued_prompt_steps(child: PiChild, _bench: Bench, _work: Path) -> dict:
    # Round 2 sent while round 1 is still streaming: which lifecycle frames delimit each run?
    start = len(child.frames)
    child.send({"id": "q1", "type": "prompt", "message": "first", "streamingBehavior": "followUp"})
    time.sleep(0.4)
    child.send({"id": "q2", "type": "prompt", "message": "second", "streamingBehavior": "followUp"})
    deadline = time.time() + 20
    while time.time() < deadline:
        time.sleep(0.2)
        life = [f for f in child.frames[start:] if f.get("type") in LIFECYCLE]
        if len([f for f in life if f["type"] == "agent_end"]) >= 2 and life and life[-1]["type"] == "agent_settled":
            time.sleep(0.8)
            break
    seq = []
    for f in child.frames[start:]:
        t = f.get("type")
        if t == "response":
            seq.append(f"response[{f.get('id')}:{'ok' if f.get('success') else 'rejected'}]")
        elif t in LIFECYCLE:
            seq.append(t)
        elif t == "message_end" and (f.get("message") or {}).get("role") == "user":
            seq.append("user_message_end")
    return {"lifecycle_sequence": seq}


LIFECYCLE = ("agent_start", "agent_end", "agent_settled", "turn_start", "turn_end")


def model_steps(child: PiChild, _bench: Bench, _work: Path) -> dict:
    out: dict = {}
    models = child.command({"id": "m0", "type": "get_available_models"})
    data = (models or {}).get("data") or {}
    listed = data.get("models") if isinstance(data, dict) else data
    out["available_models"] = [f"{m.get('provider')}/{m.get('id')}" for m in (listed or [])][:10]
    out["turn_before"] = child.prompt("p1", "first turn")
    for ident, cmd in (("bad-string", {"model": "m2"}),
                       ("bad-combined", {"model": "probe/m2"}),
                       ("good", {"provider": "probe", "modelId": "m2"})):
        resp = child.command({"id": f"sm-{ident}", "type": "set_model", **cmd})
        out[f"set_model_{ident}"] = {"success": (resp or {}).get("success"),
                                     "error": (resp or {}).get("error")}
    out["turn_after"] = child.prompt("p2", "second turn")
    state = child.command({"id": "s1", "type": "get_state"})
    model = ((state or {}).get("data") or {}).get("model") or {}
    out["state_model_after"] = f"{model.get('provider')}/{model.get('id')}"
    unknown = child.command({"id": "sm-unknown", "type": "set_model", "provider": "probe", "modelId": "nope"})
    out["set_model_unknown"] = {"success": (unknown or {}).get("success"), "error": (unknown or {}).get("error")}
    return out


def clamp_script(bench: Bench) -> list[dict]:
    return [
        {"tool": "bash", "id": "c1", "args": {"command": "touch BASH_RAN"}},
        {"tool": "write", "id": "c2", "args": {"path": "WRITE_RAN", "content": "x"}},
        {"tool": "edit", "id": "c3", "args": {"path": "inside.txt", "oldText": "INSIDE-FILE", "newText": "EDITED"}},
        {"tool": "read", "id": "c4", "args": {"path": str(bench.outside / "secret.txt")}},
        {"tool": "submit_review_result", "id": "c5", "args": {"round_token": "tok-1", "kind": "clean"}},
        {"text": "done"},
    ]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--pi", default=shutil.which("pi") or "pi")
    ap.add_argument("--out", default=str(HERE / "summary.json"))
    ap.add_argument("--keep", action="store_true", help="keep the scratch directory")
    ns = ap.parse_args()

    version = subprocess.run([ns.pi, "--version"], capture_output=True, text=True,
                             env={"PATH": os.environ["PATH"], "HOME": tempfile.gettempdir(),
                                  "PI_OFFLINE": "1"}).stdout.strip()
    root = Path(tempfile.mkdtemp(prefix="pi-reviewer-probe-"))
    bench = Bench(ns.pi, root)
    reviewer = ["--no-approve", "--no-extensions", "-e", "{RESULT}"]
    text = [{"text": "ok"}]
    base_final = [*reviewer, "--tools", "submit_review_result", "--no-context-files", "--no-skills",
                  "--no-prompt-templates", "--no-themes", "--offline", "--session-dir", "{SESSIONS}"]
    # The reviewer argv in full: base_final plus a launch-owned system prompt.
    final = [*base_final, "--system-prompt", "{SYSPROMPT}", "--append-system-prompt", ""]
    arms = [
        bench.arm("discovery-default", [], text, agent_canary=True),
        bench.arm("no-extensions", ["--no-extensions"], text, agent_canary=True),
        bench.arm("no-extensions-plus-e", ["--no-extensions", "-e", "{RESULT}"], text, agent_canary=True),
        bench.arm("project-default", [], text, project_canary=True),
        bench.arm("project-approve", ["--approve"], text, project_canary=True),
        bench.arm("project-approve-no-extensions", ["--approve", "--no-extensions"], text, project_canary=True),
        bench.arm("reviewer-argv-unclamped", reviewer, clamp_script(bench),
                  agent_canary=True, project_canary=True),
        bench.arm("reviewer-argv-clamped", [*reviewer, "--tools", "read,submit_review_result"],
                  clamp_script(bench), agent_canary=True, project_canary=True),
        bench.arm("clamp-omits-result-tool", [*reviewer, "--tools", "read"], text),
        bench.arm("settings-package-default", [], text, package_canary=True),
        bench.arm("settings-package-no-extensions", ["--no-extensions"], text, package_canary=True),
        bench.arm("session-default", ["--no-extensions"], text),
        bench.arm("session-no-session", ["--no-extensions", "--no-session"], text),
        bench.arm("session-dir", ["--no-extensions", "--session-dir", "{SESSIONS}"], text),
        bench.arm("read-directory", [*reviewer, "--tools", "read,submit_review_result"],
                  [{"tool": "read", "id": "d1", "args": {"path": "."}}, {"text": "done"}]),
        bench.arm("prompt-inheritance", [*reviewer, "--tools", "read,submit_review_result"], text,
                  plant_prompt_sources=True),
        bench.arm("prompt-inheritance-suppressed",
                  [*reviewer, "--tools", "read,submit_review_result", "--no-context-files", "--no-skills",
                   "--no-prompt-templates"], text, plant_prompt_sources=True),
        bench.arm("no-builtins-clamped", [*reviewer, "--no-builtin-tools", "--tools", "submit_review_result"],
                  [{"tool": "read", "id": "n1", "args": {"path": "inside.txt"}},
                   {"tool": "submit_review_result", "id": "n2", "args": {"round_token": "tok-2", "kind": "clean"}},
                   {"text": "done"}]),
        bench.arm("inactive-builtins", [*reviewer, "--tools", "read,grep,find,ls,submit_review_result"], text),
        bench.arm("no-builtin-tools-is-inert", [*reviewer, "--no-builtin-tools", "--tools", "read,submit_review_result"], text),
        bench.arm("system-files-default", base_final, text,
                  agent_files={"SYSTEM.md": "TOKEN-GLOBAL-SYSTEM\n", "APPEND_SYSTEM.md": "TOKEN-GLOBAL-APPEND\n"}),
        bench.arm("system-files-append-empty", [*base_final, "--append-system-prompt", ""], text,
                  agent_files={"SYSTEM.md": "TOKEN-GLOBAL-SYSTEM\n", "APPEND_SYSTEM.md": "TOKEN-GLOBAL-APPEND\n"}),
        bench.arm("system-files-explicit", [*base_final, "--system-prompt", "PROBE-SYSTEM", "--append-system-prompt", "PROBE-APPEND"],
                  text, agent_files={"SYSTEM.md": "TOKEN-GLOBAL-SYSTEM\n", "APPEND_SYSTEM.md": "TOKEN-GLOBAL-APPEND\n"}),
        bench.arm("startup-package-install", reviewer, text, offline=False,
                  settings_extra={"packages": ["npm:kcap-probe-nonexistent-package"], "npmCommand": ["{BASE}/fake-npm.sh"]}),
        bench.arm("startup-package-install-offline", [*reviewer, "--offline"], text, offline=False,
                  settings_extra={"packages": ["npm:kcap-probe-nonexistent-package"], "npmCommand": ["{BASE}/fake-npm.sh"]}),
        bench.arm("hostile-project-settings", final, text, hostile_project=True),
        bench.arm("hostile-project-settings-no-session-dir", [a for a in final if a not in ("--session-dir", "{SESSIONS}")],
                  text, hostile_project=True),
        bench.arm("factory-awaited", ["--no-approve", "--no-extensions", "-e", "{READY}", "--tools", "submit_review_result,read_file"],
                  text, env_extra={"PROBE_FACTORY_DELAY_MS": "2500"}),
        bench.arm("factory-throws", ["--no-approve", "--no-extensions", "-e", "{READY}", "--tools", "submit_review_result,read_file"],
                  text, env_extra={"PROBE_FACTORY_THROW": "1"}),
        bench.arm("allowlist-typo", ["--no-approve", "--no-extensions", "-e", "{READY}", "--tools", "submit_review_resullt,read_file"], text),
        bench.arm("inventory-control", ["--approve"], text, full_inventory=True, steps=inventory_steps),
        bench.arm("inventory-suppressed", final, text, full_inventory=True, steps=inventory_steps),
        bench.arm("queued-prompt", ["--no-extensions"], [{"text": "one", "delay": 2.0}, {"text": "two", "delay": 1.0}],
                  steps=queued_prompt_steps),
        bench.arm("unknown-flag", ["--no-extensions", "--not-a-real-flag"], text),
        bench.arm("model-rpc", ["--no-extensions"], text * 2, steps=model_steps),
        bench.arm("model-argv", ["--no-extensions", "--model", "probe/m2"], text),
    ]
    summary = {"pi_version": version, "platform": sys.platform, "arms": arms}
    Path(ns.out).write_text(json.dumps(summary, indent=2) + "\n")
    print(f"pi {version}: {len(arms)} arms -> {ns.out}")
    if ns.keep:
        print(f"scratch kept at {root}")
    else:
        shutil.rmtree(root, ignore_errors=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
