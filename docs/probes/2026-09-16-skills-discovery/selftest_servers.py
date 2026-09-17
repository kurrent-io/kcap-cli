#!/usr/bin/env python3
"""Fake vendor processes for the kit's self-tests: acp, appserver (codex app-server) and pirpc."""
from __future__ import annotations

import json
import os
import subprocess
import re
import sys
from pathlib import Path

TOKEN_RE = re.compile(r"PROBE-BODY-([0-9a-f]{12})")
UNTRUSTED_HOOK_KEY = "/x/hooks.json:SessionStart:0:0"
UNTRUSTED_HOOK_HASH = "sha256:fake"


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


def uses_tool() -> bool:
    return os.environ.get("KCAP_FAKE_TOOL_CALL") == "1"


def acp() -> None:
    if os.environ.get("KCAP_FAKE_GRANDCHILD") == "1":
        subprocess.Popen(["sleep", "60"])
    for line in sys.stdin:
        msg = json.loads(line)
        m, i, p = msg.get("method"), msg.get("id"), msg.get("params") or {}
        if m == "initialize":
            send({"jsonrpc": "2.0", "id": i, "result": {"protocolVersion": 1, "agentCapabilities": {}}})
        elif m == "session/new":
            send({"jsonrpc": "2.0", "id": i, "result": {"sessionId": "s1"}})
        elif m == "session/prompt":
            if uses_tool():
                send({"jsonrpc": "2.0", "method": "session/update", "params": {
                    "sessionId": p["sessionId"],
                    "update": {"sessionUpdate": "tool_call", "toolCallId": "t1", "title": "read",
                               "status": "completed"}}})
            send({"jsonrpc": "2.0", "method": "session/update", "params": {
                "sessionId": p["sessionId"],
                "update": {"sessionUpdate": "agent_message_chunk", "content": {"type": "text", "text": answer()}}}})
            send({"jsonrpc": "2.0", "id": i, "result": {"stopReason": "end_turn"}})
        elif i is not None:
            send({"jsonrpc": "2.0", "id": i, "error": {"code": -32601, "message": "unknown"}})


def hooks_list() -> list[dict]:
    if os.environ.get("KCAP_FAKE_UNTRUSTED_HOOK") != "1":
        return []
    seeded = any(a.startswith("hooks.state=") for a in sys.argv)
    return [{"key": UNTRUSTED_HOOK_KEY, "trustStatus": "trusted" if seeded else "untrusted",
             "currentHash": UNTRUSTED_HOOK_HASH}]


def appserver() -> None:
    for line in sys.stdin:
        msg = json.loads(line)
        m, i, p = msg.get("method"), msg.get("id"), msg.get("params") or {}
        if m == "initialize":
            send({"jsonrpc": "2.0", "id": i, "result": {}})
        elif m == "hooks/list":
            # The real app-server groups hooks per working directory under `data`.
            send({"jsonrpc": "2.0", "id": i, "result": {"data": [
                {"cwd": str(Path.cwd()), "hooks": hooks_list(), "warnings": [], "errors": []}]}})
        elif m == "thread/start":
            send({"jsonrpc": "2.0", "id": i, "result": {"thread": {"id": "t1"}, "model": "fake"}})
        elif m == "turn/start":
            send({"jsonrpc": "2.0", "id": i, "result": {"turn": {"id": "u1"}}})
            if uses_tool():
                send({"jsonrpc": "2.0", "method": "item/completed", "params": {
                    "item": {"type": "commandExecution", "id": "c1", "command": "cat SKILL.md"}}})
            send({"jsonrpc": "2.0", "method": "item/completed", "params": {
                "item": {"type": "agentMessage", "id": "m1", "text": answer()}}})
            send({"jsonrpc": "2.0", "method": "turn/completed", "params": {"turn": {"id": "u1", "status": "completed"}}})
        elif i is not None:
            send({"jsonrpc": "2.0", "id": i, "error": {"code": -32601, "message": "unknown"}})


def pirpc() -> None:
    if os.environ.get("KCAP_FAKE_GRANDCHILD") == "1":
        # A vendor that re-execs leaves a process like this one holding stdout after it exits.
        subprocess.Popen(["sleep", "60"])
    for line in sys.stdin:
        msg = json.loads(line)
        if msg.get("type") == "prompt":
            send({"id": msg.get("id"), "type": "response", "success": True})
            send({"type": "agent_start"})
            content = [{"type": "text", "text": answer()}]
            if uses_tool():
                # A real Pi tool call shows up twice: as a toolCall part and as a tool_execution_end.
                content.insert(0, {"type": "toolCall", "id": "t1", "name": "read", "arguments": {}})
                send({"type": "tool_execution_end", "toolCallId": "t1", "status": "success"})
            send({"type": "message_end", "message": {"role": "assistant", "content": content}})
            send({"type": "agent_settled"})


def tui() -> None:
    hook = os.environ.get("KCAP_FAKE_HOOK")
    if hook:
        subprocess.run([hook], input="{}", capture_output=True, text=True, timeout=10)
    frozen = answer() if os.environ.get("KCAP_FAKE_TUI_FROZEN") == "1" else None
    out = sys.stdout
    out.write("\x1b[1mfake tui\x1b[0m ready\r\n")
    if os.environ.get("KCAP_FAKE_TUI_DIALOG") == "1":
        out.write("Do you trust this folder? (y/n) ")
        out.flush()
        if not sys.stdin.readline().strip().lower().startswith("y"):
            return
    out.write("> ")
    out.flush()
    for line in sys.stdin:
        line = line.strip()
        if line in ("/exit", "/quit"):
            return
        if line == "/reload":
            if frozen is not None:
                frozen = answer()
            out.write("reloaded\r\n> ")
        elif line.startswith("You have a skill"):
            reply = frozen if frozen is not None else answer()
            entries = [] if reply == "NO-SKILL" else reply.splitlines()
            # The prompt embeds the queried skill's name; a reply for some other skill still on
            # disk must not be mistaken for it.
            match = next((e for e in entries if e.split("=", 1)[0] in line), None)
            value = match.split("=", 1)[1] if match else "NO-SKILL"
            out.write(f"\x1b[32m**PROBE-REPLY: {value}**\x1b[0m\r\n> ")
        else:
            out.write("?\r\n> ")
        out.flush()


if __name__ == "__main__":
    {"acp": acp, "appserver": appserver, "app-server": appserver, "pirpc": pirpc, "tui": tui}[sys.argv[1]]()
