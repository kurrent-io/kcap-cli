from __future__ import annotations

import codecs
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
CSI_RE = re.compile(r"\x1b\[([0-?]*)([ -/]*)([@-~])")
OSC_RE = re.compile(r"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)")
# Cursor movement is how some renderers space words apart; dropping it would glue them together.
CURSOR_FORWARD_RE = re.compile(r"\x1b\[(\d*)C")
CURSOR_COLUMN_RE = re.compile(r"\x1b\[\d*G")
KEY_RE = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]|\x1b.|.", re.S)


def strip_ansi(text: str) -> str:
    text = CURSOR_FORWARD_RE.sub(lambda m: " " * int(m.group(1) or 1), text)
    text = CURSOR_COLUMN_RE.sub(" ", text)
    return ANSI_RE.sub("", text)


class Terminal:
    """A small terminal emulator. A coding agent's UI redraws in place and streams its answer
    beside a spinner, so the bytes read back are not a screen until the cursor movements have been
    applied to a grid: concatenated raw, one status redraw lands in the middle of the reply."""

    def __init__(self, rows: int = 50, cols: int = 200) -> None:
        self.rows, self.cols = rows, cols
        self.clear()

    def clear(self) -> None:
        self.grid = [[" "] * self.cols for _ in range(self.rows)]
        self.history: list[str] = []
        self.r = self.c = 0
        self._saved = (0, 0)

    def text(self) -> str:
        return "\n".join(self.history + ["".join(row).rstrip() for row in self.grid])

    def feed(self, data: str) -> None:
        i, n = 0, len(data)
        while i < n:
            ch = data[i]
            if ch == "\x1b":
                csi = CSI_RE.match(data, i)
                if csi:
                    self._csi(csi.group(1), csi.group(3))
                    i = csi.end()
                    continue
                osc = OSC_RE.match(data, i)
                if osc:
                    i = osc.end()
                    continue
                nxt = data[i + 1] if i + 1 < n else ""
                if nxt == "7":
                    self._saved = (self.r, self.c)
                elif nxt == "8":
                    self.r, self.c = self._saved
                elif nxt == "M":
                    self._reverse_index()
                elif nxt == "D":
                    self._index()
                i += 2
                continue
            i += 1
            if ch == "\r":
                self.c = 0
            elif ch == "\n":
                self._index()
                self.c = 0
            elif ch == "\b":
                self.c = max(0, self.c - 1)
            elif ch == "\t":
                self.c = min(self.cols - 1, (self.c // 8 + 1) * 8)
            elif ch >= " " or ch == "\x7f":
                self._put(ch)

    def _put(self, ch: str) -> None:
        if self.c >= self.cols:
            self.c = 0
            self._index()
        self.grid[self.r][self.c] = ch
        self.c += 1

    def _index(self) -> None:
        if self.r + 1 >= self.rows:
            # The line leaving the screen is the transcript a scrolling UI would keep.
            self.history.append("".join(self.grid.pop(0)).rstrip())
            self.grid.append([" "] * self.cols)
        else:
            self.r += 1

    def _reverse_index(self) -> None:
        if self.r == 0:
            self.grid.insert(0, [" "] * self.cols)
            self.grid.pop()
        else:
            self.r -= 1

    def _blank(self, row: int, start: int, end: int) -> None:
        for col in range(max(0, start), min(self.cols, end)):
            self.grid[row][col] = " "

    def _csi(self, params: str, final: str) -> None:
        if params.startswith("?"):
            if final in ("h", "l") and params in ("?1049", "?47", "?1047"):
                self.clear()
            return
        nums = [int(p) if p.isdigit() else 0 for p in params.split(";")] if params else []

        def n(idx: int = 0, default: int = 1) -> int:
            return nums[idx] if idx < len(nums) and nums[idx] else default

        if final == "A":
            self.r = max(0, self.r - n())
        elif final in ("B", "e"):
            self.r = min(self.rows - 1, self.r + n())
        elif final in ("C", "a"):
            self.c = min(self.cols - 1, self.c + n())
        elif final == "D":
            self.c = max(0, self.c - n())
        elif final == "E":
            self.r, self.c = min(self.rows - 1, self.r + n()), 0
        elif final == "F":
            self.r, self.c = max(0, self.r - n()), 0
        elif final in ("G", "`"):
            self.c = min(self.cols - 1, n() - 1)
        elif final == "d":
            self.r = min(self.rows - 1, n() - 1)
        elif final in ("H", "f"):
            self.r = min(self.rows - 1, n(0) - 1)
            self.c = min(self.cols - 1, n(1) - 1)
        elif final == "J":
            mode = nums[0] if nums else 0
            if mode == 0:
                self._blank(self.r, self.c, self.cols)
                for row in range(self.r + 1, self.rows):
                    self._blank(row, 0, self.cols)
            elif mode == 1:
                self._blank(self.r, 0, self.c + 1)
                for row in range(0, self.r):
                    self._blank(row, 0, self.cols)
            else:
                for row in range(self.rows):
                    self._blank(row, 0, self.cols)
        elif final == "K":
            mode = nums[0] if nums else 0
            if mode == 0:
                self._blank(self.r, self.c, self.cols)
            elif mode == 1:
                self._blank(self.r, 0, self.c + 1)
            else:
                self._blank(self.r, 0, self.cols)
        elif final == "L":
            for _ in range(n()):
                self.grid.insert(self.r, [" "] * self.cols)
                self.grid.pop()
        elif final == "M":
            for _ in range(n()):
                self.grid.pop(self.r)
                self.grid.append([" "] * self.cols)
        elif final == "P":
            count = min(n(), self.cols - self.c)
            del self.grid[self.r][self.c:self.c + count]
            self.grid[self.r] += [" "] * count
        elif final == "@":
            count = min(n(), self.cols - self.c)
            self.grid[self.r][self.c:self.c] = [" "] * count
            del self.grid[self.r][self.cols:]
        elif final == "X":
            self._blank(self.r, self.c, self.c + n())
        elif final == "S":
            for _ in range(n()):
                self.history.append("".join(self.grid.pop(0)).rstrip())
                self.grid.append([" "] * self.cols)
        elif final == "T":
            for _ in range(n()):
                self.grid.insert(0, [" "] * self.cols)
                self.grid.pop()


def _take_terminal() -> None:
    fcntl.ioctl(0, termios.TIOCSCTTY, 0)


class PtySession(Session):
    """A vendor's interactive UI on a pseudo-terminal: type a line, read the screen it draws."""

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
        self.term = Terminal(rows, cols)
        self._decoder = codecs.getincrementaldecoder("utf-8")("replace")
        self._seen = False
        self._lock = threading.Lock()
        self._last = time.time()
        self._answered: set[int] = set()
        self.notes: list[str] = []
        self._noted = 0
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
        except BaseException:
            # No child means close() has nothing to finish: release what was opened for it here.
            os.close(self.master)
            self._log.close()
            raise
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
                self.term.feed(self._decoder.decode(chunk))
                self._seen = True
                self._last = time.time()
            self._log.write(chunk)
            self._log.flush()

    def screen(self) -> str:
        with self._lock:
            return self.term.text()

    def send(self, text: str) -> None:
        os.write(self.master, text.encode())

    def _answer_dialogs(self) -> None:
        # Never longer than the readiness window, or a vendor waiting on the dialog is declared
        # ready and then typed at instead of answered.
        settled = min(1.0, self.ready_idle)
        with self._lock:
            idle = time.time() - self._last
        # A dialog answered while its list is still being drawn moves a selection that is about to
        # be redrawn, and the Enter after it then confirms the default.
        if idle < settled:
            return
        screen = self.screen()
        for i, (pattern, keys) in enumerate(self.dialogs):
            if i not in self._answered and pattern.search(screen):
                self._answered.add(i)
                self.notes.append(f"dialog={pattern.pattern}")
                self.type(keys)
                time.sleep(1.0)

    def type(self, keys: str, gap: float = 1.0) -> None:
        """One keystroke per write, a redraw apart: an arrow and the Enter after it sent together
        confirm the option the arrow was meant to leave."""
        for key in KEY_RE.findall(keys):
            self.send(key)
            time.sleep(gap)

    def wait_ready(self, timeout: float) -> None:
        deadline = time.time() + timeout
        while time.time() < deadline:
            self._answer_dialogs()
            with self._lock:
                seen, idle = self._seen, time.time() - self._last
            if seen and idle >= self.ready_idle:
                return
            if self.proc is not None and self.proc.poll() is not None:
                raise RuntimeError(f"tui exited with {self.proc.returncode} before it was ready")
            time.sleep(0.1)
        raise TimeoutError(f"tui not ready within {timeout}s")

    def ask(self, prompt: str) -> AskResult:
        # The screen starts blank for the turn, so an earlier turn's answer cannot be read as this
        # one's; whatever the UI redraws afterwards is this turn's own output.
        with self._lock:
            self.term.clear()
        self.send(prompt)
        time.sleep(0.5)
        first = time.time()
        self.send("\r")
        deadline = time.time() + self.timeout
        reply = ""
        extra: list[str] = []
        while time.time() < deadline:
            self._answer_dialogs()
            reply = extract_tui_reply(self.screen())
            if reply:
                break
            if self.proc is not None and self.proc.poll() is not None:
                extra.append(f"tui exited with {self.proc.returncode}")
                # The exit may follow the reply by less than one poll interval.
                reply = extract_tui_reply(self.screen())
                break
            time.sleep(0.2)
        else:
            extra.append("timeout")
        if not reply:
            extra.append("no PROBE-REPLY line on the screen")
        # Dialog notes belong to the turn that answered them, once.
        notes = self.notes[self._noted:] + extra
        self._noted = len(self.notes)
        return AskResult(reply_text=reply, raw=self.screen(), argv=list(self.argv), started_at=self.started_at,
                         first_request_at=first, stderr_path=str(self.log_path), exit_code=None,
                         notes=" ".join(notes))

    def command(self, line: str, settle: float | None = None) -> str:
        with self._lock:
            self.term.clear()
        # Enter goes in its own write: inside a burst a TUI's paste detection keeps it as text.
        self.send(line)
        time.sleep(0.5)
        self.send("\r")
        time.sleep(settle if settle is not None else max(self.ready_idle, 1.0))
        self._answer_dialogs()
        return self.screen()

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
