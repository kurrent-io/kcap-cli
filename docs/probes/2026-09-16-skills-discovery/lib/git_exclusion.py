from __future__ import annotations

from pathlib import Path

from lib.isolation import git

EXCLUSIONS = ("none", "gitignore", "info-exclude")


def apply(repo: Path, exclusion: str, rel_dir: str) -> Path | None:
    pattern = f"/{rel_dir.strip('/')}/\n"
    if exclusion == "none":
        return None
    if exclusion == "gitignore":
        target = repo / ".gitignore"
        with target.open("a") as f:
            f.write(pattern)
        git(repo, "add", ".gitignore")
        git(repo, "commit", "-q", "-m", "ignore probe skill")
        return target
    if exclusion == "info-exclude":
        raw = git(repo, "rev-parse", "--git-path", "info/exclude").strip()
        target = Path(raw) if Path(raw).is_absolute() else repo / raw
        target = target.resolve()
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("a") as f:
            f.write(pattern)
        return target
    raise ValueError(exclusion)


def assert_untracked_state(repo: Path, rel_dir: str, exclusion: str) -> str:
    # An empty status is the pass condition for the excluded arms, and a skill that was never
    # written produces exactly that: prove the file is there before reading anything into it.
    target = repo / rel_dir
    if not target.exists():
        raise AssertionError(f"{rel_dir} does not exist under {repo}")
    if not any(p.is_file() for p in target.rglob("*")) and not target.is_file():
        raise AssertionError(f"{rel_dir} holds no file")
    out = git(repo, "status", "--porcelain", "--untracked-files=all", "--", rel_dir)
    if exclusion == "none":
        if "??" not in out:
            raise AssertionError(f"expected an untracked entry for {rel_dir}, got: {out!r}")
    elif out.strip():
        raise AssertionError(f"expected {rel_dir} to be excluded under {exclusion}, got: {out!r}")
    return out
