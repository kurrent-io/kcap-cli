from __future__ import annotations

import json
import time
from pathlib import Path

from harness.base import AskResult, Session
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


class AppServerSession(Session):
    def __init__(self, binary: str, cwd: Path, env: dict, stderr_path: Path, timeout: float = 180.0,
                 extra_argv: list[str] = ()) -> None:
        self.argv = [binary, "app-server", *extra_argv]
        self.cwd = Path(cwd)
        self.env = env
        self.stderr_path = stderr_path
        self.timeout = timeout
        self.child = JsonlChild(self.argv, self.cwd, env, stderr_path)
        self.rpc: _Rpc | None = None
        self.tid: str | None = None
        self.notes: list[str] = []
        self._raw_from = 0
        self.prior_frames: list[dict] = []
        self.started_at = time.time()

    def start(self) -> None:
        try:
            self._start()
        except BaseException:
            # The child is already spawned, and a caller that never got the session cannot close it.
            self.close()
            raise

    def _start(self) -> None:
        self.child.start()
        self.rpc = _Rpc(self.child)
        init = {"clientInfo": {"name": "kcap-probe", "version": "1"}, "capabilities": {}}
        self.rpc.request("initialize", init, 60)
        listed = self.rpc.request("hooks/list", {}, 60).get("result") or {}
        # The app-server groups hooks per cwd under `data`; a flat `hooks` list is kept for safety.
        hooks = list(listed.get("hooks") or [])
        for group in listed.get("data") or []:
            hooks += list(group.get("hooks") or [])
        override = hook_state_override(hooks)
        if override:
            self.child.stop()
            self.prior_frames = list(self.child.frames)
            self.argv = [*self.argv, "-c", override]
            self.child = JsonlChild(self.argv, self.cwd, self.env, self.stderr_path)
            self.child.start()
            self.rpc = _Rpc(self.child)
            self.rpc.request("initialize", init, 60)
            self.notes.append("hook_trust=seeded")
        elif not hooks:
            self.notes.append("hook_trust=none")
        elif any(h.get("trustStatus") == "trusted" for h in hooks):
            self.notes.append("hook_trust=trusted")
        else:
            self.notes.append("hook_trust=untrusted-unseedable")
        thread = self.rpc.request("thread/start", {
            "cwd": str(self.cwd), "sandbox": "read-only", "approvalPolicy": "never", "approvalsReviewer": "user",
        }, 120)
        self.tid = ((thread.get("result") or {}).get("thread") or {}).get("id")
        if not self.tid:
            self.notes.append(f"thread/start failed: {json.dumps(thread)[:500]}")

    def ask(self, prompt: str) -> AskResult:
        first = time.time()
        notes = list(self.notes)
        text, tools = "", 0
        before = len(self.rpc.notifications) if self.rpc is not None else 0
        try:
            if self.tid and self.rpc is not None:
                self.rpc.request("turn/start", {
                    "threadId": self.tid, "input": [{"type": "text", "text": prompt}],
                    "sandboxPolicy": {"type": "readOnly"}, "approvalPolicy": "never", "approvalsReviewer": "user",
                }, self.timeout)
                done = self.rpc.wait_notification("turn/completed", self.timeout)
                notes.append(f"turn={(((done or {}).get('params') or {}).get('turn') or {}).get('status')}")
                fresh = self.rpc.notifications[before:]
                items = [(n.get("params") or {}).get("item") or {} for n in fresh if n.get("method") == "item/completed"]
                completed = [i["text"] for i in items if i.get("type") == "agentMessage" and isinstance(i.get("text"), str)]
                tools = sum(1 for i in items
                            if i.get("type") and i["type"] not in ("agentMessage", "reasoning", "userMessage"))
                deltas = [n["params"].get("delta", "") for n in fresh if n.get("method") == "item/agentMessage/delta"]
                text = "\n".join(completed) if completed else "".join(deltas)
        except Exception as ex:  # noqa: BLE001
            notes.append(f"exception={ex!r}")
        # A reply the agent read off disk with a tool is not a loaded skill: the count says which it was.
        notes.append(f"tools_used={tools}")
        # Each turn's raw stream starts where the previous one ended (the first includes the
        # startup exchange), so a reply parsed from raw cannot credit an earlier turn's token.
        stream = self.prior_frames + self.child.frames
        raw = json.dumps(stream[self._raw_from:])
        self._raw_from = len(stream)
        return AskResult(reply_text=text, raw=raw, argv=list(self.argv),
                         started_at=self.started_at, first_request_at=first, stderr_path=str(self.stderr_path),
                         exit_code=self.child.returncode, notes=" ".join(notes))

    def close(self) -> None:
        self.child.stop()


def appserver_ask(binary: str, cwd: Path, env: dict, prompt: str, stderr_path: Path,
                  timeout: float = 180.0, extra_argv: list[str] = ()) -> AskResult:
    session = AppServerSession(binary, cwd, env, stderr_path, timeout, extra_argv)
    try:
        try:
            session.start()
        except Exception as ex:  # noqa: BLE001
            session.notes.append(f"exception={ex!r}")
        return session.ask(prompt)
    finally:
        session.close()
