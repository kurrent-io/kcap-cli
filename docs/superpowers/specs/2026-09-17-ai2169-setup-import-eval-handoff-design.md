# AI-2169 — Setup import: everything on disk, newest first, background remainder, eval-watch handoff

Issue: AI-2169 / GitHub #641. Supersedes the 2026-08-21 draft of the same design, which predates the
browser first-run flow, the injected setup collaborators and the harness registry.

## Problem

`kcap setup`'s terminal import step imports one repository — the one the user happens to run setup
from — synchronously, in effectively arbitrary order (chains alphabetical by transcript slug), and
then prints a prompt to paste into an agent. The first value moment, evals landing on the user's own
sessions in the tenant, happens after setup has exited, unseen. The rest of the machine's history
never moves unless the user later discovers `kcap import --all`.

On tenants where the browser first-run flow is on, the Import screen already chooses several
repositories and a window, and the terminal step reports what ran. That path is not this change's
concern: it stays exactly as it is. This spec is about the terminal path — the one that runs when
the browser did not answer Import, when the user handed over with `t`, or when the tenant does not
offer the flow.

## Terms

- **Classification**: the pre-import server probe. Each discovered session gets exactly one
  `ClassificationStatus` (`ImportCommand.cs:300`): `New`, `Partial` (server holds a prefix), `AlreadyLoaded`
  (watermark says done), `TooShort`, `Excluded`, `InternalSubSession`, `ProbeError` (the probe itself
  failed). Watermark state is known **before** any import work.
- **Capture scope**: the profile's `allowed_paths` / `allowed_repos` / excluded lists, applied to
  classifications after the probe by `CaptureScope.ApplyAsync` (`ImportCommand.cs:1189`). A session
  the scope withholds is stamped, not deleted; `CaptureScope.Actionable` names the sessions this run
  could still send.
- **Importable set**: actionable classifications with status `New` or `Partial`, after capture
  scope. `AlreadyLoaded` / `TooShort` / `Excluded` / `InternalSubSession` are terminal skips and never
  enter it. `ProbeError` is *unknown work*: not importable this run, not proven done.
- **Run candidate set**: every session id this setup run may cause to land — the importable set
  (selected and unselected) plus `ProbeError` ids. Known in full at classification time. The eval-watch
  cohort (§5) is this set.
- **Chain**: file-based sessions (Claude, Codex) sharing a transcript slug, ordered ascending so
  `previous_session_id` continuation links hold. Routed sessions (the other seven vendors) are
  imported in a second phase and never chain.

## Goals

- Setup's terminal import step imports **every session on this machine**, across all detected
  vendors and all repositories, including sessions with no resolvable repository — what
  `kcap import --all` imports today.
- Sessions are dispatched **most recent first**, so the user's freshest work lands earliest. The
  ordering change applies to plain `kcap import` too.
- After a small foreground pass (about five sessions), the rest imports in a **detached background
  process**, independent of any later choice the user makes in setup.
- Setup then **hands the user to a coding agent of their choice** — any of the nine vendors — running
  a prompt that follows the import and the evals, summarizes the first three completed evals, links
  to the results and offers the guided tour.

## Non-goals

- The browser Import screen, `SetupImportLane`, and the report/decision wire types. When the browser
  answered Import, step 6 reports as today and none of this runs.
- Triggering evals. The server dispatches them on session close; this change only observes.
- Any `kcap import` surface change beyond ordering: no new public flags, no prompt or handoff
  outside setup, `--no-prompt` keeps a full synchronous import.
- Launch recipes are argv shapes only. No vendor-specific permission flags, models or config.
- Merging the chain and routed import phases into one globally ordered schedule (the TTY renderer
  is sized to four slots and the two-phase split stays).

## Decisions taken with the owner

- **Terminal path only.** The browser path is out of scope for this change.
- **Visibility is the profile default, org-gated.** Exactly what `kcap import --all` does: the
  profile's `default_visibility` applies, and org-shared is admitted only where the repository owner
  matches the tenant's configured org, so sessions from foreign repositories land owner-only without
  a prompt. No extra `--private` pass.
- **Repo-less sessions are included.** They are 44% of a real developer machine (1552 of 3552 on the
  owner's). They land as repo-less sessions; the terminal names the count and points at `kcap remap`.
- **Titles.** The foreground pass keeps local title generation, so the sessions the user is about to
  watch have names. The background child passes `--skip-title` and leaves titling to the server.
- **One yes/no prompt, default yes**, with the discovery figures printed first. Everything or
  nothing; `kcap import` remains the place for finer scopes.

## Design

### 1. Newest-first ordering (dispatch priority, both entry points)

`ImportCommand.BuildImportChains` (`ImportCommand.cs:2929`) orders chains alphabetically by slug and
slug-less solo chains by session id. Change:

- Cross-chain **dispatch order** becomes descending by `chain.Max(ChainTimestamp)`
  (`ChainTimestamp`, `ImportCommand.cs:2059`: `Meta.FirstTimestamp`, else file mtime, else
  `DateTimeOffset.MinValue`). Slug-less solo chains merge into the same descending order by their own
  `ChainTimestamp`.
- Routed-source sessions get the same descending `ChainTimestamp` sort before their
  `Parallel.ForEachAsync`.
- **Tie-breakers, fully deterministic:**
  - Cross-chain: equal max timestamps break descending by the chain's **maximum session id**
    (ordinal). A chain whose max timestamp resolved to `MinValue` sorts last, then by the same key.
  - Within-chain: unchanged — ascending `ChainTimestamp`, then ascending session id (ordinal). This
    feeds `BuildContinuationMapFromClassifications` and is pinned by test, not left to a library's
    nullable-sort default; a `MinValue` member sorts first.
  - Routed: equal timestamps break descending by session id; `MinValue` sorts last, then descending
    session id.
  - Two runs over the same corpus produce the same order everywhere.

**What newest-first means, and does not.** The invariant is *dispatch priority*, not strict landing
order: file-based chains drain first through the four workers and routed sources run in phase two, so
a newer routed session still lands after older chain sessions; a chain whose max timestamp is newest
is dispatched first but imports its oldest member first; four workers make completion order differ
from dispatch order. User-facing text says "most recent sessions are prioritized", never "arrive
newest-first". Today's cross-chain order is arbitrary, so nothing can depend on it.

### 2. Step 6 in the terminal

**Entry.** `RunImportStepAsync` (`SetupCommand.cs:1194`) keeps its first branch: a browser
`FirstRunImportAnswer` means report and return. Otherwise `SetupDecisions.DecideImport`
(`SetupDecisions.cs:118`) loses its `hasCurrentRepo` gate — the scope is the machine, so a directory
without an origin remote no longer skips the step. The remaining gates are unchanged: not
authenticated → skip with reason; `--skip-import` → skip; `--no-prompt` → run without asking. The
"outside a git working tree" tip at the summary stays, since it is about recording, not importing.

**Discovery first.** Before the prompt, step 6 runs
`HandleImport(discoverOnly: true, onDiscovered: …)` over every detected vendor — the same call
`SetupImportLane.DiscoverAsync` makes for the browser — through the injected `ISetupImportRunner`
(a new `DiscoverAsync` member beside `RunAsync`, so tests substitute figures without a disk scan).
It prints three figures under the step's rule: repositories, sessions attributed to one, sessions
matching none. When the total is zero the step prints one line and ends without a prompt.

**The prompt.** `Import past sessions from this machine?`, default yes. Decline prints the
`kcap import --all` hint and the step ends. `--no-prompt` answers yes and runs today's full
synchronous import over `ImportScope.All` with no cap, no child, no handoff. This is the one
behaviour change unattended callers see — `kcap setup --no-prompt` now uploads the machine's history
rather than the current repository's — and the README says so where it said so for the repo-scoped
change.

**The invocation.** `ImportInvocation` (`ImportInvocation.cs`) becomes:

```
ImportInvocation(
    ImportScope                  Scope,             // All for setup's terminal step
    int?                         MaxSessions,       // null = unbounded (--no-prompt)
    (string Owner, string Name)? CurrentRepo,       // evidence hint only; may be null
    string?                      DefaultVisibility,
    bool                         AutoSkipExclusions,
    bool                         ForcePrivate,
    bool                         SkipTitle,
    ProfileContext               Profiles)
```

`SetupImportRunner.RunAsync` passes `Scope`, `MaxSessions` and `SkipTitle` straight through to
`HandleImport` with `skipConfirmation: true`, `nested: true`, and everything else as today. It runs
against the server this run chose (`ChosenServerHttp.For`), unchanged.

**Foreground selection.** `HandleImport` gains an internal `maxSessions` parameter (default null;
plain `kcap import` never sets it). When set, selection happens **after classification and after
capture scope**, and **before any import work starts**:

1. Take whole chains in the §1 descending order, accumulating their importable-session counts, until
   the total reaches `maxSessions`. Chains are never split; the boundary chain is taken whole, so the
   selection may overshoot by at most that chain's remaining length. A pathological 50-session chain
   means a 50-session foreground pass — accepted; chain integrity and determinism beat cap exactness.
2. If chains yield fewer than `maxSessions`, extend the selection with routed sessions in descending
   order until the total is reached or the importable set is exhausted.

`AlreadyLoaded` is a classification, not a runtime discovery, so watermarked sessions never consume
selection slots: a corpus whose five newest sessions are watermarked selects the five newest
*importable* ones. Because selection precedes execution there is no dispatch/completion race and no
shared counter: the pass imports exactly the selected set through the existing pool, phases and
renderer, and nothing else. `SelectForeground` is a pure static function over the ordered chains and
routed list, unit-tested on its own.

**Reporting the selection.** `ImportRunOutcome` (`ImportCommand.cs:664`) gains a nullable
`ImportRunSelection`:

```
ImportRunSelection(
    IReadOnlyList<string> RunCandidateIds,   // importable + ProbeError, §1 order
    IReadOnlyList<string> SelectedIds,
    IReadOnlyList<string> SucceededIds,      // loaded + resumed
    IReadOnlyList<string> FailedIds,
    int                   RemainderCount)    // importable − selected
```

Populated only when `maxSessions` was set; `onFinished` delivers it. Ids in these lists are the
same normalized session ids the server receives.

**Totalized outcome.** From the runner's exit code, the `ImportRunOutcome` (captured via
`onFinished`) and any exception, setup builds a `ForegroundImportOutcome` and never lets an exception
escape the step:

```
ForegroundImportOutcome {
  Certainty:        Complete | Incomplete,   // Incomplete = an exception interrupted the pass
  Selected:         int,
  Succeeded:        int,
  Failed:           int,
  RemainderExists:  bool,                    // RemainderCount > 0 || any ProbeError
  RunCandidateIds:  string[] | null          // null when classification never completed
}
```

On `Complete`, `Selected == Succeeded + Failed`: every selected session ended as exactly one of the
two, since all skip reasons are classification statuses excluded before selection. On `Incomplete`,
`Succeeded + Failed <= Selected`; a session in flight when the exception hit is counted in the gap,
and the background child covers it. A throw after classification keeps `RunCandidateIds`; a throw
before or during yields `null`.

**Background child.** After the foreground pass and before any picker, setup spawns a detached
child **iff** `RemainderExists || Failed > 0 || Certainty == Incomplete`. Full success over a fully
selected importable set with no probe errors — including the all-watermarked rerun — spawns nothing.

- Command: `kcap import --all --yes --skip-title`, executable `Environment.ProcessPath`, working
  directory the current directory.
- Environment: `KCAP_IMPORT_DETACHED_LOG=<config dir>/import-<utc-timestamp>.log`, plus
  `KCAP_URL` and `KCAP_PROFILE` (`ProfileOverrides.UrlVar` / `ProfileVar`) set to the server and
  profile this run chose, so the child imports against the same server the foreground did. No other
  environment is added or removed.
- Child-side contract: when `KCAP_IMPORT_DETACHED_LOG` is present, `kcap import` opens that file
  itself (append, `FileShare.ReadWrite`), points its console output there, treats output as non-TTY
  line mode, and calls `ProcessHelpers.DetachFromControllingTerminal()` (setsid; no-op on Windows).
  Absent the variable, `kcap import` behaves exactly as today; the detach path is unreachable from the
  public surface.
- Spawner mechanics follow `DaemonCommands.StartDetached` (`DaemonCommands.cs:160`) exactly:
  `UseShellExecute=false`, all three std streams redirected and closed by the parent immediately
  after start, `ProcessHelpers.PreventInheritedHandles()` before `Start`, `CreateNoWindow=true`.
- The spawner is an injected `IBackgroundImportSpawner` on `SetupCommand`'s constructor, with a fake
  in tests, mirroring `ISetupImportRunner`.

**Background status** is a four-state enum, decided by a 1500ms `WaitForExit` readiness check:

| Status | Meaning | Terminal line |
|---|---|---|
| `NotNeeded` | spawn condition false | — |
| `Running` | child survived the window | `Importing the remaining sessions in the background · log: <path>` |
| `ExitedZero` | child exited zero inside the window | `Background import exited immediately (exit 0) — details in <path>` — not a warning, and deliberately not a claim that the remainder completed |
| `Failed` | spawn exception, no PID, or non-zero exit inside the window | yellow warning with the exit code when there is one, and `kcap import --all --yes` as the retry |

`Running` is a launch-time snapshot: a later child failure surfaces in the log and, to the skill, as
cohort ids that never gain evals.

**Rendering.** The figures, the prompt, the status lines and the paste block render under step 6's
rule with the import display nested, following the section-label path the setup polish established.
No live Spectre region wraps the step: the import renders its own bars, and two live regions cannot
share a console.

### 3. Handoff file

Written after the spawn decision to `<config dir>/import-handoff-{run_id}.json`, `run_id` a fresh
GUID in N format. Temp file plus atomic rename, so a crash cannot leave a parseable partial. On each
write, files older than seven days are pruned. Nothing earlier shipped, so `schema_version` starts at
1.

```json
{
  "schema_version":           1,
  "run_id":                   "<guid-n>",
  "written_at":               "<utc iso-8601>",
  "handoff_offered":          true,
  "foreground_certainty":     "complete | incomplete",
  "server_url":               "<config server_url, no trailing slash>",
  "scope":                    "all",
  "cohort":                   "exact | partial_exact | unknown",
  "session_ids":              ["<run candidate set, §1 order>"],
  "foreground_succeeded_ids": ["..."],
  "unattributed_count":       1552,
  "background":               "not_needed | running | exited_zero | failed",
  "background_log":           "<path or null>"
}
```

- `cohort: "exact"` — `session_ids` is the complete run candidate set.
- `cohort: "partial_exact"` — the candidate set exceeded **500**; `session_ids` holds the newest 500
  in §1 order. The skill watches exactly those and says so.
- `cohort: "unknown"` — `Certainty == Incomplete` with `RunCandidateIds == null`; `session_ids` is
  empty and meaningless; the skill uses heuristic mode.
- `unattributed_count` is discovery's repo-less figure, so the skill can say how many watched sessions
  will show no repository.
- Every run writes its file; `handoff_offered` records §4's gating outcome.

Write failure is best-effort: warn, skip the file, continue. The skill has a documented fallback.

### 4. Agent handoff (setup only)

**When.** Offered iff the import step ran and `Succeeded ≥ 1 || background ∈ {Running, ExitedZero}`.
`Failed` background with zero successes offers nothing — the warning already gave the retry. A rerun
where everything was already imported (`NotNeeded`, `Succeeded == 0`) ends setup as today.
`--no-prompt` never shows it.

**Who is offered.** `HarnessRegistry.Identities` in registry order, filtered to vendors that are
`Detected(id)` **and** whose skills location holds the eval-watch skill after step 4 — the same on-disk
oracle `ShouldOfferGuidedTour` (`SetupCommand.cs:1111`) applies to the guided-tour skill, evaluated
per vendor: Claude through the registered plugin marketplace path, Kiro and Antigravity through their
own skills directories, every other vendor through the shared `~/.agents/skills` tree. A vendor
without the skill would receive a prompt nothing answers. Zero eligible vendors → no picker; the paste
block prints directly.

**The picker.** A `SelectionPrompt` over the eligible vendors plus an always-present **Skip**. Skip
and cancelling the prompt behave identically: print the paste block, continue to the summary.

**Launch recipes — one per vendor, all nine launchable.** Every vendor's CLI documents an
interactive session that starts with a given prompt; each recipe below was read from the installed
CLI's own `--help`:

| Vendor | Recipe (`<prompt>` is one argv element, never shell-joined) |
|---|---|
| Claude | `claude <prompt>` |
| Codex | `codex <prompt>` |
| Cursor | `cursor-agent <prompt>` |
| Copilot | `copilot -i <prompt>` |
| Gemini | `gemini -i <prompt>` |
| Kiro | `kiro-cli chat <prompt>` |
| Pi | `pi <prompt>` |
| OpenCode | `opencode --prompt <prompt>` |
| Antigravity | `agy -i <prompt>` |

Recipes live in one registration site, a `HandoffLaunchRecipe` record per `HarnessId` (leading argv,
prompt position) beside the harness modules. The executable comes from
`HarnessRegistry.ResolveExecutable(id)` (`HarnessRegistry.cs:124`), which already returns null for an
IDE-only Kiro or Antigravity install, so those fall to the paste block with no special case.
**Launchable** = eligible and the executable resolves; an eligible vendor whose CLI is not on the
search path is listed and prints the paste block on selection.

**Launch semantics.** The agent runs as a foreground child of setup: resolved path as `FileName`,
`UseShellExecute=false`, no stream redirection, same process group (Ctrl-C reaches the agent; setup
deliberately does not detach it), working directory the current directory, no environment changes.
Setup blocks on `WaitForExit`; **"Setup complete" is deferred until the agent exits**, and the picker
says so ("setup will finish after you close the agent").

- Non-zero exit within exactly **2000ms** → launch failure: yellow warning, paste block, continue.
- Zero exit within 2000ms, or any exit after → the agent ran; continue to the summary.

Skipping, cancelling or a failed launch changes nothing about the import: the child, when one was
needed, is already running. The launcher is an injected `IHandoffAgentLauncher` with a fake, so
tests assert the argv per vendor and each fallback without a process.

**The pinned prompt.** `Follow my kcap import` — a constant beside `GuidedTourPrompt`
(`SetupCommand.cs:1145`), exact wording test-pinned and verified verbatim against the skill's
frontmatter description. Both forms carry the run id so concurrent runs stay distinguishable:

- Launched agents receive two lines: `Follow my kcap import` then `(run: <run_id>)`.
- The paste block prints the same two lines as one copyable block and joins the Next-steps panel
  (`NextStepItems`, `SetupCommand.cs:1087`) as its own item above the guided-tour item. A user who
  later types the bare phrase still triggers the skill; resolution falls to newest-match, disclosed.

Plain `kcap import` never shows the picker or the prompt; the handoff is composed entirely inside
`SetupCommand`.

### 5. The eval-watch skill

New skill `kcap/skills/eval-watch/SKILL.md`, added to `AgentsSkillsInstaller.SourceNames` and to
`Resources/help-plugin.txt` (both test-pinned), so it installs everywhere guided-tour does. Trigger:
the pinned prompt `Follow my kcap import` in the frontmatter description.

**Correlation source — the handoff file.** Validation is layered; a failure in a lower layer never
re-opens selection:

- **Layer A — locator** (may this file be *selected*?): well-formed JSON; known `schema_version`;
  `run_id` matches the GUID-N grammar (and equals the prompt token for a run-id reference);
  `written_at` parses as ISO-8601 and is within 24h; `handoff_offered` is a boolean. The 2026-08-21
  draft's cwd-repository match is gone: the run spans repositories. Failure → not selectable.
- **Layer B — cohort payload** (may its ids drive *exact data*?): `cohort`, `background` and
  `foreground_certainty` are known enum values; every entry of `session_ids` and
  `foreground_succeeded_ids` matches the session-id grammar (hex/uuid); `unattributed_count` is a
  non-negative integer. Failure → data drops to heuristic, disclosed; the file stays selected.
- **Layer C — link payload**: an invalid `server_url` (not http/https) cuts file-sourced links **and**
  makes the binding below unverifiable, so data drops to heuristic too. `background_log`, when shown,
  must be a plausible path (no control characters, bounded length) rendered as plain text, never a
  link; otherwise omitted.

An unknown future `schema_version` fails layer A. Unvalidated values are never spliced into SQL,
paths or links.

**Server binding.** The analytics MCP queries the active profile's server, so the skill runs
`kcap whoami` and compares its server URL to the file's under canonicalization: scheme and host
lowercased, the scheme's own default port elided (80 for http, 443 for https, so `http://host:443`
stays distinct), trailing slash trimmed, path compared as-is. A mismatch or a `whoami` failure over a
*valid* `server_url` keeps file-sourced links with heuristic data, disclosed.

**Resolution, first action of the skill:**

1. A `(run: <id>)` line in the invoking prompt: the token must match the GUID-N grammar **before**
   being used as a filename component. A locator-valid file is selected. If it is a no-handoff file
   (`handoff_offered: false`), branch on the recorded outcome and close with the closing block:
   *proven no-work* (layer B fully valid, `foreground_certainty: "complete"`, `cohort: "exact"` with
   empty `session_ids`, background `not_needed`) → "nothing to watch — that import had no new work";
   anything else → "that import did not get running" (or "its record is unreadable"), naming the
   `kcap import --all --yes` retry and the `background_log` when displayable. Locator failure →
   explain and fall to step 2.
2. No valid run id: among `import-handoff-*.json`, the newest locator-valid file with
   `handoff_offered: true`. More than one → the skill says it picked the newest and names the others.
   Matches disagreeing on `server_url` with no provable binding → the newest supplies links, the
   ambiguity is disclosed, data is heuristic.
3. Nothing qualifies → **heuristic cohort**. A selected file with `cohort: "unknown"` routes its
   *data* here; its layer-C fields still serve the links.

**Cohorts.**

- **Exact**: the file's `session_ids` — the run candidate set, so sessions the background child lands
  before the skill's first snapshot are counted, and sessions imported concurrently by anything else
  are not. Completed eval rows already present at the first snapshot count.
- **Partial-exact**: identical mechanics over the newest 500; the skill opens by saying it watches the
  newest 500 sessions of this import and that older ones may land and evaluate unobserved. Omitted
  candidates are invisible to the bounded queries and are never labelled unrelated.
- **Heuristic**: the skill says it is watching recent activity rather than a specific import —
  completed-eval arrivals after the first snapshot plus runs with `evaluated_at` in the ten minutes
  before start, tenant-wide. Another user's sessions can be counted — accepted and disclosed.

**Data contract — every query under `scope: 'global'`.** The `kcap-analytics` MCP defaults to the
cwd repository and refuses to widen silently (`McpAnalyticsServer.BuildQueryBody`,
`McpAnalyticsServer.cs:275`), so the skill passes `scope: 'global'` on every call: the cohort spans
repositories and includes repo-less sessions, whose `repo_hash` is null.

- Cohort arrivals: `SELECT session_id, repo_hash FROM v_an_sessions WHERE session_id IN (…)` in
  batches of at most 100 ids — presence is membership, complete and cap-safe. `repo_hash` is kept
  per session for the links.
- Cohort completions: `SELECT session_id, eval_run_id, evaluated_at, overall_score, judge_model
  FROM v_an_eval_summaries WHERE session_id IN (…)`, same batching.
- Displayed import progress: `SELECT COUNT(*) FROM v_an_sessions` — one row, shown as a total, never
  used for identification.
- Heuristic mode: `v_an_eval_summaries` ordered by `evaluated_at` descending within the row cap; new
  rows enter at the top, so a capped read still surfaces them, and the skill notes the window when
  the cap is hit.
- Per-category detail: `v_an_eval_scores` (`session_id`, `category`, `question_id`, `score`) joined
  to summaries on `session_id`. Deterministic aggregation: per-category mean; strongest = highest,
  weakest = lowest, ties alphabetical by category; up to two lowest-scoring questions ordered by
  `score` then `question_id`. A session with no score rows is summarized by `overall_score` alone.
  Titles may be enriched through the `kcap-sessions` MCP when present, else the id suffices.
- There is deliberately no repo-wide id diff and no unlisted-arrival narration: identifying non-cohort
  rows would need exactly the unbounded scan this contract forbids.

**Logical-poll atomicity.** One poll = every required query (all membership and summary batches; in
heuristic mode the capped summary read). Any required-query failure discards the whole poll — no
baseline, dedup or completion state commits from it — and counts as one error toward the two-failure
stop. Each successful poll recomputes cohort state from its own full results. Enrichment queries are
optional: their failure degrades summary content, never poll success or stop logic.

**Stop rules**, evaluated after each successful poll, its state committed first:

1. Three distinct cohort sessions with completed evals → summarize the deterministic first three
   (ordered by `evaluated_at`, ties by `session_id`; the same rule for pre-snapshot rows, so more than
   three arriving at once always select the same three).
2. **All-cohort-complete**: every id in `session_ids` has a completed eval. Requires `cohort: "exact"`
   with a non-empty list; never fires in partial-exact, heuristic or unknown mode, and never on an
   empty list. Sessions that never evaluate are covered by the deadline, not inferred.
3. Immediately on the second consecutive failed poll.
4. Deadline on a monotonic 10-minute clock: no new poll or query starts after it; an in-flight query
   overruns by at most its own duration (`query_analytics` exposes no cancellation). A poll completing
   at or after the deadline still commits first; a third completion in it wins over the deadline
   summary; a second consecutive failure in it is reported as the deadline's stop, mentioning the
   failures.

No idle early-stop: quiet polls are normal. Cadence: first snapshot immediately, then every 30
seconds.

**Links.** `server_url` with any trailing slash trimmed; `repo_hash` (16 hex) and session ids
(hex/uuid) need no encoding.

- Per session with a repo hash: `{server_url}/repo/{repo_hash}/sessions/{session_id}?tab=evaluation`.
- Per session without one: `{server_url}/sessions/{session_id}?tab=evaluation`.
- Full results: `{server_url}/sessions`.

Without a valid file, `server_url` comes from `kcap whoami`. If that also fails, the closing block
carries no link: it says the results live in the Capacitor server UI and that `kcap whoami`, once
logged in, prints the URL.

**Closing block, always emitted:** how to keep watching (re-prompt `Follow my kcap import`); the
full-results link or the no-link fallback; the guided-tour offer with the exact
`Start kcap guided tour` prompt; and, when `unattributed_count > 0`, one sentence saying that many
sessions were imported without a repository and can be placed with `kcap remap`.

**Degrade explanations** name the real gates in plain terms: auto-eval may be off for a repository;
the server needs an eval agent configured; sessions under the minimum event count are skipped;
already-evaluated sessions are not re-run; the recovery sweep runs hourly. If the analytics MCP is
absent entirely, the skill closes immediately with the block above.

## Assumptions

- The server dispatches evals on session close and sweeps stragglers hourly; `kcap import` posts
  `session_end`, so imported sessions qualify subject to the per-repo auto-eval setting, a configured
  eval agent and the minimum event count. Nothing here dispatches evals.
- `v_an_sessions`, `v_an_eval_summaries` and `v_an_eval_scores` carry `session_id` and `repo_hash`,
  and the global analytics route returns repo-less rows.

## Error handling

- Foreground pass: totalized (§2); a throw becomes `Incomplete`, setup warns as today, spawns the
  child, writes `cohort: "unknown"` when the candidate set is untrusted.
- Background `Failed`: warn with the exit code when there is one and `kcap import --all --yes`;
  continue. `ExitedZero` is reported without claiming completeness.
- Handoff-file write failure: warn, continue; the skill falls back to its heuristic cohort.
- Agent launch failure (no executable, or non-zero exit within 2000ms): warn, paste block, continue.
- Skill: analytics MCP absent → immediate close with the block; failed binding → file links,
  heuristic data, disclosed; two consecutive failed polls → early stop with the block.

## Testing

**Ordering** (`BuildImportChains`, routed sort): descending by max `ChainTimestamp`; within-chain
ascending preserved with `MinValue` first; slug-less merge; tie-breakers; identical output on a
repeated corpus; a newer routed session still lands in phase two.

**Selection** (`SelectForeground`): whole chains to the cap; boundary-chain overshoot; routed top-up;
importable ≤ cap selects everything; all-watermarked selects nothing; newest sessions `AlreadyLoaded`
→ newest importable selected; selection computed after capture scope and before any import (seam
ordering); `ImportRunSelection` carries candidate, selected, succeeded and failed ids plus the
remainder count.

**Outcome totalization**: throw before classification → `Incomplete`, `RunCandidateIds == null`,
spawn, `cohort: "unknown"`; throw after classification → `Incomplete` with ids; partial foreground
success → `Succeeded + Failed < Selected`; `Complete` ⇒ equality; nothing escapes to setup.

**Spawn decision and status** through `IBackgroundImportSpawner`'s fake: remainder → spawn; probe
errors alone → spawn; failures alone → spawn; clean full pass → no spawn; each of the four statuses
prints its pinned line and lands its file value; `ExitedZero` output claims no completeness; the
child's argv is `import --all --yes --skip-title`; the contributed environment carries
`KCAP_IMPORT_DETACHED_LOG`, `KCAP_URL`, `KCAP_PROFILE` (assert contributed values, never absence —
the repo's `.envrc` pollutes).

**Child env contract**: variable set → log file written, non-TTY output, detach called; unset →
behaviour identical to today; parent closes all three streams.

**Handoff file**: per-run filename; atomic write leaves no parseable partial; §3 shape;
`handoff_offered: false` for the all-watermarked run; >500 candidates → newest 500,
`partial_exact`; two concurrent runs → two files; >7-day files pruned on write; write failure warns
and continues.

**Handoff gating and picker**: `Succeeded ≥ 1` → offered; `Running`/`ExitedZero` → offered; `Failed`
with zero successes → not; `NotNeeded` with zero successes → not; `--no-prompt` → no handoff,
unbounded import, no spawn; eligibility per vendor (detected, skill present, executable resolves vs
not); IDE-only Kiro and Antigravity print the paste block; every recipe's argv pinned per vendor with
the two-line prompt as a single element; Skip and cancel print the paste block; launch failure
(non-zero < 2000ms) falls back; zero exit < 2000ms and any exit ≥ 2000ms proceed silently.

**Step 6 sequence**, end to end through the fakes: discovery → figures → prompt → foreground →
spawn decision → handoff file → picker/paste → agent wait → summary, including each failure branch;
the "no origin remote" run now imports; the browser-answered run is untouched.

**Prompt pinning**: exact `Follow my kcap import`; panel item composition; verbatim presence in the
skill's frontmatter description; `eval-watch` in `SourceNames` and `help-plugin.txt`.

**Skill acceptance** (scripted fixtures behind recorded `query_analytics` responses): run-id and
bare-phrase resolution including non-GUID rejection and stale-file fall-through; no-handoff files by
outcome; the layered validation cases; `scope: 'global'` on every query; repo-less members counted
and linked without a repo hash; partial-exact over 500 with a listed arrival a capped scan would miss;
poll atomicity and recovery; each stop rule at its boundary; deterministic first-three under ties;
the whoami-failure no-link closing variant; the `unattributed_count` sentence.

**README**: setup section rewritten for the machine-wide default, the figures, the background
import, the picker and the paste block; the `--no-prompt` behaviour-change callout updated; the import
section mentions newest-first prioritization and nothing else changes there.

**Publish**: `dotnet publish -c Release` prints no IL2026/IL3050 (the handoff file is written with
`JsonObject` and the `(JsonNode?)` cast pattern the repo already uses).

## Out of scope / follow-ups

- The browser Import screen, `SetupImportLane` and the first-run wire types.
- Server-side eval dispatch and its gates.
- A `kcap eval status` command; the skill reads analytics views only.
- Re-specifying `kcap import`'s aggregate exit-code semantics (`ExitedZero` claims nothing beyond
  the code).
- Vendor-specific launch options beyond the argv shape (permission modes, models, config).
- Merging the chain and routed phases.
