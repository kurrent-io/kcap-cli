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
        rec = RunRecord(**data)
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
