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


from lib.probe_skill import (  # noqa: E402
    NO_SKILL, TOKEN_RE, ProbeSkill, multi_prompt, parse_reply, single_prompt, write_skill,
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
        # naming some other probe skill is not naming this one
        other = ProbeSkill.fresh()
        r = parse_reply(f"The skill listed is {other.name}, not {s.name}.", "", name=s.name)
        self.assertTrue(r.skill_named)
        r = parse_reply(f"Only {other.name} is available.", "", name=s.name)
        self.assertFalse(r.skill_named)

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

    def test_git_ignores_the_developer_global_config(self):
        with tempfile.TemporaryDirectory() as d:
            excludes = Path(d) / "excludes"
            excludes.write_text(".claude/\n")
            cfg = Path(d) / "gitconfig"
            cfg.write_text(f"[core]\n\texcludesFile = {excludes}\n")
            with mock.patch.dict(os.environ, {"GIT_CONFIG_GLOBAL": str(cfg)}):
                sb = new_sandbox("HOME", None, [], base=Path(d))
                try:
                    rel = ".claude/skills/kcap-probe-abc123"
                    (sb.repo / rel).mkdir(parents=True)
                    (sb.repo / rel / "SKILL.md").write_text("x")
                    self.assertIn("??", assert_untracked_state(sb.repo, rel, "none"))
                finally:
                    sb.cleanup()

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
            with self.assertRaises(AssertionError):
                assert_untracked_state(sb.repo, ".claude/skills/kcap-probe-absent", "none")
            (sb.repo / ".claude/skills/kcap-probe-empty").mkdir()
            with self.assertRaises(AssertionError):
                assert_untracked_state(sb.repo, ".claude/skills/kcap-probe-empty", "none")

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
        # the name echoed beside an explicit NO-SKILL is a negative, not a sighting
        self.assertEqual(judge_single("b" * 12, Reply(frozenset(), True, True)), "not_visible")

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
            self.assertEqual(len(rows), 2)
            by_version = {r["version"]: r for r in rows}
            row = by_version["1.0"]
            self.assertEqual(row["verdict"], "visible_first_turn")
            self.assertTrue(row["flaky"])
            self.assertEqual(row["runs"], 3)
            self.assertEqual(row["arm"], "S1/native")
            self.assertTrue(all(e.startswith("out/") for e in row["evidence"]))
            older = by_version["0.9"]
            self.assertEqual(older["runs"], 1)
            self.assertEqual(older["verdict"], "not_visible")
            self.assertEqual(older["arm"], "S0/none")
            self.assertEqual(json.loads((Path(d) / "matrix.json").read_text())[0]["entry"], "fake")

    def test_os_label(self):
        self.assertRegex(os_label(), r"^\S+ \S+ \S+$")


import subprocess  # noqa: E402
import time  # noqa: E402

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
            stamped = read_stamp(stamp)
            self.assertIn("fired_at", stamped)
            self.assertIsInstance(stamped["fired_at_mtime"], float)
            self.assertAlmostEqual(stamped["fired_at_mtime"], stamp.stat().st_mtime, places=3)
            self.assertEqual(Path(str(stamp) + ".stdin").read_text(), '{"hook_event_name":"SessionStart"}')

    def test_script_tolerates_a_closed_stdin(self):
        with tempfile.TemporaryDirectory() as d:
            cfg = Path(d) / "cfg"
            cfg.mkdir()
            skill = ProbeSkill.fresh()
            target = Path(d) / "repo" / ".x" / skill.name / "SKILL.md"
            script = write_hook_script(cfg, target, skill.render(), stamp_path(cfg))
            proc = subprocess.run(["sh", "-c", f"exec 0<&-; '{script}'"], capture_output=True, text=True, timeout=15)
            self.assertEqual(proc.returncode, 0, proc.stderr)
            self.assertEqual(proc.stderr, "")
            self.assertEqual(target.read_text(), skill.render())
            self.assertIsNotNone(read_stamp(stamp_path(cfg)))

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

    def test_print_driver_reports_extraction_failure(self):
        with tempfile.TemporaryDirectory() as d:
            def boom(raw):
                raise ValueError("not json")

            res = print_ask(["sh", "-c", "echo not-json"], Path(d), dict(os.environ),
                            Path(d) / "x.stderr.log", timeout=10, extract=boom)
            self.assertTrue(res.notes.startswith("extract failed"), res.notes)
            self.assertEqual(res.reply_text, "not-json\n")

    def test_adapter_defaults(self):
        self.assertIsNone(Adapter.check_auth(FakeAdapter(), None))
        self.assertIsNone(Adapter.install_registration(FakeAdapter(), None, None, None))
        self.assertIsNone(Adapter.install_startup_hook(FakeAdapter(), None, None))
        self.assertEqual(Adapter.credential_files, ())
        self.assertEqual(Adapter.passthrough_env, ())
        with self.assertRaises(TypeError):
            Adapter.extra_env["leak"] = "1"

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


import probe  # noqa: E402
from lib.recorder import load_runs as _load  # noqa: E402


class _NoFireAdapter(FakeAdapter):
    """A hook that is installed but never runs, so the skill file never lands."""

    def install_startup_hook(self, sb, script):
        return HookInfo(mechanism="fake-startup", config_path=str(script))


class _CountingAdapter(FakeAdapter):
    def __init__(self):
        super().__init__()
        self.calls = 0

    def ask(self, sb, mode, prompt):
        self.calls += 1
        return super().ask(sb, mode, prompt)


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


class _RaisingAdapter(FakeAdapter):
    def ask(self, sb, mode, prompt):
        raise RuntimeError("vendor exploded")


class _NoHookAdapter(FakeAdapter):
    def install_startup_hook(self, sb, script):
        return None


class _LeakyReaderAdapter(FakeAdapter):
    """Reads .agents/skills without documenting it."""

    documented_roots = frozenset({".fake/skills"})


class _PreIgnoredAdapter(FakeAdapter):
    """The native root is already ignored, so the none arm cannot prove the path is inside the repo."""

    def __init__(self):
        super().__init__()
        self.calls = 0

    def prepare(self, sb):
        (sb.repo / ".gitignore").write_text(".fake/skills/\n")
        git(sb.repo, "add", ".gitignore")
        git(sb.repo, "commit", "-q", "-m", "ignore the skills root")

    def ask(self, sb, mode, prompt):
        self.calls += 1
        return super().ask(sb, mode, prompt)


class _StderrAdapter(FakeAdapter):
    def ask(self, sb, mode, prompt):
        log = sb.root / "fake.stderr.log"
        log.write_text("vendor noise\n")
        res = super().ask(sb, mode, prompt)
        res.stderr_path = str(log)
        return res


class _NoBinaryAdapter(FakeAdapter):
    entry = "nobin"

    def binary_path(self):
        return None


class _RaisingStderrAdapter(FakeAdapter):
    """The vendor wrote its complaint to stderr before the driver gave up."""

    def ask(self, sb, mode, prompt):
        (sb.root / "fake.stderr.log").write_text("boom\n")
        raise RuntimeError("vendor exploded")


class _FailingVendorAdapter(FakeAdapter):
    """The vendor exited non-zero without answering; its stderr carries the reason."""

    def ask(self, sb, mode, prompt):
        log = sb.root / "fake.stderr.log"
        log.write_text("IneligibleTierError: this client is no longer supported\n")
        return AskResult(reply_text="", raw="", argv=["fake"], started_at=0.0, first_request_at=0.0,
                         stderr_path=str(log), exit_code=41)


class _OddLogNameAdapter(FakeAdapter):
    """A driver that names its stderr file outside the *.stderr.log pattern."""

    def ask(self, sb, mode, prompt):
        log = sb.root / "vendor.log"
        log.write_text("odd noise\n")
        res = super().ask(sb, mode, prompt)
        res.stderr_path = str(log)
        return res


class _LeakyControlAdapter(FakeAdapter):
    """Every reply carries a token, so S0 cannot act as a negative control."""

    def __init__(self):
        super().__init__()
        self.skill = ProbeSkill.fresh()

    def ask(self, sb, mode, prompt):
        text = self.skill.body_token
        return AskResult(reply_text=text, raw=text, argv=["fake"], started_at=0.0,
                         first_request_at=0.0, stderr_path=None, exit_code=0)


class RunnerTests(unittest.TestCase):
    def _runner(self, d, adapter=None, runs=1):
        return probe.Runner(adapter or FakeAdapter(), Path(d) / "out", runs=runs, base=Path(d))

    def test_s0_and_s1(self):
        with tempfile.TemporaryDirectory() as d:
            r = self._runner(d, runs=2)
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
            self.assertIsNotNone(by_arm["S2/hook-creates-root"][0].hook["fired_at_mtime"])
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
            r = self._runner(d, runs=2)
            r.run_scenario("print", "S1")
            recs = r.run_scenario("print", "S4")
            all_rows = [x for x in recs if x.arm == "S4/all-roots"]
            expected_keys = set(probe.ALL_ROOTS) | {"native_fake"}
            self.assertEqual(len(all_rows), 2 * len(expected_keys))
            self.assertEqual({next(iter(x.expected_tokens)) for x in all_rows}, expected_keys)
            self.assertEqual({len(x.expected_tokens) for x in all_rows}, {1})
            per_root = {}
            for x in all_rows:
                per_root.setdefault(x.root, set()).add(x.verdict)
            self.assertEqual(len(per_root), len(expected_keys))
            self.assertEqual(per_root[".fake/skills"], {"visible_first_turn"})
            self.assertEqual(per_root[".agents/skills"], {"visible_first_turn"})
            self.assertEqual(per_root[".claude/skills"], {"not_visible"})
            self.assertIn("found=['agents', 'native_fake'] leaked=[]", all_rows[0].notes)
            confirms = [x for x in recs if x.arm.startswith("S4/confirm-")]
            self.assertEqual(len(confirms), len(expected_keys) - 2)
            self.assertEqual(len({x.arm for x in confirms}), len(confirms))
            by_root = {x.root: x.verdict for x in confirms}
            self.assertEqual(by_root[".claude/skills"], "not_visible")
            self.assertNotIn(".fake/skills", by_root)
            self.assertNotIn(".agents/skills", by_root)

    def test_s4_leaked_root_gets_its_own_row(self):
        with tempfile.TemporaryDirectory() as d:
            r = probe.Runner(_LeakyReaderAdapter(), Path(d) / "out", runs=1, base=Path(d))
            recs = r.run_scenario("print", "S4")
            rows = {x.root: x for x in recs if x.arm == "S4/all-roots"}
            self.assertEqual(rows[".agents/skills"].verdict, "leaked")
            self.assertEqual(rows[".fake/skills"].verdict, "visible_first_turn")
            self.assertIn("leaked=['agents']", rows[".agents/skills"].notes)
            self.assertNotIn(".agents/skills", {x.root for x in recs if x.arm.startswith("S4/confirm-")})

    def test_s1_skips_the_turn_when_the_skill_is_already_ignored(self):
        with tempfile.TemporaryDirectory() as d:
            a = _PreIgnoredAdapter()
            r = probe.Runner(a, Path(d) / "out", runs=2, base=Path(d))
            recs = r.run_scenario("print", "S1")
            self.assertEqual([x.verdict for x in recs], ["untested", "untested"])
            self.assertIn("git state:", recs[0].notes)
            self.assertEqual(a.calls, 0)
            self.assertFalse(r.s1_ok["print"])

    def test_s2_without_a_startup_hook_is_untested(self):
        with tempfile.TemporaryDirectory() as d:
            r = probe.Runner(_NoHookAdapter(), Path(d) / "out", runs=1, base=Path(d))
            recs = r.run_scenario("print", "S2", arms=["hook-creates-root"])
            self.assertEqual([x.verdict for x in recs], ["untested"])
            self.assertEqual(recs[0].notes, "no startup hook mechanism for this entry")

    def test_s1_gate_blocks_later_scenarios(self):
        with tempfile.TemporaryDirectory() as d:
            r = self._runner(d)
            r.s1_ok["print"] = False
            recs = r.run_scenario("print", "S3")
            self.assertEqual([x.verdict for x in recs], ["untested", "untested"])
            self.assertEqual(recs[0].notes, "S1 failed")

    def test_s2_hook_never_fired_is_untested(self):
        with tempfile.TemporaryDirectory() as d:
            r = probe.Runner(_NoFireAdapter(), Path(d) / "out", runs=2, base=Path(d))
            r.run_scenario("print", "S1")
            recs = r.run_scenario("print", "S2", arms=["hook-creates-root"])
            self.assertEqual({x.verdict for x in recs}, {"untested"})
            for x in recs:
                self.assertIn("hook never fired", x.notes)
                self.assertIn("skill file absent after the turn", x.notes)

    def test_stderr_log_survives_sandbox_cleanup(self):
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "out"
            r = probe.Runner(_StderrAdapter(), out, runs=1, base=Path(d))
            recs = r.run_scenario("print", "S1")
            copied = out / "fake" / "print" / "S1" / "S1_native" / "run1.fake.stderr.log"
            self.assertEqual(copied.read_text(), "vendor noise\n")
            self.assertEqual(recs[0].stderr_path, str(copied))
            written = json.loads((copied.parent / "run1.json").read_text())
            self.assertEqual(written["stderr_path"], str(copied))

    def test_arm_exception_is_untested(self):
        with tempfile.TemporaryDirectory() as d:
            r = probe.Runner(_RaisingAdapter(), Path(d) / "out", runs=2, base=Path(d))
            recs = r.run_scenario("print", "S1")
            self.assertEqual([x.verdict for x in recs], ["untested", "untested"])
            for x in recs:
                self.assertIn("exception=RuntimeError", x.notes)
            self.assertEqual(len(_load(Path(d) / "out")), 2)

    def test_completed_arm_resumes_and_rerun_clears(self):
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "out"
            a = _CountingAdapter()
            first = probe.Runner(a, out, runs=2, base=Path(d)).run_scenario("print", "S1")
            self.assertEqual(a.calls, 2)
            again = probe.Runner(a, out, runs=2, base=Path(d)).run_scenario("print", "S1")
            self.assertEqual(a.calls, 2)
            self.assertEqual([x.reply for x in again], [x.reply for x in first])
            self.assertEqual(len(_load(out)), 2)
            probe.Runner(a, out, runs=2, base=Path(d), rerun=True).run_scenario("print", "S1")
            self.assertEqual(a.calls, 4)
            self.assertEqual(len(_load(out)), 2)

    def test_untested_arm_is_rerun_not_latched(self):
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "out"
            probe.Runner(_RaisingAdapter(), out, runs=2, base=Path(d)).run_scenario("print", "S1")
            self.assertEqual({x.verdict for x in _load(out)}, {"untested"})
            a = _CountingAdapter()
            recs = probe.Runner(a, out, runs=2, base=Path(d)).run_scenario("print", "S1")
            self.assertEqual([x.verdict for x in recs], ["visible_first_turn"] * 2)
            self.assertEqual(a.calls, 2)
            self.assertEqual(len(_load(out)), 2)

    def test_partial_arm_is_completed_on_resume(self):
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "out"
            a = _CountingAdapter()
            probe.Runner(a, out, runs=1, base=Path(d)).run_scenario("print", "S1")
            self.assertEqual(a.calls, 1)
            recs = probe.Runner(a, out, runs=2, base=Path(d)).run_scenario("print", "S1")
            self.assertEqual(a.calls, 2)
            self.assertEqual(len(recs), 2)
            self.assertEqual(len(_load(out)), 2)

    def test_gated_rows_are_idempotent(self):
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "out"
            for _ in range(2):
                r = probe.Runner(FakeAdapter(), out, runs=2, base=Path(d))
                r.s1_ok["print"] = False
                recs = r.run_scenario("print", "S3")
                self.assertEqual([x.verdict for x in recs], ["untested", "untested"])
            self.assertEqual(len(_load(out)), 2)

    def test_blocked_rows_carry_the_current_reason(self):
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "out"
            first = probe.Runner(FakeAdapter(), out, runs=2, base=Path(d))
            first.record_blocked("print", "binary not installed")
            second = probe.Runner(FakeAdapter(), out, runs=2, base=Path(d))
            second.s1_ok["print"] = False
            recs = second.run_scenario("print", "S3")
            self.assertEqual({x.notes for x in recs}, {"S1 failed"})
            self.assertEqual(len([r for r in _load(out) if r.scenario == "S3"]), 2)

    def test_exception_path_keeps_stderr_and_tears_down(self):
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "out"
            recs = probe.Runner(_RaisingStderrAdapter(), out, runs=1, base=Path(d)).run_scenario("print", "S1")
            copied = out / "fake" / "print" / "S1" / "S1_native" / "run1.fake.stderr.log"
            self.assertEqual(copied.read_text(), "boom\n")
            self.assertEqual(recs[0].stderr_path, str(copied))
            self.assertEqual([p.name for p in Path(d).iterdir() if p.name.startswith("skprobe-")], [])

    def test_failed_vendor_run_is_untested_with_its_stderr(self):
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "out"
            recs = probe.Runner(_FailingVendorAdapter(), out, runs=1, base=Path(d)).run_scenario("print", "S1")
            self.assertEqual(recs[0].verdict, "untested")
            self.assertIn("vendor exit 41", recs[0].notes)
            self.assertIn("IneligibleTierError", recs[0].notes)
            self.assertTrue(Path(recs[0].stderr_path).exists())

    def test_protocol_failure_without_a_reply_is_untested(self):
        class _ProtocolFailure(FakeAdapter):
            def ask(self, sb, mode, prompt):
                return AskResult(reply_text="", raw="[]", argv=["fake", "acp"], started_at=0.0, first_request_at=0.0,
                                 stderr_path=None, exit_code=None, notes='session/new failed: {"error": "no key"}')
        with tempfile.TemporaryDirectory() as d:
            recs = probe.Runner(_ProtocolFailure(), Path(d) / "out", runs=1, base=Path(d)).run_scenario("print", "S1")
            self.assertEqual(recs[0].verdict, "untested")
            self.assertIn("session/new failed", recs[0].notes)

    def test_odd_log_name_is_still_kept(self):
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "out"
            recs = probe.Runner(_OddLogNameAdapter(), out, runs=1, base=Path(d)).run_scenario("print", "S1")
            copied = out / "fake" / "print" / "S1" / "S1_native" / "run1.vendor.log"
            self.assertEqual(copied.read_text(), "odd noise\n")
            self.assertEqual(recs[0].stderr_path, str(copied))

    def test_s4_partial_pass_is_redone(self):
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "out"
            a = _CountingAdapter()
            probe.Runner(a, out, runs=1, base=Path(d)).run_scenario("print", "S4")
            arm_dir = out / "fake" / "print" / "S4" / "S4_all-roots"
            files = sorted(arm_dir.glob("run*.json"))
            self.assertEqual(len(files), len(probe.ALL_ROOTS) + 1)
            for f in files[1:]:
                f.unlink()
            calls = a.calls
            probe.Runner(a, out, runs=1, base=Path(d)).run_scenario("print", "S4")
            self.assertEqual(len(list(arm_dir.glob("run*.json"))), len(files))
            self.assertEqual(a.calls, calls + 1)

    def test_s0_prompt_design_failure_keeps_evidence(self):
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "out"
            a = _LeakyControlAdapter()
            r = probe.Runner(a, out, runs=2, base=Path(d))
            with self.assertRaises(PromptDesignFailure):
                r.run_scenario("print", "S0")
            recs = _load(out)
            self.assertEqual(len(recs), 1)
            self.assertEqual(recs[0].verdict, "untested")
            self.assertIn(a.skill.body_token, recs[0].reply)
            self.assertIn("prompt design failure", recs[0].notes)

    def test_third_run_on_disagreement(self):
        with tempfile.TemporaryDirectory() as d:
            r = self._runner(d, runs=2)
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
            recs = r.run_arm(flaky, "print", "S1", "S1/native", None, "none")
            self.assertEqual(len(recs), 3)

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
        with tempfile.TemporaryDirectory() as d:
            recs = self._runner(d, _NoFireAdapter()).run_scenario("print", "S6")
            self.assertEqual({r.verdict for r in recs}, {"untested"})
            self.assertTrue(all("hook never fired" in r.notes for r in recs))

    def test_s7_resume(self):
        with tempfile.TemporaryDirectory() as d:
            recs = self._runner(d).run_scenario("print", "S7")
            self.assertEqual({r.arm: r.verdict for r in recs}, {"S7/add": "visible_first_turn", "S7/update": "visible_first_turn"})
            self.assertTrue(all("session=fake-session" in r.notes and "--resume" in r.argv for r in recs))
        with tempfile.TemporaryDirectory() as d:
            a = _NoResumeAdapter()
            recs = self._runner(d, a).run_scenario("print", "S7")
            self.assertEqual({r.verdict for r in recs}, {"untested"})
            self.assertEqual(a.calls, 0)

    def test_s8_nested_cwd(self):
        with tempfile.TemporaryDirectory() as d:
            recs = self._runner(d).run_scenario("print", "S8")
            self.assertEqual({r.arm: r.verdict for r in recs}, {"S8/ancestor": "not_visible", "S8/local": "visible_first_turn"})
            self.assertTrue(all("cwd=sub/dir" in r.notes for r in recs))
        with tempfile.TemporaryDirectory() as d:
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
        with tempfile.TemporaryDirectory() as d:
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

    def test_cli_records_untested_rows_without_a_binary(self):
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "out"
            with mock.patch.dict(probe.ENTRIES, {"nobin": _NoBinaryAdapter}):
                code = probe.main(["--harness", "nobin", "--mode", "print", "--turn",
                                   "--outdir", str(out), "--base", d])
            self.assertEqual(code, 0)
            recs = _load(out)
            self.assertEqual(len(recs), 16)
            with mock.patch.dict(probe.ENTRIES, {"nobin": _NoBinaryAdapter}):
                probe.main(["--harness", "nobin", "--mode", "print", "--turn",
                            "--outdir", str(out), "--base", d])
            self.assertEqual(len(_load(out)), 16)
            self.assertEqual({r.verdict for r in recs}, {"untested"})
            self.assertEqual({r.notes for r in recs}, {"binary not installed"})
            self.assertEqual({r.arm for r in recs}, {
                "S0/none", "S1/native", "S2/hook-creates-root", "S2/hook-adds-skill", "S2/registration",
                "S3/gitignore", "S3/info-exclude", "S4/all-roots", "S6/update", "S6/delete", "S7/add",
                "S7/update", "S8/ancestor", "S8/local", "S9/linked-own", "S9/linked-other"})

    def test_cli_stops_the_entry_on_a_prompt_design_failure(self):
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "out"
            with mock.patch.dict(probe.ENTRIES, {"leaky": _LeakyControlAdapter}):
                code = probe.main(["--harness", "leaky", "--mode", "print", "--turn", "--scenario", "S0",
                                   "--outdir", str(out), "--base", d])
            self.assertEqual(code, 1)
            self.assertEqual([r.verdict for r in _load(out)], ["untested"])

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


from lib.acp_driver import acp_ask  # noqa: E402

SERVERS = KIT / "selftest_servers.py"


def _fake_repo(d: str, skill: ProbeSkill | None) -> Path:
    repo = Path(d) / "repo"
    if skill is not None:
        write_skill(repo / ".fake" / "skills", skill)
    else:
        repo.mkdir(parents=True, exist_ok=True)
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
            self.assertIn("tools_used=0", res.notes)
            self.assertGreaterEqual(res.first_request_at, res.started_at)
            self.assertEqual(res.argv[-1], "acp")

    def test_grandchild_holding_stdout_does_not_wedge_shutdown(self):
        with tempfile.TemporaryDirectory() as d:
            skill = ProbeSkill.fresh()
            repo = _fake_repo(d, skill)
            env = dict(os.environ, KCAP_FAKE_GRANDCHILD="1")
            started = time.time()
            res = acp_ask([sys.executable, str(SERVERS), "acp"], repo, env, single_prompt(skill),
                          Path(d) / "acp.stderr.log", timeout=30)
            self.assertIn(skill.body_token, res.reply_text)
            self.assertLess(time.time() - started, 20)

    def test_tool_call_updates_are_counted(self):
        with tempfile.TemporaryDirectory() as d:
            skill = ProbeSkill.fresh()
            repo = _fake_repo(d, skill)
            env = dict(os.environ, KCAP_FAKE_TOOL_CALL="1")
            res = acp_ask([sys.executable, str(SERVERS), "acp"], repo, env, single_prompt(skill),
                          Path(d) / "acp.stderr.log", timeout=30)
            self.assertIn("tools_used=1", res.notes)
            self.assertIn(skill.body_token, res.reply_text)

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
                self.assertIn("initialize", first.raw)
                self.assertNotIn("initialize", second.raw)
                self.assertEqual(second.raw.count("session/prompt"), 1)
            finally:
                s.close()


from lib.appserver_driver import appserver_ask, hook_state_override  # noqa: E402
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
            self.assertIn("tools_used=0", res.notes)
            self.assertEqual(res.argv[:2], [str(SERVERS), "app-server"])

    def test_pirpc_turn(self):
        with tempfile.TemporaryDirectory() as d:
            skill = ProbeSkill.fresh()
            repo = _fake_repo(d, skill)
            res = pirpc_ask([sys.executable, str(SERVERS), "pirpc"], repo, dict(os.environ), single_prompt(skill),
                            Path(d) / "pi.stderr.log", timeout=30)
            self.assertIn(skill.body_token, res.reply_text)
            self.assertIn("tools_used=0", res.notes)

    def test_tool_events_are_counted(self):
        with tempfile.TemporaryDirectory() as d:
            skill = ProbeSkill.fresh()
            repo = _fake_repo(d, skill)
            env = dict(os.environ, KCAP_FAKE_TOOL_CALL="1")
            served = appserver_ask(str(SERVERS), repo, env, single_prompt(skill),
                                   Path(d) / "as.stderr.log", timeout=30)
            self.assertIn("tools_used=1", served.notes)
            self.assertIn(skill.body_token, served.reply_text)
            piped = pirpc_ask([sys.executable, str(SERVERS), "pirpc"], repo, env, single_prompt(skill),
                              Path(d) / "pi.stderr.log", timeout=30)
            self.assertIn("tools_used=1", piped.notes)
            self.assertIn(skill.body_token, piped.reply_text)

    def test_hook_state_override(self):
        untrusted = [{"key": "/x/hooks.json:SessionStart:0:0", "trustStatus": "untrusted",
                      "currentHash": "sha256:abc"}]
        self.assertEqual(hook_state_override(untrusted),
                         'hooks.state={"/x/hooks.json:SessionStart:0:0"={trusted_hash="sha256:abc"}}')
        trusted = [{"key": "/x/hooks.json:SessionStart:0:0", "trustStatus": "trusted",
                    "currentHash": "sha256:abc"}]
        self.assertIsNone(hook_state_override(trusted))
        no_hash = [{"key": "/x/hooks.json:SessionStart:0:0", "trustStatus": "untrusted"}]
        self.assertIsNone(hook_state_override(no_hash))

    def test_appserver_seeds_hook_trust(self):
        with tempfile.TemporaryDirectory() as d:
            skill = ProbeSkill.fresh()
            repo = _fake_repo(d, skill)
            env = dict(os.environ)
            env["KCAP_FAKE_UNTRUSTED_HOOK"] = "1"
            res = appserver_ask(str(SERVERS), repo, env, single_prompt(skill), Path(d) / "seed.stderr.log", timeout=30)
            self.assertIn("hook_trust=seeded", res.notes)
            idx = res.argv.index("-c")
            self.assertTrue(res.argv[idx + 1].startswith("hooks.state="))
            self.assertIn(skill.body_token, res.reply_text)
            self.assertIn("hooks/list", res.raw)

    def test_grandchild_holding_stdout_does_not_wedge_the_driver(self):
        with tempfile.TemporaryDirectory() as d:
            skill = ProbeSkill.fresh()
            repo = _fake_repo(d, skill)
            env = dict(os.environ, KCAP_FAKE_GRANDCHILD="1")
            started = time.time()
            res = pirpc_ask([sys.executable, str(SERVERS), "pirpc"], repo, env, single_prompt(skill),
                            Path(d) / "pi.stderr.log", timeout=30)
            self.assertIn(skill.body_token, res.reply_text)
            self.assertLess(time.time() - started, 20)

    def test_spawn_failure_is_reported(self):
        with tempfile.TemporaryDirectory() as d:
            res = pirpc_ask(["/nonexistent/binary"], Path(d), dict(os.environ), "x",
                            Path(d) / "sf.stderr.log", timeout=5)
            self.assertIsNone(res.exit_code)
            self.assertIn("exception=", res.notes)

    def test_appserver_session_takes_two_turns(self):
        from lib.appserver_driver import AppServerSession
        with tempfile.TemporaryDirectory() as d:
            skill = ProbeSkill.fresh()
            repo = _fake_repo(d, None)
            s = AppServerSession(str(SERVERS), repo, dict(os.environ), Path(d) / "as.stderr.log", timeout=30)
            s.start()
            try:
                first = s.ask(single_prompt(skill))
                self.assertIn(NO_SKILL, first.reply_text)
                write_skill(repo / ".fake" / "skills", skill)
                second = s.ask(single_prompt(skill))
                self.assertIn(skill.body_token, second.reply_text)
                self.assertIn("thread/start", first.raw)
                self.assertNotIn("thread/start", second.raw)
                self.assertEqual(second.raw.count("turn/start"), 1)
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
                second = s.ask(single_prompt(skill))
                self.assertIn(skill.body_token, second.reply_text)
                self.assertEqual(second.raw.count('"type": "prompt"'), 1)
            finally:
                s.close()


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
                self.assertNotIn("dialog=", s.ask(tui_prompt(skill)).notes)
            finally:
                s.close()

    def test_spawn_failure_releases_the_terminal(self):
        with tempfile.TemporaryDirectory() as d:
            s = PtySession(["/nonexistent/kcap-probe-binary"], Path(d), dict(os.environ), Path(d) / "t.log",
                           ready_idle=0.3, timeout=2)
            with self.assertRaises(OSError):
                s.start()
            s.close()
            self.assertTrue(s._log.closed)
            with self.assertRaises(OSError):
                os.fstat(s.master)

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


from harness import ENTRIES  # noqa: E402

# Adapters that probe a real binary at construction are pinned to one mode here, so the
# self-tests neither spawn a vendor nor depend on which one is installed.
ADAPTER_TEST_ENV = {"KCAP_PROBE_CLAUDE_REAL_CONFIG": "0"}


class AdapterHookFilesTests(unittest.TestCase):
    def test_every_adapter_writes_a_hook_referencing_the_script(self):
        for name, cls in ENTRIES.items():
            if name == "fake":
                continue
            with self.subTest(entry=name), tempfile.TemporaryDirectory() as d, \
                    mock.patch.dict(os.environ, dict(ADAPTER_TEST_ENV, HOME=d)):
                a = cls()
                sb = new_sandbox(a.lever, None, [], base=Path(d))
                a.prepare(sb)
                script = sb.config_root / "probe-hook.sh"
                script.write_text("#!/bin/sh\nexit 0\n")
                info = a.install_startup_hook(sb, script)
                try:
                    self.assertIsNotNone(info, name)
                    self.assertTrue(Path(info.config_path).exists(), info)
                    self.assertIn(str(script), Path(info.config_path).read_text())
                    self.assertTrue(info.mechanism)
                    self.assertIn(a.native_root, a.documented_roots, name)
                finally:
                    # An adapter that must write into the real home removes its hook after a turn.
                    getattr(a, "cleanup_hook", lambda _sb: None)(sb)


class ClaudeAdapterTests(unittest.TestCase):
    def test_isolated_mode_trusts_the_repo(self):
        from harness.claude import ClaudeAdapter
        with tempfile.TemporaryDirectory() as d, mock.patch.dict(os.environ, {"KCAP_PROBE_CLAUDE_REAL_CONFIG": "0"}):
            a = ClaudeAdapter()
            sb = new_sandbox(a.lever, None, [], base=Path(d))
            a.prepare(sb)
            cfg = json.loads((sb.config_root / ".claude.json").read_text())
            self.assertTrue(cfg["projects"][str(sb.repo)]["hasTrustDialogAccepted"])
            argv = a.print_argv(sb, "hi")
            self.assertEqual(argv[1:3], ["-p", "hi"])
            self.assertEqual(argv[-2:], ["--setting-sources", "user"])
            self.assertNotIn("KCAP_SKIP", sb.env)

    def test_real_mode_uses_the_real_root_with_settings_flag(self):
        from harness.claude import ClaudeAdapter
        with tempfile.TemporaryDirectory() as d, mock.patch.dict(os.environ, {"KCAP_PROBE_CLAUDE_REAL_CONFIG": "1"}):
            a = ClaudeAdapter()
            sb = new_sandbox(a.lever, None, [], base=Path(d))
            a.prepare(sb)
            self.assertNotIn("CLAUDE_CONFIG_DIR", sb.env)
            self.assertEqual(sb.env["KCAP_SKIP"], "1")
            argv = a.print_argv(sb, "hi")
            self.assertIn("--settings", argv)
            self.assertEqual(argv[argv.index("--settings") + 1], str(sb.config_root / "settings.json"))
            self.assertEqual(argv[argv.index("--setting-sources") + 1], "project")
            info = a.install_startup_hook(sb, sb.config_root / "probe-hook.sh")
            self.assertIn("real config root", info.mechanism)


class CodexAdapterTests(unittest.TestCase):
    def test_prepare_trusts_repo_and_print_argv(self):
        from harness.codex import CodexAdapter
        with tempfile.TemporaryDirectory() as d:
            a = CodexAdapter()
            sb = new_sandbox(a.lever, None, [], base=Path(d))
            a.prepare(sb)
            toml = (sb.config_root / "config.toml").read_text()
            self.assertIn(f'[projects."{sb.repo}"]', toml)
            self.assertIn('trust_level = "trusted"', toml)
            info = a.install_startup_hook(sb, sb.config_root / "probe-hook.sh")
            hooks = json.loads(Path(info.config_path).read_text())
            self.assertEqual(hooks["hooks"]["SessionStart"][0]["hooks"][0]["command"], str(sb.config_root / "probe-hook.sh"))

    def test_tool_items_distinguish_a_listed_read_from_a_search(self):
        from harness.codex import appserver_items, classify_tool_items, exec_items
        lines = [
            json.dumps({"type": "item.completed", "item": {"type": "agent_message", "text": "hi"}}),
            json.dumps({"type": "item.completed", "item": {"type": "command_execution",
                                                            "command": "/bin/zsh -lc 'cat /r/.agents/skills/kcap-probe-1/SKILL.md'"}}),
            json.dumps({"type": "item.completed", "item": {"type": "command_execution",
                                                            "command": "/bin/zsh -lc 'find / -name SKILL.md'"}}),
            json.dumps({"type": "item.completed", "item": {"type": "command_execution", "command": "date"}}),
        ]
        self.assertEqual(classify_tool_items(exec_items("\n".join(lines))), "tools_used=2 skill_reads=1 searches=1")
        frames = [{"frame": {"method": "item/completed", "params": {"item": {"type": "commandExecution",
                                                                              "command": "cat /r/.agents/skills/x/SKILL.md"}}}},
                  {"frame": {"method": "item/completed", "params": {"item": {"type": "agentMessage", "text": "t"}}}}]
        self.assertEqual(classify_tool_items(appserver_items(json.dumps(frames))), "tools_used=0 skill_reads=1 searches=0")


class CopilotAdapterTests(unittest.TestCase):
    def test_skill_tool_is_the_native_load(self):
        from harness.copilot import acp_tool_calls, classify_copilot_tools, print_tool_calls
        lines = [json.dumps({"type": "tool.execution_start", "data": {"toolName": "skill", "arguments": {"skill": "x"}}}),
                 json.dumps({"type": "tool.execution_start", "data": {"toolName": "bash", "arguments": {"command": "find"}}}),
                 json.dumps({"type": "assistant.message", "data": {"content": "hi"}})]
        self.assertEqual(classify_copilot_tools(print_tool_calls("\n".join(lines))),
                         "tools_used=1 skill_loads=1 searches=1")
        frames = [{"frame": {"method": "session/update", "params": {"update": {
            "sessionUpdate": "tool_call", "title": "Using skill: kcap-probe-1", "kind": "other"}}}},
                  {"frame": {"method": "session/update", "params": {"update": {
                      "sessionUpdate": "tool_call", "title": "Run command", "kind": "execute"}}}}]
        self.assertEqual(classify_copilot_tools(acp_tool_calls(json.dumps(frames))),
                         "tools_used=1 skill_loads=1 searches=0")


def _row(entry, scenario, arm, root, verdict, mode, mechanism=None):
    return dict(entry=entry, harness=entry, version="1.0", os="o", mode=mode, scenario=scenario, arm=arm,
                root=root, exclusion="none", verdict=verdict, flaky=False, runs=2, mechanism=mechanism,
                evidence=[], notes="")


class ReportTests(unittest.TestCase):
    def test_summary_columns(self):
        import report
        base = dict(entry="x", harness="x", version="1.0", os="o", mode="print", exclusion="none",
                    flaky=False, runs=2, mechanism=None, evidence=[], notes="")
        rows = [
            dict(base, scenario="S1", arm="S1/native", root=".x/skills", verdict="visible_first_turn"),
            dict(base, scenario="S2", arm="S2/hook-creates-root", root=".x/skills", verdict="not_visible", mechanism="hook"),
            dict(base, scenario="S2", arm="S2/registration", root=".x/skills", verdict="visible_after_reload", mechanism="ext"),
            dict(base, scenario="S3", arm="S3/gitignore", root=".x/skills", verdict="visible_first_turn", exclusion="gitignore"),
            dict(base, scenario="S3", arm="S3/info-exclude", root=".x/skills", verdict="not_visible", exclusion="info-exclude"),
            dict(base, scenario="S4", arm="S4/all-roots", root=".x/skills", verdict="visible_first_turn"),
            dict(base, scenario="S4", arm="S4/all-roots", root=".agents/skills", verdict="visible_first_turn"),
            dict(base, scenario="S4", arm="S4/all-roots", root=".y/skills", verdict="leaked"),
            dict(base, entry="y", scenario="S4", arm="S4/all-roots", root=".agents/skills", verdict="visible_first_turn"),
            dict(base, entry="y", scenario="S1", arm="S1/native", root=".y/skills", verdict="untested", notes="binary not installed"),
        ]
        summary = {s["Entry"]: s for s in report.summarise(rows)}
        x = summary["x"]
        self.assertEqual(x["Roots consumed"], ".agents/skills, .x/skills (undocumented: .y/skills)")
        self.assertEqual(x["Startup mechanism proven"], "ext (print)")
        self.assertEqual(x["Exclusion preserving load"], "gitignore")
        self.assertEqual(x["Vendor-isolated destination"], ".x/skills")
        self.assertEqual(x["Reload path"], "ext")
        self.assertEqual(x["_status"], "measured")
        self.assertTrue(summary["y"]["_status"].startswith("untested: binary not installed"))
        self.assertEqual(summary["y"]["Minimum version"], "—")
        text = report.render(list(summary.values()))
        self.assertIn("| x | 1.0 |", text)

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
            _row("y", "S1", "S1/native", ".y/skills", "visible_first_turn", "print"),
            _row("y", "S5", "S5/add", ".y/skills", "visible_live", "tui"),
        ]
        import report
        by_entry = {r["Entry"]: r for r in report.summarise(rows)}
        # S5 ran only interactively: the daemon-mode column must not read as a measured "none".
        self.assertEqual(by_entry["y"]["Live catalogue"], "n/a (not run)")
        self.assertEqual(by_entry["y"]["Interactive"], "add=visible_live")
        s = by_entry["x"]
        self.assertEqual(s["Live catalogue"], "add=visible_live; delete=revoked")
        self.assertEqual(s["Startup rewrite"], "update=stale")
        self.assertEqual(s["Resume"], "n/a (not run)")
        self.assertEqual(s["Nested cwd"], "n/a (not run)")
        self.assertEqual(s["Worktree"], "linked-other=not_visible")
        self.assertEqual(s["Interactive"], "S1=visible_first_turn; reload=visible_after_reload")
        self.assertIn("/reload", s["Reload path"])
        self.assertIn("tui", s["Modes"])


class CursorAdapterTests(unittest.TestCase):
    def test_acp_read_of_the_listed_file_is_the_native_load(self):
        from harness.cursor import classify_cursor_tools
        def upd(**u):
            return {"frame": {"method": "session/update", "params": {"update": u}}}
        frames = [
            upd(sessionUpdate="tool_call", toolCallId="a", kind="read", title="Read File"),
            upd(sessionUpdate="tool_call_update", toolCallId="a", rawInput={"path": "/r/.cursor/skills/x/SKILL.md"}),
            upd(sessionUpdate="tool_call", toolCallId="b", kind="execute", title="Run command", rawInput={"command": "find"}),
            upd(sessionUpdate="agent_message_chunk", content={"type": "text", "text": "hi"}),
        ]
        self.assertEqual(classify_cursor_tools(json.dumps(frames)), "tools_used=1 skill_reads=1 searches=1")

    def test_print_stream_read_of_the_listed_file_is_the_native_load(self):
        from harness.cursor import classify_cursor_stream
        lines = [
            json.dumps({"type": "tool_call", "subtype": "started", "call_id": "a",
                        "tool_call": {"readToolCall": {"args": {"path": "/r/.cursor/skills/x/SKILL.md"}}}}),
            json.dumps({"type": "tool_call", "subtype": "completed", "call_id": "a",
                        "tool_call": {"readToolCall": {"args": {"path": "/r/.cursor/skills/x/SKILL.md"}}}}),
            json.dumps({"type": "tool_call", "subtype": "started", "call_id": "b",
                        "tool_call": {"shellToolCall": {"args": {"command": "find / -name SKILL.md"}}}}),
            json.dumps({"type": "assistant", "message": {"content": [{"type": "text", "text": "hi"}]}}),
        ]
        self.assertEqual(classify_cursor_stream("\n".join(lines)), "tools_used=1 skill_reads=1 searches=1")

    def test_user_hooks_variant_restores_the_real_file(self):
        from harness.cursor import CursorUserHooksAdapter
        with tempfile.TemporaryDirectory() as d, mock.patch.dict(os.environ, {"HOME": d}):
            hooks = Path(d) / ".cursor" / "hooks.json"
            hooks.parent.mkdir(parents=True)
            original = json.dumps({"version": 1, "hooks": {"sessionStart": [{"command": "kcap hook --cursor"}]}})
            hooks.write_text(original)
            a = CursorUserHooksAdapter()
            sb = new_sandbox(a.lever, None, [], base=Path(d))
            info = a.install_startup_hook(sb, sb.config_root / "probe-hook.sh")
            merged = json.loads(Path(info.config_path).read_text())
            self.assertEqual([h["command"] for h in merged["hooks"]["sessionStart"]],
                             ["kcap hook --cursor", str(sb.config_root / "probe-hook.sh")])
            a.cleanup_hook(sb)
            self.assertEqual(hooks.read_text(), original)


class KiroAdapterTests(unittest.TestCase):
    def test_acp_read_of_the_listed_file_is_the_native_load(self):
        from harness.kiro import classify_kiro_tools
        def upd(**u):
            return {"frame": {"method": "session/update", "params": {"update": u}}}
        frames = [
            upd(sessionUpdate="tool_call", toolCallId="a", kind="read", title="Reading SKILL.md:1",
                locations=[{"path": "/r/.kiro/skills/x/SKILL.md"}]),
            upd(sessionUpdate="tool_call_update", toolCallId="a", status="completed"),
            upd(sessionUpdate="tool_call", toolCallId="b", kind="execute", title="find / -name SKILL.md"),
        ]
        self.assertEqual(classify_kiro_tools(json.dumps(frames)), "tools_used=1 skill_reads=1 searches=1")

    def test_version_picks_the_hook_generation(self):
        from harness.kiro import KiroAdapter
        with tempfile.TemporaryDirectory() as d:
            a = KiroAdapter()
            a.version = lambda env=None: "kiro-cli 2.21.4"
            sb = new_sandbox(a.lever, None, [], base=Path(d))
            info = a.install_startup_hook(sb, sb.config_root / "probe-hook.sh")
            self.assertIn("agentSpawn", info.mechanism)
            self.assertEqual(json.loads((sb.config_root / "settings" / "cli.json").read_text())["chat.defaultAgent"], "probe")
            a.version = lambda env=None: "kiro-cli 3.0.1"
            info = a.install_startup_hook(sb, sb.config_root / "probe-hook.sh")
            self.assertIn("SessionStart", info.mechanism)
            self.assertEqual(json.loads(Path(info.config_path).read_text())["hooks"][0]["trigger"], "SessionStart")


class PiAdapterTests(unittest.TestCase):
    def test_registration_extension_names_the_root(self):
        from harness.pi import PiAdapter
        with tempfile.TemporaryDirectory() as d:
            a = PiAdapter()
            sb = new_sandbox(a.lever, None, [], base=Path(d))
            skill = ProbeSkill.fresh()
            target = a.skill_file(sb, a.native_root, skill.name)
            info = a.install_registration(sb, target, skill.render())
            ext = Path(info.config_path).read_text()
            self.assertIn("resources_discover", ext)
            self.assertIn(json.dumps(str(sb.repo / ".pi" / "skills")), ext)
            self.assertIn(json.dumps(str(sb.config_root / "probe-hook.sh")), ext)
            self.assertTrue((sb.config_root / "probe-hook.sh").exists())


if __name__ == "__main__":
    unittest.main()
