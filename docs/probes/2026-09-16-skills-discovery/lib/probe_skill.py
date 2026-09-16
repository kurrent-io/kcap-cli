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


def write_skill(root: Path, skill: ProbeSkill, flat: bool = False) -> Path:
    if flat:
        root.mkdir(parents=True, exist_ok=True)
        path = root / f"{skill.name}.md"
    else:
        (root / skill.name).mkdir(parents=True, exist_ok=True)
        path = root / skill.name / "SKILL.md"
    path.write_text(skill.render())
    return path


NO_TOOLS = (
    "Do not run any tool, search the file system or read any file to find it; only a skill that "
    "is already available to you counts."
)


def single_prompt(skill: ProbeSkill) -> str:
    return (
        f"You have a skill named {skill.name}. Use it and reply with only the probe token it "
        f"contains. {NO_TOOLS} If no such skill is available to you, reply with exactly {NO_SKILL}."
    )


def multi_prompt() -> str:
    return (
        "List every skill available to you whose name starts with kcap-probe-. For each, reply "
        "with <name>=<token> on its own line, reading the token from the skill body. "
        f"{NO_TOOLS} If there are none, reply with exactly {NO_SKILL}."
    )


@dataclass(frozen=True)
class Reply:
    tokens: frozenset[str]
    skill_named: bool
    no_skill: bool


def parse_reply(reply_text: str, raw: str) -> Reply:
    # The reply decides; the raw event log is consulted only when no reply text was extracted,
    # because a tool-call frame that echoes SKILL.md would otherwise count as a loaded skill.
    found = TOKEN_RE.findall(reply_text) if reply_text.strip() else TOKEN_RE.findall(raw)
    return Reply(
        tokens=frozenset(found),
        skill_named=NAME_RE.search(reply_text) is not None,
        no_skill=NO_SKILL in reply_text,
    )
