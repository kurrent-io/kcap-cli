#!/usr/bin/env python3
"""Graceful-stop variant of the ACP reconnect probe: can a vendor's session be reopened with
session/load after the owning process was stopped the way a daemon restart stops it?

The stop mirrors AgentOrchestrator.PrepareForResumeAsync + AcpHostedAgentRuntime.RequestGracefulStopAsync:
session/cancel when a turn is in flight, stdin EOF, up to 5 s for the exit, then a tree SIGKILL
(--kill daemon). --kill sigterm inserts a SIGTERM and 2 s before the kill, so a vendor that saves its
session on a signal is told apart from one that saves it on EOF. The step that ended the process is
recorded.

The reloaded session is asked for a codeword planted in the first turn, so "session/load answered"
is not mistaken for "the conversation came back".

Usage: python3 acp_restart_probe.py --vendor cursor|copilot|kiro|gemini|opencode|grok
                                    --stop idle|midturn --kill daemon|sigterm [--outdir DIR]
Writes <outdir>/<vendor>-<stop>-<kill>/frames.jsonl, stderr-*.log, summary.json.
"""

import argparse
import asyncio
import json
import os
import secrets
import shutil
import signal
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "2026-08-04-acp-reconnect-c0"))
from acp_c0_probe import AcpClient, CONVERSATION_KINDS, run_turn, update_kind  # noqa: E402

VENDORS = {
    "cursor":   {"argv": ["cursor-agent", "acp"]},
    "copilot":  {"argv": ["copilot", "--acp", "--stdio"]},
    "kiro":     {"argv": ["kiro-cli", "acp"]},
    "gemini":   {"argv": ["gemini", "--experimental-acp", "--skip-trust",
                          "--allowed-mcp-server-names", "__kcap_probe_unmatchable__"]},
    "opencode": {"argv": ["opencode", "acp"], "env": {"OPENCODE_PURE": "1"}},
    "grok":     {"argv": ["grok", "agent", "--no-leader", "stdio"]},
}

LONG_TURN = ("Run the shell command `sleep 20 && echo kcap-probe-long-done` and then reply with "
             "exactly: long-done")
TOOL_TURN = "Run the shell command `echo kcap-probe-tool` and then reply with exactly: tool-done"


def now_iso():
    return datetime.now(timezone.utc).isoformat()


class GroupedClient(AcpClient):
    """Spawns the vendor in its own process group so the stop can reach a self-re-exec'd child.

    The trailer phase starts on the reader the moment the load response is recorded: switching it
    where the request resumes would tag updates the reader dispatched in between as replay."""

    load_id = None
    load_label = None

    def record(self, direction, obj, note=None):
        super().record(direction, obj, note)
        if (direction == "in" and self.load_id is not None and obj.get("id") == self.load_id
                and ("result" in obj or "error" in obj)):
            self.phase_ref[0] = f"{self.load_label}_trailer"
            self.load_id = None

    async def start(self):
        env = dict(os.environ)
        env.update(self.extra_env)
        stderr_f = open(self.stderr_path, "ab")
        self.proc = await asyncio.create_subprocess_exec(
            *self.argv, cwd=self.cwd, env=env,
            stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE,
            stderr=stderr_f, start_new_session=True)
        self.reader_task = asyncio.create_task(self._read_loop())
        self.record("mark", {"event": "spawned", "pid": self.proc.pid, "argv": self.argv})


def group_alive(pgid):
    try:
        os.killpg(pgid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        return True


async def graceful_stop(client, in_turn, sigterm):
    """Returns how the process ended: eof, sigterm or sigkill, plus whether the group outlived it."""
    pid = client.proc.pid
    t0 = time.monotonic()
    result = {"cancel_sent": False}

    if in_turn:
        try:
            await asyncio.wait_for(client.notify("session/cancel", {"sessionId": client.session_id}), 2)
            result["cancel_sent"] = True
        except Exception as ex:  # noqa: BLE001
            result["cancel_error"] = repr(ex)

    try:
        client.proc.stdin.close()
    except Exception as ex:  # noqa: BLE001
        result["eof_error"] = repr(ex)
    client.record("mark", {"event": "stdin_eof"})

    step = "eof"
    try:
        await asyncio.wait_for(client.proc.wait(), 5)
    except asyncio.TimeoutError:
        step = "sigterm" if sigterm else "sigkill"
    if step == "sigterm":
        client.record("mark", {"event": "sigterm", "pid": pid})
        try:
            client.proc.send_signal(signal.SIGTERM)
        except ProcessLookupError:
            pass
        try:
            await asyncio.wait_for(client.proc.wait(), 2)
        except asyncio.TimeoutError:
            step = "sigkill"
    if step == "sigkill":
        client.record("mark", {"event": "sigkill", "pid": pid})
        try:
            os.killpg(pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        await client.proc.wait()

    result["ended_by"] = step
    result["exit_code"] = client.proc.returncode
    result["elapsed_s"] = round(time.monotonic() - t0, 2)

    # The daemon's tree kill reaps what the leader left; record whether there was anything to reap.
    await asyncio.sleep(0.5)
    result["group_outlived_leader"] = group_alive(pid)
    if result["group_outlived_leader"]:
        try:
            os.killpg(pid, signal.SIGKILL)
        except ProcessLookupError:
            pass

    if client.reader_task:
        await asyncio.wait([client.reader_task], timeout=5)
    client.record("mark", {"event": "stopped", **result})
    return result


async def handshake(client):
    init = await client.request("initialize", {
        "protocolVersion": 1,
        "clientCapabilities": {"fs": {"readTextFile": False, "writeTextFile": False},
                                 "terminal": False}}, timeout=90)
    return (init.get("result") or {}).get("agentCapabilities") or {}


def agent_text(frames, phase):
    out = []
    for fr in frames:
        if fr["dir"] != "in" or fr["phase"] != phase:
            continue
        obj = fr["frame"]
        if obj.get("method") == "session/update" and update_kind(obj) == "agent_message_chunk":
            out.append(((obj["params"]["update"].get("content")) or {}).get("text") or "")
    return "".join(out)


def vendor_version(argv0):
    try:
        r = subprocess.run([argv0, "--version"], capture_output=True, text=True, timeout=30)
        return (r.stdout or r.stderr).strip().splitlines()[0][:120]
    except Exception as ex:  # noqa: BLE001
        return f"unknown ({ex!r})"


async def load_session(client, cwd, summary, frames, phase_ref, label):
    phase_ref[0] = f"{label}_replay"
    client.load_id, client.load_label = client.next_id + 1, label
    load_ok, load_err = False, None
    try:
        resp = await client.request(
            "session/load", {"sessionId": client.session_id, "cwd": cwd, "mcpServers": []},
            timeout=240)
        load_ok = "error" not in resp
        load_err = resp.get("error")
    except (asyncio.TimeoutError, ConnectionError) as ex:
        load_err = repr(ex)
    client.load_id = None
    phase_ref[0] = f"{label}_trailer"
    client.record("mark", {"event": "load_response_received", "ok": load_ok})
    await asyncio.sleep(3)

    replay = [update_kind(fr["frame"]) for fr in frames
              if fr["dir"] == "in" and fr["phase"] == f"{label}_replay"
              and fr["frame"].get("method") == "session/update"]
    trailer = [update_kind(fr["frame"]) for fr in frames
               if fr["dir"] == "in" and fr["phase"] == f"{label}_trailer"
               and fr["frame"].get("method") == "session/update"]
    summary[label] = {
        "ok": load_ok, "error": load_err,
        "replayed_updates": len(replay),
        "replayed_kinds": sorted(set(k for k in replay if k)),
        "conversation_updates_after_response": sum(1 for k in trailer if k in CONVERSATION_KINDS),
        "trailer_kinds": sorted(set(k for k in trailer if k)),
    }
    return load_ok


async def probe(vendor, stop, kill, outdir):
    spec = VENDORS[vendor]
    if not shutil.which(spec["argv"][0]):
        print(f"{spec['argv'][0]} is not installed", file=sys.stderr)
        return {"vendor": vendor, "stop": stop, "skipped": "not installed"}

    vdir = Path(outdir) / f"{vendor}-{stop}-{kill}"
    vdir.mkdir(parents=True, exist_ok=True)
    cwd = vdir / "cwd"
    cwd.mkdir(exist_ok=True)
    subprocess.run(["git", "init", "-q"], cwd=cwd, check=False)

    frames, phase_ref = [], ["init1"]
    codeword = f"PELICAN-{secrets.token_hex(3).upper()}"
    summary = {"vendor": vendor, "stop": stop, "kill": kill, "version": vendor_version(spec["argv"][0]),
               "argv": spec["argv"], "started": now_iso(), "codeword": codeword}

    def note(msg):
        print(f"  [{vendor}/{stop}/{kill}] {msg}", flush=True)

    c1 = GroupedClient(spec["argv"], str(cwd), frames, phase_ref, "child1",
                       vdir / "stderr-child1.log", spec.get("env"))
    c2 = None
    try:
        await c1.start()
        caps = await handshake(c1)
        summary["load_session_advertised"] = bool(caps.get("loadSession"))

        phase_ref[0] = "session_new"
        sn = await c1.request("session/new", {"cwd": str(cwd), "mcpServers": []}, timeout=120)
        c1.session_id = (sn.get("result") or {}).get("sessionId")
        summary["session_id"] = c1.session_id
        if not c1.session_id:
            summary["session_new_error"] = sn.get("error")
            return summary
        note(f"session {c1.session_id}")

        ra = await run_turn(c1, f"Remember this codeword for later: {codeword}. Reply with exactly: noted",
                            "turn_a", phase_ref)
        rc = await run_turn(c1, TOOL_TURN, "turn_c", phase_ref)
        summary["turn_a_stop"] = ((ra or {}).get("result") or {}).get("stopReason")
        summary["turn_c_stop"] = ((rc or {}).get("result") or {}).get("stopReason")
        note(f"turns: {summary['turn_a_stop']}/{summary['turn_c_stop']}")

        in_turn = False
        if stop == "midturn":
            tool_seen = asyncio.get_event_loop().create_future()

            def on_tool(obj):
                if update_kind(obj) == "tool_call" and not tool_seen.done():
                    tool_seen.set_result(True)
                return False

            c1.update_waiters.append((on_tool, asyncio.get_event_loop().create_future()))
            long_task = asyncio.create_task(run_turn(c1, LONG_TURN, "turn_long", phase_ref))
            try:
                await asyncio.wait_for(tool_seen, 120)
                await asyncio.sleep(3)
                in_turn = not long_task.done()
            except asyncio.TimeoutError:
                note("no tool_call within 120s; stopping anyway")
                in_turn = not long_task.done()
            summary["stopped_mid_turn"] = in_turn

        phase_ref[0] = "stop1"
        summary["stop1"] = await graceful_stop(c1, in_turn, kill == "sigterm")
        note(f"stop: {summary['stop1']}")

        c2 = GroupedClient(spec["argv"], str(cwd), frames, phase_ref, "child2",
                           vdir / "stderr-child2.log", spec.get("env"))
        c2.session_id = c1.session_id
        await c2.start()
        phase_ref[0] = "init2"
        caps2 = await handshake(c2)
        summary["load_session_advertised_after"] = bool(caps2.get("loadSession"))

        ok = await load_session(c2, str(cwd), summary, frames, phase_ref, "load")
        note(f"load: {summary['load']}")
        if not ok:
            await asyncio.sleep(15)
            ok = await load_session(c2, str(cwd), summary, frames, phase_ref, "load_retry_15s")
            note(f"load retry: {summary['load_retry_15s']}")

        if ok:
            re = await run_turn(c2, "What codeword did I ask you to remember? Reply with only the codeword.",
                                "turn_recall", phase_ref)
            summary["recall_stop"] = ((re or {}).get("result") or {}).get("stopReason")
            answer = agent_text(frames, "turn_recall")
            summary["recall_answer"] = answer.strip()[:200]
            summary["codeword_recalled"] = codeword in answer
            note(f"recall: {summary['recall_stop']} recalled={summary['codeword_recalled']}")

        phase_ref[0] = "stop2"
        summary["stop2"] = await graceful_stop(c2, False, kill == "sigterm")
        c2 = None
    finally:
        for c in (c1, c2):
            if c is not None and c.proc is not None and c.proc.returncode is None:
                try:
                    os.killpg(c.proc.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
        summary["ended"] = now_iso()
        with open(vdir / "frames.jsonl", "w") as f:
            for fr in frames:
                f.write(json.dumps(fr) + "\n")
        with open(vdir / "summary.json", "w") as f:
            json.dump(summary, f, indent=2)
    return summary


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--vendor", required=True, choices=sorted(VENDORS))
    ap.add_argument("--stop", required=True, choices=["idle", "midturn"])
    ap.add_argument("--kill", required=True, choices=["daemon", "sigterm"])
    ap.add_argument("--outdir", default=str(Path(__file__).parent / "out"))
    args = ap.parse_args()
    t0 = time.time()
    summary = await probe(args.vendor, args.stop, args.kill, args.outdir)
    print(f"== {args.vendor}/{args.stop}/{args.kill} done in {time.time() - t0:.0f}s ==", flush=True)
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    asyncio.run(main())
