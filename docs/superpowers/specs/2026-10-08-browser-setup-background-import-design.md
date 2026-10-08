# Browser setup: capped first import, background remainder, eval-watch prompt

## Goal

When `kcap setup` runs the browser first-run flow, the import the user chooses on the Import step
should behave like the terminal flow's: import the newest few sessions in the foreground, hand the
rest to a detached background import, and give the user a way to watch those first sessions get
evaluated. The browser Done page shows a prompt to paste into a coding agent — the same
`Follow my kcap import (run: <id>)` prompt the terminal flow launches or prints — instead of the
terminal blocking on the whole import.

### Success criteria

- After the user decides on the Import step, the terminal finishes its foreground work in roughly the
  time a handful of sessions take, not the whole history's. Foreground work — uploads *and* visibility
  writes — is bounded by the cap, not by the size of the history in scope.
- Everything the user chose (repositories, window, privacy level per repository, titles, vendors)
  still lands; the remainder is imported by detached `kcap import` children that outlive setup.
- The Done page shows, as soon as the foreground passes report: how many sessions are still to import
  in the background (when known), and a copyable prompt that starts the `eval-watch` skill in any
  coding agent on this machine.
- The Done page never says the import finished while a background child is running.
- Pasting that prompt into an agent on the same machine, under the profile setup used, watches the
  foreground sessions' evals exactly as it does after a terminal setup; under any other profile the
  skill refuses and says how to fix it.

### Out of scope

- The terminal import step (`RunImportStepAsync`) — unchanged.
- Launching an agent or a desktop app from the browser or from the CLI on the browser's behalf.
- Showing evals on the page itself.
- Reporting the background import's progress or failures to the page after the foreground passes.
  The page infers progress only from landed-session coverage.

## Current behaviour

- `BrowserFirstRunFlow.ActOnImportDecisionAsync` calls `IFirstRunImportLane.ImportAsync`, implemented
  by `SetupImportLane` (`SetupCommand.cs`). It runs one uncapped `ImportCommand.HandleImport` per
  level — `OnlyMe` (`forcePrivate`), then `Shared` (`shareWithOrg`) — scoped to the chosen
  repositories, the window's `since`, the answer's `SkipTitle` and vendors. Polling stops for the
  duration.
- If either pass throws or reports nothing, the lane returns null and the CLI posts `run_failed` on
  three zeroes, even when the other pass landed sessions.
- The CLI then posts `POST /api/first-run/flows/{id}/import-outcome` with three counts, or a reason
  token on three zeroes.
- `DoneStep.razor` treats that post as "the import is over": `_settled |= View.Reported || …`. Settling
  switches the figures to their final arrangement, drops the recent-sessions frame, and announces
  "Import finished". The coverage poll ends once figures go quiet (four quiet ticks past a 15 s floor)
  or the 30-minute budget runs out. The endpoint's `import_outcome` telemetry labels the counts `clean`
  or `partial`.
- When the browser answered the import question, the terminal import step only prints
  `BrowserImportSummary`; no handoff file, no background child, no prompt.

## Design

### 1. Capped foreground passes (CLI, `SetupImportLane`)

`SetupImportLane.ImportAsync` runs the same two passes, in the same order, **each capped at
`SetupCommand.ForegroundImportCap` (5)**.

- **Per level, not one shared budget.** Every chosen level runs a foreground pass, so every level
  reports a selection and a candidate list (a skipped pass would leave its cohort unenumerated and
  the watch with nothing to look up). The foreground is at most two caps' worth — and only when the
  user chose both levels. "Newest" means newest within each level; the page and terminal copy say
  "the newest sessions" without claiming a global five.
- The cap reaches `HandleImport` as `maxSessions`, and the run reports its selection through
  `onSelected`, as `SetupImportRunner` does for the terminal flow. Selection is
  `ForegroundSelection.Select`: whole chains, newest first; the boundary chain may exceed the cap.
- Each pass's result is a `SetupImportRun` folded into a `ForegroundImportOutcome`, so certainty,
  remainder, candidates and succeeded ids mean what they mean in the terminal flow.
- `Pass` gains `MaxSessions`.

**Visibility work under a cap is restricted to the selection** (`ImportCommand.HandleImport`, when
`maxSessions` is set):

- The `forcePrivate` preflight ("Making existing sessions private") runs only over selected sessions
  that already exist on the server (`Partial` among the selection). It still runs *before* any of
  them receives content, and a session whose write failed is still dropped from the run.
- The `shareWithOrg` visibility write covers only the selected sessions.
- Unselected sessions' visibility work belongs to that level's background child, which runs uncapped
  and therefore does the full preflight or share write for its scope, as today.
- With `maxSessions` unset (every existing caller but this one with a level flag, and the terminal
  flow's uncapped child) behaviour is unchanged. The terminal flow's capped pass sets neither
  `forcePrivate` nor `shareWithOrg`, so it is unaffected.

**Lane result.** `ImportAsync` returns a new `FirstRunImportResult` replacing `FirstRunImportTotals?`:

- per level that ran: its `ForegroundImportOutcome` (or a fault), and its background launch;
- `Totals`: the summed counts of the passes that reported, plus `Complete` — false when any pass threw
  or reported nothing.

The lane, not the flow, spawns the children (section 2) and writes the handoff file (section 3),
because both need CLI-assembly types; `IFirstRunImportLane` only carries the result, including the
prompt text or suppression token the flow forwards.

### 2. Background remainder (CLI, `BackgroundImportSpawner`)

A level needs a background child when its outcome reports `RemainderExists`, `Failed > 0`, or
`Incomplete` (including a pass that threw) — the terminal flow's trigger, applied per level.

`BackgroundImportRequest` grows from "import everything" to a scoped request:

| Field | Child argument |
|---|---|
| repositories | `--repo owner/name`, repeated |
| since | `--since yyyy-MM-dd` |
| `OnlyMe` | `--private` |
| `Shared` | `KCAP_IMPORT_SHARE_WITH_ORG=1` (new, detached-only) |
| vendors | `--claude`, `--codex`, … (`VendorSelection.KnownVendorFlags`) |
| skip titles | `--skip-title` only when the answer asked for it |
| always | `--yes` |

The terminal flow's request keeps producing `import --all --yes --skip-title`.

- **Share-with-org travels as a detached-contract variable, not a flag.** `shareWithOrg` has no CLI
  flag, and adding one is public surface (help text, README). `DetachedImportLog` already carries
  `KCAP_IMPORT_DEFAULT_VISIBILITY` the same way; it gains the share variable, and `Program.cs` passes
  it to `HandleImport(shareWithOrg: …)` only when the detached log variable is also present.
- **One child per level, started in level order.** `--private` is per invocation, so one child cannot
  do both. A repository is chosen at exactly one level, so the two children's sessions are disjoint
  and neither writes the other's visibility. They run concurrently; ingest is idempotent per event id,
  and no local state is shared between two `kcap import` processes beyond the read-only config.
  (The plan verifies the last claim against `ImportCommand`'s local writes before relying on it.)
- **Log paths**: `import-<runId>-only-me.log` and `import-<runId>-shared.log` (the terminal flow keeps
  `import-<runId>.log`).
- Argv construction is a pure function, so tests pin the argument list without spawning.

Aggregate background status for the run: `Running` if any child is running; else `Failed` if any
failed; else `ExitedZero` if any ran; else `NotNeeded`.

**Remaining count.** Per level, `candidates − selected` from that level's `ForegroundImportOutcome`
when its candidate list is known; the run's remaining count is the sum, or unknown if any level's
list is unknown.

Exclusion prompts: the child's stdin is redirected, so `HandleImport` cannot prompt — the same
non-interactive path the terminal flow's child already takes.

### 3. Handoff file (CLI)

After the foreground passes and spawns, the lane writes the same `import-handoff-<runId>.json` the
terminal flow writes, through `ImportHandoffFile.Compose`, from a merged `ForegroundImportOutcome`:

- **Merge rule.** `RunCandidateIds` is the concatenation of each level's known candidates, `OnlyMe`
  first, or null only when *every* level's list is unknown; a level with an unknown list contributes
  nothing and makes the merged certainty `Incomplete`. `SucceededIds` concatenates in the same order.
  So one level failing to enumerate leaves the other level's foreground watchable, as a
  `partial_exact`-or-`exact` cohort per the existing 500-id rule, rather than turning the whole cohort
  `unknown`.
- `background`: the aggregate status; `background_log`: the `OnlyMe` child's log when it ran, else the
  `Shared` child's.
- `scope`: `"repos"` for this flow (the skill does not read it; it stays truthful).
- `handoff_offered` / `handoff_suppressed`: from `HandoffDecision.Decide` over the merged outcome, with
  the eligible-vendor count from `HandoffVendorEligibility.Eligible` — the predicate the terminal flow
  uses, so the page offers the prompt exactly when the terminal would have offered an agent.

Writing stays best-effort, as in the terminal flow; a write failure is reported to the page as a
suppressed handoff (`handoff_file_unwritten`, new token) so the page never offers a prompt whose file
does not exist.

### 4. eval-watch identity check (CLI, `kcap/skills/eval-watch/SKILL.md`)

A pasted prompt runs in whatever agent the user opens, not one setup launched with `KCAP_PROFILE`
pinned. Section 4 of the skill ("Bind to the server") additionally compares `kcap whoami`'s `Profile`
with the file's `profile`; a mismatch is handled exactly like a server mismatch — no eval lookup, and
the existing remediation (set `KCAP_PROFILE=<profile>`, unset `KCAP_URL`, restart the agent).

`kcap whoami` run by the agent inherits the agent's environment, which is the environment its
`kcap-sessions` MCP server was started with, so the two resolve the same profile and the check covers
the MCP connection too. The remediation says to restart the agent because the MCP server resolved its
profile at start.

### 5. Outcome report (CLI → server contract)

`POST /import-outcome` is sent **after the foreground passes and spawns**, not after the whole
import. It gains four optional fields:

| Field | Type | Meaning |
|---|---|---|
| `background` | `"not_needed" \| "running" \| "exited_zero" \| "failed"` | aggregate child status |
| `background_remaining` | non-negative int | sessions left for the children, when known |
| `handoff_prompt` | string, ≤ 200 chars | the exact prompt, `SetupCommand.HandoffPromptText(runId)` |
| `handoff_suppressed` | `HandoffSuppressedReason.Wire()` token, or `handoff_file_unwritten` | why no prompt is offered |

- Counts: the foreground passes' summed totals when `Totals.Complete`; otherwise `run_failed` on three
  zeroes, as today.
- **The new fields ride on `run_failed` too.** A lost pass no longer hides a running child or a prompt
  for the pass that landed. The server accepts them alongside a reason; the reason still forbids
  non-zero counts.
- `handoff_prompt` and `handoff_suppressed` are mutually exclusive; both absent means an older CLI, or
  an outcome with nothing to hand off (`decision_unreadable`, `no_readable_agents`, a decline).
- **The CLI owns the prompt's wording.** The skill that answers it ships with the CLI and matches on
  that phrase, so the server renders the string it was given rather than composing one. The server
  validates it: length, printable characters and `\n` only.
- Compatibility: an older server ignores the new members (`ImportOutcomeRequest` is deserialized with
  default options, which skip unknown members) and shows today's Done screen, including "Import
  finished" while children run — acceptable for the rollout window, and the reason the server PR ships
  first. An older CLI sends none, and the page renders today's screen.

### 6. Server: record, settle, render (kcap-server)

**Record.** `ImportOutcomeRequest`, `FirstRunImportOutcome`, `FirstRunImportOutcomeReportedEvent` and
`FirstRunImportOutcomeView` gain the four optional fields. The event change is additive; a stored event
without them reads as an older CLI's. The endpoint rejects an unknown `background` or
`handoff_suppressed` token, a negative remaining count and a malformed prompt, as it rejects an unknown
reason today. The `import_outcome` telemetry gains a `background` dimension, and its `clean`/`partial`
label is documented as describing the foreground passes only.

**Settle.** `_settled` keeps meaning "the import is over as far as this screen is concerned". The
machine's report settles it only when `background` is absent, `not_needed`, `exited_zero` or `failed`.
With `background: running`, the report instead sets a new `_foregroundReported` state:

- The screen stays in its in-progress arrangement (fractions, progress bar, recent-sessions frame).
- It shows the eval-watch panel (below) at once.
- The coverage poll does **not** stop on quiet; it continues at the same cadence until the existing
  30-minute budget runs out or the user leaves. Coverage reaching the discovery-time expected total
  settles the screen as finished. Budget exhaustion settles it into the existing "wait gave up" arm,
  whose copy says the rest is still importing in the background and will appear in the workspace.
- No live-region announcement says "Import finished" while in this state; the announcement on the
  report is "Your newest sessions are in. The rest is importing in the background."

**Render.** `DoneStep.razor` adds one panel, shown once the outcome has arrived:

- **Prompt offered**: "Paste this into a coding agent on *machine label* to watch these sessions'
  evals", the prompt in a monospace block with a copy button, and — when `background` is `running` —
  "N more sessions are importing in the background" (or "The rest of your history is importing in the
  background" when the count is absent).
- **Suppressed for eligibility** (`skill_not_installed`, `no_agent_detected`): a link to
  `/sessions?status=ended`, mirroring the terminal's fallback.
- **Suppressed by the import itself** (`import_failed`, `no_new_sessions`, `nothing_landed`) or
  `handoff_file_unwritten`: no panel.
- **`background` failed**: a warning line naming `kcap import` as the way to bring the rest over.
- **`run_failed` with `background: running`**: the existing failure line is reworded to say the rest,
  including what failed, is being retried in the background, instead of telling the user to run
  `kcap import`.

### 7. Terminal during the browser flow (CLI)

- `progress.Importing` says it is importing the newest sessions first.
- After the passes, the terminal prints the background line the terminal flow prints
  (`PrintBackground`), once per child.
- `BrowserImportSummary` at the end of setup also prints the paste block when a prompt was offered,
  so a user who closed the tab still has it.

## Error handling

- A foreground pass that throws or reports nothing: the outcome is `run_failed`, its level's child is
  still spawned (`Incomplete`), and the other level's foreground still feeds the handoff file and the
  prompt.
- A spawn failure makes that level's status `failed`; the aggregate follows section 2, and the prompt
  can still be offered for what landed (`HandoffDecision` already distinguishes these).
- Cancellation during the foreground passes cancels as today; no child is spawned after cancellation.

## Testing

CLI (TUnit):
- `SetupImportLane`: each chosen level runs capped at the cap; `Pass.MaxSessions`; a throwing pass
  still spawns its level's child and leaves the other level's outcome in the result.
- `ImportCommand` under `maxSessions` with `forcePrivate` / `shareWithOrg`: visibility writes cover
  only selected sessions; a selected existing session whose private write fails is dropped.
- Background argv builder: each level's flags, `--skip-title` only on request, vendor flags, the
  terminal request unchanged.
- `DetachedImportLog` / `Program` import path: the share variable reaches `shareWithOrg` only on a
  detached run.
- Merge of per-level outcomes: candidate order, one unknown level, both unknown; remaining count.
- `BrowserFirstRunFlow`: outcome carries `background`, `background_remaining` and exactly one of
  prompt/suppressed, including on `run_failed`; handoff-file write failure reports the suppression.
- eval-watch skill text: the profile comparison and its remediation (the skill's existing pinned-text
  tests, if any, extended).

Server:
- Endpoint validation of the new fields, including alongside a reason; event round-trip with and
  without them.
- `DoneStep`: report with `background: running` does not settle or announce finished; coverage
  reaching the expected total settles; budget exhaustion settles into the background copy; each panel
  state renders.

## Delivery

Two PRs: the server change first (accepts the fields, settles on them, renders the panel; inert
without them), then the CLI change. The CLI bumps kcap-server's `src/cli` submodule pin only when the
server needs the shared models.
