from __future__ import annotations

import json
import queue
import subprocess
import threading
import time
from pathlib import Path


class JsonlChild:
    def __init__(self, argv: list[str], cwd: Path, env: dict, stderr_path: Path) -> None:
        self.argv = list(argv)
        self.cwd = str(cwd)
        self.env = env
        self.stderr_path = stderr_path
        self.frames: list[dict] = []
        self._q: queue.Queue = queue.Queue()
        self.proc: subprocess.Popen | None = None

    def start(self) -> None:
        self._err = open(self.stderr_path, "ab")
        self.proc = subprocess.Popen(self.argv, cwd=self.cwd, env=self.env, stdin=subprocess.PIPE,
                                     stdout=subprocess.PIPE, stderr=self._err, text=True, bufsize=1)
        threading.Thread(target=self._reader, daemon=True).start()

    def _reader(self) -> None:
        assert self.proc and self.proc.stdout
        for line in self.proc.stdout:
            line = line.strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                obj = {"unparseable": line[:2000]}
            self.frames.append({"t": time.time(), "dir": "in", "frame": obj})
            self._q.put(obj)
        self._q.put(None)

    def send(self, obj: dict) -> None:
        assert self.proc and self.proc.stdin
        self.frames.append({"t": time.time(), "dir": "out", "frame": obj})
        self.proc.stdin.write(json.dumps(obj) + "\n")
        self.proc.stdin.flush()

    def recv(self, timeout: float) -> dict | None:
        try:
            return self._q.get(timeout=timeout)
        except queue.Empty:
            raise TimeoutError(f"no frame within {timeout}s")

    def close_stdin(self) -> None:
        if self.proc and self.proc.stdin:
            try:
                self.proc.stdin.close()
            except OSError:
                pass

    def stop(self, grace: float = 5.0) -> None:
        if not self.proc:
            return
        self.close_stdin()
        try:
            self.proc.wait(timeout=grace)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            self.proc.wait()
        self._err.close()

    @property
    def returncode(self) -> int | None:
        return self.proc.returncode if self.proc else None
