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
  still lands with the visibility the user chose; the rest is done by one detached child that
  outlives setup.
- The Done page shows, as soon as the foreground passes report: how many sessions are still to import
  in the background (when known), and a copyable prompt that starts the `eval-watch` skill in any
  coding agent on this machine.
- The Done page never says the import finished while the background child may still be working.
- Pasting that prompt into an agent on the same machine, under the profile setup used, watches the
  foreground sessions' evals exactly as it does after a terminal setup; under any other profile the
  skill refuses before reading any eval and says how to fix it.

### Out of scope

- The terminal import step (`RunImportStepAsync`) — unchanged.
- Launching an agent or a desktop app from the browser or from the CLI on the browser's behalf.
- Showing evals on the page itself.
- Reporting the background child's progress, completion or failures to the page. The page infers
  progress only from landed-session coverage, and never infers completion from it.

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
  "Import finished". `PollAsync` reads coverage every 3 s and ends after four quiet ticks past a 15 s
  floor; if the outcome is still owed it then waits out the 30-minute budget. The endpoint's
  `import_outcome` telemetry labels the counts `clean` or `partial`.
- When the browser answered the import question, the terminal import step only prints
  `BrowserImportSummary`; no handoff file, no background child, no prompt.

## Design

### 1. Capped foreground passes (CLI, `SetupImportLane`)

`SetupImportLane.ImportAsync` runs the same two passes, in the same order, **each capped at
`SetupCommand.ForegroundImportCap` (5)**.

- **Per level, not one shared budget.** Every chosen level runs a foreground pass, so every level
  reports a selection and a candidate list. The foreground is at most two caps' worth, and only when
  the user chose both levels. "Newest" means newest within each level; the copy says "your newest
  sessions" without claiming a global five.
- The cap reaches `HandleImport` as `maxSessions`, and the run reports its selection through
  `onSelected`, as `SetupImportRunner` does for the terminal flow. Selection is
  `ForegroundSelection.Select`: whole chains, newest first; the boundary chain may exceed the cap.
- Each pass's result is a `SetupImportRun` folded into a `ForegroundImportOutcome`.
- One static `SetupImportLane.RunPassAsync(pass, …)` builds the `HandleImport` call for a level, used
  by both the foreground passes and the background child (section 2), so the two cannot drift on
  scope, `since`, vendors, titles, `forcePrivate`/`shareWithOrg` or `autoSkipExclusions`.

**Visibility work under a cap is restricted to the selection** (`ImportCommand.HandleImport`, only
when `maxSessions` is set):

- The `forcePrivate` preflight runs only over selected sessions the server already has. It still runs
  before any of them receives content, and a session whose write failed is still dropped from the run.
- The `shareWithOrg` write covers only the selected sessions.
- Everything else — unselected sessions, `AlreadyLoaded` sessions, and any visibility write that failed
  in the foreground — is the background child's: it is uncapped, so it runs the full preflight and
  share write for its scope exactly as an uncapped import does today.
- With `maxSessions` unset, behaviour is unchanged. The terminal flow's capped pass sets neither
  `forcePrivate` nor `shareWithOrg`, so it is unaffected.

**Lane result.** `ImportAsync` returns a new `FirstRunImportResult` replacing `FirstRunImportTotals?`:
per level, its `ForegroundImportOutcome` or fault; the background launch; the summed totals of the
passes that reported plus `Complete` (false when any pass threw or reported nothing); and the
handoff prompt or suppression token (section 3). The lane spawns the child and writes the handoff
file because both need CLI-assembly types; `IFirstRunImportLane` only carries the result.

### 2. Background remainder: one sequential child (CLI)

**Whenever at least one level was chosen, the lane spawns exactly one background child**, after the
foreground passes, regardless of what they selected. The child is uncapped and re-runs every chosen
level; sessions the foreground already loaded classify as loaded and are skipped, so the cost of an
unneeded child is one classification pass. Always spawning is what guarantees the visibility work
the capped passes deferred (`AlreadyLoaded` sessions, failed visibility writes) happens even when the
foreground selected every candidate.

**The child runs the levels sequentially, in one process.** Two concurrent imports are not safe: the
OpenCode import ledger is loaded and rewritten whole by each process, so concurrent children would
overwrite each other's entries.

Mechanism — a detached-only plan file, not new CLI flags:

- The lane writes `import-plan-<runId>.json` (owner-only, `OwnerOnlyFile.CreateNew`, best-effort
  deleted by the child on exit and pruned with the handoff files' 7-day retention). It holds, per
  chosen level in order (`OnlyMe` first): level, repositories, `since`, vendors, skip-titles.
- `BackgroundImportSpawner` spawns `kcap import --yes` with the existing detached variables
  (`KCAP_IMPORT_DETACHED_LOG`, config dir, profile, visibility) plus `KCAP_IMPORT_PLAN=<path>`.
- `Program.cs`, when both the detached log and the plan variable are present, reads the plan and calls
  `SetupImportLane.RunPassAsync` once per level, sequentially, ignoring scope flags. A plan that cannot
  be read fails the child with a logged error. Without the detached log the plan variable is ignored,
  so it is not a user-facing input.
- The terminal flow's spawn is unchanged (`import --all --yes --skip-title`, no plan).
- One log, `import-<runId>.log`, as in the terminal flow.

Status is the spawner's existing single `BackgroundImportStatus`; there is no aggregate.

**Remaining count.** Per level, `candidates − succeeded` from its `ForegroundImportOutcome` (failed
selected sessions are still outstanding); the run's count is the sum, or unknown if any level's
candidate list is unknown. The copy calls it "sessions still to import", which is what it counts.

Exclusion prompts: the child's stdin is redirected, so it cannot prompt — the non-interactive path
the terminal flow's child already takes (`autoSkipExclusions` is set by `RunPassAsync` either way).

### 3. Handoff file (CLI)

After the foreground passes and the spawn, the lane writes the same `import-handoff-<runId>.json` the
terminal flow writes, through `ImportHandoffFile.Compose`, from a merged `ForegroundImportOutcome`:

- **Merge rule.** `RunCandidateIds` concatenates each level's known candidates, `OnlyMe` first, or is
  null when every level's list is unknown. `SucceededIds` concatenates in the same order. Certainty is
  `Complete` only if every level's is.
- **Cohort label.** `Compose` takes an explicit cohort override: when any level's list is unknown but
  another's is known, the cohort is `partial_exact`, whatever the id count — so eval-watch's
  all-complete stop rule never treats a half-enumerated cohort as exact. Otherwise the existing
  500-id rule decides.
- `background` / `background_log`: the single child's status and log.
- `scope`: `"repos"` for this flow (the skill does not read it; it stays truthful).
- `handoff_offered` / `handoff_suppressed`: `HandoffDecision.Decide` over the merged outcome, with the
  eligible-vendor count from `HandoffVendorEligibility.Eligible` — the terminal flow's predicate, so
  the page offers the prompt exactly when the terminal would have offered an agent.

Writing stays best-effort; a write failure is reported to the page as a suppressed handoff
(`handoff_file_unwritten`, new token) so the page never offers a prompt whose file does not exist.

### 4. eval-watch identity check (CLI)

A pasted prompt runs in whatever agent the user opens, not one setup launched with `KCAP_PROFILE`
pinned, and the agent's `kcap-sessions` MCP server resolved its profile when it started (from its
environment and working directory). Checking a fresh `kcap whoami` is not proof of what that server
uses, so the check moves to the connection itself:

- `get_session_evals` (`SessionEvalsTool`) returns, beside its entries, the `server_url` and `profile`
  the MCP server is using.
- eval-watch's section 4 ("Bind to the server") makes one `get_session_evals` call on the first cohort
  id and compares both fields to the file's before reading any result. A mismatch closes with no eval
  shown and the existing remediation, extended: set `KCAP_PROFILE=<profile>`, unset `KCAP_URL`, start
  the agent from that shell (restarting it, since its MCP server is already running), then prompt
  again. The `kcap whoami` comparison stays as a pre-check for a clearer message when kcap itself is
  not logged in.
- An older MCP server without the fields: the skill falls back to today's `whoami` server check.

### 5. Outcome report (CLI → server contract)

`POST /import-outcome` is sent **after the foreground passes and the spawn**, not after the whole
import. It gains four optional fields:

| Field | Type | Meaning |
|---|---|---|
| `background` | `"not_needed" \| "running" \| "exited_zero" \| "failed"` | the child's status at the readiness check |
| `background_remaining` | non-negative int | sessions still to import, when known |
| `handoff_prompt` | string, ≤ 200 chars | the exact prompt, `SetupCommand.HandoffPromptText(runId)` |
| `handoff_suppressed` | `HandoffSuppressedReason.Wire()` token, or `handoff_file_unwritten` | why no prompt is offered |

- Counts: the foreground passes' summed totals when `Complete`; otherwise `run_failed` on three zeroes,
  as today.
- **The new fields ride on `run_failed` too**, so a lost pass does not hide a running child or a prompt
  for the pass that landed. The server accepts them alongside a reason; a reason still forbids
  non-zero counts.
- `handoff_prompt` and `handoff_suppressed` are mutually exclusive; both absent means an older CLI, or
  an outcome with nothing to hand off (`decision_unreadable`, `no_readable_agents`, a decline).
- **The CLI owns the prompt's wording.** The skill that answers it ships with the CLI and matches on
  that phrase, so the server renders the string it was given. The server validates it: length,
  printable characters and `\n` only.
- Compatibility: an older server ignores the new members (`ImportOutcomeRequest` uses default
  deserializer options, which skip unknown members) and shows today's Done screen, including "Import
  finished" while the child runs — tolerable for the rollout window, and the reason the server PR ships
  first. An older CLI sends none; the page renders today's screen.

### 6. Server: record, settle, render (kcap-server)

**Record.** `ImportOutcomeRequest`, `FirstRunImportOutcome`, `FirstRunImportOutcomeReportedEvent` and
`FirstRunImportOutcomeView` gain the four optional fields; the event change is additive. The endpoint
rejects an unknown `background` or `handoff_suppressed` token, a negative count and a malformed prompt.
The `import_outcome` telemetry gains a `background` dimension; its `clean`/`partial` label describes the
foreground passes only.

**Settle.** `_settled` keeps meaning "the import is over as far as this screen is concerned", and a
`background: running` report never sets it. Instead, `View.BackgroundRunning` (the reported outcome's
`background` is `running`) puts the screen in a background arm:

- **Arrangement.** The in-progress arrangement stays (fractions, progress bar, recent-sessions frame).
  The progress fraction is capped below 100% while in this arm, because the discovery-time expected
  total is not what will land and must not read as done.
- **Polling.** On the report, the component starts a fresh `PollAsync` if none is running (a poll that
  already ended on quiet before the report arrived is restarted), with a 30-minute budget measured from
  the report. In this arm quiet does not end the reads; only the budget, or the user leaving, does.
- **Ending.** At the budget, the screen settles into the "wait gave up" arrangement with background
  copy: "The rest is still importing in the background — it keeps landing in your workspace." Nothing
  in this arm ever says the import finished.
- **Announcements.** The announcement logic checks `BackgroundRunning` before `Reported`: on the report,
  "Your newest sessions are in. The rest is importing in the background."; at the budget, the
  background copy above. Neither path reaches "Import finished".
- `background` of `not_needed`, `exited_zero` or `failed`, or absent, settles on the report as today.

**Render.** `DoneStep.razor` adds one panel, shown once the outcome has arrived:

- **Prompt offered**: "Paste this into a coding agent on *machine label* to watch your newest
  sessions' evals", the prompt in a monospace block with a copy button, and — when `background` is
  `running` — "N sessions still to import in the background" (or "The rest of your history is importing
  in the background" when the count is absent).
- **Suppressed for eligibility** (`skill_not_installed`, `no_agent_detected`): a link to
  `/sessions?status=ended`, mirroring the terminal's fallback.
- **Suppressed by the import itself** (`import_failed`, `no_new_sessions`, `nothing_landed`) or
  `handoff_file_unwritten`: no panel.
- **`background` failed**: a warning line naming `kcap import` as the way to bring the rest over —
  shown on `run_failed` too.
- **`run_failed` with `background: running`**: the failure line says the rest, including what failed,
  is being retried in the background. Accurate, because the single child re-runs every chosen level.

### 7. Terminal during the browser flow (CLI)

- `progress.Importing` says it is importing the newest sessions first.
- After the passes, the terminal prints the background line the terminal flow prints
  (`PrintBackground`).
- `BrowserImportSummary` at the end of setup also prints the paste block when a prompt was offered,
  so a user who closed the tab still has it.

## Error handling

- A foreground pass that throws or reports nothing: the outcome is `run_failed`; the child still runs
  every chosen level, and the other level's foreground still feeds the handoff file and the prompt.
- A failed foreground visibility write: the session is dropped from the foreground (private) or counted
  failed (shared), and the child's uncapped pass retries the write.
- A spawn failure: `background: failed`; the page shows the `kcap import` warning; the prompt can still
  be offered for what landed (`HandoffDecision` already distinguishes these).
- A plan-file write failure: no child is spawned and `background` is `failed`.
- Cancellation during the foreground passes cancels as today; no child is spawned after cancellation.

## Testing

CLI (TUnit):
- `SetupImportLane`: each chosen level runs capped; a throwing pass still leaves the other level's
  outcome in the result; exactly one spawn whenever any level was chosen, none on a decline.
- `ImportCommand` under `maxSessions` with `forcePrivate` / `shareWithOrg`: visibility writes cover
  only selected sessions; a selected existing session whose private write fails is dropped.
- Child plan path: the plan round-trips; `Program`'s detached plan mode runs levels in order with each
  level's `forcePrivate`/`shareWithOrg`; the plan variable is ignored without the detached log; an
  unreadable plan fails the child.
- An all-`AlreadyLoaded` repository at each level: the foreground uploads nothing and the child's pass
  performs the visibility write.
- Merge: candidate order; one unknown level yields `partial_exact`; both unknown yields `unknown`;
  remaining count includes failed selected sessions.
- `SessionEvalsTool` returns `server_url` and `profile`.
- `BrowserFirstRunFlow`: outcome carries `background`, `background_remaining` and exactly one of
  prompt/suppressed, including on `run_failed`; handoff-file write failure reports the suppression.

Server:
- Endpoint validation of the new fields, including alongside a reason; event round-trip with and
  without them.
- `DoneStep`: a `background: running` report does not settle or announce finished; a poll that ended
  on quiet before the report restarts; quiet does not end reads in the background arm; the fraction
  never shows 100% in that arm; budget exhaustion settles with the background copy and announcement;
  each panel state renders.

## Delivery

Two PRs: the server change first (accepts the fields, settles on them, renders the panel; inert
without them), then the CLI change, which includes the eval-watch skill and `SessionEvalsTool`
changes. The CLI bumps kcap-server's `src/cli` submodule pin only when the server needs the shared
models.
