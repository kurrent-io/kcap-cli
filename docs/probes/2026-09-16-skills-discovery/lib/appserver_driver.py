from __future__ import annotations

import json
import time
from pathlib import Path

from harness.base import AskResult
from lib.jsonl_child import JsonlChild


class _Rpc:
    def __init__(self, child: JsonlChild) -> None:
        self.child = child
        self.next_id = 0
        self.notifications: list[dict] = []

    def request(self, method: str, params: dict, timeout: float) -> dict:
        self.next_id += 1
        rid = self.next_id
        self.child.send({"jsonrpc": "2.0", "id": rid, "method": method, "params": params})
        deadline = time.time() + timeout
        while time.time() < deadline:
            msg = self.child.recv(max(0.1, deadline - time.time()))
            if msg is None:
                raise ConnectionError(f"EOF waiting for {method}")
            if msg.get("id") == rid and "method" not in msg:
                return msg
            self._absorb(msg)
        raise TimeoutError(f"no response to {method} within {timeout}s")

    def _absorb(self, msg: dict) -> None:
        if "method" in msg and "id" in msg:
            self.child.send({"jsonrpc": "2.0", "id": msg["id"], "error": {"code": -32601, "message": "unsupported"}})
        elif "method" in msg:
            self.notifications.append(msg)

    def wait_notification(self, method: str, timeout: float) -> dict | None:
        deadline = time.time() + timeout
        while time.time() < deadline:
            msg = self.child.recv(max(0.1, deadline - time.time()))
            if msg is None:
                return None
            self._absorb(msg)
            if msg.get("method") == method:
                return msg
        return None


def _toml_string(s: str) -> str:
    return json.dumps(s)


def hook_state_override(hooks: list[dict]) -> str | None:
    untrusted = [h for h in hooks if h.get("trustStatus") != "trusted" and h.get("currentHash") and h.get("key")]
    if not untrusted:
        return None
    entries = ",".join(f"{_toml_string(h['key'])}={{trusted_hash={_toml_string(h['currentHash'])}}}" for h in untrusted)
    return f"hooks.state={{{entries}}}"


def appserver_ask(binary: str, cwd: Path, env: dict, prompt: str, stderr_path: Path,
                  timeout: float = 180.0, extra_argv: list[str] = ()) -> AskResult:
    started = time.time()
    argv = [binary, "app-server", *extra_argv]
    notes = []
    text = ""
    tools = 0
    first = started
    child = JsonlChild(argv, cwd, env, stderr_path)
    prior_frames: list[dict] = []
    try:
        child.start()
        rpc = _Rpc(child)
        init = {"clientInfo": {"name": "kcap-probe", "version": "1"}, "capabilities": {}}
        rpc.request("initialize", init, 60)
        hooks = (rpc.request("hooks/list", {}, 60).get("result") or {}).get("hooks") or []
        override = hook_state_override(hooks)
        if override:
            child.stop()
            prior_frames = list(child.frames)
            argv = [*argv, "-c", override]
            child = JsonlChild(argv, cwd, env, stderr_path)
            child.start()
            rpc = _Rpc(child)
            rpc.request("initialize", init, 60)
            notes.append("hook_trust=seeded")
        elif not hooks:
            notes.append("hook_trust=none")
        elif any(h.get("trustStatus") == "trusted" for h in hooks):
            notes.append("hook_trust=trusted")
        else:
            notes.append("hook_trust=untrusted-unseedable")
        thread = rpc.request("thread/start", {
            "cwd": str(cwd), "sandbox": "read-only", "approvalPolicy": "never", "approvalsReviewer": "user",
        }, 120)
        tid = ((thread.get("result") or {}).get("thread") or {}).get("id")
        if not tid:
            notes.append(f"thread/start failed: {json.dumps(thread)[:500]}")
        else:
            first = time.time()
            rpc.request("turn/start", {
                "threadId": tid, "input": [{"type": "text", "text": prompt}],
                "sandboxPolicy": {"type": "readOnly"}, "approvalPolicy": "never", "approvalsReviewer": "user",
            }, timeout)
            done = rpc.wait_notification("turn/completed", timeout)
            notes.append(f"turn={(((done or {}).get('params') or {}).get('turn') or {}).get('status')}")
            items = [(n.get("params") or {}).get("item") or {} for n in rpc.notifications
                     if n.get("method") == "item/completed"]
            completed = [i["text"] for i in items if i.get("type") == "agentMessage" and isinstance(i.get("text"), str)]
            tools = sum(1 for i in items
                        if i.get("type") and i["type"] not in ("agentMessage", "reasoning", "userMessage"))
            deltas = [n["params"].get("delta", "") for n in rpc.notifications if n.get("method") == "item/agentMessage/delta"]
            text = "\n".join(completed) if completed else "".join(deltas)
    except Exception as ex:  # noqa: BLE001
        notes.append(f"exception={ex!r}")
    finally:
        child.stop()
    # A reply the agent read off disk with a tool is not a loaded skill: the count says which it was.
    notes.append(f"tools_used={tools}")
    return AskResult(reply_text=text, raw=json.dumps(prior_frames + child.frames), argv=argv, started_at=started,
                     first_request_at=first, stderr_path=str(stderr_path), exit_code=child.returncode,
                     notes=" ".join(notes))
