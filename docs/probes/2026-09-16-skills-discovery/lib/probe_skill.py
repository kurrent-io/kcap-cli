from __future__ import annotations

import re
import secrets
from dataclasses import dataclass
from pathlib import Path

TOKEN_RE = re.compile(r"PROBE-BODY-([0-9a-f]{12})")
NAME_RE = re.compile(r"kcap-probe-[0-9a-f]{6}")
NO_SKILL = "NO-SKILL"


@dataclass(frozen=True)
class ProbeSkill:
    nonce: str
    token: str

    @staticmethod
    def fresh() -> "ProbeSkill":
        return ProbeSkill(secrets.token_hex(3), secrets.token_hex(6))

    @property
    def name(self) -> str:
        return f"kcap-probe-{self.nonce}"

    @property
    def body_token(self) -> str:
        return f"PROBE-BODY-{self.token}"

    @property
    def description(self) -> str:
        return "Answers a question about the probe token for this repository."

    def render(self) -> str:
        return (
            "---\n"
            f"name: {self.name}\n"
            f"description: {self.description}\n"
            "---\n"
            "When asked for the probe token, reply with exactly this token and nothing else:\n"
            f"{self.body_token}\n"
        )

    def variant(self) -> "ProbeSkill":
        """The same skill name with a fresh token: what an in-place rewrite of the body looks like."""
        return ProbeSkill(self.nonce, secrets.token_hex(6))


def write_skill(root: Path, skill: ProbeSkill, flat: bool = False) -> Path:
    if flat:
        root.mkdir(parents=True, exist_ok=True)
        path = root / f"{skill.name}.md"
    else:
        (root / skill.name).mkdir(parents=True, exist_ok=True)
        path = root / skill.name / "SKILL.md"
    path.write_text(skill.render())
    return path


# A harness may load a listed skill's body by reading its own file; what the prompt forbids is
# hunting for a file that was never listed, which would pass discovery off as a search.
NO_SEARCH = (
    "Do not search the file system or guess where it might be: only a skill already listed for "
    "you counts, and reading that listed skill's own file is fine."
)


def single_prompt(skill: ProbeSkill) -> str:
    return (
        f"You have a skill named {skill.name}. Use it and reply with only the probe token it "
        f"contains. {NO_SEARCH} If no such skill is listed for you, reply with exactly {NO_SKILL}."
    )


def multi_prompt() -> str:
    return (
        "List every skill listed for you whose name starts with kcap-probe-. For each, reply "
        "with <name>=<token> on its own line, reading the token from the skill body. "
        f"{NO_SEARCH} If there are none, reply with exactly {NO_SKILL}."
    )


# The echoed prompt spells the form with angle brackets, so only a real value matches; a tool
# panel that shows the skill file carries the token without the prefix and does not match either.
TUI_REPLY_RE = re.compile(r"PROBE-REPLY:\s*\**\s*(PROBE-BODY-[0-9a-f]{12}|NO-SKILL)\b")


def tui_prompt(skill: ProbeSkill) -> str:
    return (
        f"You have a skill named {skill.name}. Use it and reply with one line of the form "
        f"PROBE-REPLY: <value>, where <value> is the probe token the skill contains. {NO_SEARCH} "
        f"If no such skill is listed for you, the value is {NO_SKILL}."
    )


def extract_tui_reply(screen: str) -> str:
    # Every match counts, so the caller passes only the screen region this turn produced.
    return "\n".join(f"PROBE-REPLY: {m.group(1)}" for m in TUI_REPLY_RE.finditer(screen))


@dataclass(frozen=True)
class Reply:
    tokens: frozenset[str]
    skill_named: bool
    no_skill: bool


def parse_reply(reply_text: str, raw: str, name: str | None = None) -> Reply:
    # The reply decides; the raw event log is consulted only when no reply text was extracted,
    # because a tool-call frame that echoes SKILL.md would otherwise count as a loaded skill.
    found = TOKEN_RE.findall(reply_text) if reply_text.strip() else TOKEN_RE.findall(raw)
    # A reply that lists some other probe skill has not named this one.
    named = (name in reply_text) if name else NAME_RE.search(reply_text) is not None
    return Reply(
        tokens=frozenset(found),
        skill_named=named,
        no_skill=NO_SKILL in reply_text,
    )
