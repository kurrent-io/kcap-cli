from __future__ import annotations

from harness.base import Adapter
from harness.claude import ClaudeAdapter
from harness.codex import CodexAdapter
from harness.copilot import CopilotAdapter
from harness.fake import FakeAdapter
from harness.gemini import GeminiAdapter
from harness.pi import PiAdapter

ENTRIES: dict[str, type[Adapter]] = {
    "fake": FakeAdapter,
    "claude": ClaudeAdapter,
    "codex": CodexAdapter,
    "gemini": GeminiAdapter,
    "pi": PiAdapter,
    "copilot": CopilotAdapter,
}
