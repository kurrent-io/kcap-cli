#!/usr/bin/env python3
"""Fake vendor processes for the kit's self-tests: acp, appserver (codex app-server) and pirpc."""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

TOKEN_RE = re.compile(r"PROBE-BODY-([0-9a-f]{12})")


def answer() -> str:
    lines = []
    for f in sorted(Path.cwd().glob(".fake/skills/kcap-probe-*/SKILL.md")):
        m = TOKEN_RE.search(f.read_text())
        if m:
            lines.append(f"{f.parent.name}=PROBE-BODY-{m.group(1)}")
    return "\n".join(lines) if lines else "NO-SKILL"


def send(obj: dict) -> None:
    sys.stdout.write(json.dumps(obj) + "\n")
    sys.stdout.flush()


def acp() -> None:
    for line in sys.stdin:
        msg = json.loads(line)
        m, i, p = msg.get("method"), msg.get("id"), msg.get("params") or {}
        if m == "initialize":
            send({"jsonrpc": "2.0", "id": i, "result": {"protocolVersion": 1, "agentCapabilities": {}}})
        elif m == "session/new":
            send({"jsonrpc": "2.0", "id": i, "result": {"sessionId": "s1"}})
        elif m == "session/prompt":
            send({"jsonrpc": "2.0", "method": "session/update", "params": {
                "sessionId": p["sessionId"],
                "update": {"sessionUpdate": "agent_message_chunk", "content": {"type": "text", "text": answer()}}}})
            send({"jsonrpc": "2.0", "id": i, "result": {"stopReason": "end_turn"}})
        elif i is not None:
            send({"jsonrpc": "2.0", "id": i, "error": {"code": -32601, "message": "unknown"}})


def appserver() -> None:
    for line in sys.stdin:
        msg = json.loads(line)
        m, i, p = msg.get("method"), msg.get("id"), msg.get("params") or {}
        if m == "initialize":
            send({"jsonrpc": "2.0", "id": i, "result": {}})
        elif m == "hooks/list":
            send({"jsonrpc": "2.0", "id": i, "result": {"hooks": []}})
        elif m == "thread/start":
            send({"jsonrpc": "2.0", "id": i, "result": {"thread": {"id": "t1"}, "model": "fake"}})
        elif m == "turn/start":
            send({"jsonrpc": "2.0", "id": i, "result": {"turn": {"id": "u1"}}})
            send({"jsonrpc": "2.0", "method": "item/completed", "params": {
                "item": {"type": "agentMessage", "id": "m1", "text": answer()}}})
            send({"jsonrpc": "2.0", "method": "turn/completed", "params": {"turn": {"id": "u1", "status": "completed"}}})
        elif i is not None:
            send({"jsonrpc": "2.0", "id": i, "error": {"code": -32601, "message": "unknown"}})


def pirpc() -> None:
    for line in sys.stdin:
        msg = json.loads(line)
        if msg.get("type") == "prompt":
            send({"id": msg.get("id"), "type": "response", "success": True})
            send({"type": "agent_start"})
            send({"type": "message_end", "message": {"role": "assistant", "content": [{"type": "text", "text": answer()}]}})
            send({"type": "agent_settled"})


if __name__ == "__main__":
    {"acp": acp, "appserver": appserver, "app-server": appserver, "pirpc": pirpc}[sys.argv[1]]()
