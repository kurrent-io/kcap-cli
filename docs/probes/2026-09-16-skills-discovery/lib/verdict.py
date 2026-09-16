from __future__ import annotations

from collections import Counter

from lib.probe_skill import Reply

VERDICTS = ("visible_first_turn", "visible_after_reload", "catalogue_only", "not_visible", "leaked", "untested")


class PromptDesignFailure(Exception):
    """The negative control produced a token: the prompt or parser is unsound for this entry."""


def judge_single(expected_token: str, reply: Reply, reload_used: bool = False) -> str:
    if expected_token in reply.tokens:
        return "visible_after_reload" if reload_used else "visible_first_turn"
    if reply.skill_named:
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
