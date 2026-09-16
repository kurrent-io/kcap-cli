#!/usr/bin/env python3
"""Skills discovery probes: does a repo-local skill written at startup reach the first model request?

Usage:
  probe.py [--harness ENTRY ...] [--mode print|daemon] [--scenario S0..S4 ...] [--turn]
           [--runs N] [--outdir DIR] [--keep] [--rerun] [--emit] [--matrix FILE] [--base DIR]

Without --turn only the free phase runs (binary, version, auth in an isolated root): zero model
requests. Each turn arm costs one model request per run.

An arm whose output directory already holds its runs is not run again: a sweep interrupted
halfway resumes where it stopped instead of re-spending the turns it already paid for. An arm
with fewer runs than asked for is topped up, and one carrying an `untested` row is measured
again, so a missing binary or a broken control never latches. --rerun deletes an arm's
directory before running it, which is how a settled arm is re-measured.
"""
from __future__ import annotations

import argparse
import json
import shutil
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
from lib.recorder import (  # noqa: E402
    RunRecord, emit_matrix, load_runs, next_run_path, os_label, run_dir, write_run,
)
from lib.verdict import (  # noqa: E402
    PromptDesignFailure, judge_control, judge_root, judge_single, needs_third_run, combine,
)

ALL_ROOTS: dict[str, str] = {
    "claude": ".claude/skills", "agents": ".agents/skills", "codex": ".codex/skills",
    "cursor": ".cursor/skills", "github": ".github/skills", "gemini": ".gemini/skills",
    "kiro": ".kiro/skills", "pi": ".pi/skills", "opencode": ".opencode/skills", "agent": ".agent/skills",
}
SCENARIOS = ("S0", "S1", "S2", "S3", "S4")
S2_ARMS = ("hook-creates-root", "hook-adds-skill", "registration")
S3_ARMS = ("gitignore", "info-exclude")
# Every arm a full sweep would run, so an entry that cannot run still gets a row per arm.
ALL_ARMS: tuple[tuple[str, str, bool, str], ...] = (
    ("S0", "S0/none", False, "none"),
    ("S1", "S1/native", True, "none"),
    *(("S2", f"S2/{a}", True, "none") for a in S2_ARMS),
    *(("S3", f"S3/{e}", True, e) for e in S3_ARMS),
    ("S4", "S4/all-roots", False, "none"),
)


def _verdicts_by_root(recs: list[RunRecord]) -> dict[str | None, str]:
    return {r.root: r.verdict for r in recs}


class Runner:
    def __init__(self, adapter: Adapter, outdir: Path, runs: int = 2, keep: bool = False,
                 base: Path | None = None, version: str | None = None, rerun: bool = False) -> None:
        self.adapter = adapter
        self.outdir = outdir
        self.runs = runs
        self.keep = keep
        self.base = base
        self.rerun = rerun
        self._cleared: set[Path] = set()
        self._sb: Sandbox | None = None
        self.s1_ok: dict[str, bool] = {}
        self._version = version if version is not None else adapter.version()
        self._binary = adapter.binary_path() or adapter.binary
        self._os = os_label()

    # -- sandbox plumbing ---------------------------------------------------------------------

    def sandbox(self) -> Sandbox:
        """The arm's sandbox; the guard that ran the arm tears it down, so a throwing arm still
        gets its stderr copied before the directory goes."""
        a = self.adapter
        sb = new_sandbox(a.lever, a.real_root(), list(a.credential_files), list(a.passthrough_env),
                         dict(a.extra_env), keep=self.keep, base=self.base)
        try:
            a.prepare(sb)
        except Exception:
            sb.cleanup()
            raise
        self._sb = sb
        return sb

    def _teardown(self) -> None:
        if self._sb is not None:
            self._sb.cleanup()
            self._sb = None

    def _s4_roots(self) -> dict[str, str]:
        a = self.adapter
        roots = dict(ALL_ROOTS)
        if a.native_root not in roots.values():
            roots[f"native_{a.entry}"] = a.native_root
        return roots

    def record(self, mode: str, scenario: str, arm: str, root: str | None, exclusion: str,
               res: AskResult | None, verdict: str, expected: dict[str, str], hook: dict | None = None,
               sb: Sandbox | None = None, notes: str = "", started: float | None = None,
               auth_ok: bool | None = None, name: str | None = None) -> RunRecord:
        reply = parse_reply(res.reply_text, res.raw, name) if res else None
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
        run_path = next_run_path(self.outdir, rec)
        if sb is not None:
            rec.stderr_path = self._keep_stderr(sb, run_path, rec.stderr_path)
        if res is not None and res.raw:
            # The vendor's full event stream is what a doubtful verdict is re-read against.
            run_path.parent.mkdir(parents=True, exist_ok=True)
            (run_path.parent / f"{run_path.stem}.raw.txt").write_text(res.raw)
        write_run(self.outdir, rec)
        return rec

    def _keep_stderr(self, sb: Sandbox, run_path: Path, stderr_path: str | None) -> str | None:
        """Copy the sandbox's stderr logs beside the run file: the sandbox is about to be removed."""
        wanted = {str(p) for p in sb.root.rglob("*.stderr.log")}
        if stderr_path and Path(stderr_path).is_file() and Path(stderr_path).is_relative_to(sb.root):
            wanted.add(stderr_path)
        for src in sorted(wanted):
            if src not in sb.copied_logs:
                dst = run_path.parent / f"{run_path.stem}.{Path(src).name}"
                shutil.copy2(src, dst)
                sb.copied_logs[src] = str(dst)
        if stderr_path in sb.copied_logs:
            return sb.copied_logs[stderr_path]
        if stderr_path is None and len(sb.copied_logs) == 1:
            return next(iter(sb.copied_logs.values()))
        return stderr_path

    def _load(self, mode: str, scenario: str, arm: str) -> tuple[Path, list[RunRecord]]:
        d = run_dir(self.outdir, self.adapter.entry, mode, scenario, arm)
        if self.rerun and d not in self._cleared:
            self._cleared.add(d)
            shutil.rmtree(d, ignore_errors=True)
        recs = load_runs(d) if (d / "run1.json").exists() else []
        recs.sort(key=lambda r: int(r._path.stem[3:]))  # type: ignore[attr-defined]
        return d, recs

    def _existing(self, mode: str, scenario: str, arm: str, per_pass: int = 1) -> list[RunRecord]:
        """The arm's settled runs, or [] after clearing rows nothing can resume from: an untested
        row is a failure to measure, not a measurement, and a pass cut short must be redone."""
        d, recs = self._load(mode, scenario, arm)
        if not recs:
            return []
        if any(r.verdict == "untested" for r in recs) or len(recs) % per_pass:
            shutil.rmtree(d, ignore_errors=True)
            return []
        return recs

    def _guarded(self, fn: Callable[[], RunRecord | list[RunRecord]], mode: str, scenario: str,
                 arm: str, root: str | None, exclusion: str) -> list[RunRecord]:
        try:
            out = fn()
        except PromptDesignFailure:
            raise
        except Exception as ex:  # noqa: BLE001
            return [self.record(mode, scenario, arm, root, exclusion, None, "untested", {}, sb=self._sb,
                                notes=f"exception={ex!r}")]
        finally:
            self._teardown()
        return out if isinstance(out, list) else [out]

    def run_arm(self, fn: Callable[[], RunRecord], mode: str, scenario: str, arm: str,
                root: str | None, exclusion: str) -> list[RunRecord]:
        recs = list(self._existing(mode, scenario, arm))
        for _ in range(max(0, self.runs - len(recs))):
            recs += self._guarded(fn, mode, scenario, arm, root, exclusion)
        if needs_third_run([r.verdict for r in recs]):
            recs += self._guarded(fn, mode, scenario, arm, root, exclusion)
        return recs

    def run_once(self, fn: Callable[[], RunRecord], mode: str, scenario: str, arm: str,
                 root: str | None, exclusion: str) -> list[RunRecord]:
        return self._existing(mode, scenario, arm) or self._guarded(fn, mode, scenario, arm, root, exclusion)

    def _blocked(self, mode: str, scenario: str, arm: str, root: str | None, exclusion: str,
                 notes: str = "S1 failed") -> list[RunRecord]:
        """One untested row per arm carrying the current reason: measured rows are kept, a stale
        blocked row is replaced, and a repeated sweep never stacks blocked rows."""
        d, existing = self._load(mode, scenario, arm)
        if existing and any(r.verdict != "untested" for r in existing):
            return existing
        if existing and all(r.notes == notes for r in existing):
            return existing
        shutil.rmtree(d, ignore_errors=True)
        return [self.record(mode, scenario, arm, root, exclusion, None, "untested", {}, notes=notes)]

    def record_blocked(self, mode: str, notes: str) -> list[RunRecord]:
        native = self.adapter.native_root
        out: list[RunRecord] = []
        for scenario, arm, uses_root, exclusion in ALL_ARMS:
            out += self._blocked(mode, scenario, arm, native if uses_root else None, exclusion, notes=notes)
        return out

    def _ask(self, sb: Sandbox, mode: str, prompt: str) -> AskResult:
        res = self.adapter.ask(sb, mode, prompt)
        broken = res.exit_code not in (0, None) or any(m in res.notes for m in ("failed:", "exception="))
        if not res.reply_text.strip() and broken:
            # A vendor that failed to run said nothing about the skill: that row is untested, and
            # the stderr tail is the reason a reader needs.
            reason = ""
            if res.stderr_path and Path(res.stderr_path).is_file():
                text = Path(res.stderr_path).read_text(errors="replace")
                errors = [line.strip() for line in text.splitlines() if "Error" in line or "error" in line]
                reason = (errors[0] if errors else text[-300:].replace("\n", " | "))[:300]
            raise RuntimeError(f"vendor exit {res.exit_code} with no reply; {res.notes}; stderr: {reason}")
        return res

    def _hook_dict(self, sb: Sandbox, mechanism: str, config_path: str) -> dict:
        stamp = read_stamp(stamp_path(sb.config_root)) or {}
        return {"mechanism": mechanism, "config_path": config_path,
                "fired_at": stamp.get("fired_at"), "fired_at_mtime": stamp.get("fired_at_mtime")}

    # -- arms (each runs inside _guarded, which owns the sandbox's teardown) ------------------

    def arm_s0(self, mode: str) -> RunRecord:
        started = time.time()
        sb = self.sandbox()
        skill = ProbeSkill.fresh()
        res = self._ask(sb, mode, single_prompt(skill))
        try:
            verdict = judge_control(parse_reply(res.reply_text, res.raw, skill.name))
        except PromptDesignFailure as ex:
            # The reply that broke the control is the evidence for it: record before unwinding.
            self.record(mode, "S0", "S0/none", None, "none", res, "untested", {}, sb=sb,
                        started=started, notes=f"prompt design failure: {ex}", name=skill.name)
            raise
        return self.record(mode, "S0", "S0/none", None, "none", res, verdict, {}, sb=sb, started=started,
                           name=skill.name)

    def arm_s1(self, mode: str, exclusion: str = "none", scenario: str = "S1",
               root: str | None = None) -> RunRecord:
        started = time.time()
        a = self.adapter
        root = root or a.native_root
        sb = self.sandbox()
        skill = ProbeSkill.fresh()
        write_skill(sb.repo / root, skill, flat=a.flat_skill_layout)
        rel = str(a.skill_dir(sb, root, skill.name).relative_to(sb.repo))
        apply_exclusion(sb.repo, exclusion, rel)
        arm = "S1/native" if scenario == "S1" else f"S3/{exclusion}"
        try:
            assert_untracked_state(sb.repo, rel, exclusion)
        except AssertionError as ex:
            # The arm cannot say anything about this exclusion, so it does not spend a turn.
            return self.record(mode, scenario, arm, root, exclusion, None, "untested",
                               {"native": skill.token}, sb=sb, started=started,
                               notes=f"git state: {ex}")
        res = self._ask(sb, mode, single_prompt(skill))
        verdict = judge_single(skill.token, parse_reply(res.reply_text, res.raw, skill.name))
        return self.record(mode, scenario, arm, root, exclusion, res, verdict,
                           {"native": skill.token}, sb=sb, started=started, name=skill.name)

    def arm_s2(self, mode: str, arm: str) -> RunRecord:
        started = time.time()
        a = self.adapter
        root = a.native_root
        sb = self.sandbox()
        skill = ProbeSkill.fresh()
        target = a.skill_file(sb, root, skill.name)
        if arm == "hook-adds-skill":
            write_skill(sb.repo / root, ProbeSkill.fresh(), flat=a.flat_skill_layout)
        if arm == "registration":
            info = a.install_registration(sb, target, skill.render())
            if info is None:
                return self.record(mode, "S2", "S2/registration", root, "none", None, "untested", {},
                                   sb=sb, notes="no registration mechanism for this entry", started=started)
            reload_used = True
        else:
            script = write_hook_script(sb.config_root, target, skill.render(), stamp_path(sb.config_root))
            info = a.install_startup_hook(sb, script)
            if info is None:
                return self.record(mode, "S2", f"S2/{arm}", root, "none", None, "untested", {},
                                   sb=sb, notes="no startup hook mechanism for this entry", started=started)
            reload_used = False
        res = self._ask(sb, mode, single_prompt(skill))
        verdict = judge_single(skill.token, parse_reply(res.reply_text, res.raw, skill.name),
                               reload_used=reload_used)
        hook = self._hook_dict(sb, info.mechanism, info.config_path)
        notes = "" if hook["fired_at"] is not None or arm == "registration" else "hook never fired"
        if not target.exists():
            notes = (notes + " skill file absent after the turn").strip()
            verdict = "untested"
        return self.record(mode, "S2", f"S2/{arm}", root, "none", res, verdict, {"native": skill.token},
                           hook=hook, sb=sb, started=started, notes=notes, name=skill.name)

    def arm_s3(self, mode: str, exclusion: str) -> RunRecord:
        return self.arm_s1(mode, exclusion=exclusion, scenario="S3")

    def arm_s4_all(self, mode: str) -> list[RunRecord]:
        started = time.time()
        a = self.adapter
        sb = self.sandbox()
        roots = self._s4_roots()
        skills = {key: ProbeSkill.fresh() for key in roots}
        for key, skill in skills.items():
            write_skill(sb.repo / roots[key], skill, flat=a.flat_skill_layout and roots[key] == a.native_root)
        res = self._ask(sb, mode, multi_prompt())
        reply = parse_reply(res.reply_text, res.raw)
        found = sorted(key for key, s in skills.items() if s.token in reply.tokens)
        leaked = sorted(key for key in found if roots[key] not in a.documented_roots)
        notes = f"found={found} leaked={leaked}"
        # One turn, one row per root: a single verdict for eleven roots cannot say which leaked.
        return [self.record(mode, "S4", "S4/all-roots", root, "none", res,
                            judge_root(skills[key].token, root in a.documented_roots, reply),
                            {key: skills[key].token}, sb=sb, started=started, notes=notes,
                            name=skills[key].name)
                for key, root in roots.items()]

    def arm_s4_confirm(self, mode: str, root_key: str, root: str) -> RunRecord:
        started = time.time()
        a = self.adapter
        sb = self.sandbox()
        skill = ProbeSkill.fresh()
        write_skill(sb.repo / root, skill, flat=a.flat_skill_layout and root == a.native_root)
        res = self._ask(sb, mode, single_prompt(skill))
        verdict = judge_root(skill.token, root in a.documented_roots,
                             parse_reply(res.reply_text, res.raw, skill.name))
        return self.record(mode, "S4", f"S4/confirm-{root_key}", root, "none", res, verdict,
                           {root_key: skill.token}, sb=sb, started=started, name=skill.name)

    # -- scenarios ----------------------------------------------------------------------------

    def run_scenario(self, mode: str, scenario: str, arms: list[str] | None = None) -> list[RunRecord]:
        gated = scenario not in ("S0", "S1") and not self.s1_ok.get(mode, True)
        native = self.adapter.native_root
        out: list[RunRecord] = []
        if scenario == "S0":
            out += self.run_arm(lambda: self.arm_s0(mode), mode, "S0", "S0/none", None, "none")
        elif scenario == "S1":
            recs = self.run_arm(lambda: self.arm_s1(mode), mode, "S1", "S1/native", native, "none")
            self.s1_ok[mode] = combine([r.verdict for r in recs])[0] == "visible_first_turn"
            out += recs
        elif scenario == "S2":
            for arm in arms or S2_ARMS:
                if gated:
                    out += self._blocked(mode, "S2", f"S2/{arm}", native, "none")
                elif arm == "registration":
                    out += self._registration(mode)
                else:
                    out += self.run_arm(lambda a=arm: self.arm_s2(mode, a), mode, "S2", f"S2/{arm}",
                                        native, "none")
        elif scenario == "S3":
            for exclusion in arms or S3_ARMS:
                if gated:
                    out += self._blocked(mode, "S3", f"S3/{exclusion}", native, exclusion)
                else:
                    out += self.run_arm(lambda e=exclusion: self.arm_s3(mode, e), mode, "S3",
                                        f"S3/{exclusion}", native, exclusion)
        elif scenario == "S4":
            if gated:
                out += self._blocked(mode, "S4", "S4/all-roots", None, "none")
                return out
            recs = self._s4_passes(mode)
            out += recs
            for key, root in self._s4_roots().items():
                rows = [r for r in recs if r.root == root and key in r.expected_tokens]
                if rows and all(r.expected_tokens[key] in r.tokens_found for r in rows):
                    continue
                # One confirmation turn per missed root: the multi prompt is what may have
                # stopped enumerating, and a second miss adds no evidence.
                out += self.run_once(lambda k=key, r=root: self.arm_s4_confirm(mode, k, r), mode, "S4",
                                     f"S4/confirm-{key}", root, "none")
        else:
            raise ValueError(scenario)
        return out

    def _s4_passes(self, mode: str) -> list[RunRecord]:
        per_pass = len(self._s4_roots())
        existing = self._existing(mode, "S4", "S4/all-roots", per_pass=per_pass)
        passes = [existing[i:i + per_pass] for i in range(0, len(existing), per_pass)]
        identity = (mode, "S4", "S4/all-roots", None, "none")
        while len(passes) < max(1, self.runs):
            passes.append(self._guarded(lambda: self.arm_s4_all(mode), *identity))
        if len(passes) == 2 and _verdicts_by_root(passes[0]) != _verdicts_by_root(passes[1]):
            passes.append(self._guarded(lambda: self.arm_s4_all(mode), *identity))
        return [r for p in passes for r in p]

    def _registration(self, mode: str) -> list[RunRecord]:
        identity = (mode, "S2", "S2/registration", self.adapter.native_root, "none")

        def run() -> RunRecord:
            return self.arm_s2(mode, "registration")

        recs = list(self._existing(mode, "S2", "S2/registration"))
        # An entry without a registration mechanism says so on the first run; nothing to repeat.
        while len(recs) < self.runs and not (recs and recs[-1].verdict == "untested"):
            recs += self._guarded(run, *identity)
        if needs_third_run([r.verdict for r in recs]):
            recs += self._guarded(run, *identity)
        return recs


def free_phase(adapter: Adapter, outdir: Path, mode: str, base: Path | None) -> dict:
    sb = new_sandbox(adapter.lever, adapter.real_root(), list(adapter.credential_files),
                     list(adapter.passthrough_env), dict(adapter.extra_env), base=base)
    try:
        adapter.prepare(sb)
        info = {
            "entry": adapter.entry, "binary": adapter.binary_path(), "version": adapter.version(sb.env),
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


def blocked_reason(info: dict) -> str | None:
    if info["binary"] is None:
        return "binary not installed"
    # auth_ok None is "not measurable for this entry", which is not a reason to skip the turns.
    return "auth_ok false in isolated root" if info["auth_ok"] is False else None


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
    p.add_argument("--rerun", action="store_true")
    p.add_argument("--emit", action="store_true")
    p.add_argument("--base", type=Path, default=None)
    args = p.parse_args(argv)

    if args.emit:
        rows = emit_matrix(args.outdir, args.matrix)
        print(f"wrote {len(rows)} rows to {args.matrix}")
        return 0

    entries = args.harness or [e for e in ENTRIES if e != "fake"]
    completed = aborted = 0
    for name in entries:
        adapter = ENTRIES[name]()
        if args.mode not in adapter.modes:
            print(f"{name}: mode {args.mode} unsupported, skipping")
            continue
        try:
            info = free_phase(adapter, args.outdir, args.mode, args.base)
            print(f"{name}: {info['version']} auth_ok={info['auth_ok']}")
            if args.turn:
                runner = Runner(adapter, args.outdir, runs=args.runs, keep=args.keep, base=args.base,
                                version=info["version"], rerun=args.rerun)
                blocker = blocked_reason(info)
                if blocker:
                    print(f"{name}: {blocker}, turn arms recorded as untested")
                    for r in runner.record_blocked(args.mode, blocker):
                        print(f"{name} {args.mode} {r.arm} -> {r.verdict} {r.notes}")
                else:
                    for scenario in args.scenario or SCENARIOS:
                        for r in runner.run_scenario(args.mode, scenario):
                            print(f"{name} {args.mode} {r.arm} root={r.root} excl={r.exclusion} "
                                  f"-> {r.verdict} {r.notes}")
            completed += 1
        except PromptDesignFailure as ex:
            aborted += 1
            print(f"{name}: prompt design failure: {ex}")
        except Exception as ex:  # noqa: BLE001
            aborted += 1
            print(f"{name}: aborted: {ex!r}")
    return 1 if aborted and not completed else 0


if __name__ == "__main__":
    sys.exit(main())
