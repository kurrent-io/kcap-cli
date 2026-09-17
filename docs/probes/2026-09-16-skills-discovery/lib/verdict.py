from __future__ import annotations

from collections import Counter

from lib.probe_skill import Reply

VERDICTS = ("visible_first_turn", "visible_after_reload", "visible_live", "catalogue_only", "not_visible",
            "stale", "revoked", "leaked", "untested")


class PromptDesignFailure(Exception):
    """The negative control produced a token: the prompt or parser is unsound for this entry."""


def judge_single(expected_token: str, reply: Reply, reload_used: bool = False) -> str:
    if expected_token in reply.tokens:
        return "visible_after_reload" if reload_used else "visible_first_turn"
    # Naming the skill while answering NO-SKILL is an echo of the prompt, not a sighting.
    if reply.skill_named and not reply.no_skill:
        return "catalogue_only"
    return "not_visible"


def judge_control(reply: Reply) -> str:
    if reply.tokens:
        raise PromptDesignFailure(f"negative control produced tokens {sorted(reply.tokens)}")
    return "not_visible"


def judge_root(token: str, documented: bool, reply: Reply) -> str:
    if token in reply.tokens:
        return "visible_first_turn" if documented else "leaked"
    return "not_visible"


def combine(verdicts: list[str]) -> tuple[str, bool]:
    if not verdicts:
        return "untested", False
    counts = Counter(verdicts)
    top, _ = counts.most_common(1)[0]
    return top, len(counts) > 1


def needs_third_run(verdicts: list[str]) -> bool:
    return len(verdicts) == 2 and verdicts[0] != verdicts[1]


def judge_update(new_token: str, old_token: str | None, reply: Reply, live: bool) -> str:
    if new_token in reply.tokens:
        return "visible_live" if live else "visible_first_turn"
    if old_token is not None and old_token in reply.tokens:
        return "stale"
    if reply.skill_named and not reply.no_skill:
        return "catalogue_only"
    return "not_visible"


def judge_delete(old_token: str, reply: Reply) -> str:
    if old_token in reply.tokens:
        return "stale"
    if reply.no_skill and not reply.tokens:
        return "revoked"
    # Still listed after the delete, even if unreadable: the catalogue did not follow the file.
    if reply.skill_named:
        return "stale"
    return "not_visible"
