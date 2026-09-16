from __future__ import annotations

import json
import time
from pathlib import Path

from harness.base import AskResult
from lib.jsonl_child import JsonlChild


def pirpc_ask(argv: list[str], cwd: Path, env: dict, prompt: str, stderr_path: Path, timeout: float = 180.0) -> AskResult:
    started = time.time()
    child = JsonlChild(argv, cwd, env, stderr_path)
    texts, notes = [], []
    tools = 0
    first = started
    try:
        child.start()
        first = time.time()
        child.send({"id": "1", "type": "prompt", "message": prompt, "streamingBehavior": "followUp"})
        deadline = time.time() + timeout
        while time.time() < deadline:
            msg = child.recv(max(0.1, deadline - time.time()))
            if msg is None:
                notes.append("eof before agent_settled")
                break
            t = msg.get("type")
            if t == "message_end" and (msg.get("message") or {}).get("role") == "assistant":
                for part in (msg["message"].get("content") or []):
                    if isinstance(part, dict) and part.get("type") == "text":
                        texts.append(part.get("text", ""))
                    elif isinstance(part, dict) and part.get("type") == "toolCall":
                        tools += 1
                if msg["message"].get("stopReason") == "error":
                    notes.append(f"error={msg['message'].get('errorMessage')}")
            elif t == "tool_execution_end":
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
    finally:
        child.stop()
    # A reply the agent read off disk with a tool is not a loaded skill: the count says which it was.
    notes.append(f"tools_used={tools}")
    return AskResult(reply_text="\n".join(texts), raw=json.dumps(child.frames), argv=list(argv), started_at=started,
                     first_request_at=first, stderr_path=str(stderr_path), exit_code=child.returncode,
                     notes=" ".join(notes))
