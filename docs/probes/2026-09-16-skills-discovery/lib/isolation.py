from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
from dataclasses import dataclass, field
from pathlib import Path

ENV_ALLOWLIST = ("PATH", "TERM", "LANG", "LC_ALL", "LC_CTYPE", "TMPDIR", "SHELL", "USER", "LOGNAME")
GIT_ENV_ALLOWLIST = ("PATH", "HOME", "TMPDIR", "LANG", "LC_ALL", "LC_CTYPE", "USER", "LOGNAME")


def git_env() -> dict[str, str]:
    """The sandbox's own Git environment: a developer's global core.excludesFile would otherwise
    hide the probe skill from `git status` and turn a real exclusion result into a false one."""
    env = {k: os.environ[k] for k in GIT_ENV_ALLOWLIST if k in os.environ}
    env["GIT_CONFIG_GLOBAL"] = "/dev/null"
    env["GIT_CONFIG_SYSTEM"] = "/dev/null"
    env["GIT_TERMINAL_PROMPT"] = "0"
    return env


def git(repo: Path, *args: str, env: dict | None = None) -> str:
    return subprocess.run(
        ["git", "-C", str(repo), *args], check=True, capture_output=True, text=True,
        env=env if env is not None else git_env(),
    ).stdout


@dataclass
class Sandbox:
    root: Path
    repo: Path
    config_root: Path
    env: dict[str, str]
    keep: bool = False
    # Logs already copied out of this sandbox, source path to destination, so a sandbox that
    # yields several rows copies each log once.
    copied_logs: dict[str, str] = field(default_factory=dict)

    def cleanup(self) -> None:
        if not self.keep:
            shutil.rmtree(self.root, ignore_errors=True)


def new_sandbox(
    lever: str,
    real_root: Path | None,
    credential_files: list[str],
    passthrough_env: list[str] = (),
    extra_env: dict[str, str] | None = None,
    keep: bool = False,
    base: Path | None = None,
) -> Sandbox:
    root = Path(tempfile.mkdtemp(prefix="skprobe-", dir=base)).resolve()
    repo = root / "repo"
    repo.mkdir()
    git(repo, "init", "-q", "-b", "main")
    git(repo, "config", "user.email", "probe@example.invalid")
    git(repo, "config", "user.name", "probe")
    (repo / "README.md").write_text("probe repo\n")
    git(repo, "add", "README.md")
    git(repo, "commit", "-q", "-m", "init")

    config_root = root / "config"
    config_root.mkdir()
    if real_root is not None:
        for rel in credential_files:
            src = real_root / rel
            if src.is_file():
                dst = config_root / rel
                dst.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(src, dst)

    env = {k: os.environ[k] for k in ENV_ALLOWLIST if k in os.environ}
    for k in passthrough_env:
        if k in os.environ:
            env[k] = os.environ[k]
    env["HOME"] = os.environ.get("HOME", str(root))
    env[lever] = str(config_root)
    if extra_env:
        env.update(extra_env)
    return Sandbox(root=root, repo=repo, config_root=config_root, env=env, keep=keep)
