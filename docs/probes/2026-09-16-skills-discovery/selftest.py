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


if __name__ == "__main__":
    unittest.main()
