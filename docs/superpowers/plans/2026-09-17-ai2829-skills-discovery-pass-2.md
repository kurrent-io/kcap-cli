# Skills Discovery Probes Implementation Plan (pass 2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the pass 1 kit under `docs/probes/2026-09-16-skills-discovery/` with the lifecycle scenarios S5–S10 (live catalogue, startup mutation, resume, nested cwd, worktrees, concurrent sessions) and a third launch mode `tui` driven through a pseudo-terminal, run them across the nine harnesses, and regenerate `matrix.json`, `capability-matrix.md` and `findings.md`.

**Architecture:** The adapter contract gains a `Session` (a live daemon or interactive session that takes more than one prompt), a resume launch and a session-id reader. The three daemon drivers become session classes with their one-turn `*_ask` functions kept as wrappers, and a new `lib/pty_driver.py` runs a vendor's interactive UI on a pty. `probe.py` gains the six scenarios, a per-mode scenario table, and a `prior` raw stream on two-turn rows. `report.py` gains one column per new scenario. Vendor adapters add the per-vendor pieces (session id, resume argv, interactive argv, dialogs, reload command, exit keys).

**Tech Stack:** Python 3.14 standard library only (`pty`, `termios`, `fcntl`, `asyncio`, `subprocess`); `git`; the vendor CLIs installed on the developer machine.

**Spec:** `docs/superpowers/specs/2026-09-16-skills-discovery-probes-design.md`, section "Pass 2 (same kit, later PR)".

## Global Constraints

- No production code changes: nothing under `src/` or `test/` is touched. Kit files live only under `docs/probes/2026-09-16-skills-discovery/`.
- Standard library only; `from __future__ import annotations`; nothing newer than 3.11 syntax.
- Every path handed to a vendor is resolved with `Path.resolve()`.
- Vendor processes get the sandbox's allow-listed environment plus the vendor's lever; nothing from the developer's `.envrc` leaks in.
- `out/` and `*.stderr.log` and `*-tui.log` are git-ignored; `matrix.json`, `findings.md`, `capability-matrix.md` are committed.
- Comments are scarce (CLAUDE.md "Comments"): no history, no design coordinates, no Linear ids in kit code. Docs may cite `#961`.
- Commit subjects: one imperative clause, at most 80 characters including the trailing `(#961)`, trailer `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- In this worktree run git as `/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/peppy-percolating-biscuit <args>`, one command per invocation, no heredocs, no loops. `KIT` below means `/Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/peppy-percolating-biscuit/docs/probes/2026-09-16-skills-discovery`. Run kit commands from `KIT` (`cd` there once in the command is fine; never `cd` outside the worktree).
- Self-tests run as a script: `python3 $KIT/selftest.py -v`, and must also pass under `python3 -W error::ResourceWarning $KIT/selftest.py`. They must pass with no vendor binary installed. Pass 1's 74 tests keep passing unchanged unless a task says otherwise.
- Each turn arm costs one real model request on the developer's account (two for a two-turn arm). Never loop a turn arm beyond the repetition rule (2 runs, a 3rd on disagreement). Vendor tasks spend at most the smoke turns they list.
- Existing pass 1 behaviour is preserved: `probe.py --mode print` and `--mode daemon` with scenarios S0–S4 produce the same rows as before.

---

## File structure

```
docs/probes/2026-09-16-skills-discovery/
  probe.py                  + scenarios S5–S10, MODE_SCENARIOS, arms_for(), tui mode, record(prior=)
  report.py                 + columns Live catalogue, Startup rewrite, Resume, Nested cwd, Worktree, Peer hook, Interactive
  selftest.py               + tests for every task below
  selftest_servers.py       + "tui" fake vendor
  lib/isolation.py          + Sandbox.cwd, add_worktree()
  lib/verdict.py            + judge_update(), judge_delete(), new verdict names
  lib/probe_skill.py        + ProbeSkill.variant(), tui_prompt(), extract_tui_reply(), TUI_REPLY_RE
  lib/acp_driver.py         AcpSession; acp_ask() kept as a wrapper
  lib/appserver_driver.py   AppServerSession; appserver_ask() kept as a wrapper
  lib/pirpc_driver.py       PiRpcSession; pirpc_ask() kept as a wrapper
  lib/pty_driver.py         NEW: strip_ansi(), PtySession
  harness/base.py           + Session, Adapter.open_session/resume/session_id/tui_argv, can_resume, tui_* attributes
  harness/fake.py           + FakeSession, resume, tui launch of the fake vendor
  harness/<vendor>.py       + per-vendor session id, resume, sessions, interactive launch
```

---

### Task 1: Sandbox cwd and linked worktrees

**Files:**
- Modify: `lib/isolation.py`
- Modify: every `harness/*.py` whose `ask()` passes `sb.repo` as the launch directory
- Test: `selftest.py` (`IsolationTests`, `GitExclusionTests`)

**Interfaces:**
- Produces: `Sandbox.cwd: Path` (defaults to `sb.repo`; an arm may point it at a subdirectory or a linked worktree), `add_worktree(sb: Sandbox, name: str = "wt-b") -> Path`.

- [ ] **Step 1: Write the failing tests**

Append to `IsolationTests` in `selftest.py`:

```python
    def test_cwd_defaults_to_repo_and_worktree_is_linked(self):
        from lib.isolation import add_worktree
        sb = new_sandbox("FAKE_HOME", None, [])
        try:
            self.assertEqual(sb.cwd, sb.repo)
            wt = add_worktree(sb)
            self.assertTrue((wt / ".git").is_file())
            self.assertEqual(git(wt, "branch", "--show-current").strip(), "wt-b")
            self.assertEqual(git(sb.repo, "branch", "--show-current").strip(), "main")
            self.assertTrue((wt / "README.md").is_file())
        finally:
            sb.cleanup()
```

Append to `GitExclusionTests`:

```python
    def test_info_exclude_in_linked_worktree_resolves_to_common_dir(self):
        from lib.isolation import add_worktree
        sb = new_sandbox("FAKE_HOME", None, [])
        try:
            wt = add_worktree(sb)
            d = wt / ".x" / "skills" / "kcap-probe-abc"
            d.mkdir(parents=True)
            (d / "SKILL.md").write_text("x\n")
            target = apply(wt, "info-exclude", ".x/skills/kcap-probe-abc")
            self.assertEqual(target, (sb.repo / ".git" / "info" / "exclude").resolve())
            assert_untracked_state(wt, ".x/skills/kcap-probe-abc", "info-exclude")
        finally:
            sb.cleanup()
```

(`git`, `new_sandbox`, `apply`, `assert_untracked_state` are already imported by those test classes; check the imports at the top of each class block and add `add_worktree` there instead of inside the test if the file's style prefers it.)

- [ ] **Step 2: Run them to see them fail**

Run: `python3 $KIT/selftest.py -v 2>&1 | tail -n 5`
Expected: `ImportError`/`AttributeError` for `add_worktree` and `cwd`.

- [ ] **Step 3: Implement**

In `lib/isolation.py`, change `Sandbox` to:

```python
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
    # Where the vendor is launched: the repo unless an arm moves it into a subdirectory or a
    # linked worktree. Hooks and plugins installed per project stay under `repo`.
    cwd: Path = None  # type: ignore[assignment]

    def __post_init__(self) -> None:
        if self.cwd is None:
            self.cwd = self.repo

    def cleanup(self) -> None:
        if not self.keep:
            shutil.rmtree(self.root, ignore_errors=True)
```

Add after `new_sandbox`:

```python
def add_worktree(sb: Sandbox, name: str = "wt-b") -> Path:
    """A linked worktree beside the repo on its own branch: its `.git` is a file pointing into the
    main checkout, and git resolves its `info/exclude` to the shared common directory."""
    path = sb.root / name
    git(sb.repo, "worktree", "add", "-q", "-b", name, str(path))
    return path.resolve()
```

Then in every adapter, inside `ask()` and the argv builders `ask()` calls (`print_argv`, the ACP/app-server/RPC driver calls, agy's `--add-dir`), replace `sb.repo` with `sb.cwd`. Leave `prepare()`, `install_startup_hook()`, `install_registration()`, `skill_dir()`, `skill_file()` and any plugin/hook path on `sb.repo`. Find the sites with `rtk proxy grep -n "sb.repo" $KIT/harness/*.py` and decide per line. `harness/fake.py` is handled in Task 3; leave it.

- [ ] **Step 4: Run the full suite**

Run: `python3 $KIT/selftest.py -v 2>&1 | tail -n 3` and `python3 -W error::ResourceWarning $KIT/selftest.py 2>&1 | tail -n 1`
Expected: `OK`, 76 tests.

- [ ] **Step 5: Commit**

`Launch vendors from the sandbox's cwd and add linked worktrees (#961)`

---

### Task 2: Lifecycle verdicts, the interactive prompt and the report columns

**Files:**
- Modify: `lib/verdict.py`, `lib/probe_skill.py`, `report.py`
- Test: `selftest.py` (`VerdictTests`, `ProbeSkillTests`, `ReportTests`)

**Interfaces:**
- Produces: `judge_update(new_token, old_token, reply, live) -> str`, `judge_delete(old_token, reply) -> str`, verdict names `visible_live`, `stale`, `revoked`; `ProbeSkill.variant() -> ProbeSkill` (same name, fresh token); `tui_prompt(skill) -> str`; `extract_tui_reply(screen) -> str`; `TUI_REPLY_RE`; report columns `Live catalogue`, `Startup rewrite`, `Resume`, `Nested cwd`, `Worktree`, `Peer hook`, `Interactive`.

- [ ] **Step 1: Write the failing tests**

`VerdictTests`:

```python
    def test_judge_update_and_delete(self):
        from lib.verdict import judge_delete, judge_update
        new, old = "a" * 12, "b" * 12
        r = lambda text: parse_reply(text, text, "kcap-probe-abcdef")  # noqa: E731
        self.assertEqual(judge_update(new, old, r(f"PROBE-BODY-{new}"), live=True), "visible_live")
        self.assertEqual(judge_update(new, old, r(f"PROBE-BODY-{new}"), live=False), "visible_first_turn")
        self.assertEqual(judge_update(new, old, r(f"PROBE-BODY-{old}"), live=True), "stale")
        self.assertEqual(judge_update(new, None, r("NO-SKILL"), live=True), "not_visible")
        self.assertEqual(judge_update(new, None, r("kcap-probe-abcdef is listed but empty"), live=True), "catalogue_only")
        self.assertEqual(judge_delete(old, r("NO-SKILL")), "revoked")
        self.assertEqual(judge_delete(old, r(f"PROBE-BODY-{old}")), "stale")
        self.assertEqual(judge_delete(old, r("kcap-probe-abcdef exists but I cannot read it")), "stale")
        self.assertEqual(judge_delete(old, r("I have no idea")), "not_visible")
        for v in ("visible_live", "stale", "revoked"):
            self.assertIn(v, VERDICTS)
```

(`VERDICTS` and `parse_reply` come from the existing imports; add `from lib.verdict import VERDICTS` beside them.)

`ProbeSkillTests`:

```python
    def test_variant_and_tui_prompt(self):
        from lib.probe_skill import TUI_REPLY_RE, extract_tui_reply, tui_prompt
        s = ProbeSkill.fresh()
        v = s.variant()
        self.assertEqual(v.name, s.name)
        self.assertNotEqual(v.token, s.token)
        prompt = tui_prompt(s)
        self.assertNotIn(s.token, prompt)
        self.assertIn(s.name, prompt)
        self.assertEqual(extract_tui_reply(prompt), "")
        screen = f"> {prompt}\n\n  **PROBE-REPLY: {s.body_token}**\n> "
        self.assertEqual(extract_tui_reply(screen), f"PROBE-REPLY: {s.body_token}")
        self.assertEqual(extract_tui_reply("PROBE-REPLY: NO-SKILL\n"), "PROBE-REPLY: NO-SKILL")
        self.assertIsNotNone(TUI_REPLY_RE.search("PROBE-REPLY:  NO-SKILL"))
        r = parse_reply(extract_tui_reply(screen), "", s.name)
        self.assertIn(s.token, r.tokens)
        self.assertFalse(r.skill_named)
```

`ReportTests` (mirror the existing test's row-building helper; add rows for the new scenarios):

```python
    def test_lifecycle_columns(self):
        rows = [
            _row("x", "S1", "S1/native", ".x/skills", "visible_first_turn", "print"),
            _row("x", "S5", "S5/add", ".x/skills", "visible_live", "daemon"),
            _row("x", "S5", "S5/delete", ".x/skills", "revoked", "daemon"),
            _row("x", "S6", "S6/update", ".x/skills", "stale", "print"),
            _row("x", "S6", "S6/update", ".x/skills", "stale", "daemon"),
            _row("x", "S7", "S7/add", ".x/skills", "untested", "print"),
            _row("x", "S9", "S9/linked-other", ".x/skills", "not_visible", "print"),
            _row("x", "S1", "S1/native", ".x/skills", "visible_first_turn", "tui"),
            _row("x", "S5", "S5/reload", ".x/skills", "visible_after_reload", "tui", mechanism="/reload"),
        ]
        s = {r["Entry"]: r for r in summarise(rows)}["x"]
        self.assertEqual(s["Live catalogue"], "add=visible_live; delete=revoked")
        self.assertEqual(s["Startup rewrite"], "update=stale")
        self.assertEqual(s["Resume"], "n/a (not run)")
        self.assertEqual(s["Nested cwd"], "n/a (not run)")
        self.assertEqual(s["Worktree"], "linked-other=not_visible")
        self.assertEqual(s["Interactive"], "S1=visible_first_turn; reload=visible_after_reload")
        self.assertIn("/reload", s["Reload path"])
        self.assertIn("tui", s["Modes"])
```

Define `_row(entry, scenario, arm, root, verdict, mode, mechanism=None)` beside the class if the existing tests have no such helper: it returns the dict shape `emit_matrix` writes (`entry, harness, version, os, mode, scenario, arm, root, exclusion, verdict, flaky, runs, mechanism, evidence, notes`) with `version="1.0"`, `exclusion="none"`, `flaky=False`, `runs=2`, `evidence=[]`, `notes=""`.

- [ ] **Step 2: Run them to see them fail**

Run: `python3 $KIT/selftest.py -v 2>&1 | tail -n 5`
Expected: `ImportError` for `judge_update`, `tui_prompt`; `KeyError` for `Live catalogue`.

- [ ] **Step 3: Implement `lib/verdict.py`**

```python
VERDICTS = ("visible_first_turn", "visible_after_reload", "visible_live", "catalogue_only", "not_visible",
            "stale", "revoked", "leaked", "untested")
```

and after `judge_root`:

```python
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
```

- [ ] **Step 4: Implement `lib/probe_skill.py`**

Add to `ProbeSkill`:

```python
    def variant(self) -> "ProbeSkill":
        """The same skill name with a fresh token: what an in-place rewrite of the body looks like."""
        return ProbeSkill(self.nonce, secrets.token_hex(6))
```

Add after `multi_prompt`:

```python
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
    return "\n".join(f"PROBE-REPLY: {m.group(1)}" for m in TUI_REPLY_RE.finditer(screen))
```

- [ ] **Step 5: Implement the report columns**

In `report.py` set:

```python
COLUMNS = ("Entry", "Version tested", "Modes", "Native root", "Roots consumed", "Startup mechanism proven",
           "Exclusion preserving load", "Vendor-isolated destination", "Reload path", "Live catalogue",
           "Startup rewrite", "Resume", "Nested cwd", "Worktree", "Peer hook", "Interactive", "GUI status",
           "Minimum version")
```

Inside `summarise`, after `ran = ...`, add:

```python
        modes_ran = {r["mode"] for r in rs if r["verdict"] != "untested"}

        def arm_cells(scenario: str, mode: str | None = None) -> list[str]:
            vals: dict[str, set[str]] = {}
            for r in rs:
                if r["scenario"] != scenario or r["verdict"] == "untested":
                    continue
                if mode is not None and r["mode"] != mode:
                    continue
                vals.setdefault(r["arm"].split("/", 1)[1], set()).add(r["verdict"])
            return [f"{arm}={'/'.join(sorted(v))}" for arm, v in sorted(vals.items())]

        def tui_cells() -> list[str]:
            vals: dict[str, set[str]] = {}
            for r in rs:
                if r["mode"] != "tui" or r["verdict"] == "untested" or r["scenario"] == "S0":
                    continue
                key = r["scenario"] if r["scenario"] == "S1" else r["arm"].split("/", 1)[1]
                vals.setdefault(key, set()).add(r["verdict"])
            order = {"S1": 0, "hook-adds-skill": 1, "add": 2, "reload": 3}
            return [f"{k}={'/'.join(sorted(v))}" for k, v in sorted(vals.items(), key=lambda kv: order.get(kv[0], 9))]
```

Change `cell` so the "did it run" test accepts a mode as well as a scenario:

```python
        def cell(values: list[str], scenario: str) -> str:
            # A scenario the entry never ran says so, instead of reading as a measured "none".
            if values:
                return "; ".join(values)
            if not measured:
                return "—"
            ran_it = scenario in modes_ran if scenario == "tui" else scenario in ran
            return "none" if ran_it else "n/a (not run)"
```

and add to the row dict, before `"GUI status"`:

```python
            "Live catalogue": cell(arm_cells("S5", "daemon"), "S5"),
            "Startup rewrite": cell(arm_cells("S6"), "S6"),
            "Resume": cell(arm_cells("S7"), "S7"),
            "Nested cwd": cell(arm_cells("S8"), "S8"),
            "Worktree": cell(arm_cells("S9"), "S9"),
            "Peer hook": cell(arm_cells("S10"), "S10"),
            "Interactive": cell(tui_cells(), "tui"),
```

`Reload path` already collects `mechanism` from `visible_after_reload` rows, which is where the tui `reload` arm's command lands (Task 5 records the command as the row's mechanism).

- [ ] **Step 6: Run the full suite, regenerate the pass 1 table and check it is unchanged apart from the new columns**

Run: `python3 $KIT/selftest.py -v 2>&1 | tail -n 3`; then `python3 $KIT/report.py` and `/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/peppy-percolating-biscuit diff --stat -- docs/probes/2026-09-16-skills-discovery/capability-matrix.md`.
Expected: `OK`; every existing row gains seven `n/a (not run)` cells (or `—` for blocked entries) and nothing else changes. Commit the regenerated table with the code.

- [ ] **Step 7: Commit**

`Add lifecycle verdicts, the interactive prompt and their report columns (#961)`

---

### Task 3: Sessions in the adapter contract and the daemon drivers

**Files:**
- Modify: `harness/base.py`, `lib/acp_driver.py`, `lib/appserver_driver.py`, `lib/pirpc_driver.py`, `harness/fake.py`
- Test: `selftest.py` (`AcpDriverTests`, `JsonlDriversTests`, `AdapterTests`)

**Interfaces:**
- Produces: `harness.base.Session` with `ask(prompt) -> AskResult`, `reload() -> str | None`, `close() -> None`; `Adapter.open_session(sb, mode) -> Session | None` (base handles `mode == "tui"` via `tui_argv`, Task 4); `Adapter.can_resume: bool`, `Adapter.session_id(res) -> str | None`, `Adapter.resume(sb, session_id, prompt) -> AskResult | None`; `Adapter.tui_argv(sb) -> list[str] | None`, `tui_dialogs`, `tui_reload`, `tui_exit`, `tui_ready`; `AcpSession(argv, cwd, env, stderr_path, timeout)`, `AppServerSession(binary, cwd, env, stderr_path, timeout, extra_argv)`, `PiRpcSession(argv, cwd, env, stderr_path, timeout)` each with `start()`; `FakeAdapter.live_catalogue`, `FakeSession`.

- [ ] **Step 1: Write the failing tests**

`AcpDriverTests`:

```python
    def test_session_takes_two_turns(self):
        from lib.acp_driver import AcpSession
        with tempfile.TemporaryDirectory() as d:
            skill = ProbeSkill.fresh()
            repo = _fake_repo(d, None)
            s = AcpSession([sys.executable, str(SERVERS), "acp"], repo, dict(os.environ), Path(d) / "acp.stderr.log", timeout=30)
            s.start()
            try:
                first = s.ask(single_prompt(skill))
                self.assertIn(NO_SKILL, first.reply_text)
                write_skill(repo / ".fake" / "skills", skill)
                second = s.ask(single_prompt(skill))
                self.assertIn(skill.body_token, second.reply_text)
                self.assertNotIn(skill.body_token, first.reply_text)
                self.assertGreater(second.first_request_at, first.first_request_at)
                self.assertIn("tools_used=0", second.notes)
            finally:
                s.close()
```

(`_fake_repo(d, skill)` exists; if it requires a skill, add a `skill=None` branch that writes nothing.)

`JsonlDriversTests`:

```python
    def test_appserver_session_takes_two_turns(self):
        from lib.appserver_driver import AppServerSession
        with tempfile.TemporaryDirectory() as d:
            skill = ProbeSkill.fresh()
            repo = _fake_repo(d, None)
            s = AppServerSession(str(SERVERS), repo, dict(os.environ), Path(d) / "as.stderr.log", timeout=30)
            s.start()
            try:
                self.assertIn(NO_SKILL, s.ask(single_prompt(skill)).reply_text)
                write_skill(repo / ".fake" / "skills", skill)
                self.assertIn(skill.body_token, s.ask(single_prompt(skill)).reply_text)
            finally:
                s.close()

    def test_pirpc_session_takes_two_turns(self):
        from lib.pirpc_driver import PiRpcSession
        with tempfile.TemporaryDirectory() as d:
            skill = ProbeSkill.fresh()
            repo = _fake_repo(d, None)
            s = PiRpcSession([sys.executable, str(SERVERS), "pirpc"], repo, dict(os.environ), Path(d) / "pi.stderr.log", timeout=30)
            s.start()
            try:
                self.assertIn(NO_SKILL, s.ask(single_prompt(skill)).reply_text)
                write_skill(repo / ".fake" / "skills", skill)
                self.assertIn(skill.body_token, s.ask(single_prompt(skill)).reply_text)
            finally:
                s.close()
```

The existing `test_appserver_turn` passes `str(SERVERS)` as the binary: the fake script is executable and dispatches on the literal `app-server` argument, so the session's argv `[binary, "app-server"]` runs it directly.

`AdapterTests`:

```python
    def test_fake_session_resume_and_defaults(self):
        from harness.base import Adapter, Session
        a = FakeAdapter()
        self.assertFalse(Adapter.can_resume)
        self.assertIsNone(Adapter().open_session(None, "daemon"))
        sb = new_sandbox("FAKE_HOME", None, [])
        try:
            skill = ProbeSkill.fresh()
            s = a.open_session(sb, "daemon")
            self.assertIsInstance(s, Session)
            self.assertIn(NO_SKILL, s.ask(single_prompt(skill)).reply_text)
            write_skill(sb.repo / ".fake" / "skills", skill)
            self.assertIn(skill.body_token, s.ask(single_prompt(skill)).reply_text)
            s.close()
            frozen = a.__class__()
            frozen.live_catalogue = False
            fs = frozen.open_session(sb, "daemon")
            other = ProbeSkill.fresh()
            write_skill(sb.repo / ".fake" / "skills", other)
            self.assertNotIn(other.body_token, fs.ask(single_prompt(other)).reply_text)
            self.assertEqual(fs.reload(), "/reload")
            self.assertIn(other.body_token, fs.ask(single_prompt(other)).reply_text)
            fs.close()
            res = a.ask(sb, "print", single_prompt(skill))
            self.assertEqual(a.session_id(res), "fake-session")
            self.assertTrue(a.can_resume)
            resumed = a.resume(sb, "fake-session", single_prompt(skill))
            self.assertIn(skill.body_token, resumed.reply_text)
            self.assertIn("--resume", resumed.argv)
        finally:
            sb.cleanup()
```

- [ ] **Step 2: Run them to see them fail**

Run: `python3 $KIT/selftest.py -v 2>&1 | tail -n 5`
Expected: `ImportError` for `AcpSession`, `Session`.

- [ ] **Step 3: Implement `harness/base.py`**

Add after `AskResult`:

```python
class Session:
    """A live vendor session that takes more than one prompt."""

    def ask(self, prompt: str) -> AskResult:
        raise NotImplementedError

    def reload(self) -> str | None:
        """Ask the vendor to rebuild its skill catalogue: the command used, or None if it has none."""
        return None

    def close(self) -> None:
        return None
```

Add to `Adapter` (class attributes after `turn_timeout`, methods after `ask`):

```python
    can_resume: bool = False
    # Interactive launch: (regex on the stripped screen, keys to send) pairs for the vendor's
    # dialogs, the slash command that rebuilds its catalogue, the keys that end it.
    tui_dialogs: tuple[tuple[str, str], ...] = ()
    tui_reload: str | None = None
    tui_exit: tuple[str, ...] = ("\x03", "\x03", "\x04")
    tui_ready: float = 4.0
```

```python
    def open_session(self, sb: Sandbox, mode: str) -> Session | None:
        if mode != "tui":
            return None
        argv = self.tui_argv(sb)
        if argv is None:
            return None
        from lib.pty_driver import PtySession
        session = PtySession(argv, sb.cwd, sb.env, sb.root / f"{self.harness}-tui.log", dialogs=self.tui_dialogs,
                             reload_command=self.tui_reload, exit_keys=self.tui_exit, ready_idle=self.tui_ready,
                             timeout=self.turn_timeout)
        session.start()
        return session

    def tui_argv(self, sb: Sandbox) -> list[str] | None:
        return None

    def session_id(self, res: AskResult) -> str | None:
        return None

    def resume(self, sb: Sandbox, session_id: str, prompt: str) -> AskResult | None:
        return None
```

The test in Step 1 calls `Adapter().open_session(None, "daemon")`, which returns `None` before touching `sb`.

- [ ] **Step 4: Implement `lib/acp_driver.py`**

Replace `_turn` and `acp_ask` with:

```python
class AcpSession(Session):
    """One ACP agent process holding one session across prompts."""

    def __init__(self, argv: list[str], cwd: Path, env: dict, stderr_path: Path, timeout: float = 180.0) -> None:
        self.argv = list(argv)
        self.cwd = Path(cwd)
        self.stderr_path = stderr_path
        self.timeout = timeout
        self.loop = asyncio.new_event_loop()
        self.client = IsolatedAcpClient(self.argv, str(self.cwd), env, stderr_path)
        self.sid: str | None = None
        self.notes: list[str] = []
        self.started_at = time.time()

    def start(self) -> None:
        self.loop.run_until_complete(self._start())

    async def _start(self) -> None:
        await self.client.start()
        await self.client.request("initialize", INIT_PARAMS, timeout=90)
        new = await self.client.request("session/new", {"cwd": str(self.cwd), "mcpServers": []}, timeout=120)
        self.sid = (new.get("result") or {}).get("sessionId")
        if not self.sid:
            self.notes.append(f"session/new failed: {json.dumps(new)[:500]}")

    def ask(self, prompt: str) -> AskResult:
        first = time.time()
        before = len(self.client.frames)
        notes = list(self.notes)
        text = ""
        try:
            if self.sid:
                resp = self.loop.run_until_complete(self.client.request(
                    "session/prompt", {"sessionId": self.sid, "prompt": [{"type": "text", "text": prompt}]},
                    timeout=self.timeout))
                notes.append(f"stopReason={(resp.get('result') or {}).get('stopReason')}")
                if "error" in resp:
                    notes.append(f"error={json.dumps(resp['error'])[:500]}")
                text = agent_text(self.client.frames[before:])
        except Exception as ex:  # noqa: BLE001
            notes.append(f"exception={ex!r}")
        # A reply the agent read off disk with a tool is not a loaded skill: the count says which it was.
        notes.append(f"tools_used={tool_calls(self.client.frames[before:])}")
        exit_code = self.client.proc.returncode if self.client.proc is not None else None
        return AskResult(reply_text=text, raw=json.dumps(self.client.frames), argv=list(self.argv),
                         started_at=self.started_at, first_request_at=first, stderr_path=str(self.stderr_path),
                         exit_code=exit_code, notes=" ".join(notes))

    def close(self) -> None:
        try:
            self.loop.run_until_complete(self.client.shutdown())
        finally:
            self.loop.close()


def acp_ask(argv: list[str], cwd: Path, env: dict, prompt: str, stderr_path: Path, timeout: float = 180.0) -> AskResult:
    session = AcpSession(argv, cwd, env, stderr_path, timeout)
    try:
        try:
            session.start()
        except Exception as ex:  # noqa: BLE001
            session.notes.append(f"exception={ex!r}")
        return session.ask(prompt)
    finally:
        session.close()
```

Import `Session` beside `AskResult`. The existing three ACP tests must still pass unchanged.

- [ ] **Step 5: Implement `lib/appserver_driver.py`**

Replace `appserver_ask` with a session class holding the same start sequence (initialize, `hooks/list`, trust seeding with a respawn, `thread/start`) and a per-prompt `turn/start`:

```python
class AppServerSession(Session):
    def __init__(self, binary: str, cwd: Path, env: dict, stderr_path: Path, timeout: float = 180.0,
                 extra_argv: list[str] = ()) -> None:
        self.argv = [binary, "app-server", *extra_argv]
        self.cwd = Path(cwd)
        self.env = env
        self.stderr_path = stderr_path
        self.timeout = timeout
        self.child = JsonlChild(self.argv, self.cwd, env, stderr_path)
        self.rpc: _Rpc | None = None
        self.tid: str | None = None
        self.notes: list[str] = []
        self.prior_frames: list[dict] = []
        self.started_at = time.time()

    def start(self) -> None:
        self.child.start()
        self.rpc = _Rpc(self.child)
        init = {"clientInfo": {"name": "kcap-probe", "version": "1"}, "capabilities": {}}
        self.rpc.request("initialize", init, 60)
        listed = self.rpc.request("hooks/list", {}, 60).get("result") or {}
        # The app-server groups hooks per cwd under `data`; a flat `hooks` list is kept for safety.
        hooks = list(listed.get("hooks") or [])
        for group in listed.get("data") or []:
            hooks += list(group.get("hooks") or [])
        override = hook_state_override(hooks)
        if override:
            self.child.stop()
            self.prior_frames = list(self.child.frames)
            self.argv = [*self.argv, "-c", override]
            self.child = JsonlChild(self.argv, self.cwd, self.env, self.stderr_path)
            self.child.start()
            self.rpc = _Rpc(self.child)
            self.rpc.request("initialize", init, 60)
            self.notes.append("hook_trust=seeded")
        elif not hooks:
            self.notes.append("hook_trust=none")
        elif any(h.get("trustStatus") == "trusted" for h in hooks):
            self.notes.append("hook_trust=trusted")
        else:
            self.notes.append("hook_trust=untrusted-unseedable")
        thread = self.rpc.request("thread/start", {
            "cwd": str(self.cwd), "sandbox": "read-only", "approvalPolicy": "never", "approvalsReviewer": "user",
        }, 120)
        self.tid = ((thread.get("result") or {}).get("thread") or {}).get("id")
        if not self.tid:
            self.notes.append(f"thread/start failed: {json.dumps(thread)[:500]}")

    def ask(self, prompt: str) -> AskResult:
        first = time.time()
        notes = list(self.notes)
        text, tools = "", 0
        before = len(self.rpc.notifications) if self.rpc is not None else 0
        try:
            if self.tid and self.rpc is not None:
                self.rpc.request("turn/start", {
                    "threadId": self.tid, "input": [{"type": "text", "text": prompt}],
                    "sandboxPolicy": {"type": "readOnly"}, "approvalPolicy": "never", "approvalsReviewer": "user",
                }, self.timeout)
                done = self.rpc.wait_notification("turn/completed", self.timeout)
                notes.append(f"turn={(((done or {}).get('params') or {}).get('turn') or {}).get('status')}")
                fresh = self.rpc.notifications[before:]
                items = [(n.get("params") or {}).get("item") or {} for n in fresh if n.get("method") == "item/completed"]
                completed = [i["text"] for i in items if i.get("type") == "agentMessage" and isinstance(i.get("text"), str)]
                tools = sum(1 for i in items
                            if i.get("type") and i["type"] not in ("agentMessage", "reasoning", "userMessage"))
                deltas = [n["params"].get("delta", "") for n in fresh if n.get("method") == "item/agentMessage/delta"]
                text = "\n".join(completed) if completed else "".join(deltas)
        except Exception as ex:  # noqa: BLE001
            notes.append(f"exception={ex!r}")
        # A reply the agent read off disk with a tool is not a loaded skill: the count says which it was.
        notes.append(f"tools_used={tools}")
        return AskResult(reply_text=text, raw=json.dumps(self.prior_frames + self.child.frames), argv=list(self.argv),
                         started_at=self.started_at, first_request_at=first, stderr_path=str(self.stderr_path),
                         exit_code=self.child.returncode, notes=" ".join(notes))

    def close(self) -> None:
        self.child.stop()


def appserver_ask(binary: str, cwd: Path, env: dict, prompt: str, stderr_path: Path,
                  timeout: float = 180.0, extra_argv: list[str] = ()) -> AskResult:
    session = AppServerSession(binary, cwd, env, stderr_path, timeout, extra_argv)
    try:
        try:
            session.start()
        except Exception as ex:  # noqa: BLE001
            session.notes.append(f"exception={ex!r}")
        return session.ask(prompt)
    finally:
        session.close()
```

- [ ] **Step 6: Implement `lib/pirpc_driver.py`**

```python
class PiRpcSession(Session):
    def __init__(self, argv: list[str], cwd: Path, env: dict, stderr_path: Path, timeout: float = 180.0) -> None:
        self.argv = list(argv)
        self.stderr_path = stderr_path
        self.timeout = timeout
        self.child = JsonlChild(self.argv, cwd, env, stderr_path)
        self.next_id = 0
        self.notes: list[str] = []
        self.started_at = time.time()

    def start(self) -> None:
        self.child.start()

    def ask(self, prompt: str) -> AskResult:
        first = time.time()
        texts, notes = [], list(self.notes)
        tools = 0
        self.next_id += 1
        try:
            self.child.send({"id": str(self.next_id), "type": "prompt", "message": prompt, "streamingBehavior": "followUp"})
            deadline = time.time() + self.timeout
            while time.time() < deadline:
                msg = self.child.recv(max(0.1, deadline - time.time()))
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
                elif t == "tool_execution_end":
                    # A tool call also appears as a toolCall part of the message; count it once.
                    tools += 1
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
        # A reply the agent read off disk with a tool is not a loaded skill: the count says which it was.
        notes.append(f"tools_used={tools}")
        return AskResult(reply_text="\n".join(texts), raw=json.dumps(self.child.frames), argv=list(self.argv),
                         started_at=self.started_at, first_request_at=first, stderr_path=str(self.stderr_path),
                         exit_code=self.child.returncode, notes=" ".join(notes))

    def close(self) -> None:
        self.child.stop()


def pirpc_ask(argv: list[str], cwd: Path, env: dict, prompt: str, stderr_path: Path, timeout: float = 180.0) -> AskResult:
    session = PiRpcSession(argv, cwd, env, stderr_path, timeout)
    try:
        try:
            session.start()
        except Exception as ex:  # noqa: BLE001
            session.notes.append(f"exception={ex!r}")
        return session.ask(prompt)
    finally:
        session.close()
```

- [ ] **Step 7: Implement `harness/fake.py`**

```python
class FakeSession(Session):
    def __init__(self, adapter: "FakeAdapter", sb: Sandbox) -> None:
        self.adapter = adapter
        self.sb = sb
        # A frozen catalogue is what a vendor that indexes once looks like; /reload refreshes it.
        self.frozen: list[str] | None = None if adapter.live_catalogue else adapter.catalogue(sb)

    def ask(self, prompt: str) -> AskResult:
        lines = self.frozen if self.frozen is not None else self.adapter.catalogue(self.sb)
        return self.adapter.reply(lines, ["fake", "--session"])

    def reload(self) -> str | None:
        if self.frozen is not None:
            self.frozen = self.adapter.catalogue(self.sb)
        return "/reload"


class FakeAdapter(Adapter):
    entry = "fake"
    harness = "fake"
    binary = "sh"
    lever = "FAKE_HOME"
    native_root = ".fake/skills"
    documented_roots = frozenset({".fake/skills", ".agents/skills"})
    modes = ("print", "daemon", "tui")
    can_resume = True
    tui_reload = "/reload"
    tui_ready = 0.3
    # What the fake vendor actually loads, which a subclass keeps while narrowing what it documents.
    read_roots = (".agents/skills", ".fake/skills")
    live_catalogue = True

    def __init__(self) -> None:
        self._hook: Path | None = None

    def version(self, env: dict | None = None) -> str:
        return "1.0"

    def install_startup_hook(self, sb: Sandbox, script: Path) -> HookInfo:
        self._hook = script
        return HookInfo(mechanism="fake-startup", config_path=str(script))

    def catalogue(self, sb: Sandbox) -> list[str]:
        lines = []
        for root in self.read_roots:
            for skill_md in sorted((sb.cwd / root).glob("kcap-probe-*/SKILL.md")):
                m = TOKEN_RE.search(skill_md.read_text())
                if m:
                    lines.append(f"{skill_md.parent.name}=PROBE-BODY-{m.group(1)}")
        return lines

    def reply(self, lines: list[str], argv: list[str]) -> AskResult:
        started = time.time()
        text = "\n".join(lines) if lines else NO_SKILL
        return AskResult(reply_text=text, raw=text, argv=argv, started_at=started,
                         first_request_at=started, stderr_path=None, exit_code=0)

    def ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        if self._hook is not None:
            subprocess.run([str(self._hook)], input="{}", capture_output=True, text=True, timeout=10)
        return self.reply(self.catalogue(sb), ["fake"])

    def open_session(self, sb: Sandbox, mode: str) -> Session | None:
        if mode == "daemon":
            return FakeSession(self, sb)
        return super().open_session(sb, mode)

    def session_id(self, res: AskResult) -> str | None:
        return "fake-session"

    def resume(self, sb: Sandbox, session_id: str, prompt: str) -> AskResult | None:
        return self.reply(self.catalogue(sb), ["fake", "--resume", session_id])

    def tui_argv(self, sb: Sandbox) -> list[str] | None:
        servers = Path(__file__).resolve().parent.parent / "selftest_servers.py"
        if self._hook is not None:
            sb.env["KCAP_FAKE_HOOK"] = str(self._hook)
        if not self.live_catalogue:
            sb.env["KCAP_FAKE_TUI_FROZEN"] = "1"
        return [sys.executable, str(servers), "tui"]
```

Add `import sys` and `from harness.base import Adapter, AskResult, HookInfo, Session`. Existing subclasses in `selftest.py` override `ask`/`read_roots`; keep their behaviour (run the suite).

- [ ] **Step 8: Run the full suite**

Run: `python3 $KIT/selftest.py -v 2>&1 | tail -n 3` and the `-W error::ResourceWarning` variant.
Expected: `OK`; the tui branch of `open_session` is exercised only from Task 4 on.

- [ ] **Step 9: Commit**

`Hold a vendor session across prompts in the daemon drivers (#961)`

---

### Task 4: The pseudo-terminal driver and the fake interactive vendor

**Files:**
- Create: `lib/pty_driver.py`
- Modify: `selftest_servers.py` (add `tui`), `.gitignore` (add `*-tui.log`)
- Test: `selftest.py` (new `PtyDriverTests`)

**Interfaces:**
- Produces: `strip_ansi(text) -> str`; `PtySession(argv, cwd, env, log_path, dialogs=(), reload_command=None, exit_keys=(...), ready_idle=4.0, timeout=180.0, cols=200, rows=50)` with `start()` (spawns and waits for the screen to settle), `screen() -> str`, `send(text)`, `ask(prompt) -> AskResult`, `command(line, settle=None) -> str`, `reload() -> str | None`, `close()`.
- Consumes: `extract_tui_reply` (Task 2), `Session`/`AskResult` (Task 3), `kill_group`.

- [ ] **Step 1: Write the failing tests**

```python
from lib.pty_driver import PtySession, strip_ansi  # noqa: E402
from lib.probe_skill import tui_prompt  # noqa: E402


class PtyDriverTests(unittest.TestCase):
    def _session(self, d, skill=None, env=None, **kw):
        repo = _fake_repo(d, skill)
        argv = [sys.executable, str(SERVERS), "tui"]
        s = PtySession(argv, repo, dict(os.environ, **(env or {})), Path(d) / "fake-tui.log", ready_idle=0.3,
                       timeout=15, **kw)
        s.start()
        return repo, s

    def test_strip_ansi(self):
        self.assertEqual(strip_ansi("\x1b[1mbold\x1b[0m\r\n\x1b]0;title\x07x"), "bold\nx")

    def test_ask_reads_the_reply_not_the_echoed_prompt(self):
        with tempfile.TemporaryDirectory() as d:
            skill = ProbeSkill.fresh()
            repo, s = self._session(d, skill)
            try:
                res = s.ask(tui_prompt(skill))
                self.assertEqual(res.reply_text, f"PROBE-REPLY: {skill.body_token}")
                self.assertIn("PROBE-REPLY: <value>", res.raw)
                self.assertGreaterEqual(res.first_request_at, res.started_at)
                other = ProbeSkill.fresh()
                self.assertEqual(s.ask(tui_prompt(other)).reply_text, "PROBE-REPLY: NO-SKILL")
            finally:
                s.close()
            self.assertIsNotNone(s.proc.poll())
            self.assertTrue((Path(d) / "fake-tui.log").stat().st_size > 0)

    def test_dialog_is_answered_before_the_prompt(self):
        with tempfile.TemporaryDirectory() as d:
            skill = ProbeSkill.fresh()
            repo, s = self._session(d, skill, env={"KCAP_FAKE_TUI_DIALOG": "1"},
                                    dialogs=((r"trust this folder", "y\r"),))
            try:
                res = s.ask(tui_prompt(skill))
                self.assertEqual(res.reply_text, f"PROBE-REPLY: {skill.body_token}")
                self.assertIn("dialog=", res.notes)
            finally:
                s.close()

    def test_reload_refreshes_a_frozen_catalogue(self):
        with tempfile.TemporaryDirectory() as d:
            skill = ProbeSkill.fresh()
            repo, s = self._session(d, None, env={"KCAP_FAKE_TUI_FROZEN": "1"}, reload_command="/reload")
            try:
                self.assertEqual(s.ask(tui_prompt(skill)).reply_text, "PROBE-REPLY: NO-SKILL")
                write_skill(repo / ".fake" / "skills", skill)
                self.assertEqual(s.ask(tui_prompt(skill)).reply_text, "PROBE-REPLY: NO-SKILL")
                self.assertEqual(s.reload(), "/reload")
                self.assertEqual(s.ask(tui_prompt(skill)).reply_text, f"PROBE-REPLY: {skill.body_token}")
            finally:
                s.close()

    def test_silent_process_times_out_and_is_killed(self):
        with tempfile.TemporaryDirectory() as d:
            s = PtySession([sys.executable, "-c", "import time; time.sleep(60)"], Path(d), dict(os.environ),
                           Path(d) / "t.log", ready_idle=0.3, timeout=2)
            with self.assertRaises(TimeoutError):
                s.start()
            s.close()
            self.assertIsNotNone(s.proc.poll())

    def test_no_reply_line_is_reported(self):
        with tempfile.TemporaryDirectory() as d:
            repo, s = self._session(d, None)
            try:
                s.timeout = 3
                res = s.ask("hello")
                self.assertEqual(res.reply_text, "")
                self.assertIn("no PROBE-REPLY line", res.notes)
            finally:
                s.close()
```

- [ ] **Step 2: Run them to see them fail**

Run: `python3 $KIT/selftest.py -v 2>&1 | tail -n 5`
Expected: `ModuleNotFoundError: lib.pty_driver`.

- [ ] **Step 3: Add the fake interactive vendor to `selftest_servers.py`**

```python
def tui() -> None:
    hook = os.environ.get("KCAP_FAKE_HOOK")
    if hook:
        subprocess.run([hook], input="{}", capture_output=True, text=True, timeout=10)
    frozen = answer() if os.environ.get("KCAP_FAKE_TUI_FROZEN") == "1" else None
    out = sys.stdout
    out.write("\x1b[1mfake tui\x1b[0m ready\r\n")
    if os.environ.get("KCAP_FAKE_TUI_DIALOG") == "1":
        out.write("Do you trust this folder? (y/n) ")
        out.flush()
        if not sys.stdin.readline().strip().lower().startswith("y"):
            return
    out.write("> ")
    out.flush()
    for line in sys.stdin:
        line = line.strip()
        if line in ("/exit", "/quit"):
            return
        if line == "/reload":
            if frozen is not None:
                frozen = answer()
            out.write("reloaded\r\n> ")
        elif line.startswith("You have a skill"):
            reply = frozen if frozen is not None else answer()
            value = "NO-SKILL" if reply == "NO-SKILL" else reply.splitlines()[0].split("=", 1)[1]
            out.write(f"\x1b[32m**PROBE-REPLY: {value}**\x1b[0m\r\n> ")
        else:
            out.write("?\r\n> ")
        out.flush()
```

and register it: `{"acp": acp, "appserver": appserver, "app-server": appserver, "pirpc": pirpc, "tui": tui}`.

- [ ] **Step 4: Implement `lib/pty_driver.py`**

```python
from __future__ import annotations

import fcntl
import os
import pty
import re
import struct
import subprocess
import termios
import threading
import time
from pathlib import Path

from harness.base import AskResult, Session
from lib.probe_skill import extract_tui_reply
from lib.procs import kill_group

ANSI_RE = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[@-Z\\-_]|\r")


def strip_ansi(text: str) -> str:
    return ANSI_RE.sub("", text)


def _take_terminal() -> None:
    fcntl.ioctl(0, termios.TIOCSCTTY, 0)


class PtySession(Session):
    """A vendor's interactive UI on a pseudo-terminal: type a line, read the screen stream."""

    def __init__(self, argv: list[str], cwd: Path, env: dict, log_path: Path,
                 dialogs: tuple[tuple[str, str], ...] = (), reload_command: str | None = None,
                 exit_keys: tuple[str, ...] = ("\x03", "\x03", "\x04"), ready_idle: float = 4.0,
                 timeout: float = 180.0, cols: int = 200, rows: int = 50) -> None:
        self.argv = list(argv)
        self.cwd = Path(cwd)
        self.log_path = Path(log_path)
        self.env = {**env, "TERM": "xterm-256color", "COLUMNS": str(cols), "LINES": str(rows)}
        self.dialogs = [(re.compile(pattern), keys) for pattern, keys in dialogs]
        self.reload_command = reload_command
        self.exit_keys = tuple(exit_keys)
        self.ready_idle = ready_idle
        self.timeout = timeout
        self.cols, self.rows = cols, rows
        self.proc: subprocess.Popen | None = None
        self.master = -1
        self._buf = bytearray()
        self._lock = threading.Lock()
        self._last = time.time()
        self._answered: set[int] = set()
        self.notes: list[str] = []
        self.started_at = time.time()

    def start(self) -> None:
        self.master, slave = pty.openpty()
        fcntl.ioctl(self.master, termios.TIOCSWINSZ, struct.pack("HHHH", self.rows, self.cols, 0, 0))
        self._log = self.log_path.open("ab")
        try:
            # Its own session with the slave as controlling terminal, so the vendor sees a real
            # tty and a stuck one can be killed with everything it spawned.
            self.proc = subprocess.Popen(self.argv, cwd=str(self.cwd), env=self.env, stdin=slave, stdout=slave,
                                         stderr=slave, start_new_session=True, preexec_fn=_take_terminal)
        finally:
            os.close(slave)
        self._reader = threading.Thread(target=self._read, daemon=True)
        self._reader.start()
        self.wait_ready(self.timeout)

    def _read(self) -> None:
        while True:
            try:
                chunk = os.read(self.master, 65536)
            except OSError:
                break
            if not chunk:
                break
            with self._lock:
                self._buf += chunk
                self._last = time.time()
            self._log.write(chunk)
            self._log.flush()

    def screen(self) -> str:
        with self._lock:
            data = bytes(self._buf)
        return strip_ansi(data.decode("utf-8", "replace"))

    def send(self, text: str) -> None:
        os.write(self.master, text.encode())

    def _answer_dialogs(self) -> None:
        screen = self.screen()
        for i, (pattern, keys) in enumerate(self.dialogs):
            if i not in self._answered and pattern.search(screen):
                self._answered.add(i)
                self.notes.append(f"dialog={pattern.pattern}")
                self.send(keys)
                time.sleep(0.5)

    def wait_ready(self, timeout: float) -> None:
        deadline = time.time() + timeout
        while time.time() < deadline:
            self._answer_dialogs()
            with self._lock:
                seen, idle = len(self._buf) > 0, time.time() - self._last
            if seen and idle >= self.ready_idle:
                return
            if self.proc is not None and self.proc.poll() is not None:
                raise RuntimeError(f"tui exited with {self.proc.returncode} before it was ready")
            time.sleep(0.1)
        raise TimeoutError(f"tui not ready within {timeout}s")

    def ask(self, prompt: str) -> AskResult:
        offset = len(self.screen())
        self.send(prompt)
        time.sleep(0.5)
        first = time.time()
        self.send("\r")
        deadline = time.time() + self.timeout
        reply = ""
        notes = list(self.notes)
        while time.time() < deadline:
            self._answer_dialogs()
            reply = extract_tui_reply(self.screen()[offset:])
            if reply:
                break
            if self.proc is not None and self.proc.poll() is not None:
                notes.append(f"tui exited with {self.proc.returncode}")
                break
            time.sleep(0.2)
        else:
            notes.append("timeout")
        if not reply:
            notes.append("no PROBE-REPLY line on the screen")
        return AskResult(reply_text=reply, raw=self.screen()[offset:], argv=list(self.argv), started_at=self.started_at,
                         first_request_at=first, stderr_path=str(self.log_path), exit_code=None,
                         notes=" ".join(notes))

    def command(self, line: str, settle: float | None = None) -> str:
        offset = len(self.screen())
        self.send(line + "\r")
        time.sleep(settle if settle is not None else max(self.ready_idle, 1.0))
        self._answer_dialogs()
        return self.screen()[offset:]

    def reload(self) -> str | None:
        if self.reload_command is None:
            return None
        self.command(self.reload_command)
        return self.reload_command

    def close(self) -> None:
        if self.proc is None:
            return
        try:
            for keys in self.exit_keys:
                if self.proc.poll() is not None:
                    break
                try:
                    self.send(keys)
                except OSError:
                    break
                time.sleep(0.5)
            try:
                self.proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                kill_group(self.proc.pid)
                self.proc.wait(timeout=5)
        finally:
            kill_group(self.proc.pid)
            try:
                os.close(self.master)
            except OSError:
                pass
            self._reader.join(timeout=5)
            self._log.close()
```

Add `*-tui.log` to the kit's `.gitignore`.

- [ ] **Step 5: Run the full suite, including the resource-warning variant**

Expected: `OK`. If `preexec_fn` warns under 3.14, keep it (a controlling terminal is what makes Ctrl-C reach the vendor) and note the warning in the report.

- [ ] **Step 6: Commit**

`Drive a vendor's interactive UI through a pseudo-terminal (#961)`

---

### Task 5: Scenarios S5–S10 and the tui mode in the orchestrator

**Files:**
- Modify: `probe.py`
- Test: `selftest.py` (`RunnerTests`)

**Interfaces:**
- Consumes: everything Tasks 1–4 produce.
- Produces: `SCENARIOS` S0–S10, `MODE_SCENARIOS`, `arms_for(mode)`, `Runner.arm_s5/arm_s6/arm_s7/arm_s8/arm_s9/arm_s10`, `Runner.record(..., prior=)`, `--mode tui`.

- [ ] **Step 1: Write the failing tests**

Add these `FakeAdapter` subclasses beside the existing ones in `selftest.py`:

```python
class _FrozenAdapter(FakeAdapter):
    live_catalogue = False


class _AncestorAdapter(FakeAdapter):
    """A vendor anchored at the git root: reads the repo's roots wherever it is launched."""
    def catalogue(self, sb):
        lines = []
        for root in self.read_roots:
            for skill_md in sorted((sb.repo / root).glob("kcap-probe-*/SKILL.md")):
                m = TOKEN_RE.search(skill_md.read_text())
                if m:
                    lines.append(f"{skill_md.parent.name}=PROBE-BODY-{m.group(1)}")
        return lines


class _NoResumeAdapter(_CountingAdapter):
    can_resume = False
```

(`TOKEN_RE` is imported from `lib.probe_skill` at the top of the test module; `_CountingAdapter` counts `ask` calls in `self.calls` or similar; read it and use its counter's name.)

Then in `RunnerTests`:

```python
    def _runner(self, d, adapter=None, runs=1):
        return Runner(adapter or FakeAdapter(), Path(d) / "out", runs=runs, base=Path(d))

    def test_s5_live_catalogue(self):
        with tempfile.TemporaryDirectory() as d:
            recs = self._runner(d).run_scenario("daemon", "S5")
            self.assertEqual({r.arm: r.verdict for r in recs},
                             {"S5/add": "visible_live", "S5/update": "visible_live", "S5/delete": "revoked"})
            self.assertTrue(all("turn1=" in r.notes for r in recs))
            self.assertTrue((Path(d) / "out" / "fake" / "daemon" / "S5" / "S5_add" / "run1.prior.raw.txt").is_file())

    def test_s5_frozen_catalogue_is_stale(self):
        with tempfile.TemporaryDirectory() as d:
            recs = self._runner(d, _FrozenAdapter()).run_scenario("daemon", "S5")
            self.assertEqual({r.arm: r.verdict for r in recs},
                             {"S5/add": "not_visible", "S5/update": "stale", "S5/delete": "stale"})

    def test_s5_without_a_session_is_untested_without_a_turn(self):
        with tempfile.TemporaryDirectory() as d:
            a = _CountingAdapter()
            a.open_session = lambda sb, mode: None
            recs = self._runner(d, a).run_scenario("daemon", "S5")
            self.assertEqual({r.verdict for r in recs}, {"untested"})
            self.assertEqual(a.calls, 0)

    def test_s6_startup_mutation(self):
        with tempfile.TemporaryDirectory() as d:
            recs = self._runner(d).run_scenario("print", "S6")
            self.assertEqual({r.arm: r.verdict for r in recs}, {"S6/update": "visible_first_turn", "S6/delete": "revoked"})
            self.assertTrue(all(r.hook and r.hook["fired_at"] for r in recs))
            recs = self._runner(d, _NoFireAdapter()).run_scenario("print", "S6")
            self.assertEqual({r.verdict for r in recs}, {"untested"})
            self.assertTrue(all("hook never fired" in r.notes for r in recs))

    def test_s7_resume(self):
        with tempfile.TemporaryDirectory() as d:
            recs = self._runner(d).run_scenario("print", "S7")
            self.assertEqual({r.arm: r.verdict for r in recs}, {"S7/add": "visible_first_turn", "S7/update": "visible_first_turn"})
            self.assertTrue(all("session=fake-session" in r.notes and "--resume" in r.argv for r in recs))
            a = _NoResumeAdapter()
            recs = self._runner(d, a).run_scenario("print", "S7")
            self.assertEqual({r.verdict for r in recs}, {"untested"})
            self.assertEqual(a.calls, 0)

    def test_s8_nested_cwd(self):
        with tempfile.TemporaryDirectory() as d:
            recs = self._runner(d).run_scenario("print", "S8")
            self.assertEqual({r.arm: r.verdict for r in recs}, {"S8/ancestor": "not_visible", "S8/local": "visible_first_turn"})
            self.assertTrue(all("cwd=sub/dir" in r.notes for r in recs))
            recs = self._runner(d, _AncestorAdapter()).run_scenario("print", "S8")
            self.assertEqual({r.arm: r.verdict for r in recs}, {"S8/ancestor": "visible_first_turn", "S8/local": "not_visible"})

    def test_s9_worktrees(self):
        with tempfile.TemporaryDirectory() as d:
            recs = self._runner(d).run_scenario("print", "S9")
            self.assertEqual({r.arm: r.verdict for r in recs},
                             {"S9/linked-own": "visible_first_turn", "S9/linked-other": "not_visible"})
            self.assertTrue(all(r.exclusion == "info-exclude" for r in recs))

    def test_s10_peer_hook(self):
        with tempfile.TemporaryDirectory() as d:
            recs = self._runner(d).run_scenario("daemon", "S10")
            self.assertEqual([(r.arm, r.verdict) for r in recs], [("S10/hook-from-peer", "visible_live")])
            self.assertIn("peer=visible_first_turn", recs[0].notes)
            self.assertIn("turn1=not_visible", recs[0].notes)

    def test_tui_mode_runs_controls_hook_and_live_arms(self):
        with tempfile.TemporaryDirectory() as d:
            r = self._runner(d)
            self.assertEqual([x.verdict for x in r.run_scenario("tui", "S0")], ["not_visible"])
            self.assertEqual([x.verdict for x in r.run_scenario("tui", "S1")], ["visible_first_turn"])
            recs = r.run_scenario("tui", "S2")
            self.assertEqual({x.arm: x.verdict for x in recs}, {"S2/hook-adds-skill": "visible_first_turn"})
            recs = r.run_scenario("tui", "S5")
            self.assertEqual({x.arm: x.verdict for x in recs}, {"S5/add": "visible_live", "S5/reload": "visible_after_reload"})
            self.assertEqual([x.hook["mechanism"] for x in recs if x.arm == "S5/reload"], ["/reload"])
            recs = self._runner(d, _FrozenAdapter()).run_scenario("tui", "S5")
            self.assertEqual({x.arm: x.verdict for x in recs}, {"S5/add": "not_visible", "S5/reload": "visible_after_reload"})

    def test_mode_scenario_table_and_blocked_rows(self):
        with tempfile.TemporaryDirectory() as d:
            r = self._runner(d)
            self.assertEqual(r.run_scenario("print", "S5"), [])
            self.assertEqual(r.run_scenario("tui", "S4"), [])
            self.assertEqual({x.scenario for x in r.record_blocked("tui", "nope")}, {"S0", "S1", "S2", "S5"})
            self.assertEqual({x.scenario for x in r.record_blocked("daemon", "nope")},
                             {"S0", "S1", "S2", "S3", "S4", "S5", "S6", "S10"})
            self.assertEqual({x.scenario for x in r.record_blocked("print", "nope")},
                             {"S0", "S1", "S2", "S3", "S4", "S6", "S7", "S8", "S9"})
```

`_NoFireAdapter` exists (its hook never runs); check it still fits (it must also skip the hook in `ask`).

- [ ] **Step 2: Run them to see them fail**

Expected: `ValueError: S5` from `run_scenario`, `NameError` for `_FrozenAdapter` until added.

- [ ] **Step 3: Implement the tables and imports in `probe.py`**

Imports: add `add_worktree` from `lib.isolation`, `judge_delete`, `judge_update` from `lib.verdict`, `Session` from `harness.base`.

Replace `SCENARIOS`, keep `S2_ARMS`/`S3_ARMS`, replace `ALL_ARMS` with:

```python
SCENARIOS = ("S0", "S1", "S2", "S3", "S4", "S5", "S6", "S7", "S8", "S9", "S10")
S2_ARMS = ("hook-creates-root", "hook-adds-skill", "registration")
S3_ARMS = ("gitignore", "info-exclude")
S5_ARMS = ("add", "update", "delete")
S5_TUI_ARMS = ("add", "reload")
S6_ARMS = ("update", "delete")
S7_ARMS = ("add", "update")
S8_ARMS = ("ancestor", "local")
S9_ARMS = ("linked-own", "linked-other")
# Discovery does not depend on the launch mode, so the discovery-only scenarios run once, in the
# cheapest mode; the lifecycle scenarios run where a second turn exists.
MODE_SCENARIOS: dict[str, tuple[str, ...]] = {
    "print": ("S0", "S1", "S2", "S3", "S4", "S6", "S7", "S8", "S9"),
    "daemon": ("S0", "S1", "S2", "S3", "S4", "S5", "S6", "S10"),
    "tui": ("S0", "S1", "S2", "S5"),
}


def arms_for(mode: str) -> list[tuple[str, str, bool, str]]:
    """Every arm a full sweep runs in this mode, so an entry that cannot run gets a row per arm."""
    wanted = MODE_SCENARIOS[mode]
    out: list[tuple[str, str, bool, str]] = [("S0", "S0/none", False, "none"), ("S1", "S1/native", True, "none")]
    out += [("S2", f"S2/{a}", True, "none") for a in (("hook-adds-skill",) if mode == "tui" else S2_ARMS)]
    if "S3" in wanted:
        out += [("S3", f"S3/{e}", True, e) for e in S3_ARMS]
    if "S4" in wanted:
        out.append(("S4", "S4/all-roots", False, "none"))
    if "S5" in wanted:
        out += [("S5", f"S5/{a}", True, "none") for a in (S5_TUI_ARMS if mode == "tui" else S5_ARMS)]
    if "S6" in wanted:
        out += [("S6", f"S6/{a}", True, "none") for a in S6_ARMS]
    if "S7" in wanted:
        out += [("S7", f"S7/{a}", True, "none") for a in S7_ARMS]
    if "S8" in wanted:
        out += [("S8", f"S8/{a}", True, "none") for a in S8_ARMS]
    if "S9" in wanted:
        out += [("S9", f"S9/{a}", True, "info-exclude") for a in S9_ARMS]
    if "S10" in wanted:
        out.append(("S10", "S10/hook-from-peer", True, "none"))
    return out
```

`record_blocked` iterates `arms_for(mode)` instead of `ALL_ARMS`.

- [ ] **Step 4: Implement the runner changes**

`record` gains `prior: AskResult | None = None`; after the `.raw.txt` write add:

```python
        if prior is not None and prior.raw:
            run_path.parent.mkdir(parents=True, exist_ok=True)
            (run_path.parent / f"{run_path.stem}.prior.raw.txt").write_text(prior.raw)
```

and its `parse_reply` call becomes `parse_reply(res.reply_text, self._raw_for(mode, res), name)` with:

```python
    @staticmethod
    def _raw_for(mode: str, res: AskResult) -> str:
        # A screen carries the echoed prompt and any tool panel, so it never stands in for a reply.
        return "" if mode == "tui" else res.raw
```

`_ask` handles the tui mode:

```python
    def _ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        if mode == "tui":
            session = self._open(sb, "tui")
            try:
                res = session.ask(prompt)
            finally:
                session.close()
        else:
            res = self.adapter.ask(sb, mode, prompt)
        ...existing broken-run check unchanged...

    def _open(self, sb: Sandbox, mode: str) -> Session:
        session = self.adapter.open_session(sb, mode)
        if session is None:
            raise RuntimeError(f"no {mode} session for this entry")
        return session

    def _judge_turn(self, mode: str, res: AskResult, skill: ProbeSkill) -> str:
        return judge_single(skill.token, parse_reply(res.reply_text, self._raw_for(mode, res), skill.name))

    def _remove_skill(self, sb: Sandbox, root: str, skill: ProbeSkill) -> None:
        a = self.adapter
        if a.flat_skill_layout:
            a.skill_file(sb, root, skill.name).unlink()
        else:
            shutil.rmtree(a.skill_dir(sb, root, skill.name))
```

Every existing `parse_reply(res.reply_text, res.raw, ...)` inside the arms stays as it is for print and daemon; the S2 arm and `arm_s1` are also run in tui mode, so change their parse calls to `self._raw_for(mode, res)` too.

The arms:

```python
    def arm_s5(self, mode: str, arm: str) -> RunRecord:
        started = time.time()
        a = self.adapter
        root = a.native_root
        sb = self.sandbox()
        skill = ProbeSkill.fresh()
        old: str | None = None
        used: str | None = None
        if arm in ("update", "delete"):
            write_skill(sb.repo / root, skill, flat=a.flat_skill_layout)
        session = self._open(sb, mode)
        try:
            first = session.ask(single_prompt(skill))
            turn1 = self._judge_turn(mode, first, skill)
            if arm in ("update", "delete") and turn1 != "visible_first_turn":
                return self.record(mode, "S5", f"S5/{arm}", root, "none", first, "untested", {"native": skill.token},
                                   sb=sb, started=started, notes=f"turn1={turn1}: no baseline to test against",
                                   name=skill.name)
            if arm == "add":
                write_skill(sb.repo / root, skill, flat=a.flat_skill_layout)
            elif arm == "reload":
                write_skill(sb.repo / root, skill, flat=a.flat_skill_layout)
                used = session.reload()
                if used is None:
                    return self.record(mode, "S5", "S5/reload", root, "none", first, "untested", {"native": skill.token},
                                       sb=sb, started=started, notes=f"turn1={turn1}; no reload command for this entry",
                                       name=skill.name)
            elif arm == "update":
                old = skill.token
                skill = skill.variant()
                write_skill(sb.repo / root, skill, flat=a.flat_skill_layout)
            else:
                old = skill.token
                self._remove_skill(sb, root, skill)
            second = session.ask(single_prompt(skill))
        finally:
            session.close()
        reply = parse_reply(second.reply_text, self._raw_for(mode, second), skill.name)
        if arm == "delete":
            verdict = judge_delete(old, reply)
        elif arm == "reload":
            verdict = judge_single(skill.token, reply, reload_used=True)
        else:
            verdict = judge_update(skill.token, old, reply, live=True)
        hook = {"mechanism": used, "config_path": "", "fired_at": None, "fired_at_mtime": None} if used else None
        expected = {"old": old} if arm == "delete" else {"native": skill.token}
        return self.record(mode, "S5", f"S5/{arm}", root, "none", second, verdict, expected, hook=hook, sb=sb,
                           started=started, notes=f"turn1={turn1}", name=skill.name, prior=first)

    def arm_s6(self, mode: str, arm: str) -> RunRecord:
        started = time.time()
        a = self.adapter
        root = a.native_root
        sb = self.sandbox()
        old_skill = ProbeSkill.fresh()
        write_skill(sb.repo / root, old_skill, flat=a.flat_skill_layout)
        target = a.skill_file(sb, root, old_skill.name)
        skill = old_skill.variant() if arm == "update" else old_skill
        if arm == "update":
            script = write_hook_script(sb.config_root, target, skill.render(), stamp_path(sb.config_root))
        else:
            script = write_hook_script(sb.config_root, target, "", stamp_path(sb.config_root), delete=True)
        info = a.install_startup_hook(sb, script)
        if info is None:
            return self.record(mode, "S6", f"S6/{arm}", root, "none", None, "untested", {}, sb=sb, started=started,
                               notes="no startup hook mechanism for this entry")
        res = self._ask(sb, mode, single_prompt(skill))
        reply = parse_reply(res.reply_text, self._raw_for(mode, res), skill.name)
        if arm == "update":
            verdict = judge_update(skill.token, old_skill.token, reply, live=False)
        else:
            verdict = judge_delete(old_skill.token, reply)
        hook = self._hook_dict(sb, info.mechanism, info.config_path)
        notes = ""
        if hook["fired_at"] is None:
            notes, verdict = "hook never fired", "untested"
        elif arm == "update" and (not target.exists() or skill.body_token not in target.read_text()):
            notes, verdict = "skill file not rewritten after the turn", "untested"
        elif arm == "delete" and target.exists():
            notes, verdict = "skill file still present after the turn", "untested"
        expected = {"native": skill.token, "old": old_skill.token} if arm == "update" else {"old": old_skill.token}
        return self.record(mode, "S6", f"S6/{arm}", root, "none", res, verdict, expected, hook=hook, sb=sb,
                           started=started, notes=notes, name=skill.name)

    def arm_s7(self, mode: str, arm: str) -> RunRecord:
        started = time.time()
        a = self.adapter
        root = a.native_root
        sb = self.sandbox()
        skill = ProbeSkill.fresh()
        if not a.can_resume:
            return self.record(mode, "S7", f"S7/{arm}", root, "none", None, "untested", {}, sb=sb, started=started,
                               notes="no resume launch for this entry")
        old: str | None = None
        if arm == "update":
            write_skill(sb.repo / root, skill, flat=a.flat_skill_layout)
        first = self._ask(sb, mode, single_prompt(skill))
        turn1 = self._judge_turn(mode, first, skill)
        sid = a.session_id(first)
        if sid is None:
            return self.record(mode, "S7", f"S7/{arm}", root, "none", first, "untested", {"native": skill.token},
                               sb=sb, started=started, notes=f"turn1={turn1}; no session id in the first run's output",
                               name=skill.name)
        if arm == "update":
            if turn1 != "visible_first_turn":
                return self.record(mode, "S7", f"S7/{arm}", root, "none", first, "untested", {"native": skill.token},
                                   sb=sb, started=started, notes=f"turn1={turn1}: no baseline to test against",
                                   name=skill.name)
            old = skill.token
            skill = skill.variant()
        write_skill(sb.repo / root, skill, flat=a.flat_skill_layout)
        res = a.resume(sb, sid, single_prompt(skill))
        if res is None:
            return self.record(mode, "S7", f"S7/{arm}", root, "none", first, "untested", {"native": skill.token},
                               sb=sb, started=started, notes=f"turn1={turn1}; no resume launch for this entry",
                               name=skill.name)
        verdict = judge_update(skill.token, old, parse_reply(res.reply_text, res.raw, skill.name), live=False)
        return self.record(mode, "S7", f"S7/{arm}", root, "none", res, verdict, {"native": skill.token}, sb=sb,
                           started=started, notes=f"turn1={turn1} session={sid}", name=skill.name, prior=first)

    def arm_s8(self, mode: str, arm: str) -> RunRecord:
        started = time.time()
        a = self.adapter
        root = a.native_root
        sb = self.sandbox()
        sb.cwd = sb.repo / "sub" / "dir"
        sb.cwd.mkdir(parents=True)
        (sb.cwd / "README.md").write_text("nested\n")
        skill = ProbeSkill.fresh()
        tree = sb.repo if arm == "ancestor" else sb.cwd
        write_skill(tree / root, skill, flat=a.flat_skill_layout)
        res = self._ask(sb, mode, single_prompt(skill))
        verdict = self._judge_turn(mode, res, skill)
        return self.record(mode, "S8", f"S8/{arm}", root, "none", res, verdict, {"native": skill.token}, sb=sb,
                           started=started, notes="cwd=sub/dir", name=skill.name)

    def arm_s9(self, mode: str, arm: str) -> RunRecord:
        started = time.time()
        a = self.adapter
        root = a.native_root
        sb = self.sandbox()
        wt = add_worktree(sb)
        sb.cwd = wt
        skill = ProbeSkill.fresh()
        tree = wt if arm == "linked-own" else sb.repo
        write_skill(tree / root, skill, flat=a.flat_skill_layout)
        rel = str(a.skill_dir(sb, root, skill.name).relative_to(sb.repo))
        apply_exclusion(tree, "info-exclude", rel)
        try:
            assert_untracked_state(tree, rel, "info-exclude")
        except AssertionError as ex:
            return self.record(mode, "S9", f"S9/{arm}", root, "info-exclude", None, "untested",
                               {"native": skill.token}, sb=sb, started=started, notes=f"git state: {ex}")
        res = self._ask(sb, mode, single_prompt(skill))
        verdict = self._judge_turn(mode, res, skill)
        where = "linked" if tree == wt else "main"
        return self.record(mode, "S9", f"S9/{arm}", root, "info-exclude", res, verdict, {"native": skill.token},
                           sb=sb, started=started, notes=f"cwd={wt.name} skill_in={where}", name=skill.name)

    def arm_s10(self, mode: str) -> RunRecord:
        started = time.time()
        a = self.adapter
        root = a.native_root
        sb = self.sandbox()
        skill = ProbeSkill.fresh()
        target = a.skill_file(sb, root, skill.name)
        session = self._open(sb, "daemon")
        try:
            first = session.ask(single_prompt(skill))
            turn1 = self._judge_turn("daemon", first, skill)
            script = write_hook_script(sb.config_root, target, skill.render(), stamp_path(sb.config_root))
            info = a.install_startup_hook(sb, script)
            if info is None:
                return self.record(mode, "S10", "S10/hook-from-peer", root, "none", first, "untested", {}, sb=sb,
                                   started=started, notes="no startup hook mechanism for this entry", name=skill.name)
            peer = self._ask(sb, "print", single_prompt(skill))
            peer_verdict = self._judge_turn("print", peer, skill)
            second = session.ask(single_prompt(skill))
        finally:
            session.close()
        hook = self._hook_dict(sb, info.mechanism, info.config_path)
        reply = parse_reply(second.reply_text, second.raw, skill.name)
        verdict = judge_update(skill.token, None, reply, live=True)
        notes = f"turn1={turn1} peer={peer_verdict}"
        if hook["fired_at"] is None:
            notes, verdict = notes + " hook never fired", "untested"
        return self.record(mode, "S10", "S10/hook-from-peer", root, "none", second, verdict, {"native": skill.token},
                           hook=hook, sb=sb, started=started, notes=notes, name=skill.name, prior=peer)
```

`run_scenario`: at the top add `if scenario not in MODE_SCENARIOS[mode]: return []`; in the S2 branch use `("hook-adds-skill",)` as the default arms when `mode == "tui"`; add the branches:

```python
        elif scenario in ("S5", "S6", "S7", "S8", "S9"):
            table = {"S5": S5_TUI_ARMS if mode == "tui" else S5_ARMS, "S6": S6_ARMS, "S7": S7_ARMS,
                     "S8": S8_ARMS, "S9": S9_ARMS}[scenario]
            fn = {"S5": self.arm_s5, "S6": self.arm_s6, "S7": self.arm_s7, "S8": self.arm_s8, "S9": self.arm_s9}[scenario]
            exclusion = "info-exclude" if scenario == "S9" else "none"
            for arm in arms or table:
                if gated:
                    out += self._blocked(mode, scenario, f"{scenario}/{arm}", native, exclusion)
                else:
                    out += self.run_arm(lambda a_=arm: fn(mode, a_), mode, scenario, f"{scenario}/{arm}", native, exclusion)
        elif scenario == "S10":
            if gated:
                out += self._blocked(mode, "S10", "S10/hook-from-peer", native, "none")
            elif "print" not in self.adapter.modes:
                out += self._blocked(mode, "S10", "S10/hook-from-peer", native, "none", notes="no print mode for the peer")
            else:
                out += self.run_arm(lambda: self.arm_s10(mode), mode, "S10", "S10/hook-from-peer", native, "none")
```

`main`: `--mode` choices `("print", "daemon", "tui")`; the default scenario list becomes `MODE_SCENARIOS[args.mode]`; a scenario passed explicitly but absent from that table prints `f"{name}: {scenario} n/a in {args.mode} mode"` and is skipped. Update the module docstring's usage line (`S0..S10`, `--mode print|daemon|tui`).

- [ ] **Step 5: Run the full suite, then a fake sweep end to end**

Run: `python3 $KIT/selftest.py -v 2>&1 | tail -n 3`; then with a scratch outdir `python3 $KIT/probe.py --harness fake --mode tui --turn --runs 1 --outdir /tmp/kcap-probe-fake` and `--mode daemon`, `--mode print`, then `--emit --matrix /tmp/kcap-probe-fake/matrix.json --outdir /tmp/kcap-probe-fake` and `python3 $KIT/report.py --matrix /tmp/kcap-probe-fake/matrix.json --out /tmp/kcap-probe-fake/table.md`.
Expected: `OK`; every S5–S10 and tui row present with the fake's verdicts; the table's new columns filled for `fake`. Remove the scratch directory afterwards.

- [ ] **Step 6: Commit**

`Add the lifecycle scenarios and the interactive mode to the orchestrator (#961)`

---

### Tasks 6–14: per-vendor pass 2 support (inline)

One task per entry, same order as pass 1: Claude, Codex, Pi, Copilot, Kiro, OpenCode V1, OpenCode V2, Cursor, Antigravity, then Gemini's flags only. Each task follows the same steps; the vendor-specific values are in the table after the steps.

**Files:** `harness/<vendor>.py`, `selftest.py` (`<Vendor>AdapterTests`).

- [ ] **Step 1: Session id and resume.** Implement `session_id(res)` reading the value from `res.raw` (the print run's event stream), set `can_resume = True`, and implement `resume(sb, session_id, prompt)` as the print launch plus the resume flag, going through the same `extract` and tool classification as `ask`. Add a unit test that feeds a captured raw sample (two or three lines, inlined) to `session_id` and checks the resume argv contains the flag and the id.
- [ ] **Step 2: Daemon session.** Where the adapter has a daemon mode, implement `open_session(sb, "daemon")` returning the started driver session with the same argv, env and stderr path the daemon branch of `ask` uses, and route the daemon branch of `ask` through the `*_ask` wrapper as before (no behaviour change). Kiro and OpenCode V2 run extra setup around a turn (agent creation, `service stop`): fold it into the session's `close()` via a small subclass.
- [ ] **Step 3: Interactive launch.** Implement `tui_argv(sb)`, add `"tui"` to `modes`, set `tui_exit`. Start it once through `PtySession` from a scratch script (`ready_idle=6`), capture `screen()` after readiness, add every dialog seen to `tui_dialogs` with the keys that dismiss it, send `/help` with `command("/help")`, and record the slash commands in the task report. If a command reloads skills or extensions, set `tui_reload`. This costs no model turn.
- [ ] **Step 4: Free phase and one smoke turn.** `python3 $KIT/probe.py --harness <entry> --mode tui` (free phase) then `--turn --scenario S1 --runs 1 --mode tui --outdir /tmp/kcap-probe-smoke`. Expected: `visible_first_turn`. Fix the argv, dialogs or timing until it is, then remove the scratch outdir. One more smoke turn is allowed for `S7/add` in print mode.
- [ ] **Step 5: Tests and commit.** Unit tests for the argv shapes (`tui_argv`, resume argv) in the vendor's test class. Commit: `Add <Vendor>'s resume, session and interactive launches to the probes (#961)`.

| Entry | Session id in the print stream | Resume launch | Interactive launch (to verify) |
| -- | -- | -- | -- |
| claude | top-level `session_id` of the result JSON | print argv + `--resume <id>` | `claude --strict-mcp-config --allowedTools Skill` plus the pass 1 settings flags; trust dialog likely; exit `/exit\r` |
| codex | `thread_id` in the `thread.started` JSONL event | `codex exec resume <id> --json --skip-git-repo-check --sandbox read-only --color never --dangerously-bypass-hook-trust --output-last-message <f> -` | `codex --sandbox read-only -a never`; trust dialog likely; exit `/quit\r` |
| pi | `id` of the session header line in `--mode json` output | `pi -p --mode json --approve --session <id> -- <prompt>` | `pi --approve`; check `/reload`; exit `/quit\r` |
| copilot | `id` of the session start event | `copilot -p <prompt> --allow-all-tools --output-format json --resume=<id>` | `copilot --allow-all-tools`; exit `/exit\r` |
| kiro | none; `--resume` is per directory, so `session_id` returns `"cwd"` | `kiro-cli chat --no-interactive --trust-all-tools --resume [--agent <name>] <prompt>` | `kiro-cli chat --trust-all-tools [--agent <name>]`; exit `/quit\r` |
| opencode-v1 | `sessionID` in the JSON events | `opencode run --format json --session <id> <prompt>` | `opencode`; exit Ctrl-C twice |
| opencode-v2 | same | `opencode run --standalone --format json --session <id> <prompt>` | `opencode --standalone`? verify; `service stop` after close |
| cursor | `session_id` in the stream-json init event | `agent -p --output-format stream-json --trust --force --resume=<id> <prompt>` | `agent --trust`; exit `/exit\r`; never two cursor processes at once |
| agy-dirlayout | `conversation_id` in the stream-json events | print argv + `--conversation <id>` | `agy --add-dir <cwd>`; exit `/exit\r` |
| gemini | `session_id` if present; else `--resume latest` with `"latest"` as the id | `gemini -p <prompt> -o json --approval-mode yolo --resume <id>` | `gemini --approval-mode yolo`; stays blocked on credentials, so no smoke turn |

---

### Task 15: Sweeps, findings, matrix, PR

**Files:** `out/` (ignored), `matrix.json`, `capability-matrix.md`, `findings.md`.

- [ ] **Step 1: Sweep per entry.** For each of claude, codex, pi, copilot, kiro, opencode-v1, opencode-v2, cursor, agy-dirlayout, gemini: `--mode print --scenario S6 --scenario S7 --scenario S8 --scenario S9 --turn`, then `--mode daemon --scenario S5 --scenario S6 --scenario S10 --turn`, then `--mode tui --turn` (S0, S1, S2, S5). Run one entry at a time; cursor never concurrently with anything. Re-run an entry whose rows are `untested` for a kit reason after fixing the kit; leave vendor reasons as they are.
- [ ] **Step 2: Regenerate.** `python3 $KIT/probe.py --emit` then `python3 $KIT/report.py`; `python3 $KIT/selftest.py`; `bash scripts/check-linear-ids.sh`.
- [ ] **Step 3: Findings.** Add a "Pass 2" section to `findings.md`: subject line with the versions and OS, one subsection per entry with the S5–S10 and tui results and the reply excerpts that decided them, the consequences for #778 (where a materializer may write into a live session, what a resumed session sees, which trees are scanned) and #962 (which vendors need a reload path in the startup adapter), and the kit traps found (dialogs, paste handling, timing).
- [ ] **Step 4: PR.** Commit `Record the pass 2 lifecycle and interactive matrix (#961)`; push the branch to `https://github.com/kurrent-io/kcap-cli.git`; open the PR with base `alexeyzimarev/ai-2829-verify-repo-local-skill-discovery-and-startup-ordering`, description per `.github/PULL_REQUEST_TEMPLATE.md` with `Closes #961` and `AI-2829` on the reference line.
