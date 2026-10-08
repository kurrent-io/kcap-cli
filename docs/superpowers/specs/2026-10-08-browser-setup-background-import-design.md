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
  time five sessions take, not the whole history's.
- Everything the user chose (repositories, window, privacy level per repository, titles, vendors)
  still lands; the remainder is imported by a detached `kcap import` that outlives setup.
- The Done page shows, as soon as the foreground pass reports: how many sessions are importing in the
  background, and a copyable prompt that starts the `eval-watch` skill in any coding agent on this
  machine.
- Pasting that prompt into an agent on the same machine watches the foreground sessions' evals exactly
  as it does after a terminal setup.

### Out of scope

- The terminal import step (`RunImportStepAsync`) — unchanged.
- Launching an agent or a desktop app from the browser or from the CLI on the browser's behalf.
- Showing evals on the page itself.
- Reporting the background import's progress or failures to the page after the foreground pass.

## Current behaviour

- `BrowserFirstRunFlow.ActOnImportDecisionAsync` calls `IFirstRunImportLane.ImportAsync`, implemented
  by `SetupImportLane` (`SetupCommand.cs`). It runs one uncapped `ImportCommand.HandleImport` per
  level — `OnlyMe` (`forcePrivate`), then `Shared` (`shareWithOrg`) — scoped to the chosen
  repositories, the window's `since`, the answer's `SkipTitle` and vendors. Polling stops for the
  duration.
- The CLI then posts `POST /api/first-run/flows/{id}/import-outcome` with three counts, or a reason
  token on three zeroes. That post is the page's signal that the machine finished.
- The server's `DoneStep.razor` reads landed-session coverage every 3 s until it settles and shows the
  outcome's failures. It never reads per-session evals.
- When the browser answered the import question, the terminal import step only prints
  `BrowserImportSummary`; no handoff file, no background child, no prompt.

## Design

### 1. Capped foreground passes (CLI, `SetupImportLane`)

`SetupImportLane.ImportAsync` runs the same two passes, in the same order, each with a session cap:

- **One budget across both passes**: `SetupCommand.ForegroundImportCap` (5). The `OnlyMe` pass gets the
  full budget; the `Shared` pass gets what the first pass's selection left (`cap − selected`). A pass
  whose budget is zero is not run in the foreground; its level goes entirely to the background.
- The cap reaches `HandleImport` as `maxSessions`, and the run reports its selection through
  `onSelected`, exactly as `SetupImportRunner` does for the terminal flow. Selection is
  `ForegroundSelection.Select`: whole chains, newest first; the boundary chain may exceed the budget.
- Each pass's result becomes a `ForegroundImportOutcome` (via the same `SetupImportRun` shape), so
  certainty, remainder and succeeded ids mean what they mean in the terminal flow.
- `Pass` gains `MaxSessions`, so tests can assert the budget each level received.

`ImportAsync` keeps its return contract (totals or null) and additionally exposes the per-level
foreground outcomes to the caller (a new result type replacing `FirstRunImportTotals?`, or a property
on the lane alongside `Failed` — decided in the plan by what keeps `IFirstRunImportLane` honest).

### 2. Background remainder (CLI, `BackgroundImportSpawner`)

A level needs a background child when its pass was skipped for budget, or its outcome reports
`RemainderExists`, `Failed > 0`, or `Incomplete` — the terminal flow's trigger, per level.

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
  `KCAP_IMPORT_DEFAULT_VISIBILITY` the same way; it gains the share variable and `Program.cs` passes
  it to `HandleImport(shareWithOrg: …)` only for a detached run.
- **One child per level, started in level order.** `--private` is per invocation, so one child cannot
  do both. A repository is chosen at one level, so the two children read disjoint sessions.
- **Log paths**: `import-<runId>-only-me.log` and `import-<runId>-shared.log` (the terminal flow keeps
  `import-<runId>.log`).
- Argv construction becomes a pure function so tests pin the argument list without spawning.

Aggregate background status for the run: `Running` if any child is running; else `Failed` if any
failed; else `ExitedZero` if any ran; else `NotNeeded`.

Exclusion prompts: the child's stdin is redirected, so `HandleImport` cannot prompt — the same
non-interactive path the terminal flow's child already takes.

### 3. Handoff file (CLI)

After the foreground passes and spawns, the browser flow writes the same
`import-handoff-<runId>.json` the terminal flow writes, through `ImportHandoffFile.Compose`:

- `session_ids` / `foreground_succeeded_ids`: the two passes' candidates and succeeded ids, `OnlyMe`
  first, through the existing 500-id cap with foreground ids reserved.
- `background`: the aggregate status; `background_log`: the first spawned child's log.
- `scope`: `"repos"` for this flow (the skill does not read it; it stays truthful).
- `handoff_offered` / `handoff_suppressed`: from `HandoffDecision.Decide`, with the eligible-vendor
  count from `HandoffVendorEligibility.Eligible` — the same predicate the terminal flow uses, so the
  page offers the prompt exactly when the terminal would have offered an agent.

`Compose` takes a combined `ForegroundImportOutcome` (the two passes merged: certainty is `Complete`
only if both are; counts and id lists concatenate). Writing stays best-effort, as in the terminal
flow; a write failure is reported to the page as a suppressed handoff
(`handoff_file_unwritten`, new token) so the page never offers a prompt whose file does not exist.

### 4. Outcome report (CLI → server contract)

`POST /import-outcome` is sent **after the foreground passes and spawns**, not after the whole
import. Its three counts are the foreground passes' totals. It gains three optional fields:

| Field | Type | Meaning |
|---|---|---|
| `background` | `"not_needed" \| "running" \| "exited_zero" \| "failed"` | aggregate child status |
| `handoff_prompt` | string, ≤ 200 chars | the exact prompt text, `SetupCommand.HandoffPromptText(runId)` |
| `handoff_suppressed` | `HandoffSuppressedReason.Wire()` token, or `handoff_file_unwritten` | why no prompt is offered |

- `handoff_prompt` and `handoff_suppressed` are mutually exclusive; both absent means an older CLI.
- **The CLI owns the prompt's wording.** The skill that answers it ships with the CLI and matches on
  that phrase, so the server renders the string it was given rather than composing one. The server
  validates it: length, printable characters and `\n` only.
- On a reason-token outcome (`run_failed` and friends) all three new fields are absent.
- Compatibility: an older server ignores the new members (`ImportOutcomeRequest` is deserialized with
  default options, which skip unknown members). An older CLI sends none, and the page renders today's
  Done screen.

### 5. Server: record and render (kcap-server)

- `ImportOutcomeRequest`, `FirstRunImportOutcome`, `FirstRunImportOutcomeReportedEvent` and
  `FirstRunImportOutcomeView` gain the three optional fields. The event change is additive; a stored
  event without them reads as an older CLI's.
- The endpoint rejects an unknown `background` or `handoff_suppressed` token and a malformed prompt,
  the same way it rejects an unknown reason today.
- `DoneStep.razor` adds one panel, shown once the outcome has arrived:
  - **Prompt offered**: "Paste this into a coding agent on *machine label* to watch these sessions'
    evals", the prompt in a monospace block with a copy button, and — when `background` is `running` —
    "The rest of your history is importing in the background."
  - **Suppressed for eligibility** (`skill_not_installed`, `no_agent_detected`): a link to
    `/sessions?status=ended`, mirroring the terminal's fallback.
  - **Suppressed by the import itself** (`import_failed`, `no_new_sessions`, `nothing_landed`) or
    `handoff_file_unwritten`: no panel.
  - **`background` failed**: a warning line naming `kcap import` as the way to bring the rest over.
- The coverage poll, settle rule and figures are unchanged. The background child's uploads keep
  feeding the same coverage read, so the figures continue to fill after the foreground pass.

### 6. Terminal during the browser flow (CLI)

- `progress.Importing` says it is importing the newest sessions first.
- After the passes, the terminal prints the same background line the terminal flow prints
  (`PrintBackground`).
- `BrowserImportSummary` at the end of setup also prints the paste block when a prompt was offered,
  so a user who closed the tab still has it.

## Error handling

- A foreground pass that throws or reports nothing still makes the outcome `run_failed`, as today; the
  remainder for that level is still spawned (the terminal trigger includes `Incomplete`).
- A spawn failure makes `background: failed`; the prompt can still be offered for what did land
  (`HandoffDecision` already distinguishes these).
- Cancellation during the foreground passes cancels as today; no child is spawned after cancellation.

## Testing

CLI (TUnit):
- `SetupImportLane`: budget split across levels, zero-budget level skipped, `Pass.MaxSessions`.
- Background argv builder: each level's flags, `--skip-title` only on request, vendor flags, the
  terminal request unchanged.
- `DetachedImportLog` / `Program` import path: the share variable reaches `shareWithOrg` only on a
  detached run.
- `BrowserFirstRunFlow`: outcome carries `background` and exactly one of prompt/suppressed; reason-token
  outcomes carry none; handoff-file write failure reports the suppression.
- `ImportHandoffFile.Compose` with merged outcomes: foreground-first ordering under the cap.

Server:
- Endpoint validation of the new fields, event round-trip with and without them.
- `DoneStep` rendering of each panel state (bUnit or the existing first-run component tests).

## Delivery

Two PRs: the server change first (accepts and renders the fields; inert without them), then the CLI
change. The CLI bumps kcap-server's `src/cli` submodule pin only when the server needs the shared
models.
