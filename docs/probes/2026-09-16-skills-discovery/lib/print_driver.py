from __future__ import annotations

import subprocess
import time
from pathlib import Path
from typing import Callable

from harness.base import AskResult
from lib.procs import kill_group


def print_ask(
    argv: list[str], cwd: Path, env: dict, stderr_path: Path, timeout: float,
    stdin_text: str | None = None, extract: Callable[[str], str] | None = None,
) -> AskResult:
    started = time.time()
    notes = ""
    with stderr_path.open("ab") as err:
        # Its own session: a vendor that re-execs leaves a grandchild holding stdout, and waiting
        # on the pipe after killing only the child never returns.
        proc = subprocess.Popen(
            argv, cwd=str(cwd), env=env, stdout=subprocess.PIPE, stderr=err, text=True,
            stdin=subprocess.PIPE if stdin_text is not None else subprocess.DEVNULL,
            start_new_session=True,
        )
        try:
            raw, _ = proc.communicate(input=stdin_text, timeout=timeout)
            code = proc.returncode
        except subprocess.TimeoutExpired:
            kill_group(proc.pid)
            proc.kill()
            try:
                raw, _ = proc.communicate(timeout=10)
            except subprocess.TimeoutExpired:
                raw = ""
            code, notes = None, "timeout"
    reply = raw
    if extract is not None:
        try:
            reply = extract(raw)
        except Exception as ex:  # noqa: BLE001
            # A silent fallback to the raw stream reads as a vendor that answered in plain text.
            reply, notes = raw, (notes + f" extract failed: {ex!r}").strip()
    return AskResult(reply_text=reply, raw=raw, argv=list(argv), started_at=started,
                     first_request_at=started, stderr_path=str(stderr_path), exit_code=code, notes=notes)
