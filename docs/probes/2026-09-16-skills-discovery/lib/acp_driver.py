from __future__ import annotations

import asyncio
import json
import sys
import time
from pathlib import Path

from harness.base import AskResult

KIT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(KIT.parent / "2026-08-04-acp-reconnect-c0"))
from acp_c0_probe import AcpClient  # noqa: E402

INIT_PARAMS = {
    "protocolVersion": 1,
    "clientCapabilities": {"fs": {"readTextFile": False, "writeTextFile": False}, "terminal": False},
}


class IsolatedAcpClient(AcpClient):
    """AcpClient whose child sees exactly the sandbox environment, not the developer's."""

    def __init__(self, argv, cwd, env, stderr_path):
        super().__init__(argv, cwd, frames=[], phase_ref=["turn"], label="acp", stderr_path=str(stderr_path))
        self.env = env

    async def start(self):
        stderr_f = open(self.stderr_path, "ab")
        self.proc = await asyncio.create_subprocess_exec(
            *self.argv, cwd=self.cwd, env=self.env,
            stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=stderr_f)
        self.reader_task = asyncio.create_task(self._read_loop())
        self.record("mark", {"event": "spawned", "pid": self.proc.pid, "argv": self.argv})


def agent_text(frames: list[dict]) -> str:
    out = []
    for f in frames:
        fr = f.get("frame") or {}
        if fr.get("method") != "session/update":
            continue
        upd = ((fr.get("params") or {}).get("update") or {})
        if upd.get("sessionUpdate") not in ("agent_message_chunk", "agent_message"):
            continue
        content = upd.get("content")
        if isinstance(content, dict) and isinstance(content.get("text"), str):
            out.append(content["text"])
        elif isinstance(content, list):
            out.extend(c["text"] for c in content if isinstance(c, dict) and isinstance(c.get("text"), str))
    return "".join(out)


async def _turn(argv, cwd, env, prompt, stderr_path, timeout) -> AskResult:
    client = IsolatedAcpClient(argv, str(cwd), env, stderr_path)
    started = time.time()
    first = started
    text, notes = "", []
    await client.start()
    try:
        await client.request("initialize", INIT_PARAMS, timeout=90)
        new = await client.request("session/new", {"cwd": str(cwd), "mcpServers": []}, timeout=120)
        sid = (new.get("result") or {}).get("sessionId")
        if not sid:
            notes.append(f"session/new failed: {json.dumps(new)[:500]}")
        else:
            first = time.time()
            resp = await client.request(
                "session/prompt", {"sessionId": sid, "prompt": [{"type": "text", "text": prompt}]}, timeout=timeout)
            notes.append(f"stopReason={(resp.get('result') or {}).get('stopReason')}")
            if "error" in resp:
                notes.append(f"error={json.dumps(resp['error'])[:500]}")
            text = agent_text(client.frames)
    except Exception as ex:  # noqa: BLE001
        notes.append(f"exception={ex!r}")
    finally:
        await client.shutdown()
    return AskResult(reply_text=text, raw=json.dumps(client.frames), argv=list(argv), started_at=started,
                     first_request_at=first, stderr_path=str(stderr_path), exit_code=client.proc.returncode,
                     notes=" ".join(notes))


def acp_ask(argv: list[str], cwd: Path, env: dict, prompt: str, stderr_path: Path, timeout: float = 180.0) -> AskResult:
    return asyncio.run(_turn(argv, cwd, env, prompt, stderr_path, timeout))
