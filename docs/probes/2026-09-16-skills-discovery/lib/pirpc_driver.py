from __future__ import annotations

import json
import time
from pathlib import Path

from harness.base import AskResult, Session
from lib.jsonl_child import JsonlChild


class PiRpcSession(Session):
    def __init__(self, argv: list[str], cwd: Path, env: dict, stderr_path: Path, timeout: float = 180.0) -> None:
        self.argv = list(argv)
        self.stderr_path = stderr_path
        self.timeout = timeout
        self.child = JsonlChild(self.argv, cwd, env, stderr_path)
        self.next_id = 0
        self.notes: list[str] = []
        self.started_at = time.time()

    def start(self) -> None:
        self.child.start()

    def ask(self, prompt: str) -> AskResult:
        first = time.time()
        texts, notes = [], list(self.notes)
        tools = 0
        self.next_id += 1
        try:
            self.child.send({"id": str(self.next_id), "type": "prompt", "message": prompt, "streamingBehavior": "followUp"})
            deadline = time.time() + self.timeout
            while time.time() < deadline:
                msg = self.child.recv(max(0.1, deadline - time.time()))
                if msg is None:
                    notes.append("eof before agent_settled")
                    break
                t = msg.get("type")
                if t == "message_end" and (msg.get("message") or {}).get("role") == "assistant":
                    for part in (msg["message"].get("content") or []):
                        if isinstance(part, dict) and part.get("type") == "text":
                            texts.append(part.get("text", ""))
                    if msg["message"].get("stopReason") == "error":
                        notes.append(f"error={msg['message'].get('errorMessage')}")
                elif t == "tool_execution_end":
                    # A tool call also appears as a toolCall part of the message; count it once.
                    tools += 1
                elif t == "response" and msg.get("success") is False:
                    notes.append(f"prompt rejected: {json.dumps(msg)[:300]}")
                elif t == "extension_ui_request":
                    notes.append(f"blocked on ui request {msg.get('method')}")
                    break
                elif t == "agent_settled":
                    break
            else:
                notes.append("timeout")
        except Exception as ex:  # noqa: BLE001
            notes.append(f"exception={ex!r}")
        # A reply the agent read off disk with a tool is not a loaded skill: the count says which it was.
        notes.append(f"tools_used={tools}")
        return AskResult(reply_text="\n".join(texts), raw=json.dumps(self.child.frames), argv=list(self.argv),
                         started_at=self.started_at, first_request_at=first, stderr_path=str(self.stderr_path),
                         exit_code=self.child.returncode, notes=" ".join(notes))

    def close(self) -> None:
        self.child.stop()


def pirpc_ask(argv: list[str], cwd: Path, env: dict, prompt: str, stderr_path: Path, timeout: float = 180.0) -> AskResult:
    session = PiRpcSession(argv, cwd, env, stderr_path, timeout)
    try:
        try:
            session.start()
        except Exception as ex:  # noqa: BLE001
            session.notes.append(f"exception={ex!r}")
        return session.ask(prompt)
    finally:
        session.close()
