from __future__ import annotations

import asyncio
import json
import sys
import time
from pathlib import Path

from harness.base import AskResult, Session
from lib.procs import kill_group

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
        self.stderr_f = None

    async def start(self):
        self.stderr_f = open(self.stderr_path, "ab")
        # Its own session, so a vendor that re-execs itself leaves no grandchild holding the
        # pipes open after the child is gone (an open pipe is a shutdown that never returns).
        self.proc = await asyncio.create_subprocess_exec(
            *self.argv, cwd=self.cwd, env=self.env, start_new_session=True,
            stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=self.stderr_f)
        self.reader_task = asyncio.create_task(self._read_loop())
        self.record("mark", {"event": "spawned", "pid": self.proc.pid, "argv": self.argv})

    async def shutdown(self, hard_after=5):
        # asyncio's Process.wait() only completes once every pipe is closed, so a grandchild that
        # inherited stdout keeps the base class's wait from ever returning: the whole session is
        # killed as soon as a graceful exit has not happened.
        if self.proc is not None:
            if self.proc.returncode is None:
                try:
                    self.proc.terminate()
                except ProcessLookupError:
                    pass
                try:
                    await asyncio.wait_for(self.proc.wait(), hard_after)
                except asyncio.TimeoutError:
                    kill_group(self.proc.pid)
                    try:
                        await asyncio.wait_for(self.proc.wait(), hard_after)
                    except asyncio.TimeoutError:
                        pass
            kill_group(self.proc.pid)
            if self.reader_task is not None:
                await asyncio.wait([self.reader_task], timeout=5)
        if self.stderr_f is not None:
            self.stderr_f.close()
            self.stderr_f = None


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


def tool_calls(frames: list[dict]) -> int:
    n = 0
    for f in frames:
        fr = f.get("frame") or {}
        if fr.get("method") != "session/update":
            continue
        if ((fr.get("params") or {}).get("update") or {}).get("sessionUpdate") == "tool_call":
            n += 1
    return n


class AcpSession(Session):
    """One ACP agent process holding one session across prompts."""

    def __init__(self, argv: list[str], cwd: Path, env: dict, stderr_path: Path, timeout: float = 180.0) -> None:
        self.argv = list(argv)
        self.cwd = Path(cwd)
        self.stderr_path = stderr_path
        self.timeout = timeout
        self.loop = asyncio.new_event_loop()
        self.client = IsolatedAcpClient(self.argv, str(self.cwd), env, stderr_path)
        self.sid: str | None = None
        self.notes: list[str] = []
        self.started_at = time.time()

    def start(self) -> None:
        self.loop.run_until_complete(self._start())

    async def _start(self) -> None:
        await self.client.start()
        await self.client.request("initialize", INIT_PARAMS, timeout=90)
        new = await self.client.request("session/new", {"cwd": str(self.cwd), "mcpServers": []}, timeout=120)
        self.sid = (new.get("result") or {}).get("sessionId")
        if not self.sid:
            self.notes.append(f"session/new failed: {json.dumps(new)[:500]}")

    def ask(self, prompt: str) -> AskResult:
        first = time.time()
        before = len(self.client.frames)
        notes = list(self.notes)
        text = ""
        try:
            if self.sid:
                resp = self.loop.run_until_complete(self.client.request(
                    "session/prompt", {"sessionId": self.sid, "prompt": [{"type": "text", "text": prompt}]},
                    timeout=self.timeout))
                notes.append(f"stopReason={(resp.get('result') or {}).get('stopReason')}")
                if "error" in resp:
                    notes.append(f"error={json.dumps(resp['error'])[:500]}")
                text = agent_text(self.client.frames[before:])
        except Exception as ex:  # noqa: BLE001
            notes.append(f"exception={ex!r}")
        # A reply the agent read off disk with a tool is not a loaded skill: the count says which it was.
        notes.append(f"tools_used={tool_calls(self.client.frames[before:])}")
        exit_code = self.client.proc.returncode if self.client.proc is not None else None
        return AskResult(reply_text=text, raw=json.dumps(self.client.frames), argv=list(self.argv),
                         started_at=self.started_at, first_request_at=first, stderr_path=str(self.stderr_path),
                         exit_code=exit_code, notes=" ".join(notes))

    def close(self) -> None:
        try:
            self.loop.run_until_complete(self.client.shutdown())
        finally:
            self.loop.close()


def acp_ask(argv: list[str], cwd: Path, env: dict, prompt: str, stderr_path: Path, timeout: float = 180.0) -> AskResult:
    session = AcpSession(argv, cwd, env, stderr_path, timeout)
    try:
        try:
            session.start()
        except Exception as ex:  # noqa: BLE001
            session.notes.append(f"exception={ex!r}")
        return session.ask(prompt)
    finally:
        session.close()
