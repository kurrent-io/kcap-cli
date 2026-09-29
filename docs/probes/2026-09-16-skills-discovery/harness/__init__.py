from __future__ import annotations

from harness.agy import AgyAdapter, AgyCliDirAdapter, AgyDirLayoutAdapter
from harness.base import Adapter
from harness.claude import ClaudeAdapter
from harness.codex import CodexAdapter
from harness.copilot import CopilotAdapter
from harness.cursor import CursorAdapter, CursorUserHooksAdapter
from harness.fake import FakeAdapter
from harness.gemini import GeminiAdapter
from harness.kiro import KiroAdapter, KiroAgentBareAdapter, KiroAgentSkillsAdapter
from harness.opencode_v1 import OpenCodeV1Adapter
from harness.opencode_v2 import OpenCodeV2Adapter, OpenCodeV2PromptHookAdapter
from harness.pi import PiAdapter

ENTRIES: dict[str, type[Adapter]] = {
    "fake": FakeAdapter,
    "claude": ClaudeAdapter,
    "codex": CodexAdapter,
    "gemini": GeminiAdapter,
    "pi": PiAdapter,
    "cursor": CursorAdapter,
    "cursor-userhooks": CursorUserHooksAdapter,
    "copilot": CopilotAdapter,
    "kiro": KiroAdapter,
    "kiro-agent-bare": KiroAgentBareAdapter,
    "kiro-agent-skills": KiroAgentSkillsAdapter,
    "opencode-v1": OpenCodeV1Adapter,
    "opencode-v2": OpenCodeV2Adapter,
    "opencode-v2-prompt": OpenCodeV2PromptHookAdapter,
    "agy": AgyAdapter,
    "agy-dirlayout": AgyDirLayoutAdapter,
    "agy-clidir": AgyCliDirAdapter,
}
