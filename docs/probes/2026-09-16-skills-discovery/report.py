#!/usr/bin/env python3
"""Render capability-matrix.md from matrix.json: one row per entry, the columns #778 and #962 read.

Usage: report.py [--matrix FILE] [--out FILE]
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from collections import defaultdict
from pathlib import Path

KIT = Path(__file__).resolve().parent

VISIBLE = ("visible_first_turn", "visible_after_reload")
COLUMNS = ("Entry", "Version tested", "Modes", "Native root", "Roots consumed", "Startup mechanism proven",
           "Exclusion preserving load", "Vendor-isolated destination", "Reload path", "Live catalogue",
           "Startup rewrite", "Resume", "Nested cwd", "Worktree", "Peer hook", "Interactive", "GUI status",
           "Minimum version")

# GUI launch modes the kit cannot drive; the findings carry a manual procedure for each.
GUI_STATUS = {
    "cursor": "Cursor desktop: untested (manual procedure in findings.md)",
    "agy": "Antigravity IDE: untested (manual procedure in findings.md)",
    "agy-dirlayout": "Antigravity IDE: untested (manual procedure in findings.md)",
    "agy-clidir": "Antigravity IDE: untested (manual procedure in findings.md)",
    "kiro": "Kiro IDE: untested (manual procedure in findings.md)",
}


def _version_key(version: str) -> tuple:
    """Order versions by their numbers, so 1.9 comes before 1.10."""
    return tuple(int(p) if p.isdigit() else p for p in re.split(r"(\d+)", version))


def load_rows(path: Path) -> list[dict]:
    return json.loads(path.read_text())


def summarise(rows: list[dict]) -> list[dict]:
    by_entry: dict[str, list[dict]] = defaultdict(list)
    for r in rows:
        by_entry[r["entry"]].append(r)
    consumed_by: dict[str, set[str]] = defaultdict(set)
    for r in rows:
        # A root another vendor loads is shared even when that vendor never documented it, so a
        # leaked sighting counts against isolation exactly like a documented one. Two entries of
        # one vendor are one consumer: they are the same CLI under different configurations.
        if r["scenario"] == "S4" and r["verdict"] in VISIBLE + ("leaked",) and r["root"]:
            consumed_by[r["root"]].add(r["harness"])
    out = []
    for entry in sorted(by_entry):
        rs = by_entry[entry]
        versions = sorted({r["version"] for r in rs})
        modes = sorted({r["mode"] for r in rs})
        native = next((r["root"] for r in rs if r["scenario"] == "S1" and r["root"]), "")
        roots = sorted({r["root"] for r in rs if r["scenario"] == "S4" and r["verdict"] in VISIBLE and r["root"]})
        leaked = sorted({r["root"] for r in rs if r["scenario"] == "S4" and r["verdict"] == "leaked"})
        mechanisms = sorted({f"{r['mechanism']} ({r['mode']})" for r in rs
                             if r["scenario"] == "S2" and r["verdict"] in VISIBLE and r["mechanism"]})
        exclusions = sorted({r["exclusion"] for r in rs if r["scenario"] == "S3" and r["verdict"] in VISIBLE})
        reload = sorted({r["mechanism"] for r in rs if r["verdict"] == "visible_after_reload" and r["mechanism"]})
        untested = sorted({r["notes"][:80] for r in rs if r["verdict"] == "untested" and r["scenario"] == "S1"})
        harness = next((r["harness"] for r in rs if r.get("harness")), entry)
        isolated = [root for root in roots if consumed_by[root] == {harness}]
        s1 = {r["verdict"] for r in rs if r["scenario"] == "S1"}
        if s1 & set(VISIBLE):
            status = "measured"
        elif untested:
            status = "untested: " + "; ".join(untested)
        elif s1:
            status = "S1 failed: the native root's skill was not loaded"
        else:
            status = "no S1 row"
        measured = status == "measured"
        proven = {r["version"] for r in rs if r["scenario"] == "S1" and r["verdict"] in VISIBLE}
        ran = {r["scenario"] for r in rs if r["verdict"] != "untested"}
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

        def cell(values: list[str], scenario: str, mode: str | None = None) -> str:
            # A scenario the entry never ran says so, instead of reading as a measured "none".
            if values:
                return "; ".join(values)
            if not measured:
                return "—"
            if scenario == "tui":
                ran_it = "tui" in modes_ran
            else:
                ran_it = any(r["scenario"] == scenario and r["verdict"] != "untested"
                             and (mode is None or r["mode"] == mode) for r in rs)
            return "none" if ran_it else "n/a (not run)"

        out.append({
            "Entry": entry,
            "Version tested": ", ".join(versions),
            "Modes": ", ".join(modes),
            "Native root": native,
            "Roots consumed": cell([", ".join(roots) + (f" (undocumented: {', '.join(leaked)})" if leaked else "")]
                                   if roots else [], "S4"),
            "Startup mechanism proven": cell(mechanisms, "S2"),
            "Exclusion preserving load": cell([", ".join(exclusions)] if exclusions else [], "S3"),
            "Vendor-isolated destination": cell([", ".join(isolated)] if isolated else [], "S4"),
            "Reload path": cell(reload, "S2"),
            "Live catalogue": cell(arm_cells("S5", "daemon"), "S5", "daemon"),
            "Startup rewrite": cell(arm_cells("S6"), "S6"),
            "Resume": cell(arm_cells("S7"), "S7"),
            "Nested cwd": cell(arm_cells("S8"), "S8"),
            "Worktree": cell(arm_cells("S9"), "S9"),
            "Peer hook": cell(arm_cells("S10"), "S10"),
            "Interactive": cell(tui_cells(), "tui"),
            "GUI status": GUI_STATUS.get(entry, "n/a"),
            "Minimum version": min(proven, key=_version_key) if proven else "—",
            "_status": status,
        })
    return out


def render(summary: list[dict]) -> str:
    lines = ["# Capability matrix — repo-local skill discovery", "",
             "Generated by `report.py` from `matrix.json`; do not edit by hand. A `—` cell means the entry",
             "could not run (see its status line below and `findings.md`).", "",
             "| " + " | ".join(COLUMNS) + " |", "|" + " -- |" * len(COLUMNS)]
    for row in summary:
        lines.append("| " + " | ".join(str(row[c]).replace("|", "\\|") for c in COLUMNS) + " |")
    lines += ["", "## Status per entry", ""]
    for row in summary:
        lines.append(f"- `{row['Entry']}`: {row['_status']}")
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--matrix", type=Path, default=KIT / "matrix.json")
    p.add_argument("--out", type=Path, default=KIT / "capability-matrix.md")
    args = p.parse_args(argv)
    args.out.write_text(render(summarise(load_rows(args.matrix))))
    print(f"wrote {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
