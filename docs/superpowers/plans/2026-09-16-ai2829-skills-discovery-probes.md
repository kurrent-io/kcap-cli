# Skills Discovery Probes Implementation Plan (pass 1)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A reproducible Python probe kit under `docs/probes/2026-09-16-skills-discovery/` that measures, per harness and launch mode, whether a repo-local skill written by a startup hook is visible to the first model request, which Git exclusion keeps it loadable, and which vendor directories each harness consumes, and emits `matrix.json`, `findings.md` and `capability-matrix.md`.

**Architecture:** A shared library (`lib/`) owns the sandbox (fresh git repo + private vendor config root), the nonce probe skill and its prompts, Git exclusion, the run recorder and verdict rules. One adapter per harness (`harness/`) implements four primitives: install the vendor's startup hook into the isolated user-level config, launch in a mode, send the first prompt and return the reply, optionally list the catalogue. `probe.py` runs scenarios S0 to S4 over adapters and rebuilds the matrix from run files. Vendor hook configuration is hand-written by the adapters so a verdict describes the vendor, never kcap.

**Tech Stack:** Python 3.14 standard library only (no third-party packages); `git`; the vendor CLIs installed on the developer machine; the `AcpClient` from `docs/probes/2026-08-04-acp-reconnect-c0/acp_c0_probe.py` for ACP daemon modes.

**Spec:** `docs/superpowers/specs/2026-09-16-skills-discovery-probes-design.md`

## Global Constraints

- No production code changes: nothing under `src/` or `test/` is touched. Kit files live only under `docs/probes/2026-09-16-skills-discovery/`.
- Standard library only. `python3` on the machine is 3.14.5; use `from __future__ import annotations` and nothing newer than 3.11 syntax so an older machine can run the kit.
- Every path handed to a vendor is resolved with `Path.resolve()` (macOS `/var` is a symlink to `/private`).
- Vendor processes get a minimal allow-listed environment plus the vendor's config-root lever; nothing from the developer's `.envrc` leaks in.
- Credential files are copied by explicit per-adapter lists, never whole directories.
- `out/` and `*.stderr.log` are git-ignored; `matrix.json`, `findings.md`, `capability-matrix.md` are committed.
- Comments are scarce (see CLAUDE.md "Comments"): no history, no design coordinates, no Linear ids anywhere in kit code. Docs may cite `#961`.
- Commit subjects: one imperative clause, at most 80 characters including the trailing `(#961)`, trailer `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- In this worktree run git as `/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/peppy-percolating-biscuit <args>`, one command per invocation, no heredocs. `KIT` below means `/Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/peppy-percolating-biscuit/docs/probes/2026-09-16-skills-discovery`.
- Self-tests run as a script: `python3 $KIT/selftest.py -v`. They must pass with no vendor binary installed.
- Each turn arm costs one real model request on the developer's account. Never loop a turn arm beyond the repetition rule (2 runs, a 3rd on disagreement).

---

## File structure

```
docs/probes/2026-09-16-skills-discovery/
  .gitignore                out/, *.stderr.log
  probe.py                  CLI orchestrator: scenarios, repetition, --emit
  selftest.py               unittest suite over lib/, harness/base.py and probe.py with a fake adapter
  lib/__init__.py
  lib/isolation.py          Sandbox, new_sandbox(), git()
  lib/probe_skill.py        ProbeSkill, write_skill(), single_prompt(), multi_prompt(), parse_reply()
  lib/git_exclusion.py      apply(), assert_untracked_state()
  lib/verdict.py            judge_single(), judge_root(), combine(), PromptDesignFailure
  lib/recorder.py           RunRecord, write_run(), load_runs(), emit_matrix()
  lib/hook_script.py        write_hook_script(), read_stamp()
  lib/acp_driver.py         acp_ask(): one ACP turn over AcpClient
  lib/print_driver.py       print_ask(): one print-mode turn over subprocess
  harness/__init__.py       ENTRIES registry
  harness/base.py           Adapter, AskResult, HookInfo
  harness/fake.py           FakeAdapter used by selftest.py
  harness/claude.py … harness/agy.py   one adapter per entry (Tasks 10-19)
  findings.md               written in Task 21
  capability-matrix.md      written in Task 21
  matrix.json               emitted in Task 20
```

Shared vocabulary used by every task:

- `entry`: `claude`, `codex`, `gemini`, `pi`, `cursor`, `copilot`, `kiro`, `opencode-v1`, `opencode-v2`, `agy`.
- `mode`: `print` or `daemon` (pass 1), `interactive` reserved.
- `scenario`: `S0`, `S1`, `S2`, `S3`, `S4`; `arm` names within a scenario: `S0/none`, `S1/native`, `S2/hook-creates-root`, `S2/hook-adds-skill`, `S2/registration`, `S3/gitignore`, `S3/info-exclude`, `S4/all-roots`, `S4/confirm-<root-key>`.
- `root`: a repo-relative skills directory such as `.claude/skills`; `root key` is the same string with `/` replaced by `_` for file names.

---

### Task 1: Kit skeleton and self-test runner

**Files:**
- Create: `docs/probes/2026-09-16-skills-discovery/.gitignore`
- Create: `docs/probes/2026-09-16-skills-discovery/lib/__init__.py`
- Create: `docs/probes/2026-09-16-skills-discovery/harness/__init__.py`
- Create: `docs/probes/2026-09-16-skills-discovery/selftest.py`

**Interfaces:**
- Produces: `selftest.py` runnable as `python3 $KIT/selftest.py -v`; it puts the kit directory first on `sys.path` so `import lib.x` and `import harness.x` work from any cwd.

- [ ] **Step 1: Create the directories and the ignore file**

`.gitignore`:

```
out/
*.stderr.log
```

`lib/__init__.py` and `harness/__init__.py` are empty files.

- [ ] **Step 2: Write the self-test runner**

`selftest.py`:

```python
#!/usr/bin/env python3
"""Self-tests for the skills discovery probe kit. Needs git and Python; no vendor binary."""
from __future__ import annotations

import sys
import unittest
from pathlib import Path

KIT = Path(__file__).resolve().parent
sys.path.insert(0, str(KIT))


class KitLayoutTests(unittest.TestCase):
    def test_out_is_ignored(self):
        ignore = (KIT / ".gitignore").read_text().splitlines()
        self.assertIn("out/", ignore)
        self.assertIn("*.stderr.log", ignore)


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 3: Run it**

Run: `python3 $KIT/selftest.py -v`
Expected: `test_out_is_ignored ... ok`, `OK`

- [ ] **Step 4: Commit**

```
/usr/bin/git -C <worktree> add docs/probes/2026-09-16-skills-discovery
/usr/bin/git -C <worktree> commit -m "Scaffold the skills discovery probe kit (#961)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: Probe skill, prompts and reply parser

**Files:**
- Create: `docs/probes/2026-09-16-skills-discovery/lib/probe_skill.py`
- Modify: `docs/probes/2026-09-16-skills-discovery/selftest.py`

**Interfaces:**
- Produces:
  - `class ProbeSkill(nonce: str, token: str)` frozen dataclass; `ProbeSkill.fresh() -> ProbeSkill`; properties `name -> str` (`kcap-probe-<nonce>`), `body_token -> str` (`PROBE-BODY-<token>`), `description -> str`; method `render() -> str` (the SKILL.md text).
  - `write_skill(root: Path, skill: ProbeSkill, flat: bool = False) -> Path` writes `<root>/<name>/SKILL.md` (or `<root>/<name>.md` when `flat`) and returns the file path.
  - `single_prompt(skill: ProbeSkill) -> str`, `multi_prompt() -> str`.
  - `class Reply(tokens: frozenset[str], skill_named: bool, no_skill: bool)`; `parse_reply(reply_text: str, raw: str) -> Reply`. `tokens` are the 12-hex values found anywhere in `raw`; `skill_named` is whether any `kcap-probe-<6hex>` appears in `reply_text`; `no_skill` is whether `reply_text` contains `NO-SKILL`.
  - Constants `TOKEN_RE`, `NAME_RE`, `NO_SKILL = "NO-SKILL"`.

- [ ] **Step 1: Write the failing tests**

Append to `selftest.py` (above `if __name__`):

```python
from lib.probe_skill import (  # noqa: E402
    NO_SKILL, ProbeSkill, multi_prompt, parse_reply, single_prompt, write_skill,
)


class ProbeSkillTests(unittest.TestCase):
    def test_token_only_in_body(self):
        s = ProbeSkill.fresh()
        text = s.render()
        head, _, body = text.partition("\n---\n")
        self.assertIn(f"name: {s.name}", head)
        self.assertNotIn(s.token, head)
        self.assertIn(s.body_token, body)
        self.assertNotIn(s.token, single_prompt(s))
        self.assertNotIn(s.token, multi_prompt())
        self.assertIn(s.name, single_prompt(s))

    def test_write_skill_layouts(self):
        import tempfile
        s = ProbeSkill.fresh()
        with tempfile.TemporaryDirectory() as d:
            root = Path(d) / ".claude" / "skills"
            f = write_skill(root, s)
            self.assertEqual(f, root / s.name / "SKILL.md")
            self.assertEqual(f.read_text(), s.render())
            flat = write_skill(Path(d) / "flat", s, flat=True)
            self.assertEqual(flat.name, f"{s.name}.md")

    def test_parse_reply(self):
        s = ProbeSkill.fresh()
        r = parse_reply(f"The token is {s.body_token}.", f'{{"result":"The token is {s.body_token}."}}')
        self.assertEqual(r.tokens, frozenset({s.token}))
        self.assertFalse(r.skill_named)
        r = parse_reply(f"I can see {s.name} but cannot read it", "")
        self.assertEqual(r.tokens, frozenset())
        self.assertTrue(r.skill_named)
        r = parse_reply(NO_SKILL, NO_SKILL)
        self.assertTrue(r.no_skill)
        # raw is the fallback only when no reply text was extracted
        r = parse_reply("", f"chunk: {s.body_token}")
        self.assertEqual(r.tokens, frozenset({s.token}))
        r = parse_reply(NO_SKILL, f'{{"tool_result":"{s.body_token}"}}')
        self.assertEqual(r.tokens, frozenset())
```

- [ ] **Step 2: Run to verify failure**

Run: `python3 $KIT/selftest.py -v`
Expected: `ModuleNotFoundError: No module named 'lib.probe_skill'`

- [ ] **Step 3: Implement**

`lib/probe_skill.py`:

```python
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
```

- [ ] **Step 4: Run to verify pass**

Run: `python3 $KIT/selftest.py -v`
Expected: 4 tests, `OK`

- [ ] **Step 5: Commit**

```
/usr/bin/git -C <worktree> add docs/probes/2026-09-16-skills-discovery
/usr/bin/git -C <worktree> commit -m "Add the probe skill, prompts and reply parser (#961)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: Sandbox isolation

**Files:**
- Create: `docs/probes/2026-09-16-skills-discovery/lib/isolation.py`
- Modify: `docs/probes/2026-09-16-skills-discovery/selftest.py`

**Interfaces:**
- Produces:
  - `git(repo: Path, *args: str, env: dict | None = None) -> str` runs `git -C <repo> <args>`, raises `subprocess.CalledProcessError` on failure, returns stdout.
  - `class Sandbox(root: Path, repo: Path, config_root: Path, env: dict[str, str])` with `cleanup()`.
  - `new_sandbox(lever: str, real_root: Path | None, credential_files: list[str], passthrough_env: list[str] = (), extra_env: dict[str, str] | None = None, keep: bool = False, base: Path | None = None) -> Sandbox`. `lever` is the env var name that relocates the vendor root (`HOME` for a full home override). `env[lever]` is `str(config_root)`. With `lever != "HOME"`, `env["HOME"]` is the real home. `credential_files` are paths relative to `real_root`, copied to the same relative path under `config_root` when present. `passthrough_env` names extra variables copied from `os.environ` when set.
  - `ENV_ALLOWLIST = ("PATH", "TERM", "LANG", "LC_ALL", "LC_CTYPE", "TMPDIR", "SHELL", "USER", "LOGNAME")`.

- [ ] **Step 1: Write the failing tests**

Append to `selftest.py`:

```python
import os  # noqa: E402
import tempfile  # noqa: E402
from unittest import mock  # noqa: E402

from lib.isolation import ENV_ALLOWLIST, Sandbox, git, new_sandbox  # noqa: E402


class IsolationTests(unittest.TestCase):
    def test_sandbox_repo_and_lever(self):
        with tempfile.TemporaryDirectory() as d:
            real = Path(d) / "real"
            (real / "nested").mkdir(parents=True)
            (real / "auth.json").write_text("{}")
            (real / "nested" / "creds").write_text("x")
            (real / "secret.db").write_text("no")
            with mock.patch.dict(os.environ, {"KCAP_URL": "https://leak.invalid"}):
                sb = new_sandbox("CODEX_HOME", real, ["auth.json", "nested/creds", "missing.json"],
                                 base=Path(d))
            try:
                self.assertEqual(sb.repo, sb.repo.resolve())
                self.assertEqual(git(sb.repo, "rev-list", "--count", "HEAD").strip(), "1")
                self.assertEqual(sb.env["CODEX_HOME"], str(sb.config_root))
                self.assertEqual(sb.env["HOME"], os.environ["HOME"])
                self.assertNotIn("KCAP_URL", sb.env)
                self.assertTrue((sb.config_root / "auth.json").exists())
                self.assertTrue((sb.config_root / "nested" / "creds").exists())
                self.assertFalse((sb.config_root / "secret.db").exists())
                for k in sb.env:
                    self.assertTrue(k in ENV_ALLOWLIST or k in ("HOME", "CODEX_HOME"), k)
            finally:
                sb.cleanup()
            self.assertFalse(sb.root.exists())

    def test_home_lever_and_passthrough(self):
        with tempfile.TemporaryDirectory() as d:
            with mock.patch.dict(os.environ, {"GH_TOKEN": "t", "OTHER": "o"}):
                sb = new_sandbox("HOME", None, [], passthrough_env=["GH_TOKEN"],
                                 extra_env={"XDG_CONFIG_HOME": "/x"}, base=Path(d))
            try:
                self.assertEqual(sb.env["HOME"], str(sb.config_root))
                self.assertEqual(sb.env["GH_TOKEN"], "t")
                self.assertNotIn("OTHER", sb.env)
                self.assertEqual(sb.env["XDG_CONFIG_HOME"], "/x")
            finally:
                sb.cleanup()
```

- [ ] **Step 2: Run to verify failure**

Run: `python3 $KIT/selftest.py -v`
Expected: `ModuleNotFoundError: No module named 'lib.isolation'`

- [ ] **Step 3: Implement**

`lib/isolation.py`:

```python
from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path

ENV_ALLOWLIST = ("PATH", "TERM", "LANG", "LC_ALL", "LC_CTYPE", "TMPDIR", "SHELL", "USER", "LOGNAME")


def git(repo: Path, *args: str, env: dict | None = None) -> str:
    return subprocess.run(
        ["git", "-C", str(repo), *args], check=True, capture_output=True, text=True, env=env,
    ).stdout


@dataclass
class Sandbox:
    root: Path
    repo: Path
    config_root: Path
    env: dict[str, str]
    keep: bool = False

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
```

- [ ] **Step 4: Run to verify pass**

Run: `python3 $KIT/selftest.py -v`
Expected: 6 tests, `OK`

- [ ] **Step 5: Commit**

```
/usr/bin/git -C <worktree> add docs/probes/2026-09-16-skills-discovery
/usr/bin/git -C <worktree> commit -m "Add the probe sandbox with isolated vendor roots (#961)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: Git exclusion arms

**Files:**
- Create: `docs/probes/2026-09-16-skills-discovery/lib/git_exclusion.py`
- Modify: `docs/probes/2026-09-16-skills-discovery/selftest.py`

**Interfaces:**
- Consumes: `lib.isolation.git`, `lib.isolation.new_sandbox`.
- Produces:
  - `EXCLUSIONS = ("none", "gitignore", "info-exclude")`.
  - `apply(repo: Path, exclusion: str, rel_dir: str) -> Path | None`: writes the pattern `/<rel_dir>/` to `.gitignore` (then commits it) or to the file `git rev-parse --git-path info/exclude` names (resolved against `repo` when relative); returns the file written or `None` for `none`.
  - `assert_untracked_state(repo: Path, rel_dir: str, exclusion: str) -> str`: runs `git status --porcelain --untracked-files=all -- <rel_dir>`; for `none` requires at least one `??` line, otherwise requires empty output; returns the porcelain text; raises `AssertionError` with the porcelain text on mismatch.

- [ ] **Step 1: Write the failing tests**

Append to `selftest.py`:

```python
from lib.git_exclusion import EXCLUSIONS, apply, assert_untracked_state  # noqa: E402


class GitExclusionTests(unittest.TestCase):
    def _sandbox(self, d):
        return new_sandbox("HOME", None, [], base=Path(d))

    def test_none_shows_untracked(self):
        with tempfile.TemporaryDirectory() as d:
            sb = self._sandbox(d)
            (sb.repo / ".claude/skills/kcap-probe-abc123").mkdir(parents=True)
            (sb.repo / ".claude/skills/kcap-probe-abc123/SKILL.md").write_text("x")
            self.assertIsNone(apply(sb.repo, "none", ".claude/skills/kcap-probe-abc123"))
            out = assert_untracked_state(sb.repo, ".claude/skills/kcap-probe-abc123", "none")
            self.assertIn("??", out)
            with self.assertRaises(AssertionError):
                assert_untracked_state(sb.repo, ".claude/skills/kcap-probe-abc123", "gitignore")

    def test_gitignore_and_info_exclude(self):
        for exclusion in ("gitignore", "info-exclude"):
            with tempfile.TemporaryDirectory() as d:
                sb = self._sandbox(d)
                rel = ".agents/skills/kcap-probe-abc123"
                (sb.repo / rel).mkdir(parents=True)
                (sb.repo / rel / "SKILL.md").write_text("x")
                written = apply(sb.repo, exclusion, rel)
                self.assertIsNotNone(written)
                self.assertIn(f"/{rel}/", written.read_text())
                out = assert_untracked_state(sb.repo, rel, exclusion)
                self.assertEqual(out, "")
                if exclusion == "gitignore":
                    self.assertEqual(git(sb.repo, "status", "--porcelain").strip(), "")
                else:
                    self.assertNotIn(".gitignore", git(sb.repo, "ls-files"))

    def test_info_exclude_inside_worktree(self):
        with tempfile.TemporaryDirectory() as d:
            sb = self._sandbox(d)
            wt = sb.root / "wt"
            git(sb.repo, "worktree", "add", "-q", "-b", "wt", str(wt))
            rel = ".pi/skills/kcap-probe-abc123"
            (wt / rel).mkdir(parents=True)
            (wt / rel / "SKILL.md").write_text("x")
            written = apply(wt, "info-exclude", rel)
            self.assertEqual(written, written.resolve())
            self.assertEqual(assert_untracked_state(wt, rel, "info-exclude"), "")
            self.assertIn(EXCLUSIONS[2], "info-exclude")
```

- [ ] **Step 2: Run to verify failure**

Run: `python3 $KIT/selftest.py -v`
Expected: `ModuleNotFoundError: No module named 'lib.git_exclusion'`

- [ ] **Step 3: Implement**

`lib/git_exclusion.py`:

```python
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
    out = git(repo, "status", "--porcelain", "--untracked-files=all", "--", rel_dir)
    if exclusion == "none":
        if "??" not in out:
            raise AssertionError(f"expected an untracked entry for {rel_dir}, got: {out!r}")
    elif out.strip():
        raise AssertionError(f"expected {rel_dir} to be excluded under {exclusion}, got: {out!r}")
    return out
```

- [ ] **Step 4: Run to verify pass**

Run: `python3 $KIT/selftest.py -v`
Expected: 9 tests, `OK`

- [ ] **Step 5: Commit**

```
/usr/bin/git -C <worktree> add docs/probes/2026-09-16-skills-discovery
/usr/bin/git -C <worktree> commit -m "Add the Git exclusion arms to the probe kit (#961)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: Verdict rules

**Files:**
- Create: `docs/probes/2026-09-16-skills-discovery/lib/verdict.py`
- Modify: `docs/probes/2026-09-16-skills-discovery/selftest.py`

**Interfaces:**
- Consumes: `lib.probe_skill.Reply`.
- Produces:
  - `VERDICTS = ("visible_first_turn", "visible_after_reload", "catalogue_only", "not_visible", "leaked", "untested")`.
  - `class PromptDesignFailure(Exception)`.
  - `judge_single(expected_token: str, reply: Reply, reload_used: bool = False) -> str`.
  - `judge_control(reply: Reply) -> str`: returns `not_visible` when no token; raises `PromptDesignFailure` when any token is present.
  - `judge_root(token: str, documented: bool, reply: Reply) -> str`: `visible_first_turn` when found and documented, `leaked` when found and undocumented, else `not_visible`.
  - `combine(verdicts: list[str]) -> tuple[str, bool]`: majority verdict and a `flaky` flag (`True` when the runs disagree). Empty input gives `("untested", False)`.
  - `needs_third_run(verdicts: list[str]) -> bool`: `True` when exactly two runs disagree.

- [ ] **Step 1: Write the failing tests**

Append to `selftest.py`:

```python
from lib.probe_skill import Reply  # noqa: E402
from lib.verdict import (  # noqa: E402
    VERDICTS, PromptDesignFailure, combine, judge_control, judge_root, judge_single, needs_third_run,
)


class VerdictTests(unittest.TestCase):
    def test_judge_single(self):
        found = Reply(frozenset({"a" * 12}), True, False)
        self.assertEqual(judge_single("a" * 12, found), "visible_first_turn")
        self.assertEqual(judge_single("a" * 12, found, reload_used=True), "visible_after_reload")
        self.assertEqual(judge_single("b" * 12, Reply(frozenset(), True, False)), "catalogue_only")
        self.assertEqual(judge_single("b" * 12, Reply(frozenset(), False, True)), "not_visible")

    def test_judge_control(self):
        self.assertEqual(judge_control(Reply(frozenset(), False, True)), "not_visible")
        with self.assertRaises(PromptDesignFailure):
            judge_control(Reply(frozenset({"c" * 12}), False, False))

    def test_judge_root(self):
        r = Reply(frozenset({"d" * 12}), True, False)
        self.assertEqual(judge_root("d" * 12, True, r), "visible_first_turn")
        self.assertEqual(judge_root("d" * 12, False, r), "leaked")
        self.assertEqual(judge_root("e" * 12, True, r), "not_visible")

    def test_combine(self):
        self.assertEqual(combine([]), ("untested", False))
        self.assertEqual(combine(["visible_first_turn", "visible_first_turn"]), ("visible_first_turn", False))
        self.assertEqual(combine(["visible_first_turn", "not_visible", "visible_first_turn"]),
                         ("visible_first_turn", True))
        self.assertTrue(needs_third_run(["visible_first_turn", "not_visible"]))
        self.assertFalse(needs_third_run(["not_visible", "not_visible"]))
        self.assertFalse(needs_third_run(["not_visible"]))
        for v in VERDICTS:
            self.assertEqual(combine([v]), (v, False))
```

- [ ] **Step 2: Run to verify failure**

Run: `python3 $KIT/selftest.py -v`
Expected: `ModuleNotFoundError: No module named 'lib.verdict'`

- [ ] **Step 3: Implement**

`lib/verdict.py`:

```python
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
```

- [ ] **Step 4: Run to verify pass**

Run: `python3 $KIT/selftest.py -v`
Expected: 13 tests, `OK`

- [ ] **Step 5: Commit**

```
/usr/bin/git -C <worktree> add docs/probes/2026-09-16-skills-discovery
/usr/bin/git -C <worktree> commit -m "Add verdict rules to the probe kit (#961)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: Run recorder and matrix emission

**Files:**
- Create: `docs/probes/2026-09-16-skills-discovery/lib/recorder.py`
- Modify: `docs/probes/2026-09-16-skills-discovery/selftest.py`

**Interfaces:**
- Consumes: `lib.verdict.combine`.
- Produces:
  - `@dataclass class RunRecord` with fields (all JSON-serialisable): `entry: str`, `harness: str`, `binary: str`, `version: str`, `os: str`, `mode: str`, `argv: list[str]`, `isolation_lever: str`, `credential_files: list[str]`, `auth_ok: bool | None`, `scenario: str`, `arm: str`, `root: str | None`, `exclusion: str`, `hook: dict | None` (`{"mechanism", "config_path", "fired_at"}`), `first_request_at: float | None`, `reply: str`, `tokens_found: list[str]`, `skill_named: bool`, `stderr_path: str | None`, `verdict: str`, `duration_ms: int`, `notes: str = ""`, `expected_tokens: dict[str, str]` (root key to token for S4, single key `"native"` otherwise).
  - `os_label() -> str` such as `macOS 26.6.2 arm64`.
  - `run_dir(outdir: Path, entry: str, mode: str, scenario: str, arm: str) -> Path` (`<outdir>/<entry>/<mode>/<scenario>/<arm-with-slashes-replaced-by-_>`).
  - `write_run(outdir: Path, rec: RunRecord) -> Path`: next free `run<N>.json` (N from 1) in `run_dir`.
  - `load_runs(outdir: Path) -> list[RunRecord]`.
  - `emit_matrix(outdir: Path, target: Path) -> list[dict]`: groups runs by `(entry, mode, scenario, arm, root, exclusion)`, keeps only the highest `version` per entry, applies `combine`, writes rows sorted by that key to `target` as JSON with keys `entry, harness, version, os, mode, scenario, arm, root, exclusion, verdict, flaky, runs, mechanism, evidence, notes` and returns them. `mechanism` is the `hook.mechanism` of the first run, `evidence` the run file paths relative to `target.parent`.

- [ ] **Step 1: Write the failing tests**

Append to `selftest.py`:

```python
import json  # noqa: E402

from lib.recorder import RunRecord, emit_matrix, load_runs, os_label, run_dir, write_run  # noqa: E402


def _rec(**over):
    base = dict(
        entry="fake", harness="fake", binary="/bin/fake", version="1.0", os=os_label(), mode="print",
        argv=["fake", "-p"], isolation_lever="FAKE_HOME", credential_files=[], auth_ok=True,
        scenario="S1", arm="S1/native", root=".fake/skills", exclusion="none",
        hook=None, first_request_at=1.0, reply="tok", tokens_found=["a" * 12], skill_named=True,
        stderr_path=None, verdict="visible_first_turn", duration_ms=10, expected_tokens={"native": "a" * 12},
    )
    base.update(over)
    return RunRecord(**base)


class RecorderTests(unittest.TestCase):
    def test_write_load_emit(self):
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "out"
            p1 = write_run(out, _rec())
            p2 = write_run(out, _rec(verdict="not_visible"))
            self.assertEqual(p1.name, "run1.json")
            self.assertEqual(p2.name, "run2.json")
            self.assertEqual(p1.parent, run_dir(out, "fake", "print", "S1", "S1/native"))
            write_run(out, _rec(verdict="visible_first_turn"))
            write_run(out, _rec(version="0.9", verdict="not_visible", arm="S0/none", scenario="S0"))
            self.assertEqual(len(load_runs(out)), 4)
            rows = emit_matrix(out, Path(d) / "matrix.json")
            self.assertEqual(len(rows), 1)
            row = rows[0]
            self.assertEqual(row["verdict"], "visible_first_turn")
            self.assertTrue(row["flaky"])
            self.assertEqual(row["runs"], 3)
            self.assertEqual(row["version"], "1.0")
            self.assertTrue(all(e.startswith("out/") for e in row["evidence"]))
            self.assertEqual(json.loads((Path(d) / "matrix.json").read_text())[0]["entry"], "fake")

    def test_os_label(self):
        self.assertRegex(os_label(), r"^\S+ \S+ \S+$")
```

- [ ] **Step 2: Run to verify failure**

Run: `python3 $KIT/selftest.py -v`
Expected: `ModuleNotFoundError: No module named 'lib.recorder'`

- [ ] **Step 3: Implement**

`lib/recorder.py`:

```python
from __future__ import annotations

import json
import platform
import subprocess
from dataclasses import asdict, dataclass, field
from pathlib import Path

from lib.verdict import combine


@dataclass
class RunRecord:
    entry: str
    harness: str
    binary: str
    version: str
    os: str
    mode: str
    argv: list[str]
    isolation_lever: str
    credential_files: list[str]
    auth_ok: bool | None
    scenario: str
    arm: str
    root: str | None
    exclusion: str
    hook: dict | None
    first_request_at: float | None
    reply: str
    tokens_found: list[str]
    skill_named: bool
    stderr_path: str | None
    verdict: str
    duration_ms: int
    expected_tokens: dict[str, str] = field(default_factory=dict)
    notes: str = ""


def os_label() -> str:
    if platform.system() == "Darwin":
        ver = subprocess.run(["sw_vers", "-productVersion"], capture_output=True, text=True).stdout.strip()
        return f"macOS {ver} {platform.machine()}"
    return f"{platform.system()} {platform.release()} {platform.machine()}"


def run_dir(outdir: Path, entry: str, mode: str, scenario: str, arm: str) -> Path:
    return outdir / entry / mode / scenario / arm.replace("/", "_")


def write_run(outdir: Path, rec: RunRecord) -> Path:
    d = run_dir(outdir, rec.entry, rec.mode, rec.scenario, rec.arm)
    d.mkdir(parents=True, exist_ok=True)
    n = 1
    while (d / f"run{n}.json").exists():
        n += 1
    path = d / f"run{n}.json"
    path.write_text(json.dumps(asdict(rec), indent=2, sort_keys=True) + "\n")
    return path


def load_runs(outdir: Path) -> list[RunRecord]:
    runs = []
    for path in sorted(outdir.rglob("run*.json")):
        data = json.loads(path.read_text())
        data["_path"] = path
        rec = RunRecord(**{k: v for k, v in data.items() if k != "_path"})
        rec._path = path  # type: ignore[attr-defined]
        runs.append(rec)
    return runs


def _version_key(v: str) -> tuple:
    parts = []
    for piece in v.replace("-", ".").split("."):
        parts.append((0, int(piece)) if piece.isdigit() else (1, piece))
    return tuple(parts)


def emit_matrix(outdir: Path, target: Path) -> list[dict]:
    runs = load_runs(outdir)
    latest: dict[str, str] = {}
    for r in runs:
        if r.entry not in latest or _version_key(r.version) > _version_key(latest[r.entry]):
            latest[r.entry] = r.version
    groups: dict[tuple, list[RunRecord]] = {}
    for r in runs:
        if r.version != latest[r.entry]:
            continue
        groups.setdefault((r.entry, r.mode, r.scenario, r.arm, r.root or "", r.exclusion), []).append(r)
    rows = []
    for key in sorted(groups):
        members = groups[key]
        verdict, flaky = combine([m.verdict for m in members])
        first = members[0]
        rows.append({
            "entry": first.entry, "harness": first.harness, "version": first.version, "os": first.os,
            "mode": first.mode, "scenario": first.scenario, "arm": first.arm, "root": first.root,
            "exclusion": first.exclusion, "verdict": verdict, "flaky": flaky, "runs": len(members),
            "mechanism": (first.hook or {}).get("mechanism"),
            "evidence": [str(m._path.relative_to(target.parent)) for m in members],  # type: ignore[attr-defined]
            "notes": "; ".join(n for n in {m.notes for m in members} if n),
        })
    target.write_text(json.dumps(rows, indent=2) + "\n")
    return rows
```

- [ ] **Step 4: Run to verify pass**

Run: `python3 $KIT/selftest.py -v`
Expected: 15 tests, `OK`

- [ ] **Step 5: Commit**

```
/usr/bin/git -C <worktree> add docs/probes/2026-09-16-skills-discovery
/usr/bin/git -C <worktree> commit -m "Add the run recorder and matrix emission (#961)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: Startup hook payload

**Files:**
- Create: `docs/probes/2026-09-16-skills-discovery/lib/hook_script.py`
- Modify: `docs/probes/2026-09-16-skills-discovery/selftest.py`

**Interfaces:**
- Consumes: `lib.probe_skill.ProbeSkill`.
- Produces:
  - `write_hook_script(config_root: Path, skill_file: Path, body: str, stamp: Path, delete: bool = False) -> Path`: writes `<config_root>/probe-hook.sh` (mode 0755) which creates `skill_file`'s parent, writes `body` to `skill_file` (or deletes the parent directory when `delete`), writes `{"fired_at": <epoch seconds>, "pid": <pid>}` to `stamp`, saves up to one second of stdin to `<stamp>.stdin`, prints nothing, exits 0.
  - `read_stamp(stamp: Path) -> dict | None`.
  - `stamp_path(config_root: Path) -> Path` = `<config_root>/probe-hook-fired.json`.

- [ ] **Step 1: Write the failing tests**

Append to `selftest.py`:

```python
import subprocess  # noqa: E402

from lib.hook_script import read_stamp, stamp_path, write_hook_script  # noqa: E402


class HookScriptTests(unittest.TestCase):
    def test_script_writes_skill_and_stamp(self):
        with tempfile.TemporaryDirectory() as d:
            cfg = Path(d) / "cfg"
            cfg.mkdir()
            skill = ProbeSkill.fresh()
            target = Path(d) / "repo" / ".claude" / "skills" / skill.name / "SKILL.md"
            stamp = stamp_path(cfg)
            script = write_hook_script(cfg, target, skill.render(), stamp)
            self.assertEqual(script.stat().st_mode & 0o111, 0o111)
            self.assertIsNone(read_stamp(stamp))
            proc = subprocess.run([str(script)], input='{"hook_event_name":"SessionStart"}',
                                  capture_output=True, text=True, timeout=10)
            self.assertEqual(proc.returncode, 0, proc.stderr)
            self.assertEqual(proc.stdout, "")
            self.assertEqual(target.read_text(), skill.render())
            self.assertIn("fired_at", read_stamp(stamp))
            self.assertEqual(Path(str(stamp) + ".stdin").read_text(), '{"hook_event_name":"SessionStart"}')

    def test_script_survives_open_stdin_and_can_delete(self):
        with tempfile.TemporaryDirectory() as d:
            cfg = Path(d) / "cfg"
            cfg.mkdir()
            skill = ProbeSkill.fresh()
            target = Path(d) / "repo" / ".x" / skill.name / "SKILL.md"
            target.parent.mkdir(parents=True)
            target.write_text("old")
            stamp = stamp_path(cfg)
            script = write_hook_script(cfg, target, "", stamp, delete=True)
            with subprocess.Popen([str(script)], stdin=subprocess.PIPE, stdout=subprocess.PIPE) as p:
                out, _ = p.communicate(timeout=10)
            self.assertEqual(p.returncode, 0)
            self.assertFalse(target.parent.exists())
            self.assertIsNotNone(read_stamp(stamp))
```

- [ ] **Step 2: Run to verify failure**

Run: `python3 $KIT/selftest.py -v`
Expected: `ModuleNotFoundError: No module named 'lib.hook_script'`

- [ ] **Step 3: Implement**

`lib/hook_script.py`:

```python
from __future__ import annotations

import json
from pathlib import Path


def stamp_path(config_root: Path) -> Path:
    return config_root / "probe-hook-fired.json"


def write_hook_script(config_root: Path, skill_file: Path, body: str, stamp: Path, delete: bool = False) -> Path:
    if delete:
        action = f"rm -rf '{skill_file.parent}'\n"
    else:
        action = (
            f"mkdir -p '{skill_file.parent}'\n"
            f"cat > '{skill_file}' <<'KCAP_PROBE_EOF'\n{body}KCAP_PROBE_EOF\n"
        )
    script = config_root / "probe-hook.sh"
    script.write_text(
        "#!/bin/sh\n"
        "set -eu\n"
        f"{action}"
        f"printf '{{\"fired_at\": %s, \"pid\": %s}}\\n' \"$(date +%s)\" \"$$\" > '{stamp}'\n"
        # A vendor that never closes the hook's stdin must not wedge the launch: capture briefly.
        f"( cat > '{stamp}.stdin' ) & cat_pid=$!\n"
        "sleep 1\n"
        "kill $cat_pid 2>/dev/null || true\n"
        "exit 0\n"
    )
    script.chmod(0o755)
    return script


def read_stamp(stamp: Path) -> dict | None:
    if not stamp.exists():
        return None
    return json.loads(stamp.read_text())
```

- [ ] **Step 4: Run to verify pass**

Run: `python3 $KIT/selftest.py -v`
Expected: 17 tests, `OK`. If `test_script_writes_skill_and_stamp` reports an empty `.stdin` file, the background `cat` lost the race to `kill`: raise `sleep 1` to `sleep 2` in the script and re-run.

- [ ] **Step 5: Commit**

```
/usr/bin/git -C <worktree> add docs/probes/2026-09-16-skills-discovery
/usr/bin/git -C <worktree> commit -m "Add the startup hook payload to the probe kit (#961)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: Adapter contract, fake adapter, print driver

**Files:**
- Create: `docs/probes/2026-09-16-skills-discovery/harness/base.py`
- Create: `docs/probes/2026-09-16-skills-discovery/harness/fake.py`
- Create: `docs/probes/2026-09-16-skills-discovery/lib/print_driver.py`
- Modify: `docs/probes/2026-09-16-skills-discovery/selftest.py`

**Interfaces:**
- Consumes: `lib.isolation.Sandbox`, `lib.hook_script`, `lib.probe_skill`.
- Produces (`harness/base.py`):
  - `@dataclass class HookInfo(mechanism: str, config_path: str)`.
  - `@dataclass class AskResult(reply_text: str, raw: str, argv: list[str], started_at: float, first_request_at: float, stderr_path: str | None, exit_code: int | None)`.
  - `class Adapter` with class attributes `entry: str`, `harness: str`, `binary: str`, `lever: str`, `credential_files: list[str] = []`, `passthrough_env: list[str] = []`, `extra_env: dict[str, str] = {}`, `native_root: str`, `documented_roots: frozenset[str]`, `flat_skill_layout: bool = False`, `modes: tuple[str, ...] = ("print", "daemon")`, `turn_timeout: float = 180.0`; methods `binary_path() -> str | None` (`shutil.which`), `version() -> str` (runs `<binary> --version`, first line, stripped), `real_root() -> Path | None`, `prepare(sb: Sandbox) -> None` (default no-op), `check_auth(sb: Sandbox) -> bool | None` (default `None`), `install_startup_hook(sb: Sandbox, script: Path) -> HookInfo` (abstract), `install_registration(sb: Sandbox, skill_file: Path, body: str) -> HookInfo | None` (default `None`), `ask(sb: Sandbox, mode: str, prompt: str) -> AskResult` (abstract), `list_catalogue(sb: Sandbox) -> str | None` (default `None`), `skill_dir(sb: Sandbox, root: str, name: str) -> Path`, `skill_file(sb: Sandbox, root: str, name: str) -> Path` (honours `flat_skill_layout`).
- Produces (`lib/print_driver.py`): `print_ask(argv: list[str], cwd: Path, env: dict, stderr_path: Path, timeout: float, stdin_text: str | None = None, extract: Callable[[str], str] | None = None) -> AskResult`: runs the process, captures stdout to `raw`, `reply_text = extract(raw)` when given else `raw`, records `started_at` and `first_request_at` (both the spawn instant for print mode), kills on timeout and records `exit_code=None`.
- Produces (`harness/fake.py`): `class FakeAdapter(Adapter)` whose `ask` runs the installed hook script if one was installed, then reads every `kcap-probe-*/SKILL.md` under the roots in `documented_roots` inside `sb.repo` and returns `"<name>=<body_token>"` lines, or `NO-SKILL`. Entry `fake`, native root `.fake/skills`, documented roots `{".fake/skills", ".agents/skills"}`, lever `FAKE_HOME`, binary `sh`.

- [ ] **Step 1: Write the failing tests**

Append to `selftest.py`:

```python
from harness.base import Adapter, AskResult, HookInfo  # noqa: E402
from harness.fake import FakeAdapter  # noqa: E402
from lib.print_driver import print_ask  # noqa: E402


class AdapterTests(unittest.TestCase):
    def test_fake_adapter_round_trip(self):
        with tempfile.TemporaryDirectory() as d:
            fa = FakeAdapter()
            sb = new_sandbox(fa.lever, None, [], base=Path(d))
            skill = ProbeSkill.fresh()
            write_skill(sb.repo / fa.native_root, skill)
            res = fa.ask(sb, "print", single_prompt(skill))
            self.assertIsInstance(res, AskResult)
            self.assertIn(skill.body_token, res.reply_text)
            self.assertIn(skill.body_token, res.raw)
            self.assertEqual(fa.skill_file(sb, fa.native_root, skill.name),
                             sb.repo / ".fake/skills" / skill.name / "SKILL.md")

    def test_fake_adapter_runs_hook_before_reading(self):
        with tempfile.TemporaryDirectory() as d:
            fa = FakeAdapter()
            sb = new_sandbox(fa.lever, None, [], base=Path(d))
            skill = ProbeSkill.fresh()
            script = write_hook_script(sb.config_root, fa.skill_file(sb, fa.native_root, skill.name),
                                       skill.render(), stamp_path(sb.config_root))
            info = fa.install_startup_hook(sb, script)
            self.assertIsInstance(info, HookInfo)
            res = fa.ask(sb, "print", single_prompt(skill))
            self.assertIn(skill.body_token, res.reply_text)
            self.assertIsNotNone(read_stamp(stamp_path(sb.config_root)))

    def test_print_driver_timeout_and_extract(self):
        with tempfile.TemporaryDirectory() as d:
            res = print_ask(["sh", "-c", "echo '{\"result\":\"hi\"}'"], Path(d), dict(os.environ),
                            Path(d) / "e.stderr.log", timeout=10,
                            extract=lambda raw: json.loads(raw)["result"])
            self.assertEqual(res.reply_text, "hi")
            self.assertEqual(res.exit_code, 0)
            slow = print_ask(["sh", "-c", "sleep 5"], Path(d), dict(os.environ), Path(d) / "s.stderr.log", timeout=0.5)
            self.assertIsNone(slow.exit_code)

    def test_adapter_defaults(self):
        self.assertIsNone(Adapter.check_auth(FakeAdapter(), None))
        self.assertIsNone(Adapter.install_registration(FakeAdapter(), None, None, None))
```

- [ ] **Step 2: Run to verify failure**

Run: `python3 $KIT/selftest.py -v`
Expected: `ModuleNotFoundError: No module named 'harness.base'`

- [ ] **Step 3: Implement the contract**

`harness/base.py`:

```python
from __future__ import annotations

import shutil
import subprocess
from dataclasses import dataclass, field
from pathlib import Path

from lib.isolation import Sandbox


@dataclass
class HookInfo:
    mechanism: str
    config_path: str


@dataclass
class AskResult:
    reply_text: str
    raw: str
    argv: list[str]
    started_at: float
    first_request_at: float
    stderr_path: str | None
    exit_code: int | None
    notes: str = ""


class Adapter:
    entry: str = ""
    harness: str = ""
    binary: str = ""
    lever: str = ""
    credential_files: list[str] = []
    passthrough_env: list[str] = []
    extra_env: dict[str, str] = {}
    native_root: str = ""
    documented_roots: frozenset[str] = frozenset()
    flat_skill_layout: bool = False
    modes: tuple[str, ...] = ("print", "daemon")
    turn_timeout: float = 180.0

    def binary_path(self) -> str | None:
        return shutil.which(self.binary)

    def version(self) -> str:
        path = self.binary_path()
        if path is None:
            return "not-installed"
        out = subprocess.run([path, "--version"], capture_output=True, text=True, timeout=60)
        return (out.stdout or out.stderr).strip().splitlines()[0] if (out.stdout or out.stderr).strip() else "unknown"

    def real_root(self) -> Path | None:
        return None

    def prepare(self, sb: Sandbox) -> None:
        return None

    def check_auth(self, sb: Sandbox) -> bool | None:
        return None

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        raise NotImplementedError

    def install_registration(self, sb: Sandbox, skill_file: Path, body: str) -> HookInfo | None:
        return None

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        raise NotImplementedError

    def list_catalogue(self, sb: Sandbox) -> str | None:
        return None

    def skill_dir(self, sb: Sandbox, root: str, name: str) -> Path:
        return sb.repo / root if self.flat_skill_layout else sb.repo / root / name

    def skill_file(self, sb: Sandbox, root: str, name: str) -> Path:
        if self.flat_skill_layout:
            return sb.repo / root / f"{name}.md"
        return sb.repo / root / name / "SKILL.md"
```

`lib/print_driver.py`:

```python
from __future__ import annotations

import subprocess
import time
from pathlib import Path
from typing import Callable

from harness.base import AskResult


def print_ask(
    argv: list[str], cwd: Path, env: dict, stderr_path: Path, timeout: float,
    stdin_text: str | None = None, extract: Callable[[str], str] | None = None,
) -> AskResult:
    started = time.time()
    with stderr_path.open("ab") as err:
        try:
            proc = subprocess.run(
                argv, cwd=str(cwd), env=env, input=stdin_text, capture_output=False,
                stdout=subprocess.PIPE, stderr=err, text=True, timeout=timeout,
                stdin=None if stdin_text is not None else subprocess.DEVNULL,
            )
            raw, code = proc.stdout, proc.returncode
        except subprocess.TimeoutExpired as ex:
            raw = (ex.stdout or b"").decode("utf-8", "replace") if isinstance(ex.stdout, bytes) else (ex.stdout or "")
            code = None
    reply = raw
    if extract is not None:
        try:
            reply = extract(raw)
        except Exception:  # noqa: BLE001
            reply = raw
    return AskResult(reply_text=reply, raw=raw, argv=list(argv), started_at=started,
                     first_request_at=started, stderr_path=str(stderr_path), exit_code=code)
```

`harness/fake.py`:

```python
from __future__ import annotations

import subprocess
import time
from pathlib import Path

from harness.base import Adapter, AskResult, HookInfo
from lib.isolation import Sandbox
from lib.probe_skill import NO_SKILL, TOKEN_RE


class FakeAdapter(Adapter):
    entry = "fake"
    harness = "fake"
    binary = "sh"
    lever = "FAKE_HOME"
    native_root = ".fake/skills"
    documented_roots = frozenset({".fake/skills", ".agents/skills"})

    def __init__(self) -> None:
        self._hook: Path | None = None

    def version(self) -> str:
        return "1.0"

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        self._hook = script
        return HookInfo(mechanism="fake-startup", config_path=str(script))

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        started = time.time()
        if self._hook is not None:
            subprocess.run([str(self._hook)], input="{}", capture_output=True, text=True, timeout=10)
        lines = []
        for root in sorted(self.documented_roots):
            for skill_md in sorted((sb.repo / root).glob("kcap-probe-*/SKILL.md")):
                m = TOKEN_RE.search(skill_md.read_text())
                if m:
                    lines.append(f"{skill_md.parent.name}=PROBE-BODY-{m.group(1)}")
        text = "\n".join(lines) if lines else NO_SKILL
        return AskResult(reply_text=text, raw=text, argv=["fake"], started_at=started,
                         first_request_at=started, stderr_path=None, exit_code=0)
```

- [ ] **Step 4: Run to verify pass**

Run: `python3 $KIT/selftest.py -v`
Expected: 21 tests, `OK`

- [ ] **Step 5: Commit**

```
/usr/bin/git -C <worktree> add docs/probes/2026-09-16-skills-discovery
/usr/bin/git -C <worktree> commit -m "Add the adapter contract, fake adapter and print driver (#961)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: Orchestrator with scenarios S0 to S4

**Files:**
- Create: `docs/probes/2026-09-16-skills-discovery/probe.py`
- Modify: `docs/probes/2026-09-16-skills-discovery/harness/__init__.py`
- Modify: `docs/probes/2026-09-16-skills-discovery/selftest.py`

**Interfaces:**
- Consumes: everything above.
- Produces (`probe.py`, importable as a module and runnable as a script):
  - `ALL_ROOTS: dict[str, str]` mapping root key to root: `{"claude": ".claude/skills", "agents": ".agents/skills", "codex": ".codex/skills", "cursor": ".cursor/skills", "github": ".github/skills", "gemini": ".gemini/skills", "kiro": ".kiro/skills", "pi": ".pi/skills", "opencode": ".opencode/skills", "agent": ".agent/skills"}`.
  - `class Runner(adapter: Adapter, outdir: Path, runs: int = 2, keep: bool = False, base: Path | None = None)` with `run_scenario(mode: str, scenario: str, arms: list[str] | None = None) -> list[RunRecord]` and the per-arm methods `arm_s0`, `arm_s1(exclusion="none")`, `arm_s2(arm)`, `arm_s3(exclusion)`, `arm_s4_all()`, `arm_s4_confirm(root_key)`, each returning one `RunRecord` per run and writing it via `write_run`. Repetition rule inside `run_arm(fn) -> list[RunRecord]`: run `self.runs` times; if exactly two disagree, run a third.
  - S1 gate: `Runner.s1_ok: dict[str, bool]` per mode; when S1's combined verdict is not `visible_first_turn`, later scenarios in that mode write a single `untested` record with `notes="S1 failed"` instead of spending turns.
  - `main(argv: list[str] | None = None) -> int` with the CLI from the spec: `--harness` (repeatable), `--mode`, `--scenario` (repeatable), `--turn`, `--runs`, `--outdir`, `--keep`, `--emit`. Without `--turn` only the free phase runs: version, `binary_path`, `check_auth` in a sandbox, and a `free.json` written under `run_dir(outdir, entry, mode, "free", "free")`.
  - `harness/__init__.py`: `ENTRIES: dict[str, type[Adapter]]`, filled by later tasks; Task 9 registers only `fake` under the key `fake`.

- [ ] **Step 1: Write the failing tests**

Append to `selftest.py`:

```python
import probe  # noqa: E402
from lib.recorder import load_runs as _load  # noqa: E402


class RunnerTests(unittest.TestCase):
    def _runner(self, d, runs=2):
        return probe.Runner(FakeAdapter(), Path(d) / "out", runs=runs, base=Path(d))

    def test_s0_and_s1(self):
        with tempfile.TemporaryDirectory() as d:
            r = self._runner(d)
            recs = r.run_scenario("print", "S0")
            self.assertEqual([x.verdict for x in recs], ["not_visible", "not_visible"])
            recs = r.run_scenario("print", "S1")
            self.assertEqual([x.verdict for x in recs], ["visible_first_turn"] * 2)
            self.assertTrue(r.s1_ok["print"])
            self.assertEqual(recs[0].root, ".fake/skills")
            self.assertEqual(recs[0].expected_tokens.keys(), {"native"})
            self.assertEqual(len(_load(Path(d) / "out")), 4)

    def test_s2_hook_arms_and_registration_skip(self):
        with tempfile.TemporaryDirectory() as d:
            r = self._runner(d)
            r.run_scenario("print", "S1")
            recs = r.run_scenario("print", "S2")
            by_arm = {}
            for x in recs:
                by_arm.setdefault(x.arm, []).append(x)
            self.assertEqual({x.verdict for x in by_arm["S2/hook-creates-root"]}, {"visible_first_turn"})
            self.assertEqual({x.verdict for x in by_arm["S2/hook-adds-skill"]}, {"visible_first_turn"})
            self.assertEqual({x.verdict for x in by_arm["S2/registration"]}, {"untested"})
            self.assertIsNotNone(by_arm["S2/hook-creates-root"][0].hook["fired_at"])
            self.assertEqual(by_arm["S2/hook-creates-root"][0].hook["mechanism"], "fake-startup")

    def test_s3_exclusions(self):
        with tempfile.TemporaryDirectory() as d:
            r = self._runner(d)
            r.run_scenario("print", "S1")
            recs = r.run_scenario("print", "S3")
            self.assertEqual({x.exclusion for x in recs}, {"gitignore", "info-exclude"})
            self.assertEqual({x.verdict for x in recs}, {"visible_first_turn"})

    def test_s4_roots_and_confirmation(self):
        with tempfile.TemporaryDirectory() as d:
            r = self._runner(d)
            r.run_scenario("print", "S1")
            recs = r.run_scenario("print", "S4")
            all_rows = [x for x in recs if x.arm == "S4/all-roots"]
            self.assertEqual(len(all_rows), 2)
            self.assertEqual(all_rows[0].verdict, "visible_first_turn")
            per_root = {}
            for x in recs:
                if x.arm.startswith("S4/confirm-"):
                    per_root[x.root] = x.verdict
            self.assertEqual(per_root[".claude/skills"], "not_visible")
            self.assertNotIn(".fake/skills", per_root)
            self.assertNotIn(".agents/skills", per_root)
            self.assertEqual(all_rows[0].expected_tokens.keys(), set(probe.ALL_ROOTS) | {"fake"})

    def test_s1_gate_blocks_later_scenarios(self):
        with tempfile.TemporaryDirectory() as d:
            r = self._runner(d)
            r.s1_ok["print"] = False
            recs = r.run_scenario("print", "S3")
            self.assertEqual([x.verdict for x in recs], ["untested", "untested"])
            self.assertEqual(recs[0].notes, "S1 failed")

    def test_third_run_on_disagreement(self):
        with tempfile.TemporaryDirectory() as d:
            r = self._runner(d)
            calls = {"n": 0}

            def flaky():
                calls["n"] += 1
                v = "visible_first_turn" if calls["n"] != 2 else "not_visible"
                return probe.RunRecord(entry="fake", harness="fake", binary="x", version="1", os="o",
                                       mode="print", argv=[], isolation_lever="L", credential_files=[],
                                       auth_ok=None, scenario="S1", arm="S1/native", root=None,
                                       exclusion="none", hook=None, first_request_at=None, reply="",
                                       tokens_found=[], skill_named=False, stderr_path=None, verdict=v,
                                       duration_ms=0)
            recs = r.run_arm(flaky)
            self.assertEqual(len(recs), 3)

    def test_cli_free_phase_and_emit(self):
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "out"
            code = probe.main(["--harness", "fake", "--mode", "print", "--outdir", str(out), "--base", d])
            self.assertEqual(code, 0)
            self.assertTrue((out / "fake" / "print" / "free" / "free" / "free.json").exists())
            code = probe.main(["--harness", "fake", "--mode", "print", "--scenario", "S1", "--turn",
                               "--outdir", str(out), "--base", d, "--matrix", str(Path(d) / "m.json")])
            self.assertEqual(code, 0)
            code = probe.main(["--emit", "--outdir", str(out), "--matrix", str(Path(d) / "m.json")])
            self.assertEqual(code, 0)
            rows = json.loads((Path(d) / "m.json").read_text())
            self.assertEqual({r["scenario"] for r in rows}, {"S1"})
```

- [ ] **Step 2: Run to verify failure**

Run: `python3 $KIT/selftest.py -v`
Expected: `ModuleNotFoundError: No module named 'probe'`

- [ ] **Step 3: Implement**

`harness/__init__.py`:

```python
from __future__ import annotations

from harness.base import Adapter
from harness.fake import FakeAdapter

ENTRIES: dict[str, type[Adapter]] = {
    "fake": FakeAdapter,
}
```

`probe.py`:

```python
#!/usr/bin/env python3
"""Skills discovery probes: does a repo-local skill written at startup reach the first model request?

Usage:
  probe.py [--harness ENTRY ...] [--mode print|daemon] [--scenario S0..S4 ...] [--turn]
           [--runs N] [--outdir DIR] [--keep] [--emit] [--matrix FILE] [--base DIR]

Without --turn only the free phase runs (binary, version, auth in an isolated root): zero model
requests. Each turn arm costs one model request per run.
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path
from typing import Callable

KIT = Path(__file__).resolve().parent
sys.path.insert(0, str(KIT))

from harness import ENTRIES  # noqa: E402
from harness.base import Adapter, AskResult  # noqa: E402
from lib.git_exclusion import apply as apply_exclusion, assert_untracked_state  # noqa: E402
from lib.hook_script import read_stamp, stamp_path, write_hook_script  # noqa: E402
from lib.isolation import Sandbox, new_sandbox  # noqa: E402
from lib.probe_skill import ProbeSkill, multi_prompt, parse_reply, single_prompt, write_skill  # noqa: E402
from lib.recorder import RunRecord, emit_matrix, os_label, run_dir, write_run  # noqa: E402
from lib.verdict import judge_control, judge_root, judge_single, needs_third_run, combine  # noqa: E402

ALL_ROOTS: dict[str, str] = {
    "claude": ".claude/skills", "agents": ".agents/skills", "codex": ".codex/skills",
    "cursor": ".cursor/skills", "github": ".github/skills", "gemini": ".gemini/skills",
    "kiro": ".kiro/skills", "pi": ".pi/skills", "opencode": ".opencode/skills", "agent": ".agent/skills",
}
SCENARIOS = ("S0", "S1", "S2", "S3", "S4")
S2_ARMS = ("hook-creates-root", "hook-adds-skill", "registration")
S3_ARMS = ("gitignore", "info-exclude")


class Runner:
    def __init__(self, adapter: Adapter, outdir: Path, runs: int = 2, keep: bool = False,
                 base: Path | None = None) -> None:
        self.adapter = adapter
        self.outdir = outdir
        self.runs = runs
        self.keep = keep
        self.base = base
        self.s1_ok: dict[str, bool] = {}
        self._version = adapter.version()
        self._binary = adapter.binary_path() or adapter.binary
        self._os = os_label()

    # -- sandbox plumbing ---------------------------------------------------------------------

    def sandbox(self) -> Sandbox:
        a = self.adapter
        sb = new_sandbox(a.lever, a.real_root(), a.credential_files, a.passthrough_env,
                         dict(a.extra_env), keep=self.keep, base=self.base)
        a.prepare(sb)
        return sb

    def record(self, mode: str, scenario: str, arm: str, root: str | None, exclusion: str,
               res: AskResult | None, verdict: str, expected: dict[str, str], hook: dict | None = None,
               sb: Sandbox | None = None, notes: str = "", started: float | None = None,
               auth_ok: bool | None = None) -> RunRecord:
        reply = parse_reply(res.reply_text, res.raw) if res else None
        rec = RunRecord(
            entry=self.adapter.entry, harness=self.adapter.harness, binary=self._binary,
            version=self._version, os=self._os, mode=mode, argv=res.argv if res else [],
            isolation_lever=self.adapter.lever, credential_files=list(self.adapter.credential_files),
            auth_ok=auth_ok, scenario=scenario, arm=arm, root=root, exclusion=exclusion, hook=hook,
            first_request_at=res.first_request_at if res else None, reply=res.reply_text if res else "",
            tokens_found=sorted(reply.tokens) if reply else [], skill_named=reply.skill_named if reply else False,
            stderr_path=res.stderr_path if res else None, verdict=verdict,
            duration_ms=int((time.time() - (started or time.time())) * 1000),
            expected_tokens=expected, notes=(notes + ("" if not res or not res.notes else f" {res.notes}")).strip(),
        )
        write_run(self.outdir, rec)
        return rec

    def run_arm(self, fn: Callable[[], RunRecord]) -> list[RunRecord]:
        recs = [fn() for _ in range(self.runs)]
        if needs_third_run([r.verdict for r in recs]):
            recs.append(fn())
        return recs

    def _blocked(self, mode: str, scenario: str, arm: str, root: str | None, exclusion: str) -> RunRecord:
        return self.record(mode, scenario, arm, root, exclusion, None, "untested", {}, notes="S1 failed")

    def _ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        return self.adapter.ask(sb, mode, prompt)

    def _hook_dict(self, sb: Sandbox, mechanism: str, config_path: str) -> dict:
        stamp = read_stamp(stamp_path(sb.config_root))
        return {"mechanism": mechanism, "config_path": config_path,
                "fired_at": stamp.get("fired_at") if stamp else None}

    # -- arms ---------------------------------------------------------------------------------

    def arm_s0(self, mode: str) -> RunRecord:
        started = time.time()
        sb = self.sandbox()
        try:
            skill = ProbeSkill.fresh()
            res = self._ask(sb, mode, single_prompt(skill))
            verdict = judge_control(parse_reply(res.reply_text, res.raw))
            return self.record(mode, "S0", "S0/none", None, "none", res, verdict, {}, started=started)
        finally:
            sb.cleanup()

    def arm_s1(self, mode: str, exclusion: str = "none", scenario: str = "S1",
               root: str | None = None) -> RunRecord:
        started = time.time()
        a = self.adapter
        root = root or a.native_root
        sb = self.sandbox()
        try:
            skill = ProbeSkill.fresh()
            write_skill(sb.repo / root, skill, flat=a.flat_skill_layout)
            rel = str(a.skill_dir(sb, root, skill.name).relative_to(sb.repo))
            apply_exclusion(sb.repo, exclusion, rel)
            notes = ""
            try:
                assert_untracked_state(sb.repo, rel, exclusion)
            except AssertionError as ex:
                notes = f"git state: {ex}"
            res = self._ask(sb, mode, single_prompt(skill))
            verdict = judge_single(skill.token, parse_reply(res.reply_text, res.raw))
            if notes:
                verdict = "untested"
            arm = "S1/native" if scenario == "S1" else f"S3/{exclusion}"
            return self.record(mode, scenario, arm, root, exclusion, res, verdict,
                               {"native": skill.token}, started=started, notes=notes)
        finally:
            sb.cleanup()

    def arm_s2(self, mode: str, arm: str) -> RunRecord:
        started = time.time()
        a = self.adapter
        root = a.native_root
        sb = self.sandbox()
        try:
            skill = ProbeSkill.fresh()
            target = a.skill_file(sb, root, skill.name)
            if arm == "hook-adds-skill":
                write_skill(sb.repo / root, ProbeSkill.fresh(), flat=a.flat_skill_layout)
            if arm == "registration":
                info = a.install_registration(sb, target, skill.render())
                if info is None:
                    return self.record(mode, "S2", "S2/registration", root, "none", None, "untested", {},
                                       notes="no registration mechanism for this entry", started=started)
                reload_used = True
            else:
                script = write_hook_script(sb.config_root, target, skill.render(), stamp_path(sb.config_root))
                info = a.install_startup_hook(sb, script)
                reload_used = False
            res = self._ask(sb, mode, single_prompt(skill))
            verdict = judge_single(skill.token, parse_reply(res.reply_text, res.raw), reload_used=reload_used)
            hook = self._hook_dict(sb, info.mechanism, info.config_path)
            notes = "" if hook["fired_at"] is not None or arm == "registration" else "hook never fired"
            if not target.exists():
                notes = (notes + " skill file absent after the turn").strip()
            return self.record(mode, "S2", f"S2/{arm}", root, "none", res, verdict, {"native": skill.token},
                               hook=hook, started=started, notes=notes)
        finally:
            sb.cleanup()

    def arm_s3(self, mode: str, exclusion: str) -> RunRecord:
        return self.arm_s1(mode, exclusion=exclusion, scenario="S3")

    def arm_s4_all(self, mode: str) -> RunRecord:
        started = time.time()
        a = self.adapter
        sb = self.sandbox()
        try:
            roots = dict(ALL_ROOTS)
            if a.native_root not in roots.values():
                roots[a.entry.replace("-", "_")] = a.native_root
            skills = {key: ProbeSkill.fresh() for key in roots}
            for key, skill in skills.items():
                write_skill(sb.repo / roots[key], skill, flat=a.flat_skill_layout and roots[key] == a.native_root)
            res = self._ask(sb, mode, multi_prompt())
            reply = parse_reply(res.reply_text, res.raw)
            found = {key for key, s in skills.items() if s.token in reply.tokens}
            leaked = {key for key in found if roots[key] not in a.documented_roots}
            verdict = "leaked" if leaked else ("visible_first_turn" if found else "not_visible")
            notes = f"found={sorted(found)} leaked={sorted(leaked)}"
            return self.record(mode, "S4", "S4/all-roots", None, "none", res, verdict,
                               {k: s.token for k, s in skills.items()}, started=started, notes=notes)
        finally:
            sb.cleanup()

    def arm_s4_confirm(self, mode: str, root_key: str, root: str) -> RunRecord:
        started = time.time()
        a = self.adapter
        sb = self.sandbox()
        try:
            skill = ProbeSkill.fresh()
            write_skill(sb.repo / root, skill, flat=a.flat_skill_layout and root == a.native_root)
            res = self._ask(sb, mode, single_prompt(skill))
            verdict = judge_root(skill.token, root in a.documented_roots, parse_reply(res.reply_text, res.raw))
            return self.record(mode, "S4", f"S4/confirm-{root_key}", root, "none", res, verdict,
                               {root_key: skill.token}, started=started)
        finally:
            sb.cleanup()

    # -- scenarios ----------------------------------------------------------------------------

    def run_scenario(self, mode: str, scenario: str, arms: list[str] | None = None) -> list[RunRecord]:
        gated = scenario not in ("S0", "S1") and not self.s1_ok.get(mode, True)
        out: list[RunRecord] = []
        if scenario == "S0":
            out += self.run_arm(lambda: self.arm_s0(mode))
        elif scenario == "S1":
            recs = self.run_arm(lambda: self.arm_s1(mode))
            self.s1_ok[mode] = combine([r.verdict for r in recs])[0] == "visible_first_turn"
            out += recs
        elif scenario == "S2":
            for arm in arms or S2_ARMS:
                if gated:
                    out += self.run_arm(lambda: self._blocked(mode, "S2", f"S2/{arm}", self.adapter.native_root, "none"))
                elif arm == "registration":
                    first = self.arm_s2(mode, arm)
                    out.append(first)
                    if first.verdict != "untested":
                        recs = [first, self.arm_s2(mode, arm)]
                        if needs_third_run([r.verdict for r in recs]):
                            recs.append(self.arm_s2(mode, arm))
                        out += recs[1:]
                else:
                    out += self.run_arm(lambda a=arm: self.arm_s2(mode, a))
        elif scenario == "S3":
            for exclusion in arms or S3_ARMS:
                if gated:
                    out += self.run_arm(lambda e=exclusion: self._blocked(mode, "S3", f"S3/{e}", self.adapter.native_root, e))
                else:
                    out += self.run_arm(lambda e=exclusion: self.arm_s3(mode, e))
        elif scenario == "S4":
            if gated:
                out += self.run_arm(lambda: self._blocked(mode, "S4", "S4/all-roots", None, "none"))
                return out
            recs = self.run_arm(lambda: self.arm_s4_all(mode))
            out += recs
            roots = dict(ALL_ROOTS)
            if self.adapter.native_root not in roots.values():
                roots[self.adapter.entry.replace("-", "_")] = self.adapter.native_root
            for key, root in roots.items():
                seen_every_run = all(recs[i].expected_tokens.get(key) in recs[i].tokens_found for i in range(len(recs)))
                if seen_every_run:
                    continue
                out += self.run_arm(lambda k=key, r=root: self.arm_s4_confirm(mode, k, r))
        else:
            raise ValueError(scenario)
        return out


def free_phase(adapter: Adapter, outdir: Path, mode: str, base: Path | None) -> dict:
    sb = new_sandbox(adapter.lever, adapter.real_root(), adapter.credential_files, adapter.passthrough_env,
                     dict(adapter.extra_env), base=base)
    try:
        adapter.prepare(sb)
        info = {
            "entry": adapter.entry, "binary": adapter.binary_path(), "version": adapter.version(),
            "os": os_label(), "mode": mode, "isolation_lever": adapter.lever,
            "credential_files": [c for c in adapter.credential_files if (sb.config_root / c).exists()],
            "auth_ok": adapter.check_auth(sb) if adapter.binary_path() else None,
            "catalogue": adapter.list_catalogue(sb) if adapter.binary_path() else None,
        }
    finally:
        sb.cleanup()
    d = run_dir(outdir, adapter.entry, mode, "free", "free")
    d.mkdir(parents=True, exist_ok=True)
    (d / "free.json").write_text(json.dumps(info, indent=2) + "\n")
    return info


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--harness", action="append", choices=sorted(ENTRIES))
    p.add_argument("--mode", default="print", choices=("print", "daemon"))
    p.add_argument("--scenario", action="append", choices=SCENARIOS)
    p.add_argument("--turn", action="store_true")
    p.add_argument("--runs", type=int, default=2)
    p.add_argument("--outdir", type=Path, default=KIT / "out")
    p.add_argument("--matrix", type=Path, default=KIT / "matrix.json")
    p.add_argument("--keep", action="store_true")
    p.add_argument("--emit", action="store_true")
    p.add_argument("--base", type=Path, default=None)
    args = p.parse_args(argv)

    if args.emit:
        rows = emit_matrix(args.outdir, args.matrix)
        print(f"wrote {len(rows)} rows to {args.matrix}")
        return 0

    entries = args.harness or [e for e in ENTRIES if e != "fake"]
    for name in entries:
        adapter = ENTRIES[name]()
        if args.mode not in adapter.modes:
            print(f"{name}: mode {args.mode} unsupported, skipping")
            continue
        info = free_phase(adapter, args.outdir, args.mode, args.base)
        print(f"{name}: {info['version']} auth_ok={info['auth_ok']}")
        if not args.turn:
            continue
        if info["binary"] is None:
            print(f"{name}: binary not installed, turn arms skipped")
            continue
        runner = Runner(adapter, args.outdir, runs=args.runs, keep=args.keep, base=args.base)
        for scenario in args.scenario or SCENARIOS:
            recs = runner.run_scenario(args.mode, scenario)
            for r in recs:
                print(f"{name} {args.mode} {r.arm} root={r.root} excl={r.exclusion} -> {r.verdict} {r.notes}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
```

- [ ] **Step 4: Run to verify pass**

Run: `python3 $KIT/selftest.py -v`
Expected: 28 tests, `OK`

- [ ] **Step 5: Smoke the CLI by hand**

Run: `python3 $KIT/probe.py --harness fake --mode print --scenario S0 --scenario S1 --turn --outdir /tmp/skprobe-smoke --base /tmp`
Expected: lines ending in `-> not_visible` for S0 and `-> visible_first_turn` for S1.

- [ ] **Step 6: Commit**

```
/usr/bin/git -C <worktree> add docs/probes/2026-09-16-skills-discovery
/usr/bin/git -C <worktree> commit -m "Add the probe orchestrator with scenarios S0 to S4 (#961)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 10: ACP one-turn driver

**Files:**
- Create: `docs/probes/2026-09-16-skills-discovery/lib/acp_driver.py`
- Create: `docs/probes/2026-09-16-skills-discovery/selftest_servers.py`
- Modify: `docs/probes/2026-09-16-skills-discovery/selftest.py`

**Interfaces:**
- Consumes: `AcpClient` from `docs/probes/2026-08-04-acp-reconnect-c0/acp_c0_probe.py` (its `start()` merges `extra_env` over `os.environ`, so the subclass below replaces `start()` to use the sandbox environment verbatim), `harness.base.AskResult`.
- Produces: `acp_ask(argv: list[str], cwd: Path, env: dict, prompt: str, stderr_path: Path, timeout: float = 180.0) -> AskResult`. Sequence: `initialize` → `session/new {cwd, mcpServers: []}` → `session/prompt {sessionId, prompt:[{type:"text", text}]}`. `reply_text` is the concatenation of `session/update` notifications whose `update.sessionUpdate` is `agent_message_chunk` or `agent_message` (`update.content.text` or each `content[i].text`). `raw` is the JSON frame log. `notes` carries `stopReason=<value>` and any error. `first_request_at` is stamped just before `session/prompt`.
- Produces: `selftest_servers.py`, a stdlib script that fakes a vendor: `python3 selftest_servers.py acp|appserver|pirpc`. Every fake reads `.fake/skills/kcap-probe-*/SKILL.md` under its cwd and answers each prompt with `<name>=PROBE-BODY-<token>` lines or `NO-SKILL`.

- [ ] **Step 1: Write the fake servers**

`selftest_servers.py`:

```python
#!/usr/bin/env python3
"""Fake vendor processes for the kit's self-tests: acp, appserver (codex app-server) and pirpc."""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

TOKEN_RE = re.compile(r"PROBE-BODY-([0-9a-f]{12})")


def answer() -> str:
    lines = []
    for f in sorted(Path.cwd().glob(".fake/skills/kcap-probe-*/SKILL.md")):
        m = TOKEN_RE.search(f.read_text())
        if m:
            lines.append(f"{f.parent.name}=PROBE-BODY-{m.group(1)}")
    return "\n".join(lines) if lines else "NO-SKILL"


def send(obj: dict) -> None:
    sys.stdout.write(json.dumps(obj) + "\n")
    sys.stdout.flush()


def acp() -> None:
    for line in sys.stdin:
        msg = json.loads(line)
        m, i, p = msg.get("method"), msg.get("id"), msg.get("params") or {}
        if m == "initialize":
            send({"jsonrpc": "2.0", "id": i, "result": {"protocolVersion": 1, "agentCapabilities": {}}})
        elif m == "session/new":
            send({"jsonrpc": "2.0", "id": i, "result": {"sessionId": "s1"}})
        elif m == "session/prompt":
            send({"jsonrpc": "2.0", "method": "session/update", "params": {
                "sessionId": p["sessionId"],
                "update": {"sessionUpdate": "agent_message_chunk", "content": {"type": "text", "text": answer()}}}})
            send({"jsonrpc": "2.0", "id": i, "result": {"stopReason": "end_turn"}})
        elif i is not None:
            send({"jsonrpc": "2.0", "id": i, "error": {"code": -32601, "message": "unknown"}})


def appserver() -> None:
    for line in sys.stdin:
        msg = json.loads(line)
        m, i, p = msg.get("method"), msg.get("id"), msg.get("params") or {}
        if m == "initialize":
            send({"jsonrpc": "2.0", "id": i, "result": {}})
        elif m == "hooks/list":
            send({"jsonrpc": "2.0", "id": i, "result": {"hooks": []}})
        elif m == "thread/start":
            send({"jsonrpc": "2.0", "id": i, "result": {"thread": {"id": "t1"}, "model": "fake"}})
        elif m == "turn/start":
            send({"jsonrpc": "2.0", "id": i, "result": {"turn": {"id": "u1"}}})
            send({"jsonrpc": "2.0", "method": "item/completed", "params": {
                "item": {"type": "agentMessage", "id": "m1", "text": answer()}}})
            send({"jsonrpc": "2.0", "method": "turn/completed", "params": {"turn": {"id": "u1", "status": "completed"}}})
        elif i is not None:
            send({"jsonrpc": "2.0", "id": i, "error": {"code": -32601, "message": "unknown"}})


def pirpc() -> None:
    for line in sys.stdin:
        msg = json.loads(line)
        if msg.get("type") == "prompt":
            send({"id": msg.get("id"), "type": "response", "success": True})
            send({"type": "agent_start"})
            send({"type": "message_end", "message": {"role": "assistant", "content": [{"type": "text", "text": answer()}]}})
            send({"type": "agent_settled"})


if __name__ == "__main__":
    {"acp": acp, "appserver": appserver, "app-server": appserver, "pirpc": pirpc}[sys.argv[1]]()
```

Make it executable so a driver can launch it exactly like a vendor binary: `chmod 755 $KIT/selftest_servers.py`. The `app-server` alias lets `appserver_ask` run it as `<binary> app-server`.

- [ ] **Step 2: Write the failing test**

Append to `selftest.py`:

```python
from lib.acp_driver import acp_ask  # noqa: E402

SERVERS = KIT / "selftest_servers.py"


def _fake_repo(d: str, skill: ProbeSkill) -> Path:
    repo = Path(d) / "repo"
    write_skill(repo / ".fake" / "skills", skill)
    return repo


class AcpDriverTests(unittest.TestCase):
    def test_one_turn(self):
        with tempfile.TemporaryDirectory() as d:
            skill = ProbeSkill.fresh()
            repo = _fake_repo(d, skill)
            res = acp_ask([sys.executable, str(SERVERS), "acp"], repo, dict(os.environ), single_prompt(skill),
                          Path(d) / "acp.stderr.log", timeout=30)
            self.assertIn(skill.body_token, res.reply_text)
            self.assertIn("stopReason=end_turn", res.notes)
            self.assertGreaterEqual(res.first_request_at, res.started_at)
            self.assertEqual(res.argv[-1], "acp")
```

- [ ] **Step 3: Run to verify failure**

Run: `python3 $KIT/selftest.py -v`
Expected: `ModuleNotFoundError: No module named 'lib.acp_driver'`

- [ ] **Step 4: Implement**

`lib/acp_driver.py`:

```python
from __future__ import annotations

import asyncio
import json
import sys
import time
from pathlib import Path

from harness.base import AskResult

KIT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(KIT.parent / "2026-08-04-acp-reconnect-c0"))
from acp_c0_probe import AcpClient  # noqa: E402

INIT_PARAMS = {
    "protocolVersion": 1,
    "clientCapabilities": {"fs": {"readTextFile": False, "writeTextFile": False}, "terminal": False},
}


class IsolatedAcpClient(AcpClient):
    """AcpClient whose child sees exactly the sandbox environment, not the developer's."""

    def __init__(self, argv, cwd, env, stderr_path):
        super().__init__(argv, cwd, frames=[], phase_ref=["turn"], label="acp", stderr_path=str(stderr_path))
        self.env = env

    async def start(self):
        stderr_f = open(self.stderr_path, "ab")
        self.proc = await asyncio.create_subprocess_exec(
            *self.argv, cwd=self.cwd, env=self.env,
            stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=stderr_f)
        self.reader_task = asyncio.create_task(self._read_loop())
        self.record("mark", {"event": "spawned", "pid": self.proc.pid, "argv": self.argv})


def agent_text(frames: list[dict]) -> str:
    out = []
    for f in frames:
        fr = f.get("frame") or {}
        if fr.get("method") != "session/update":
            continue
        upd = ((fr.get("params") or {}).get("update") or {})
        if upd.get("sessionUpdate") not in ("agent_message_chunk", "agent_message"):
            continue
        content = upd.get("content")
        if isinstance(content, dict) and isinstance(content.get("text"), str):
            out.append(content["text"])
        elif isinstance(content, list):
            out.extend(c["text"] for c in content if isinstance(c, dict) and isinstance(c.get("text"), str))
    return "".join(out)


async def _turn(argv, cwd, env, prompt, stderr_path, timeout) -> AskResult:
    client = IsolatedAcpClient(argv, str(cwd), env, stderr_path)
    started = time.time()
    first = started
    text, notes = "", []
    await client.start()
    try:
        await client.request("initialize", INIT_PARAMS, timeout=90)
        new = await client.request("session/new", {"cwd": str(cwd), "mcpServers": []}, timeout=120)
        sid = (new.get("result") or {}).get("sessionId")
        if not sid:
            notes.append(f"session/new failed: {json.dumps(new)[:500]}")
        else:
            first = time.time()
            resp = await client.request(
                "session/prompt", {"sessionId": sid, "prompt": [{"type": "text", "text": prompt}]}, timeout=timeout)
            notes.append(f"stopReason={(resp.get('result') or {}).get('stopReason')}")
            if "error" in resp:
                notes.append(f"error={json.dumps(resp['error'])[:500]}")
            text = agent_text(client.frames)
    except Exception as ex:  # noqa: BLE001
        notes.append(f"exception={ex!r}")
    finally:
        await client.shutdown()
    return AskResult(reply_text=text, raw=json.dumps(client.frames), argv=list(argv), started_at=started,
                     first_request_at=first, stderr_path=str(stderr_path), exit_code=client.proc.returncode,
                     notes=" ".join(notes))


def acp_ask(argv: list[str], cwd: Path, env: dict, prompt: str, stderr_path: Path, timeout: float = 180.0) -> AskResult:
    return asyncio.run(_turn(argv, cwd, env, prompt, stderr_path, timeout))
```

- [ ] **Step 5: Run to verify pass**

Run: `python3 $KIT/selftest.py -v`
Expected: 29 tests, `OK`

- [ ] **Step 6: Commit**

```
/usr/bin/git -C <worktree> add docs/probes/2026-09-16-skills-discovery
/usr/bin/git -C <worktree> commit -m "Add the ACP one-turn driver and fake vendor servers (#961)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 11: Codex app-server and Pi RPC drivers

**Files:**
- Create: `docs/probes/2026-09-16-skills-discovery/lib/jsonl_child.py`
- Create: `docs/probes/2026-09-16-skills-discovery/lib/appserver_driver.py`
- Create: `docs/probes/2026-09-16-skills-discovery/lib/pirpc_driver.py`
- Modify: `docs/probes/2026-09-16-skills-discovery/selftest.py`

**Interfaces:**
- Produces (`lib/jsonl_child.py`): `class JsonlChild(argv, cwd, env, stderr_path)` with `start()`, `send(obj)`, `recv(timeout) -> dict | None` (next JSON line, `None` on EOF), `close_stdin()`, `stop()`, `frames: list[dict]` (everything sent and received), `returncode`. Synchronous, thread-backed reader with a queue.
- Produces (`lib/appserver_driver.py`): `appserver_ask(binary: str, cwd: Path, env: dict, prompt: str, stderr_path: Path, timeout: float = 180.0, extra_argv: list[str] = ()) -> AskResult`. Sequence: spawn `[binary, "app-server", *extra_argv]`; `initialize {clientInfo:{name:"kcap-probe",version:"1"}, capabilities:{}}`; `hooks/list {}`; when any hook in `result.hooks[]` has `trustStatus != "trusted"` and a `currentHash`, stop the child and respawn with `-c hooks.state={"<key>"={trusted_hash="<hash>"},…}` appended (the same full-table override the daemon uses; dotted keys silently fail); `thread/start {cwd, sandbox:"read-only", approvalPolicy:"never", approvalsReviewer:"user"}` → `result.thread.id`; `turn/start {threadId, input:[{type:"text",text}], sandboxPolicy:{type:"readOnly"}, approvalPolicy:"never", approvalsReviewer:"user"}`; read notifications until `turn/completed`; `reply_text` is the `params.item.text` of `item/completed` items with `type == "agentMessage"`, or the accumulated `item/agentMessage/delta` `params.delta` values when no completed item arrived. Server requests are answered with `-32601`. `notes` records `hook_trust=<seeded|trusted|none>` and the turn status.
- Produces (`lib/pirpc_driver.py`): `pirpc_ask(argv: list[str], cwd: Path, env: dict, prompt: str, stderr_path: Path, timeout: float = 180.0) -> AskResult`. Sends `{"id":"1","type":"prompt","message":<prompt>,"streamingBehavior":"followUp"}`; reads until `{"type":"agent_settled"}`; `reply_text` joins `message.content[].text` of `message_end` events whose `message.role == "assistant"`.

- [ ] **Step 1: Write the failing tests**

Append to `selftest.py`:

```python
from lib.appserver_driver import appserver_ask  # noqa: E402
from lib.pirpc_driver import pirpc_ask  # noqa: E402
from lib.jsonl_child import JsonlChild  # noqa: E402


class JsonlDriversTests(unittest.TestCase):
    def test_jsonl_child_round_trip(self):
        with tempfile.TemporaryDirectory() as d:
            c = JsonlChild([sys.executable, str(SERVERS), "pirpc"], Path(d), dict(os.environ), Path(d) / "c.stderr.log")
            c.start()
            c.send({"id": "1", "type": "prompt", "message": "x", "streamingBehavior": "followUp"})
            first = c.recv(10)
            self.assertEqual(first["type"], "response")
            c.close_stdin()
            c.stop()
            self.assertGreaterEqual(len(c.frames), 2)

    def test_appserver_turn(self):
        with tempfile.TemporaryDirectory() as d:
            skill = ProbeSkill.fresh()
            repo = _fake_repo(d, skill)
            res = appserver_ask(str(SERVERS), repo, dict(os.environ), single_prompt(skill),
                                Path(d) / "as.stderr.log", timeout=30)
            self.assertIn(skill.body_token, res.reply_text)
            self.assertIn("hook_trust=none", res.notes)
            self.assertEqual(res.argv[:2], [str(SERVERS), "app-server"])

    def test_pirpc_turn(self):
        with tempfile.TemporaryDirectory() as d:
            skill = ProbeSkill.fresh()
            repo = _fake_repo(d, skill)
            res = pirpc_ask([sys.executable, str(SERVERS), "pirpc"], repo, dict(os.environ), single_prompt(skill),
                            Path(d) / "pi.stderr.log", timeout=30)
            self.assertIn(skill.body_token, res.reply_text)
```

Note: the fake server treats the literal `app-server` argument that `appserver_ask` inserts as its mode selector; the assertion on `argv[:2]` pins that insertion.

- [ ] **Step 2: Run to verify failure**

Run: `python3 $KIT/selftest.py -v`
Expected: `ModuleNotFoundError: No module named 'lib.appserver_driver'`

- [ ] **Step 3: Implement the child**

`lib/jsonl_child.py`:

```python
from __future__ import annotations

import json
import queue
import subprocess
import threading
import time
from pathlib import Path


class JsonlChild:
    def __init__(self, argv: list[str], cwd: Path, env: dict, stderr_path: Path) -> None:
        self.argv = list(argv)
        self.cwd = str(cwd)
        self.env = env
        self.stderr_path = stderr_path
        self.frames: list[dict] = []
        self._q: queue.Queue = queue.Queue()
        self.proc: subprocess.Popen | None = None

    def start(self) -> None:
        self._err = open(self.stderr_path, "ab")
        self.proc = subprocess.Popen(self.argv, cwd=self.cwd, env=self.env, stdin=subprocess.PIPE,
                                     stdout=subprocess.PIPE, stderr=self._err, text=True, bufsize=1)
        threading.Thread(target=self._reader, daemon=True).start()

    def _reader(self) -> None:
        assert self.proc and self.proc.stdout
        for line in self.proc.stdout:
            line = line.strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                obj = {"unparseable": line[:2000]}
            self.frames.append({"t": time.time(), "dir": "in", "frame": obj})
            self._q.put(obj)
        self._q.put(None)

    def send(self, obj: dict) -> None:
        assert self.proc and self.proc.stdin
        self.frames.append({"t": time.time(), "dir": "out", "frame": obj})
        self.proc.stdin.write(json.dumps(obj) + "\n")
        self.proc.stdin.flush()

    def recv(self, timeout: float) -> dict | None:
        try:
            return self._q.get(timeout=timeout)
        except queue.Empty:
            raise TimeoutError(f"no frame within {timeout}s")

    def close_stdin(self) -> None:
        if self.proc and self.proc.stdin:
            try:
                self.proc.stdin.close()
            except OSError:
                pass

    def stop(self, grace: float = 5.0) -> None:
        if not self.proc:
            return
        self.close_stdin()
        try:
            self.proc.wait(timeout=grace)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            self.proc.wait()
        self._err.close()

    @property
    def returncode(self) -> int | None:
        return self.proc.returncode if self.proc else None
```

- [ ] **Step 4: Implement the app-server driver**

`lib/appserver_driver.py`:

```python
from __future__ import annotations

import json
import time
from pathlib import Path

from harness.base import AskResult
from lib.jsonl_child import JsonlChild


class _Rpc:
    def __init__(self, child: JsonlChild) -> None:
        self.child = child
        self.next_id = 0
        self.notifications: list[dict] = []

    def request(self, method: str, params: dict, timeout: float) -> dict:
        self.next_id += 1
        rid = self.next_id
        self.child.send({"jsonrpc": "2.0", "id": rid, "method": method, "params": params})
        deadline = time.time() + timeout
        while True:
            msg = self.child.recv(max(0.1, deadline - time.time()))
            if msg is None:
                raise ConnectionError(f"EOF waiting for {method}")
            if msg.get("id") == rid and "method" not in msg:
                return msg
            self._absorb(msg)

    def _absorb(self, msg: dict) -> None:
        if "method" in msg and "id" in msg:
            self.child.send({"jsonrpc": "2.0", "id": msg["id"], "error": {"code": -32601, "message": "unsupported"}})
        elif "method" in msg:
            self.notifications.append(msg)

    def wait_notification(self, method: str, timeout: float) -> dict | None:
        deadline = time.time() + timeout
        while time.time() < deadline:
            msg = self.child.recv(max(0.1, deadline - time.time()))
            if msg is None:
                return None
            self._absorb(msg)
            if msg.get("method") == method:
                return msg
        return None


def _toml_string(s: str) -> str:
    return json.dumps(s)


def hook_state_override(hooks: list[dict]) -> str | None:
    untrusted = [h for h in hooks if h.get("trustStatus") != "trusted" and h.get("currentHash") and h.get("key")]
    if not untrusted:
        return None
    entries = ",".join(f"{_toml_string(h['key'])}={{trusted_hash={_toml_string(h['currentHash'])}}}" for h in untrusted)
    return f"hooks.state={{{entries}}}"


def appserver_ask(binary: str, cwd: Path, env: dict, prompt: str, stderr_path: Path,
                  timeout: float = 180.0, extra_argv: list[str] = ()) -> AskResult:
    started = time.time()
    argv = [binary, "app-server", *extra_argv]
    notes = []
    text = ""
    first = started
    child = JsonlChild(argv, cwd, env, stderr_path)
    child.start()
    rpc = _Rpc(child)
    try:
        init = {"clientInfo": {"name": "kcap-probe", "version": "1"}, "capabilities": {}}
        rpc.request("initialize", init, 60)
        hooks = (rpc.request("hooks/list", {}, 60).get("result") or {}).get("hooks") or []
        override = hook_state_override(hooks)
        if override:
            child.stop()
            argv = [*argv, "-c", override]
            child = JsonlChild(argv, cwd, env, stderr_path)
            child.start()
            rpc = _Rpc(child)
            rpc.request("initialize", init, 60)
            notes.append("hook_trust=seeded")
        else:
            notes.append("hook_trust=trusted" if hooks else "hook_trust=none")
        thread = rpc.request("thread/start", {
            "cwd": str(cwd), "sandbox": "read-only", "approvalPolicy": "never", "approvalsReviewer": "user",
        }, 120)
        tid = ((thread.get("result") or {}).get("thread") or {}).get("id")
        if not tid:
            notes.append(f"thread/start failed: {json.dumps(thread)[:500]}")
        else:
            first = time.time()
            rpc.request("turn/start", {
                "threadId": tid, "input": [{"type": "text", "text": prompt}],
                "sandboxPolicy": {"type": "readOnly"}, "approvalPolicy": "never", "approvalsReviewer": "user",
            }, 60)
            done = rpc.wait_notification("turn/completed", timeout)
            notes.append(f"turn={(((done or {}).get('params') or {}).get('turn') or {}).get('status')}")
            completed = [n["params"]["item"]["text"] for n in rpc.notifications
                         if n.get("method") == "item/completed"
                         and (n.get("params") or {}).get("item", {}).get("type") == "agentMessage"
                         and isinstance(n["params"]["item"].get("text"), str)]
            deltas = [n["params"].get("delta", "") for n in rpc.notifications if n.get("method") == "item/agentMessage/delta"]
            text = "\n".join(completed) if completed else "".join(deltas)
    except Exception as ex:  # noqa: BLE001
        notes.append(f"exception={ex!r}")
    finally:
        child.stop()
    return AskResult(reply_text=text, raw=json.dumps(child.frames), argv=argv, started_at=started,
                     first_request_at=first, stderr_path=str(stderr_path), exit_code=child.returncode,
                     notes=" ".join(notes))
```

- [ ] **Step 5: Implement the Pi RPC driver**

`lib/pirpc_driver.py`:

```python
from __future__ import annotations

import json
import time
from pathlib import Path

from harness.base import AskResult
from lib.jsonl_child import JsonlChild


def pirpc_ask(argv: list[str], cwd: Path, env: dict, prompt: str, stderr_path: Path, timeout: float = 180.0) -> AskResult:
    started = time.time()
    child = JsonlChild(argv, cwd, env, stderr_path)
    child.start()
    texts, notes = [], []
    first = time.time()
    try:
        child.send({"id": "1", "type": "prompt", "message": prompt, "streamingBehavior": "followUp"})
        deadline = time.time() + timeout
        while time.time() < deadline:
            msg = child.recv(max(0.1, deadline - time.time()))
            if msg is None:
                notes.append("eof before agent_settled")
                break
            t = msg.get("type")
            if t == "message_end" and (msg.get("message") or {}).get("role") == "assistant":
                for part in (msg["message"].get("content") or []):
                    if isinstance(part, dict) and part.get("type") == "text":
                        texts.append(part.get("text", ""))
                if msg["message"].get("stopReason") == "error":
                    notes.append(f"error={msg['message'].get('errorMessage')}")
            elif t == "response" and msg.get("success") is False:
                notes.append(f"prompt rejected: {json.dumps(msg)[:300]}")
            elif t == "extension_ui_request":
                notes.append(f"blocked on ui request {msg.get('method')}")
                break
            elif t == "agent_settled":
                break
        else:
            notes.append("timeout")
    except Exception as ex:  # noqa: BLE001
        notes.append(f"exception={ex!r}")
    finally:
        child.stop()
    return AskResult(reply_text="\n".join(texts), raw=json.dumps(child.frames), argv=list(argv), started_at=started,
                     first_request_at=first, stderr_path=str(stderr_path), exit_code=child.returncode,
                     notes=" ".join(notes))
```

- [ ] **Step 6: Run to verify pass**

Run: `python3 $KIT/selftest.py -v`
Expected: 32 tests, `OK`

- [ ] **Step 7: Commit**

```
/usr/bin/git -C <worktree> add docs/probes/2026-09-16-skills-discovery
/usr/bin/git -C <worktree> commit -m "Add the Codex app-server and Pi RPC drivers (#961)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 12: Claude adapter

**Files:**
- Create: `docs/probes/2026-09-16-skills-discovery/harness/claude.py`
- Modify: `docs/probes/2026-09-16-skills-discovery/harness/__init__.py`
- Modify: `docs/probes/2026-09-16-skills-discovery/selftest.py`

**Interfaces:**
- Consumes: `Adapter`, `HookInfo`, `print_ask`.
- Produces: `class ClaudeAdapter(Adapter)`, entry `claude`, lever `CLAUDE_CONFIG_DIR`, native root `.claude/skills`, documented roots `{".claude/skills"}`, modes `("print",)` (the daemon launches Claude the same way). Hook goes to `<config>/settings.json` under `hooks.SessionStart`, the location kcap's plugin hooks resolve to. Print argv: `claude -p <prompt> --output-format json --max-turns 4 --strict-mcp-config --allowedTools Skill --setting-sources user`; reply is the top-level `result` field. Environment switch `KCAP_PROBE_CLAUDE_REAL_CONFIG=1` makes the adapter use the developer's real config root (for a keychain-only credential) with hooks supplied through `--settings <file>` and `KCAP_SKIP=1` so kcap's own hooks stand down.

- [ ] **Step 1: Write the failing test**

Append to `selftest.py` a generic hook-file test that every adapter task extends by adding its class to `ENTRIES`:

```python
from harness import ENTRIES  # noqa: E402


class AdapterHookFilesTests(unittest.TestCase):
    def test_every_adapter_writes_a_hook_referencing_the_script(self):
        for name, cls in ENTRIES.items():
            if name == "fake":
                continue
            with self.subTest(entry=name), tempfile.TemporaryDirectory() as d:
                a = cls()
                sb = new_sandbox(a.lever, None, [], base=Path(d))
                a.prepare(sb)
                script = sb.config_root / "probe-hook.sh"
                script.write_text("#!/bin/sh\nexit 0\n")
                info = a.install_startup_hook(sb, script)
                self.assertTrue(Path(info.config_path).exists(), info)
                self.assertIn(str(script), Path(info.config_path).read_text())
                self.assertTrue(info.mechanism)
                self.assertTrue(a.native_root in a.documented_roots, name)
```

Also append:

```python
class ClaudeAdapterTests(unittest.TestCase):
    def test_prepare_trusts_repo(self):
        from harness.claude import ClaudeAdapter
        with tempfile.TemporaryDirectory() as d:
            a = ClaudeAdapter()
            sb = new_sandbox(a.lever, None, [], base=Path(d))
            a.prepare(sb)
            cfg = json.loads((sb.config_root / ".claude.json").read_text())
            self.assertTrue(cfg["projects"][str(sb.repo)]["hasTrustDialogAccepted"])
            self.assertEqual(a.print_argv(sb, "hi")[:3], [a.binary_path() or "claude", "-p", "hi"])
```

- [ ] **Step 2: Run to verify failure**

Run: `python3 $KIT/selftest.py -v`
Expected: `ModuleNotFoundError: No module named 'harness.claude'`

- [ ] **Step 3: Implement**

`harness/claude.py`:

```python
from __future__ import annotations

import json
import os
import subprocess
from pathlib import Path

from harness.base import Adapter, AskResult, HookInfo
from lib.isolation import Sandbox
from lib.print_driver import print_ask


class ClaudeAdapter(Adapter):
    entry = "claude"
    harness = "claude"
    binary = "claude"
    lever = "CLAUDE_CONFIG_DIR"
    credential_files = [".credentials.json"]
    native_root = ".claude/skills"
    documented_roots = frozenset({".claude/skills"})
    modes = ("print",)

    def __init__(self) -> None:
        self.real_config = os.environ.get("KCAP_PROBE_CLAUDE_REAL_CONFIG") == "1"

    def real_root(self) -> Path | None:
        return Path(os.environ.get("CLAUDE_CONFIG_DIR") or (Path.home() / ".claude"))

    def prepare(self, sb: Sandbox) -> None:
        if self.real_config:
            sb.env["CLAUDE_CONFIG_DIR"] = str(self.real_root())
            sb.env["KCAP_SKIP"] = "1"
            (sb.config_root / "settings.json").write_text("{}\n")
            return
        (sb.config_root / ".claude.json").write_text(json.dumps({
            "hasCompletedOnboarding": True,
            "projects": {str(sb.repo): {"hasTrustDialogAccepted": True}},
        }, indent=2))
        settings = sb.config_root / "settings.json"
        if not settings.exists():
            settings.write_text("{}\n")

    def check_auth(self, sb: Sandbox) -> bool | None:
        out = subprocess.run([self.binary_path() or self.binary, "auth", "status"], env=sb.env,
                             capture_output=True, text=True, timeout=60)
        try:
            return bool(json.loads(out.stdout).get("loggedIn"))
        except (json.JSONDecodeError, AttributeError):
            return False

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        settings = sb.config_root / "settings.json"
        data = json.loads(settings.read_text() or "{}")
        data.setdefault("hooks", {})["SessionStart"] = [
            {"hooks": [{"type": "command", "command": str(script), "timeout": 10}]}
        ]
        settings.write_text(json.dumps(data, indent=2) + "\n")
        where = "--settings file" if self.real_config else "settings.json"
        return HookInfo(mechanism=f"{where} hooks.SessionStart", config_path=str(settings))

    def print_argv(self, sb: Sandbox, prompt: str) -> list[str]:
        argv = [self.binary_path() or self.binary, "-p", prompt, "--output-format", "json", "--max-turns", "4",
                "--strict-mcp-config", "--allowedTools", "Skill"]
        if self.real_config:
            argv += ["--settings", str(sb.config_root / "settings.json")]
        else:
            argv += ["--setting-sources", "user"]
        return argv

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        def extract(raw: str) -> str:
            obj = json.loads(raw)
            if obj.get("is_error"):
                raise ValueError(obj)
            return obj.get("result") or ""
        return print_ask(self.print_argv(sb, prompt), sb.repo, sb.env, sb.root / "claude.stderr.log",
                         self.turn_timeout, extract=extract)
```

Register in `harness/__init__.py`:

```python
from harness.claude import ClaudeAdapter
ENTRIES["claude"] = ClaudeAdapter
```

(`ENTRIES` becomes a dict literal followed by assignments; keep `fake` first.)

- [ ] **Step 4: Run to verify pass**

Run: `python3 $KIT/selftest.py -v`
Expected: 34 tests, `OK`

- [ ] **Step 5: Free phase against the real binary**

Run: `python3 $KIT/probe.py --harness claude --mode print`
Expected: `claude: 2.1.273 (Claude Code) auth_ok=True`. If `auth_ok=False` (keychain credential not reachable under the isolated root), rerun with `KCAP_PROBE_CLAUDE_REAL_CONFIG=1` and keep that variable exported for every later Claude run; record the lever actually used in `findings.md`.

- [ ] **Step 6: Positive control, one turn**

Run: `python3 $KIT/probe.py --harness claude --mode print --scenario S0 --scenario S1 --turn --runs 1`
Expected: `claude print S0/none ... -> not_visible` and `claude print S1/native ... -> visible_first_turn`. If S1 is `not_visible`, open `out/claude/print/S1/S1_native/run1.json`, read `reply` and the stderr log, adjust `print_argv` (the usual culprits are a trust prompt or the `Skill` tool name) and rerun; note the change in `findings.md`.

- [ ] **Step 7: Commit**

```
/usr/bin/git -C <worktree> add docs/probes/2026-09-16-skills-discovery
/usr/bin/git -C <worktree> commit -m "Add the Claude probe adapter (#961)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 13: Codex adapter

**Files:**
- Create: `docs/probes/2026-09-16-skills-discovery/harness/codex.py`
- Modify: `docs/probes/2026-09-16-skills-discovery/harness/__init__.py`

**Interfaces:**
- Consumes: `print_ask`, `appserver_ask`.
- Produces: `class CodexAdapter(Adapter)`, entry `codex`, lever `CODEX_HOME`, credential `auth.json`, native root `.agents/skills`, documented roots `{".agents/skills"}`. Hook file `<CODEX_HOME>/hooks.json` in kcap's shape. Print mode: `codex exec --skip-git-repo-check --sandbox read-only --color never --dangerously-bypass-hook-trust --output-last-message <file> -` with the prompt on stdin; reply is the file. Daemon mode: `appserver_ask` (which seeds hook trust itself).

- [ ] **Step 1: Implement**

`harness/codex.py`:

```python
from __future__ import annotations

import json
import subprocess
from pathlib import Path

from harness.base import Adapter, AskResult, HookInfo
from lib.appserver_driver import appserver_ask
from lib.isolation import Sandbox
from lib.print_driver import print_ask


class CodexAdapter(Adapter):
    entry = "codex"
    harness = "codex"
    binary = "codex"
    lever = "CODEX_HOME"
    credential_files = ["auth.json"]
    native_root = ".agents/skills"
    documented_roots = frozenset({".agents/skills"})

    def real_root(self) -> Path | None:
        return Path.home() / ".codex"

    def prepare(self, sb: Sandbox) -> None:
        (sb.config_root / "config.toml").write_text(
            f'[projects.{json.dumps(str(sb.repo))}]\ntrust_level = "trusted"\n')

    def check_auth(self, sb: Sandbox) -> bool | None:
        out = subprocess.run([self.binary_path() or self.binary, "login", "status"], env=sb.env,
                             capture_output=True, text=True, timeout=60)
        return out.returncode == 0 and "Logged in" in (out.stdout + out.stderr)

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        hooks = sb.config_root / "hooks.json"
        hooks.write_text(json.dumps({"hooks": {"SessionStart": [
            {"hooks": [{"type": "command", "command": str(script), "timeout": 30}]}
        ]}}, indent=2) + "\n")
        return HookInfo(mechanism="hooks.json SessionStart", config_path=str(hooks))

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        binary = self.binary_path() or self.binary
        if mode == "daemon":
            return appserver_ask(binary, sb.repo, sb.env, prompt, sb.root / "codex-appserver.stderr.log", self.turn_timeout)
        last = sb.root / "last-message.txt"
        argv = [binary, "exec", "--skip-git-repo-check", "--sandbox", "read-only", "--color", "never",
                "--dangerously-bypass-hook-trust", "--output-last-message", str(last), "-"]
        res = print_ask(argv, sb.repo, sb.env, sb.root / "codex.stderr.log", self.turn_timeout, stdin_text=prompt)
        if last.exists():
            res.reply_text = last.read_text().strip()
        return res
```

Register: `from harness.codex import CodexAdapter` and `ENTRIES["codex"] = CodexAdapter`.

- [ ] **Step 2: Self-tests still pass**

Run: `python3 $KIT/selftest.py -v`
Expected: 34 tests, `OK` (the hook-files test now covers `codex`).

- [ ] **Step 3: Free phase and positive control in both modes**

Run: `python3 $KIT/probe.py --harness codex --mode print` then `python3 $KIT/probe.py --harness codex --mode daemon`
Expected: `codex: codex-cli 0.154.0 auth_ok=True` twice.

Run: `python3 $KIT/probe.py --harness codex --mode print --scenario S0 --scenario S1 --turn --runs 1` and the same with `--mode daemon`
Expected: S0 `not_visible`, S1 `visible_first_turn` in both modes. If daemon mode reports `hook_trust=` anything but `none` on S1 (no hook installed yet) the `hooks/list` shape differs from the daemon's; compare against `src/Capacitor.Cli.Daemon/Harness/Codex/CodexAppServerHostedAgentRuntime.cs` field names (`key`, `trustStatus`, `currentHash`) and fix `hook_state_override`.

- [ ] **Step 4: Commit**

```
/usr/bin/git -C <worktree> add docs/probes/2026-09-16-skills-discovery
/usr/bin/git -C <worktree> commit -m "Add the Codex probe adapter (#961)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 14: Gemini adapter

**Files:**
- Create: `docs/probes/2026-09-16-skills-discovery/harness/gemini.py`
- Modify: `docs/probes/2026-09-16-skills-discovery/harness/__init__.py`

**Interfaces:**
- Produces: `class GeminiAdapter(Adapter)`, entry `gemini`, lever `GEMINI_CLI_HOME` (the parent of `.gemini`), credentials `.gemini/oauth_creds.json`, `.gemini/google_accounts.json`, `.gemini/installation_id` copied from `$HOME`, native root `.gemini/skills`, documented roots `{".gemini/skills", ".agents/skills"}`. Hook in `<config>/.gemini/settings.json` under `hooks.SessionStart` (kcap's shape, timeout in ms). Print: `gemini -p <prompt> -o json --approval-mode yolo`, reply `response`. Daemon: `gemini --experimental-acp --skip-trust --approval-mode yolo` over `acp_ask`. Catalogue: `gemini skills list`.

- [ ] **Step 1: Update Gemini first**

Run: `npm install -g @google/gemini-cli@latest` then `gemini --version`
Expected: `0.60.0` (the version published at planning time; record whatever prints).

- [ ] **Step 2: Implement**

`harness/gemini.py`:

```python
from __future__ import annotations

import json
import subprocess
from pathlib import Path

from harness.base import Adapter, AskResult, HookInfo
from lib.acp_driver import acp_ask
from lib.isolation import Sandbox
from lib.print_driver import print_ask


class GeminiAdapter(Adapter):
    entry = "gemini"
    harness = "gemini"
    binary = "gemini"
    lever = "GEMINI_CLI_HOME"
    credential_files = [".gemini/oauth_creds.json", ".gemini/google_accounts.json", ".gemini/installation_id"]
    native_root = ".gemini/skills"
    documented_roots = frozenset({".gemini/skills", ".agents/skills"})

    def real_root(self) -> Path | None:
        return Path.home()

    def _settings(self, sb: Sandbox) -> Path:
        return sb.config_root / ".gemini" / "settings.json"

    def prepare(self, sb: Sandbox) -> None:
        self._settings(sb).parent.mkdir(parents=True, exist_ok=True)
        if not self._settings(sb).exists():
            self._settings(sb).write_text(json.dumps({"security": {"folderTrust": {"enabled": False}}}, indent=2) + "\n")
        (sb.config_root / ".gemini" / "trustedFolders.json").write_text(json.dumps({str(sb.repo): "TRUST_FOLDER"}) + "\n")

    def check_auth(self, sb: Sandbox) -> bool | None:
        return (sb.config_root / ".gemini" / "oauth_creds.json").exists() or None

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        data = json.loads(self._settings(sb).read_text() or "{}")
        data.setdefault("hooks", {})["SessionStart"] = [
            {"hooks": [{"name": "probe", "type": "command", "command": str(script), "timeout": 30000}]}
        ]
        self._settings(sb).write_text(json.dumps(data, indent=2) + "\n")
        return HookInfo(mechanism="settings.json hooks.SessionStart", config_path=str(self._settings(sb)))

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        binary = self.binary_path() or self.binary
        if mode == "daemon":
            return acp_ask([binary, "--experimental-acp", "--skip-trust", "--approval-mode", "yolo"], sb.repo, sb.env,
                           prompt, sb.root / "gemini-acp.stderr.log", self.turn_timeout)
        argv = [binary, "-p", prompt, "-o", "json", "--approval-mode", "yolo"]
        return print_ask(argv, sb.repo, sb.env, sb.root / "gemini.stderr.log", self.turn_timeout,
                         extract=lambda raw: json.loads(raw).get("response") or "")

    def list_catalogue(self, sb: Sandbox) -> str | None:
        out = subprocess.run([self.binary_path() or self.binary, "skills", "list"], cwd=str(sb.repo), env=sb.env,
                             capture_output=True, text=True, timeout=120)
        return out.stdout
```

Register `gemini`.

- [ ] **Step 3: Self-tests, free phase, positive control**

Run: `python3 $KIT/selftest.py -v` → `OK`.
Run: `python3 $KIT/probe.py --harness gemini --mode print` and `--mode daemon` → `auth_ok=True`.
Run: `python3 $KIT/probe.py --harness gemini --mode print --scenario S0 --scenario S1 --turn --runs 1` and the same for `--mode daemon`.
Expected: S1 `visible_first_turn`. If the reply mentions a consent or trust prompt, the `folderTrust` key or the skill activation consent is the cause: check `gemini --help` for the current approval flags and `docs/probes/2026-09-16-skills-discovery/out/gemini/print/S1/S1_native/gemini.stderr.log`; record the fix.

- [ ] **Step 4: Commit**

```
/usr/bin/git -C <worktree> add docs/probes/2026-09-16-skills-discovery
/usr/bin/git -C <worktree> commit -m "Add the Gemini probe adapter (#961)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 15: Pi adapter

**Files:**
- Create: `docs/probes/2026-09-16-skills-discovery/harness/pi.py`
- Modify: `docs/probes/2026-09-16-skills-discovery/harness/__init__.py`

**Interfaces:**
- Produces: `class PiAdapter(Adapter)`, entry `pi`, lever `PI_CODING_AGENT_DIR` (the agent-state leaf), credentials `auth.json`, `models.json`, `settings.json` from `~/.pi/agent`, native root `.pi/skills`, documented roots `{".pi/skills", ".agents/skills"}`. Startup hook: extension `<agent>/extensions/probe.ts` whose `session_start` handler runs the script. Registration arm: extension `probe-register.ts` whose `session_start` writes the skill and whose `resources_discover` returns `{skillPaths: [<absolute skills root>]}`. Print: `pi -p --mode json --approve -- <prompt>`, reply from `message_end` assistant text parts. Daemon: `pi --mode rpc --approve` over `pirpc_ask`.

- [ ] **Step 1: Update Pi first**

Run: `npm install -g @earendil-works/pi-coding-agent@latest` then `pi --version`; record the version.

- [ ] **Step 2: Implement**

`harness/pi.py`:

```python
from __future__ import annotations

import json
import subprocess
from pathlib import Path

from harness.base import Adapter, AskResult, HookInfo
from lib.isolation import Sandbox
from lib.pirpc_driver import pirpc_ask
from lib.print_driver import print_ask

HOOK_EXT = """import {{ execFileSync }} from "node:child_process";

export default function (pi: any) {{
  pi.on("session_start", async () => {{
    try {{ execFileSync({script}, {{ stdio: "ignore" }}); }} catch {{}}
  }});
}}
"""

REGISTER_EXT = """import {{ execFileSync }} from "node:child_process";

export default function (pi: any) {{
  pi.on("session_start", async () => {{
    try {{ execFileSync({script}, {{ stdio: "ignore" }}); }} catch {{}}
  }});
  pi.on("resources_discover", async () => {{
    return {{ skillPaths: [{root}] }};
  }});
}}
"""


class PiAdapter(Adapter):
    entry = "pi"
    harness = "pi"
    binary = "pi"
    lever = "PI_CODING_AGENT_DIR"
    credential_files = ["auth.json", "models.json", "settings.json"]
    native_root = ".pi/skills"
    documented_roots = frozenset({".pi/skills", ".agents/skills"})

    def real_root(self) -> Path | None:
        return Path.home() / ".pi" / "agent"

    def check_auth(self, sb: Sandbox) -> bool | None:
        auth = sb.config_root / "auth.json"
        if not auth.exists():
            return False
        for provider in json.loads(auth.read_text()).keys():
            out = subprocess.run([self.binary_path() or self.binary, "auth", "check", "--provider", provider, "--json"],
                                 env=sb.env, capture_output=True, text=True, timeout=60)
            if out.returncode == 0:
                return True
        return False

    def _ext_dir(self, sb: Sandbox) -> Path:
        d = sb.config_root / "extensions"
        d.mkdir(parents=True, exist_ok=True)
        return d

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        ext = self._ext_dir(sb) / "probe.ts"
        ext.write_text(HOOK_EXT.format(script=json.dumps(str(script))))
        return HookInfo(mechanism="extension session_start", config_path=str(ext))

    def install_registration(self, sb: Sandbox, skill_file: Path, body: str) -> HookInfo | None:
        from lib.hook_script import stamp_path, write_hook_script
        script = write_hook_script(sb.config_root, skill_file, body, stamp_path(sb.config_root))
        root = skill_file.parent.parent
        ext = self._ext_dir(sb) / "probe-register.ts"
        ext.write_text(REGISTER_EXT.format(script=json.dumps(str(script)), root=json.dumps(str(root))))
        return HookInfo(mechanism="extension session_start + resources_discover skillPaths", config_path=str(ext))

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        binary = self.binary_path() or self.binary
        if mode == "daemon":
            return pirpc_ask([binary, "--mode", "rpc", "--approve"], sb.repo, sb.env, prompt,
                             sb.root / "pi-rpc.stderr.log", self.turn_timeout)

        def extract(raw: str) -> str:
            texts = []
            for line in raw.splitlines():
                try:
                    msg = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if msg.get("type") == "message_end" and (msg.get("message") or {}).get("role") == "assistant":
                    texts += [p.get("text", "") for p in msg["message"].get("content") or []
                              if isinstance(p, dict) and p.get("type") == "text"]
            return "\n".join(texts)
        argv = [binary, "-p", "--mode", "json", "--approve", "--", prompt]
        return print_ask(argv, sb.repo, sb.env, sb.root / "pi.stderr.log", self.turn_timeout, extract=extract)
```

Register `pi`.

- [ ] **Step 3: Self-tests, free phase, positive control**

Run: `python3 $KIT/selftest.py -v` → `OK`.
Run: `python3 $KIT/probe.py --harness pi --mode print` and `--mode daemon` → `auth_ok=True`.
Run: `python3 $KIT/probe.py --harness pi --mode print --scenario S0 --scenario S1 --turn --runs 1` and `--mode daemon`.
Expected: S1 `visible_first_turn`. If Pi refuses because no default model is configured in the copied `settings.json`, export `KCAP_PROBE_PI_MODEL=<provider/model>` and add `["--model", os.environ["KCAP_PROBE_PI_MODEL"]]` to both argv lists when set; record it.

- [ ] **Step 4: Commit**

```
/usr/bin/git -C <worktree> add docs/probes/2026-09-16-skills-discovery
/usr/bin/git -C <worktree> commit -m "Add the Pi probe adapter (#961)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 16: Install the missing vendor CLIs

**Files:** none in the repo; this task changes the developer machine and records versions for `findings.md`.

- [ ] **Step 1: Install**

```
curl https://cursor.com/install -fsS | bash
npm install -g @github/copilot
brew install --cask kiro-cli
npm install -g opencode-ai
mkdir -p ~/.local/opencode-v2 && npm install -g --prefix ~/.local/opencode-v2 @opencode/cli
curl -fsSL https://antigravity.google/cli/install.sh | bash
```

`~/.local/bin` must be on `PATH` for `cursor-agent` and `agy`. OpenCode V2 stays out of `PATH`; the adapter reads its binary from `KCAP_OPENCODE_V2_PATH` (default `~/.local/opencode-v2/bin/opencode`).

- [ ] **Step 2: Record versions**

Run each: `cursor-agent --version`, `copilot --version`, `kiro-cli --version`, `opencode --version`, `~/.local/opencode-v2/bin/opencode --version`, `agy --version`.
Expected: each prints a version; the V2 binary reports a `2.x` version and the V1 binary a `1.x` one. Paste all six into a scratch note for `findings.md`.

- [ ] **Step 3: Logins (developer runs these, they open a browser)**

Ask the developer to run, in this order, and report which succeeded:

```
cursor-agent login
kiro-cli login
opencode auth login
agy
```

(`agy` starts the OAuth flow on first launch; `/logout` ends it.) Copilot needs no login when `gh auth token` yields an OAuth token; the adapter passes it as `COPILOT_GITHUB_TOKEN`. Kiro's headless mode may additionally need `KIRO_API_KEY`; if `kiro-cli chat --no-interactive` refuses after login, the developer exports that key or the Kiro turn arms stay `untested`.

- [ ] **Step 4: Capture the flags the installed builds expose**

Run: `cursor-agent --help`, `copilot --help`, `kiro-cli chat --help`, `opencode run --help`, `~/.local/opencode-v2/bin/opencode run --help`, `agy --help`.
Expected flags, to be confirmed and corrected in the adapters that follow: Cursor `-p/--print`, `--output-format json|text|stream-json`, `--trust`, `--force`; Copilot `-p/--prompt`, `--allow-all-tools`, `-s/--silent`; Kiro `--no-interactive`, `--trust-all-tools`, `--agent`, `--output-format stream-json`; OpenCode `run` with `--format json`; agy `-p`, `--output-format stream-json`, `--dangerously-skip-permissions`, `--print-timeout`. Where a flag is absent or renamed, use the build's name in the adapter and note it in `findings.md`.

---

### Task 17: Cursor adapter

**Files:**
- Create: `docs/probes/2026-09-16-skills-discovery/harness/cursor.py`
- Modify: `docs/probes/2026-09-16-skills-discovery/harness/__init__.py`

**Interfaces:**
- Produces: `class CursorAdapter(Adapter)`, entry `cursor`, lever `HOME` (no narrower lever exists), native root `.cursor/skills`, documented roots `{".cursor/skills", ".agents/skills", ".claude/skills", ".codex/skills"}`. Hook: `<HOME>/.cursor/hooks.json` `{"version":1,"hooks":{"sessionStart":[{"command":<script>}]}}`. Registration arm: a `workspaceOpen` hook whose command writes the skill into a plugin directory `<sandbox>/plugin/skills/<name>/SKILL.md` (with `.cursor-plugin/plugin.json`) and prints `{"pluginPaths":["<sandbox>/plugin"]}`. Print: `cursor-agent -p --output-format json --trust --force <prompt>`. Daemon: `cursor-agent acp --trust` over `acp_ask`.
- Credential files are discovered once: before `cursor-agent login`, `touch /tmp/cursor-marker`; after login, `find ~ -maxdepth 4 -type f -newer /tmp/cursor-marker -not -path '*/Library/Caches/*'` lists the files the login wrote. Put their `$HOME`-relative paths in `credential_files`. The list below is the expected result and is corrected from that listing.

- [ ] **Step 1: Implement**

`harness/cursor.py`:

```python
from __future__ import annotations

import json
import subprocess
from pathlib import Path

from harness.base import Adapter, AskResult, HookInfo
from lib.acp_driver import acp_ask
from lib.isolation import Sandbox
from lib.print_driver import print_ask


class CursorAdapter(Adapter):
    entry = "cursor"
    harness = "cursor"
    binary = "cursor-agent"
    lever = "HOME"
    credential_files = [".config/cursor-agent/auth.json", ".cursor/auth.json", ".cursor/cli-config.json"]
    native_root = ".cursor/skills"
    documented_roots = frozenset({".cursor/skills", ".agents/skills", ".claude/skills", ".codex/skills"})

    def real_root(self) -> Path | None:
        return Path.home()

    def check_auth(self, sb: Sandbox) -> bool | None:
        out = subprocess.run([self.binary_path() or self.binary, "status"], env=sb.env, capture_output=True,
                             text=True, timeout=60)
        return out.returncode == 0 and "Logged in" in (out.stdout + out.stderr)

    def _hooks(self, sb: Sandbox) -> Path:
        d = sb.config_root / ".cursor"
        d.mkdir(parents=True, exist_ok=True)
        return d / "hooks.json"

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        self._hooks(sb).write_text(json.dumps({"version": 1, "hooks": {"sessionStart": [{"command": str(script)}]}},
                                              indent=2) + "\n")
        return HookInfo(mechanism="hooks.json sessionStart", config_path=str(self._hooks(sb)))

    def install_registration(self, sb: Sandbox, skill_file: Path, body: str) -> HookInfo | None:
        plugin = sb.root / "plugin"
        (plugin / ".cursor-plugin").mkdir(parents=True, exist_ok=True)
        (plugin / ".cursor-plugin" / "plugin.json").write_text(json.dumps({"name": "kcap-probe", "version": "1.0.0"}))
        target = plugin / "skills" / skill_file.parent.name / "SKILL.md"
        script = sb.config_root / "probe-workspace-open.sh"
        script.write_text(
            "#!/bin/sh\nset -eu\n"
            f"mkdir -p '{target.parent}'\n"
            f"cat > '{target}' <<'KCAP_PROBE_EOF'\n{body}KCAP_PROBE_EOF\n"
            f"printf '{{\"fired_at\": %s}}\\n' \"$(date +%s)\" > '{sb.config_root / 'probe-hook-fired.json'}'\n"
            f"printf '%s\\n' '{json.dumps({"pluginPaths": [str(plugin)]})}'\n")
        script.chmod(0o755)
        self._hooks(sb).write_text(json.dumps({"version": 1, "hooks": {"workspaceOpen": [{"command": str(script)}]}},
                                              indent=2) + "\n")
        return HookInfo(mechanism="hooks.json workspaceOpen pluginPaths", config_path=str(self._hooks(sb)))

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        binary = self.binary_path() or self.binary
        if mode == "daemon":
            return acp_ask([binary, "acp", "--trust"], sb.repo, sb.env, prompt, sb.root / "cursor-acp.stderr.log",
                           self.turn_timeout)

        def extract(raw: str) -> str:
            texts = []
            for line in raw.splitlines():
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError:
                    continue
                for key in ("result", "text", "content"):
                    if isinstance(obj.get(key), str):
                        texts.append(obj[key])
            return "\n".join(texts) if texts else raw
        argv = [binary, "-p", "--output-format", "json", "--trust", "--force", prompt]
        return print_ask(argv, sb.repo, sb.env, sb.root / "cursor.stderr.log", self.turn_timeout, extract=extract)
```

Register `cursor`.

- [ ] **Step 2: Self-tests, free phase, positive control**

Run: `python3 $KIT/selftest.py -v` → `OK`.
Run: `python3 $KIT/probe.py --harness cursor --mode print` → `auth_ok=True`. If `False`, the credential list is wrong: redo the discovery in this task's interface note and fix `credential_files`.
Run: `python3 $KIT/probe.py --harness cursor --mode print --scenario S0 --scenario S1 --turn --runs 1` and `--mode daemon`.
Expected: S1 `visible_first_turn`. A reply of `Upgrade your plan to continue` means the account tier blocks agent turns (seen on Free in the ACP probe); record `untested` with that reason.

- [ ] **Step 3: Commit**

```
/usr/bin/git -C <worktree> add docs/probes/2026-09-16-skills-discovery
/usr/bin/git -C <worktree> commit -m "Add the Cursor probe adapter (#961)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 18: Copilot adapter

**Files:**
- Create: `docs/probes/2026-09-16-skills-discovery/harness/copilot.py`
- Modify: `docs/probes/2026-09-16-skills-discovery/harness/__init__.py`

**Interfaces:**
- Produces: `class CopilotAdapter(Adapter)`, entry `copilot`, lever `COPILOT_HOME`, no credential files: `prepare` sets `COPILOT_GITHUB_TOKEN` in the sandbox env from `gh auth token`. Native root `.github/skills`, documented roots `{".github/skills", ".agents/skills", ".claude/skills"}`. Hook: `<COPILOT_HOME>/hooks/probe.json` `{"version":1,"hooks":{"sessionStart":[{"type":"command","command":<script>,"timeoutSec":30}]}}`. Print: `copilot -p <prompt> --allow-all-tools -s`. Daemon: `copilot --acp --stdio` over `acp_ask`. Catalogue: `copilot skill list --json`.

- [ ] **Step 1: Implement**

`harness/copilot.py`:

```python
from __future__ import annotations

import json
import subprocess
from pathlib import Path

from harness.base import Adapter, AskResult, HookInfo
from lib.acp_driver import acp_ask
from lib.isolation import Sandbox
from lib.print_driver import print_ask


class CopilotAdapter(Adapter):
    entry = "copilot"
    harness = "copilot"
    binary = "copilot"
    lever = "COPILOT_HOME"
    native_root = ".github/skills"
    documented_roots = frozenset({".github/skills", ".agents/skills", ".claude/skills"})

    def __init__(self) -> None:
        try:
            out = subprocess.run(["gh", "auth", "token"], capture_output=True, text=True, timeout=30)
            self.token = out.stdout.strip() if out.returncode == 0 else ""
        except (OSError, subprocess.SubprocessError):
            self.token = ""

    def real_root(self) -> Path | None:
        return Path.home() / ".copilot"

    def prepare(self, sb: Sandbox) -> None:
        if self.token:
            sb.env["COPILOT_GITHUB_TOKEN"] = self.token

    def check_auth(self, sb: Sandbox) -> bool | None:
        return bool(self.token)

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        hooks = sb.config_root / "hooks"
        hooks.mkdir(parents=True, exist_ok=True)
        path = hooks / "probe.json"
        path.write_text(json.dumps({"version": 1, "hooks": {"sessionStart": [
            {"type": "command", "command": str(script), "timeoutSec": 30}
        ]}}, indent=2) + "\n")
        return HookInfo(mechanism="hooks/*.json sessionStart", config_path=str(path))

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        binary = self.binary_path() or self.binary
        if mode == "daemon":
            return acp_ask([binary, "--acp", "--stdio"], sb.repo, sb.env, prompt, sb.root / "copilot-acp.stderr.log",
                           self.turn_timeout)
        argv = [binary, "-p", prompt, "--allow-all-tools", "-s"]
        return print_ask(argv, sb.repo, sb.env, sb.root / "copilot.stderr.log", self.turn_timeout)

    def list_catalogue(self, sb: Sandbox) -> str | None:
        out = subprocess.run([self.binary_path() or self.binary, "skill", "list", "--json"], cwd=str(sb.repo),
                             env=sb.env, capture_output=True, text=True, timeout=120)
        return out.stdout
```

Register `copilot`.

- [ ] **Step 2: Self-tests, free phase, positive control**

Run: `python3 $KIT/selftest.py -v` → `OK`.
Run: `python3 $KIT/probe.py --harness copilot --mode print` → `auth_ok=True` and a `catalogue` value in `out/copilot/print/free/free/free.json`.
Run: `python3 $KIT/probe.py --harness copilot --mode print --scenario S0 --scenario S1 --turn --runs 1` and `--mode daemon`.
Expected: S1 `visible_first_turn`. If the token is rejected (`ghp_` classic tokens are refused), the developer runs `copilot login` and the adapter's `credential_files` gains the file that login writes under `~/.copilot` (find it with the same marker technique as Task 17).

- [ ] **Step 3: Commit**

```
/usr/bin/git -C <worktree> add docs/probes/2026-09-16-skills-discovery
/usr/bin/git -C <worktree> commit -m "Add the Copilot probe adapter (#961)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 19: Kiro adapters (default agent, custom agent bare, custom agent with skills)

**Files:**
- Create: `docs/probes/2026-09-16-skills-discovery/harness/kiro.py`
- Modify: `docs/probes/2026-09-16-skills-discovery/harness/__init__.py`

**Interfaces:**
- Produces: `class KiroAdapter(Adapter)` (entry `kiro`), `class KiroAgentBareAdapter(KiroAdapter)` (entry `kiro-agent-bare`, print only, a custom agent declaring no resources), `class KiroAgentSkillsAdapter(KiroAdapter)` (entry `kiro-agent-skills`, print only, a custom agent declaring `skill://.kiro/skills/*/SKILL.md`). Lever `KIRO_HOME`, native root `.kiro/skills`, documented roots `{".kiro/skills"}`, passthrough env `KIRO_API_KEY`. Hook generation by version: major `>= 3` writes `<KIRO_HOME>/hooks/probe.json` with `trigger: "SessionStart"`; older writes an agent file with `hooks.agentSpawn` and makes it the default agent. Print: `kiro-cli chat --no-interactive --trust-all-tools [--agent <name>] <prompt>`. Daemon: `kiro-cli acp` over `acp_ask`.

- [ ] **Step 1: Implement**

`harness/kiro.py`:

```python
from __future__ import annotations

import json
import re
from pathlib import Path

from harness.base import Adapter, AskResult, HookInfo
from lib.acp_driver import acp_ask
from lib.isolation import Sandbox
from lib.print_driver import print_ask


class KiroAdapter(Adapter):
    entry = "kiro"
    harness = "kiro"
    binary = "kiro-cli"
    lever = "KIRO_HOME"
    passthrough_env = ["KIRO_API_KEY"]
    native_root = ".kiro/skills"
    documented_roots = frozenset({".kiro/skills"})
    agent_name: str | None = None
    agent_resources: list[str] = []

    def real_root(self) -> Path | None:
        return Path.home() / ".kiro"

    def major(self) -> int:
        m = re.search(r"(\d+)\.\d+", self.version())
        return int(m.group(1)) if m else 0

    def _settings(self, sb: Sandbox) -> Path:
        d = sb.config_root / "settings"
        d.mkdir(parents=True, exist_ok=True)
        return d / "cli.json"

    def _agent_file(self, sb: Sandbox, name: str, hooks: dict | None) -> Path:
        agents = sb.config_root / "agents"
        agents.mkdir(parents=True, exist_ok=True)
        path = agents / f"{name}.json"
        data = {"name": name, "description": "probe agent", "resources": list(self.agent_resources)}
        if hooks:
            data["hooks"] = hooks
        path.write_text(json.dumps(data, indent=2) + "\n")
        return path

    def prepare(self, sb: Sandbox) -> None:
        if self.agent_name:
            self._agent_file(sb, self.agent_name, None)

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        if self.major() >= 3:
            hooks = sb.config_root / "hooks"
            hooks.mkdir(parents=True, exist_ok=True)
            path = hooks / "probe.json"
            path.write_text(json.dumps({"version": "v1", "hooks": [{
                "name": "probe", "trigger": "SessionStart",
                "action": {"type": "command", "command": str(script)}, "timeout": 30, "enabled": True,
            }]}, indent=2) + "\n")
            return HookInfo(mechanism="hooks/*.json SessionStart (cli 3.x)", config_path=str(path))
        name = self.agent_name or "probe"
        path = self._agent_file(sb, name, {"agentSpawn": [{"command": str(script)}]})
        self._settings(sb).write_text(json.dumps({"chat.defaultAgent": name}) + "\n")
        return HookInfo(mechanism="agent hooks.agentSpawn (cli 2.x)", config_path=str(path))

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        binary = self.binary_path() or self.binary
        if mode == "daemon":
            return acp_ask([binary, "acp"], sb.repo, sb.env, prompt, sb.root / "kiro-acp.stderr.log", self.turn_timeout)
        argv = [binary, "chat", "--no-interactive", "--trust-all-tools"]
        if self.agent_name:
            argv += ["--agent", self.agent_name]
        argv.append(prompt)
        return print_ask(argv, sb.repo, sb.env, sb.root / "kiro.stderr.log", self.turn_timeout)


class KiroAgentBareAdapter(KiroAdapter):
    entry = "kiro-agent-bare"
    modes = ("print",)
    agent_name = "probe-bare"


class KiroAgentSkillsAdapter(KiroAdapter):
    entry = "kiro-agent-skills"
    modes = ("print",)
    agent_name = "probe-skills"
    agent_resources = ["skill://.kiro/skills/*/SKILL.md"]
```

Register all three.

- [ ] **Step 2: Self-tests, free phase, positive control**

Run: `python3 $KIT/selftest.py -v` → `OK` (`version()` is called by the hook-files test through `major()`; with no binary it returns `not-installed` and the 2.x branch is exercised, which is fine).
Run: `python3 $KIT/probe.py --harness kiro --mode print` → version line; `auth_ok=None` is expected (Kiro keeps its credential outside `KIRO_HOME`).
Run: `python3 $KIT/probe.py --harness kiro --mode print --scenario S0 --scenario S1 --turn --runs 1` and `--mode daemon`.
Expected: S1 `visible_first_turn`. If the CLI refuses headless use without `KIRO_API_KEY`, the developer exports it and reruns; otherwise record the Kiro turn arms `untested` with that reason.
Run: `python3 $KIT/probe.py --harness kiro-agent-bare --harness kiro-agent-skills --mode print --scenario S1 --turn`
Expected: `kiro-agent-bare` `not_visible` and `kiro-agent-skills` `visible_first_turn`, which answers the custom-resource inheritance question in the issue. Both results go into `findings.md` whatever they are.

- [ ] **Step 3: Commit**

```
/usr/bin/git -C <worktree> add docs/probes/2026-09-16-skills-discovery
/usr/bin/git -C <worktree> commit -m "Add the Kiro probe adapters (#961)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 20: OpenCode V1 and V2 adapters

**Files:**
- Create: `docs/probes/2026-09-16-skills-discovery/harness/opencode_v1.py`
- Create: `docs/probes/2026-09-16-skills-discovery/harness/opencode_v2.py`
- Modify: `docs/probes/2026-09-16-skills-discovery/harness/__init__.py`

**Interfaces:**
- Produces: `class OpenCodeV1Adapter(Adapter)` (entry `opencode-v1`), `class OpenCodeV2Adapter(OpenCodeV1Adapter)` (entry `opencode-v2`, binary from `KCAP_OPENCODE_V2_PATH`), `class OpenCodeV2PromptHookAdapter(OpenCodeV2Adapter)` (entry `opencode-v2-prompt`, registration through the awaited `prompt` hook). Lever `OPENCODE_CONFIG_DIR`; `prepare` also points `XDG_DATA_HOME` at `<sandbox>/data` and copies `~/.local/share/opencode/auth.json` there, and writes `opencode.json` with the developer's default `model` (from `~/.config/opencode/opencode.json`, overridable by `KCAP_PROBE_OPENCODE_MODEL`). Native root `.opencode/skills`, documented roots `{".opencode/skills", ".claude/skills", ".agents/skills"}`. V1 hook: plugin `<config>/plugins/probe.ts` running the script on `session.created`; V1 registration: plugin whose `experimental.chat.system.transform` runs the script (awaited before the request). V2 hook: `Plugin.define` whose `setup` runs the script; V2 registration: `setup` runs the script then `await ctx.skill.reload()`; the `-prompt` entry does both inside `ctx.session.hook("prompt", …)`. Print: `opencode run --format json <prompt>`. Daemon: `opencode acp` over `acp_ask`.

- [ ] **Step 1: Implement V1**

`harness/opencode_v1.py`:

```python
from __future__ import annotations

import json
import os
import shutil
from pathlib import Path

from harness.base import Adapter, AskResult, HookInfo
from lib.acp_driver import acp_ask
from lib.isolation import Sandbox
from lib.print_driver import print_ask

V1_EVENT_PLUGIN = """import {{ execFileSync }} from "node:child_process"

export const ProbePlugin = async () => ({{
  event: async ({{ event }}: any) => {{
    if (event?.type === "session.created") {{
      try {{ execFileSync({script}, {{ stdio: "ignore" }}) }} catch {{}}
    }}
  }},
}})
"""

V1_TRANSFORM_PLUGIN = """import {{ execFileSync }} from "node:child_process"

export const ProbeRegisterPlugin = async () => ({{
  "experimental.chat.system.transform": async () => {{
    try {{ execFileSync({script}, {{ stdio: "ignore" }}) }} catch {{}}
  }},
}})
"""


class OpenCodeV1Adapter(Adapter):
    entry = "opencode-v1"
    harness = "opencode"
    binary = "opencode"
    lever = "OPENCODE_CONFIG_DIR"
    native_root = ".opencode/skills"
    documented_roots = frozenset({".opencode/skills", ".claude/skills", ".agents/skills"})

    def real_root(self) -> Path | None:
        return Path.home() / ".config" / "opencode"

    def default_model(self) -> str | None:
        if os.environ.get("KCAP_PROBE_OPENCODE_MODEL"):
            return os.environ["KCAP_PROBE_OPENCODE_MODEL"]
        cfg = (self.real_root() or Path()) / "opencode.json"
        if cfg.exists():
            try:
                return json.loads(cfg.read_text()).get("model")
            except json.JSONDecodeError:
                return None
        return None

    def prepare(self, sb: Sandbox) -> None:
        data = sb.root / "data"
        (data / "opencode").mkdir(parents=True, exist_ok=True)
        sb.env["XDG_DATA_HOME"] = str(data)
        auth = Path.home() / ".local" / "share" / "opencode" / "auth.json"
        if auth.exists():
            shutil.copy2(auth, data / "opencode" / "auth.json")
        cfg = {"$schema": "https://opencode.ai/config.json"}
        if self.default_model():
            cfg["model"] = self.default_model()
        (sb.config_root / "opencode.json").write_text(json.dumps(cfg, indent=2) + "\n")

    def check_auth(self, sb: Sandbox) -> bool | None:
        return (Path(sb.env["XDG_DATA_HOME"]) / "opencode" / "auth.json").exists()

    def _plugins(self, sb: Sandbox) -> Path:
        d = sb.config_root / "plugins"
        d.mkdir(parents=True, exist_ok=True)
        return d

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        path = self._plugins(sb) / "probe.ts"
        path.write_text(V1_EVENT_PLUGIN.format(script=json.dumps(str(script))))
        return HookInfo(mechanism="plugin event session.created", config_path=str(path))

    def install_registration(self, sb: Sandbox, skill_file: Path, body: str) -> HookInfo | None:
        from lib.hook_script import stamp_path, write_hook_script
        script = write_hook_script(sb.config_root, skill_file, body, stamp_path(sb.config_root))
        path = self._plugins(sb) / "probe-register.ts"
        path.write_text(V1_TRANSFORM_PLUGIN.format(script=json.dumps(str(script))))
        return HookInfo(mechanism="plugin experimental.chat.system.transform", config_path=str(path))

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        binary = self.binary_path() or self.binary
        if mode == "daemon":
            return acp_ask([binary, "acp"], sb.repo, sb.env, prompt, sb.root / "opencode-acp.stderr.log", self.turn_timeout)

        def extract(raw: str) -> str:
            texts = []
            for line in raw.splitlines():
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError:
                    continue
                part = obj.get("part") if isinstance(obj.get("part"), dict) else obj
                if part.get("type") == "text" and isinstance(part.get("text"), str):
                    texts.append(part["text"])
            return "\n".join(texts) if texts else raw
        argv = [binary, "run", "--format", "json", prompt]
        return print_ask(argv, sb.repo, sb.env, sb.root / "opencode.stderr.log", self.turn_timeout, extract=extract)
```

- [ ] **Step 2: Implement V2**

`harness/opencode_v2.py`:

```python
from __future__ import annotations

import json
import os
from pathlib import Path

from harness.base import HookInfo
from harness.opencode_v1 import OpenCodeV1Adapter
from lib.isolation import Sandbox

V2_SETUP_PLUGIN = """import {{ execFileSync }} from "node:child_process"
import {{ Plugin }} from "@opencode/plugin"

export default Plugin.define({{
  id: "probe",
  async setup(ctx) {{
    try {{ execFileSync({script}, {{ stdio: "ignore" }}) }} catch {{}}
    {reload}
  }},
}})
"""

V2_PROMPT_PLUGIN = """import {{ execFileSync }} from "node:child_process"
import {{ Plugin }} from "@opencode/plugin"

export default Plugin.define({{
  id: "probe",
  async setup(ctx) {{
    await ctx.session.hook("prompt", async () => {{
      try {{ execFileSync({script}, {{ stdio: "ignore" }}) }} catch {{}}
      await ctx.skill.reload()
    }})
  }},
}})
"""


class OpenCodeV2Adapter(OpenCodeV1Adapter):
    entry = "opencode-v2"

    def binary_path(self) -> str | None:
        p = Path(os.environ.get("KCAP_OPENCODE_V2_PATH") or (Path.home() / ".local" / "opencode-v2" / "bin" / "opencode"))
        return str(p) if p.exists() else None

    def _plugin_file(self, sb: Sandbox) -> Path:
        d = self._plugins(sb) / "probe"
        d.mkdir(parents=True, exist_ok=True)
        return d / "index.ts"

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        path = self._plugin_file(sb)
        path.write_text(V2_SETUP_PLUGIN.format(script=json.dumps(str(script)), reload=""))
        return HookInfo(mechanism="plugin setup (no reload)", config_path=str(path))

    def install_registration(self, sb: Sandbox, skill_file: Path, body: str) -> HookInfo | None:
        from lib.hook_script import stamp_path, write_hook_script
        script = write_hook_script(sb.config_root, skill_file, body, stamp_path(sb.config_root))
        path = self._plugin_file(sb)
        path.write_text(V2_SETUP_PLUGIN.format(script=json.dumps(str(script)), reload="await ctx.skill.reload()"))
        return HookInfo(mechanism="plugin setup + skill.reload", config_path=str(path))


class OpenCodeV2PromptHookAdapter(OpenCodeV2Adapter):
    entry = "opencode-v2-prompt"

    def install_registration(self, sb: Sandbox, skill_file: Path, body: str) -> HookInfo | None:
        from lib.hook_script import stamp_path, write_hook_script
        script = write_hook_script(sb.config_root, skill_file, body, stamp_path(sb.config_root))
        path = self._plugin_file(sb)
        path.write_text(V2_PROMPT_PLUGIN.format(script=json.dumps(str(script))))
        return HookInfo(mechanism="plugin prompt hook + skill.reload", config_path=str(path))
```

Register `opencode-v1`, `opencode-v2`, `opencode-v2-prompt`.

- [ ] **Step 3: Self-tests, free phase, positive control**

Run: `python3 $KIT/selftest.py -v` → `OK`.
Run: `python3 $KIT/probe.py --harness opencode-v1 --harness opencode-v2 --mode print` → `auth_ok=True` for both (the V2 line shows the `2.x` version).
Run: `python3 $KIT/probe.py --harness opencode-v1 --mode print --scenario S0 --scenario S1 --turn --runs 1`, then `--mode daemon`, then the same two for `opencode-v2`.
Expected: S1 `visible_first_turn`. If V2 rejects the plugin import of `@opencode/plugin`, check `~/.local/opencode-v2/bin/opencode run --help` and the V2 docs for how local plugins resolve that package (a `package.json` beside `index.ts` with the dependency is the usual answer) and add that file in `_plugin_file`; note it in `findings.md`.

- [ ] **Step 4: Commit**

```
/usr/bin/git -C <worktree> add docs/probes/2026-09-16-skills-discovery
/usr/bin/git -C <worktree> commit -m "Add the OpenCode V1 and V2 probe adapters (#961)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 21: Antigravity CLI adapters

**Files:**
- Create: `docs/probes/2026-09-16-skills-discovery/harness/agy.py`
- Modify: `docs/probes/2026-09-16-skills-discovery/harness/__init__.py`

**Interfaces:**
- Produces: `class AgyAdapter(Adapter)` (entry `agy`: flat `.agents/skills/<name>.md` layout, hooks under `<HOME>/.gemini/config/plugins/probe/`, the location kcap installs to), `class AgyDirLayoutAdapter(AgyAdapter)` (entry `agy-dirlayout`: `<name>/SKILL.md` layout), `class AgyCliDirAdapter(AgyAdapter)` (entry `agy-clidir`: hooks under `<HOME>/.gemini/antigravity-cli/plugins/probe/`, the location the CLI docs name). Lever `HOME`; credential file `.gemini/antigravity-cli/settings.json` (the OAuth session itself lives in the keychain, which a `HOME` override does not move). Native root `.agents/skills`, documented roots `{".agents/skills", ".agent/skills"}`. Hook event `PreInvocation` (there is no session-start event) in kcap's two-file shape (`plugin.json` + `hooks.json`). Print only: `agy -p <prompt> --output-format stream-json --dangerously-skip-permissions --print-timeout 180s`; reply is the concatenated `step_update.text_delta` of `agent_response` steps.

- [ ] **Step 1: Implement**

`harness/agy.py`:

```python
from __future__ import annotations

import json
from pathlib import Path

from harness.base import Adapter, AskResult, HookInfo
from lib.isolation import Sandbox
from lib.print_driver import print_ask


class AgyAdapter(Adapter):
    entry = "agy"
    harness = "antigravity"
    binary = "agy"
    lever = "HOME"
    credential_files = [".gemini/antigravity-cli/settings.json"]
    native_root = ".agents/skills"
    documented_roots = frozenset({".agents/skills", ".agent/skills"})
    flat_skill_layout = True
    modes = ("print",)
    plugin_parent = (".gemini", "config", "plugins")

    def real_root(self) -> Path | None:
        return Path.home()

    def prepare(self, sb: Sandbox) -> None:
        (sb.config_root / "tmp").mkdir(exist_ok=True)
        sb.env["TMPDIR"] = str(sb.config_root / "tmp")

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        plugin = sb.config_root.joinpath(*self.plugin_parent) / "probe"
        plugin.mkdir(parents=True, exist_ok=True)
        (plugin / "plugin.json").write_text(json.dumps({"name": "probe", "version": "1.0.0", "description": "probe"}, indent=2) + "\n")
        hooks = plugin / "hooks.json"
        hooks.write_text(json.dumps({"probe": {
            "PreInvocation": [{"type": "command", "command": str(script), "timeout": 15000}],
        }}, indent=2) + "\n")
        return HookInfo(mechanism=f"{'/'.join(self.plugin_parent)} PreInvocation", config_path=str(hooks))

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        def extract(raw: str) -> str:
            texts = []
            for line in raw.splitlines():
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if obj.get("event") == "step_update":
                    su = obj.get("step_update") or {}
                    if su.get("step_type", "agent_response") == "agent_response" and isinstance(su.get("text_delta"), str):
                        texts.append(su["text_delta"])
            return "".join(texts)
        argv = [self.binary_path() or self.binary, "-p", prompt, "--output-format", "stream-json",
                "--dangerously-skip-permissions", "--print-timeout", "180s"]
        return print_ask(argv, sb.repo, sb.env, sb.root / "agy.stderr.log", self.turn_timeout, extract=extract)


class AgyDirLayoutAdapter(AgyAdapter):
    entry = "agy-dirlayout"
    flat_skill_layout = False


class AgyCliDirAdapter(AgyAdapter):
    entry = "agy-clidir"
    plugin_parent = (".gemini", "antigravity-cli", "plugins")
```

Register all three.

- [ ] **Step 2: Self-tests, free phase, positive control**

Run: `python3 $KIT/selftest.py -v` → `OK`.
Run: `python3 $KIT/probe.py --harness agy --mode print` → version line (`auth_ok=None`).
Run: `python3 $KIT/probe.py --harness agy --harness agy-dirlayout --mode print --scenario S0 --scenario S1 --turn --runs 1`
Expected: at least one of the two layouts is `visible_first_turn`; the other result is the layout finding the issue asks for. If both are `not_visible` with a stderr login prompt, the OAuth session did not survive the `HOME` override: the developer runs `agy` once with `HOME` pointed at a scratch directory to see what it writes, and that file joins `credential_files`.

- [ ] **Step 3: Commit**

```
/usr/bin/git -C <worktree> add docs/probes/2026-09-16-skills-discovery
/usr/bin/git -C <worktree> commit -m "Add the Antigravity CLI probe adapters (#961)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 22: Run pass 1 and emit the matrix

**Files:**
- Create: `docs/probes/2026-09-16-skills-discovery/matrix.json` (emitted)

- [ ] **Step 1: Run every entry in print mode**

Run: `python3 $KIT/probe.py --mode print --turn`
Expected: one line per arm and run; no Python traceback. A `PromptDesignFailure` aborts that entry: inspect the S0 reply, tighten the prompt for that entry only (an adapter may override `single_prompt` by wrapping `ask`), and rerun the entry.

- [ ] **Step 2: Run every entry in daemon mode**

Run: `python3 $KIT/probe.py --mode daemon --turn`
Expected: entries whose `modes` exclude `daemon` print `mode daemon unsupported, skipping`; the rest produce arm lines.

- [ ] **Step 3: Fill the untested rows deliberately**

For every entry that could not run (missing binary, no credential, plan tier), the runs above wrote `untested` rows with a reason. Check `out/<entry>/<mode>/free/free/free.json` for each and confirm the reason is the real one, not a kit defect.

- [ ] **Step 4: Emit**

Run: `python3 $KIT/probe.py --emit`
Expected: `wrote N rows to .../matrix.json`. Open the file and confirm every entry in `ENTRIES` (except `fake`) has rows for S0 to S4 in print mode.

- [ ] **Step 5: Commit**

```
/usr/bin/git -C <worktree> add docs/probes/2026-09-16-skills-discovery/matrix.json
/usr/bin/git -C <worktree> commit -m "Record pass 1 skills discovery probe results (#961)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 23: Findings, capability matrix and PR

**Files:**
- Create: `docs/probes/2026-09-16-skills-discovery/findings.md`
- Create: `docs/probes/2026-09-16-skills-discovery/capability-matrix.md`

- [ ] **Step 1: Write `findings.md`**

Structure, matching the existing probe findings style:

```markdown
# Repo-local skill discovery probes — 2026-09-16

**Subject:** <one line per entry: binary, version, install channel>, macOS <version> / arm64.
**Driver:** `probe.py` (scenarios S0–S4, two runs per arm, third on disagreement); `selftest.py` covers the kit.
**Cost:** free phase issues zero model requests; pass 1 issued <N> turns in total.

Every verdict is the starting session's own reply: a skill counts as discovered when the model names it and as loaded when the reply carries the token that exists only in the skill body. A file on disk, a hook exit code or a listing command never decides a row.

## How to reproduce
<the exact commands from Task 22, the environment variables an entry needs, and the login steps>

## <entry> (<version>)
- Isolation: lever, credential files copied, auth_ok.
- Hook: mechanism and config path written; whether the stamp file showed it fired before the request.
- S1/S2/S3/S4 results with the reply excerpt that decided each, quoting `out/...` paths.
- Limitations and minimum version.
- Manual GUI procedure (Cursor desktop, Antigravity IDE, Kiro IDE only): what to place, what to type, what reply proves each verdict.

## Cross-vendor consumption
<table roots × entries from S4>

## Git exclusion
<table entries × {gitignore, info-exclude} from S3>
```

Fill every section from `matrix.json` and the run files; each claim cites a run file path.

- [ ] **Step 2: Write `capability-matrix.md`**

One table with the columns from the spec (entry and version, native root, roots consumed, startup mechanism proven, exclusion preserving load, vendor-isolated destination, reload path, GUI status, minimum version), one row per entry, values taken from `matrix.json`. Below the table, a short list of the consequences for #778 and #962: which entries have no vendor-isolated destination, which need a registration path rather than a file drop, and which are untested.

- [ ] **Step 3: Self-review against the issue's acceptance criteria**

Check each: reproducible probes inspect the session's catalogue and body (S1 to S4, self-tests); results name binary version, launch mode, OS and the evidence (every run file); each harness has a working automatic path or a documented limitation with minimum version (per-entry sections); Git exclusion and shared directories identified (the two tables); the capability matrix exists. Anything missing is a gap to fix before the PR.

- [ ] **Step 4: Run the Linear-id check and the self-tests one last time**

Run: `bash scripts/check-linear-ids.sh` → exit 0. Run: `python3 $KIT/selftest.py` → `OK`.

- [ ] **Step 5: Commit and open the PR**

```
/usr/bin/git -C <worktree> add docs/probes/2026-09-16-skills-discovery
/usr/bin/git -C <worktree> commit -m "Document skills discovery probe findings and capability matrix (#961)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
/usr/bin/git -C <worktree> push https://github.com/kurrent-io/kcap-cli.git alexeyzimarev/ai-2829-verify-repo-local-skill-discovery-and-startup-ordering
```

Open the PR with `gh pr create` following `.github/PULL_REQUEST_TEMPLATE.md`. Title: `Probe repo-local skill discovery and startup ordering across harnesses`. The reference line carries `Refs #961` (not a closing keyword: pass 2 closes it) and `AI-2829`. End the description with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

---

## Self-review notes

- Spec coverage: layout (Task 1), shared library (Tasks 2 to 7, 10, 11), adapters and modes (Tasks 12 to 21), scenarios and evidence rules (Task 9), outputs (Tasks 22, 23), installs and logins (Task 16), self-tests (every task), delivery (Task 23). Interactive mode and the pass 2 scenarios are deliberately absent.
- Names used across tasks: `Adapter`, `AskResult`, `HookInfo`, `Sandbox`, `new_sandbox`, `git`, `ProbeSkill`, `write_skill`, `single_prompt`, `multi_prompt`, `parse_reply`, `Reply`, `apply`, `assert_untracked_state`, `judge_single`, `judge_control`, `judge_root`, `combine`, `needs_third_run`, `RunRecord`, `write_run`, `load_runs`, `emit_matrix`, `run_dir`, `os_label`, `write_hook_script`, `read_stamp`, `stamp_path`, `print_ask`, `acp_ask`, `appserver_ask`, `pirpc_ask`, `JsonlChild`, `Runner`, `ALL_ROOTS`, `ENTRIES`.
