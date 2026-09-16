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


if __name__ == "__main__":
    unittest.main()
