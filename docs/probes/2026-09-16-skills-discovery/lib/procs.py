from __future__ import annotations

import os
import signal


def kill_group(pid: int) -> None:
    """Kill every process in the child's own session: a vendor that re-execs itself leaves a
    grandchild holding the pipes, and a pipe still open is a read that never returns."""
    try:
        os.killpg(pid, signal.SIGKILL)
    except (ProcessLookupError, PermissionError):
        pass
