from __future__ import annotations

import subprocess
import time
from pathlib import Path
from typing import Callable

from harness.base import AskResult


def print_ask(
    argv: list[str], cwd: Path, env: dict, stderr_path: Path, timeout: float,
    stdin_text: str | None = None, extract: Callable[[str], str] | None = None,
) -> AskResult:
    started = time.time()
    with stderr_path.open("ab") as err:
        try:
            proc = subprocess.run(
                argv, cwd=str(cwd), env=env, input=stdin_text, capture_output=False,
                stdout=subprocess.PIPE, stderr=err, text=True, timeout=timeout,
                stdin=None if stdin_text is not None else subprocess.DEVNULL,
            )
            raw, code = proc.stdout, proc.returncode
        except subprocess.TimeoutExpired as ex:
            raw = (ex.stdout or b"").decode("utf-8", "replace") if isinstance(ex.stdout, bytes) else (ex.stdout or "")
            code = None
    reply = raw
    if extract is not None:
        try:
            reply = extract(raw)
        except Exception:  # noqa: BLE001
            reply = raw
    return AskResult(reply_text=reply, raw=raw, argv=list(argv), started_at=started,
                     first_request_at=started, stderr_path=str(stderr_path), exit_code=code)
