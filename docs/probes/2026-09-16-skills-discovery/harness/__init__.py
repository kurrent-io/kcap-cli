from __future__ import annotations

from harness.base import Adapter
from harness.claude import ClaudeAdapter
from harness.fake import FakeAdapter

ENTRIES: dict[str, type[Adapter]] = {
    "fake": FakeAdapter,
    "claude": ClaudeAdapter,
}
