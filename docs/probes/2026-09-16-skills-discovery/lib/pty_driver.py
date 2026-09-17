from __future__ import annotations

import fcntl
import os
import pty
import re
import struct
import subprocess
import termios
import threading
import time
from pathlib import Path

from harness.base import AskResult, Session
from lib.probe_skill import extract_tui_reply
from lib.procs import kill_group

ANSI_RE = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[@-Z\\-_]|\r")


def strip_ansi(text: str) -> str:
    return ANSI_RE.sub("", text)


def _take_terminal() -> None:
    fcntl.ioctl(0, termios.TIOCSCTTY, 0)


class PtySession(Session):
    """A vendor's interactive UI on a pseudo-terminal: type a line, read the screen stream."""

    def __init__(self, argv: list[str], cwd: Path, env: dict, log_path: Path,
                 dialogs: tuple[tuple[str, str], ...] = (), reload_command: str | None = None,
                 exit_keys: tuple[str, ...] = ("\x03", "\x03", "\x04"), ready_idle: float = 4.0,
                 timeout: float = 180.0, cols: int = 200, rows: int = 50) -> None:
        self.argv = list(argv)
        self.cwd = Path(cwd)
        self.log_path = Path(log_path)
        self.env = {**env, "TERM": "xterm-256color", "COLUMNS": str(cols), "LINES": str(rows)}
        self.dialogs = [(re.compile(pattern), keys) for pattern, keys in dialogs]
        self.reload_command = reload_command
        self.exit_keys = tuple(exit_keys)
        self.ready_idle = ready_idle
        self.timeout = timeout
        self.cols, self.rows = cols, rows
        self.proc: subprocess.Popen | None = None
        self.master = -1
        self._buf = bytearray()
        self._lock = threading.Lock()
        self._last = time.time()
        self._answered: set[int] = set()
        self.notes: list[str] = []
        self.started_at = time.time()

    def start(self) -> None:
        self.master, slave = pty.openpty()
        fcntl.ioctl(self.master, termios.TIOCSWINSZ, struct.pack("HHHH", self.rows, self.cols, 0, 0))
        self._log = self.log_path.open("ab")
        try:
            # Its own session with the slave as controlling terminal, so the vendor sees a real
            # tty and a stuck one can be killed with everything it spawned.
            self.proc = subprocess.Popen(self.argv, cwd=str(self.cwd), env=self.env, stdin=slave, stdout=slave,
                                         stderr=slave, start_new_session=True, preexec_fn=_take_terminal)
        finally:
            os.close(slave)
        self._reader = threading.Thread(target=self._read, daemon=True)
        self._reader.start()
        self.wait_ready(self.timeout)

    def _read(self) -> None:
        while True:
            try:
                chunk = os.read(self.master, 65536)
            except OSError:
                break
            if not chunk:
                break
            with self._lock:
                self._buf += chunk
                self._last = time.time()
            self._log.write(chunk)
            self._log.flush()

    def screen(self) -> str:
        with self._lock:
            data = bytes(self._buf)
        return strip_ansi(data.decode("utf-8", "replace"))

    def send(self, text: str) -> None:
        os.write(self.master, text.encode())

    def _answer_dialogs(self) -> None:
        screen = self.screen()
        for i, (pattern, keys) in enumerate(self.dialogs):
            if i not in self._answered and pattern.search(screen):
                self._answered.add(i)
                self.notes.append(f"dialog={pattern.pattern}")
                self.send(keys)
                time.sleep(0.5)

    def wait_ready(self, timeout: float) -> None:
        deadline = time.time() + timeout
        while time.time() < deadline:
            self._answer_dialogs()
            with self._lock:
                seen, idle = len(self._buf) > 0, time.time() - self._last
            if seen and idle >= self.ready_idle:
                return
            if self.proc is not None and self.proc.poll() is not None:
                raise RuntimeError(f"tui exited with {self.proc.returncode} before it was ready")
            time.sleep(0.1)
        raise TimeoutError(f"tui not ready within {timeout}s")

    def ask(self, prompt: str) -> AskResult:
        offset = len(self.screen())
        self.send(prompt)
        time.sleep(0.5)
        first = time.time()
        self.send("\r")
        deadline = time.time() + self.timeout
        reply = ""
        notes = list(self.notes)
        while time.time() < deadline:
            self._answer_dialogs()
            reply = extract_tui_reply(self.screen()[offset:])
            if reply:
                break
            if self.proc is not None and self.proc.poll() is not None:
                notes.append(f"tui exited with {self.proc.returncode}")
                break
            time.sleep(0.2)
        else:
            notes.append("timeout")
        if not reply:
            notes.append("no PROBE-REPLY line on the screen")
        return AskResult(reply_text=reply, raw=self.screen()[offset:], argv=list(self.argv), started_at=self.started_at,
                         first_request_at=first, stderr_path=str(self.log_path), exit_code=None,
                         notes=" ".join(notes))

    def command(self, line: str, settle: float | None = None) -> str:
        offset = len(self.screen())
        self.send(line + "\r")
        time.sleep(settle if settle is not None else max(self.ready_idle, 1.0))
        self._answer_dialogs()
        return self.screen()[offset:]

    def reload(self) -> str | None:
        if self.reload_command is None:
            return None
        self.command(self.reload_command)
        return self.reload_command

    def close(self) -> None:
        if self.proc is None:
            return
        try:
            for keys in self.exit_keys:
                if self.proc.poll() is not None:
                    break
                try:
                    self.send(keys)
                except OSError:
                    break
                time.sleep(0.5)
            try:
                self.proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                kill_group(self.proc.pid)
                self.proc.wait(timeout=5)
        finally:
            kill_group(self.proc.pid)
            try:
                os.close(self.master)
            except OSError:
                pass
            self._reader.join(timeout=5)
            self._log.close()
