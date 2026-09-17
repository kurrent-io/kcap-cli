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
  the scope withholds is stamped, not deleted; `CaptureScope.Actionable` (`New`, `Partial`,
  `AlreadyLoaded`) names the sessions this run could still send.
- **Importable set**: actionable classifications with status `New` or `Partial`, after capture
  scope. `TooShort` / `Excluded` / `InternalSubSession` are terminal skips and never enter it.
  `ProbeError` is *unknown work*: not importable this run, not proven done.
- **Replay rows**: routed `AlreadyLoaded` classifications. `kcap import` deliberately includes them
  in the routed plan (`ImportCommand.cs:1305-1315`) so a vendor source can re-assert lifecycle hooks,
  backfill the repository node, or attach a previously unloaded nested child. They are actionable
  work, not new sessions.
- **Run candidate set**: every session id this setup run may cause to land — the importable set
  (selected and unselected) plus `ProbeError` ids. Known in full at classification time. The eval-watch
  cohort (§5) is this set. Replay rows are not candidates: they are already on the server.
- **Chain**: file-based sessions (Claude, Codex) sharing a transcript slug, ordered ascending so
  `previous_session_id` continuation links hold.
- **Routed unit**: one routed session, or a Cursor parent together with its correlated subagent
  children (`SourceMeta["SubagentChildren"]` / `IsSubagentChild` + `ParentSessionId`). Children are
  imported inline by their parent's call (`CursorImportSource.cs:578-605`) and their own call returns
  `ImportOutcome.Skipped` (`:422-438`), so the unit is the smallest thing that can run whole. A unit
  is **eligible** for foreground selection only when its parent is `New` or `Partial`; a unit whose
  parent is a replay row is left whole to the background child, however its children are classified.
- **Selection unit**: a chain or an eligible routed unit.
- **Remainder**: unselected `New`/`Partial` classifications, routed replay rows, and `ProbeError`
  classifications. File-based `AlreadyLoaded` rows are not remainder: nothing in `kcap import` runs
  them.

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
- Routed units get the same descending sort — a unit's timestamp is its parent's — before their
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

**Candidate order (cohort, not dispatch).** The handoff file (§3) needs one total order over every
candidate — chain members, routed sessions and `ProbeError` classifications alike — independent of
the two dispatch phases. `CandidateTimestamp(c)` = `Meta.FirstTimestamp`, else the transcript file's
mtime when `FilePath` is set, else `MinValue`. Candidates sort descending by `CandidateTimestamp`,
ties descending by session id (ordinal), `MinValue` last then by the same key. This comparator is a
pure static function with its own tests; it changes nothing about dispatch.

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
`kcap import --all` hint and the step ends; nothing else in this section runs. `--no-prompt` answers
yes and runs today's full synchronous import over `ImportScope.All` with no cap: **no child, no
handoff file, no picker**. This is the one behaviour change unattended callers see —
`kcap setup --no-prompt` now uploads the machine's history rather than the current repository's —
and the README says so where it said so for the repo-scoped change.

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
capture scope**, and **before any import work starts**, over selection units:

1. Take whole chains in the §1 descending order, accumulating their importable-session counts, until
   the total reaches `maxSessions`. Chains are never split; the boundary chain is taken whole, so the
   selection may overshoot by at most that chain's remaining length. A pathological 50-session chain
   means a 50-session foreground pass — accepted; unit integrity and determinism beat cap exactness.
2. If chains yield fewer than `maxSessions`, extend the selection with eligible routed units in
   descending order until the total is reached or the importable set is exhausted. A unit is taken
   whole and counts its parent plus every `New`/`Partial` child toward the cap.

**Selected ids versus executed ids.** Selected ids are the `New`/`Partial` members of the selected
units; they are what the partition below accounts for and what enters the cohort. The foreground
routed plan additionally *carries* a selected unit's `AlreadyLoaded` children, because the parent's
call needs them present in `routed` to import inline and their own call short-circuits to `Skipped`
with nothing sent. Carried children are not selected ids, consume no slot, appear in no partition
list and are not cohort candidates. A unit whose parent is `AlreadyLoaded` is never eligible, so its
`New`/`Partial` children are never selected in the foreground: they are remainder, and the
background child's full plan imports them through the parent's replay exactly as `kcap import --all`
does today. No replay parent ever runs in the foreground. `AlreadyLoaded` is a classification, not a
runtime discovery, so watermarked sessions never consume selection slots: a corpus whose five newest
sessions are watermarked selects the five newest *importable* ones.

Selection narrows the routed plan **before** `ReconcileOrphanedCursorSubagentChildren`
(`ImportCommand.cs:1325`), so the reconcile sees exactly the selected units' members (selected ids
plus carried children): a child whose parent was not selected is never in the plan, so it can neither
be orphaned into a standalone import nor imported by an absent parent, and a selected parent cannot
import a child outside its unit. Because selection precedes execution there is no dispatch/completion race and no
shared counter: the pass imports exactly the selected set through the existing pool, phases and
renderer, and nothing else. `SelectForeground` is a pure static function over the ordered chains and
routed units, unit-tested on its own.

**Terminal partition of the selected set.** Every selected session's own import call ends in exactly
one of `Loaded`, `Resumed`, `Skipped` or `Failed` (`ImportOutcome`, `IImportSource.cs:78`).
`Skipped` is a real runtime outcome, not a classification: a Cursor child imported inline by its
parent, a session quarantined after classification, a routed session with no sendable content. The
selection therefore partitions into **Succeeded** (`Loaded` + `Resumed`), **Skipped** and **Failed**,
and the run records which ids fell where. `ResolveRoutedOutcomeForCounting` (`ImportCommand.cs:522`)
keeps governing the Done-grid *counts*; the per-id partition is recorded from the raw outcome before
that suppression, so the two never disagree about an id.

**Reporting the selection.** `ImportRunOutcome` (`ImportCommand.cs:664`) gains a nullable
`ImportRunSelection`:

```
ImportRunSelection(
    IReadOnlyList<string> RunCandidateIds,   // importable + ProbeError, candidate order (§1)
    IReadOnlyList<string> SelectedIds,
    IReadOnlyList<string> SucceededIds,      // own call returned Loaded or Resumed
    IReadOnlyList<string> SkippedIds,        // own call returned Skipped
    IReadOnlyList<string> FailedIds,
    bool                  RemainderExists)     // Terms: unselected New/Partial, routed replay rows, ProbeError
```

Populated only when `maxSessions` was set; `onFinished` delivers it. Ids are the same normalized
session ids the server receives.

**Totalized outcome.** From the runner's exit code, the `ImportRunOutcome` (captured via
`onFinished`) and any exception, setup builds a `ForegroundImportOutcome` and never lets an exception
escape the step:

```
ForegroundImportOutcome {
  Certainty:        Complete | Incomplete,   // Incomplete = an exception interrupted the pass
  Selected:         int,
  Succeeded:        int,
  Skipped:          int,
  Failed:           int,
  RemainderExists:  bool,                    // the selection's RemainderExists
  RunCandidateIds:  string[] | null          // null when classification never completed
}
```

On `Complete`, `Selected == Succeeded + Skipped + Failed`: every selected session's own call ended
in exactly one of the three. On `Incomplete`, `Succeeded + Skipped + Failed <= Selected`; a session in
flight when the exception hit is counted in the gap, and the background child covers it. A throw
after classification keeps `RunCandidateIds`; a throw before or during yields `null`.

**Background child.** After the foreground pass and before any picker, setup spawns a detached
child **iff** `RemainderExists || Failed > 0 || Certainty == Incomplete`. Because routed replay rows
count toward `RemainderExists`, an all-watermarked Cursor corpus still spawns the child — the repairs
`kcap import --all` performs today are not lost to the cap. An all-watermarked Claude/Codex corpus
does not: file-based `AlreadyLoaded` rows are never run by any import, so there is nothing for a child
to do. Nothing spawns only when the remainder is empty, none failed and the pass completed.

- Command: `kcap import --all --yes --skip-title`, executable `Environment.ProcessPath`, working
  directory the current directory.
- Environment, following the detached-process precedent in `RefreshTokenHandoff`
  (`RefreshTokenHandoff.cs:49-53`): set `KCAP_CONFIG_DIR` (`ConfigRoot.ConfigDirEnvVar`) to this
  run's config directory, set `KCAP_PROFILE` (`ProfileOverrides.ProfileVar`) to the profile setup
  just saved, **remove** `KCAP_URL` (`ProfileOverrides.UrlVar`) — a URL override outranks the profile
  pin in `ProfileResolver` and resolves to *no* profile, which would let the child's allow lists and
  default visibility come from somewhere other than the profile setup wrote — set
  `KCAP_IMPORT_DETACHED_LOG=<config dir>/import-<utc-timestamp>.log`, and set
  `KCAP_IMPORT_DEFAULT_VISIBILITY` to the exact value the foreground pass stamped. Setup persists the
  profile (server URL, visibility, capture lists) before step 6, so the persisted profile is
  authoritative for the child's server and capture scope. No other environment is added or removed.
- **Visibility parity.** Plain `kcap import` passes no `defaultVisibility` to `HandleImport`
  (`Program.cs`); the profile's value is read only for the confirmation text
  (`ImportCommand.cs:1090`), and an omitted stamp leaves the server to choose. Setup's foreground
  pass stamps the saved default explicitly. The child must stamp the same value, so the detached
  contract carries it: when `KCAP_IMPORT_DETACHED_LOG` is present and `KCAP_IMPORT_DEFAULT_VISIBILITY`
  is set, the child passes that value as `defaultVisibility`. The variable is read **only** on the
  detached path — a plain `kcap import` ignores it — so the public surface is unchanged. Whether plain
  `kcap import` should honour the profile default itself is a follow-up (Out of scope).
- Child-side contract: when `KCAP_IMPORT_DETACHED_LOG` is present, `kcap import` opens that file
  itself (append, `FileShare.ReadWrite`), points its console output there, treats output as non-TTY
  line mode, applies `KCAP_IMPORT_DEFAULT_VISIBILITY` as above, and calls
  `ProcessHelpers.DetachFromControllingTerminal()` (setsid; no-op on Windows). Absent the log
  variable, `kcap import` behaves exactly as today; the detach path is unreachable from the public
  surface.
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

Written **iff the foreground pass ran** — an interactive run whose prompt was accepted. A declined
prompt, `--skip-import`, an unauthenticated skip and `--no-prompt` write nothing. Location:
`<config dir>/import-handoff-{run_id}.json`, where the config dir is `KCAP_CONFIG_DIR` when set,
else `~/.config/kcap`; `run_id` is a fresh GUID in N format. Temp file plus atomic rename, so a
crash cannot leave a parseable partial. On each write, files older than seven days are pruned.
Nothing earlier shipped, so `schema_version` starts at 1.

```json
{
  "schema_version":           1,
  "run_id":                   "<guid-n>",
  "written_at":               "<utc iso-8601>",
  "handoff_offered":          true,
  "handoff_suppressed":       null,
  "foreground_certainty":     "complete | incomplete",
  "server_url":               "<profile server_url, no trailing slash>",
  "scope":                    "all",
  "cohort":                   "exact | partial_exact | unknown",
  "session_ids":              ["<run candidate set, candidate order>"],
  "foreground_succeeded_ids": ["<own-call Loaded/Resumed ids>"],
  "unattributed_count":       1552,
  "background":               "not_needed | running | exited_zero | failed",
  "background_log":           "<path or null>"
}
```

- `cohort: "exact"` — `session_ids` is the complete run candidate set in candidate order (§1).
- `cohort: "partial_exact"` — the candidate set exceeded **500**; `session_ids` holds the first 500 in
  candidate order, so the cut is deterministic across chains, routed sessions and probe errors alike.
  The skill watches exactly those and says so.
- `cohort: "unknown"` — `Certainty == Incomplete` with `RunCandidateIds == null`; `session_ids` is
  empty and meaningless; the skill uses heuristic mode.
- `foreground_succeeded_ids` lists own-call successes only. A Cursor child landed inline by its
  parent is a candidate (it is in `session_ids`) but not listed here; the field's only consumer is
  the no-work branch below, which does not need it.
- `unattributed_count` is discovery's repo-less figure, so the skill can say how many watched sessions
  will show no repository.
- `handoff_offered` records §4's gating outcome. When it is `false`, `handoff_suppressed` names why,
  one of `"no_new_sessions"` (empty exact candidate set), `"import_failed"` (`Failed` background with
  zero successes, or `Incomplete` with nothing landed), `"analytics_not_in_plan"` (the cached plan
  denied analytics; the import itself ran). When `handoff_offered` is `true` it is `null`. The skill
  branches on this field rather than inferring the reason.

Write failure is best-effort: warn, skip the file, continue. The skill has a documented fallback.

### 4. Agent handoff (setup only)

**When.** Offered iff the foreground pass ran, the candidate set is non-empty or unknown
(`RunCandidateIds is null || RunCandidateIds.Length > 0`), and
`Succeeded ≥ 1 || background ∈ {Running, ExitedZero}`. An empty exact candidate set — a replay-only
or all-watermarked rerun — offers nothing whatever the background did, since there are no new
sessions to watch; setup ends as today. `Failed` background with zero successes offers nothing — the
warning already gave the retry. `--no-prompt` never shows it.

**Plan gate.** The eval-watch skill reads analytics, which the server denies to Free tenants
(`analytics_not_in_plan`). Setup consults the cached entitlement the CLI already keeps from the
`X-Kcap-Plan` response header (`PlanEntitlementStore.Get(serverUrl, …).Allows(PlanEntitlements.Analytics)`).
A cached denial → no picker and no paste block; the file records
`handoff_suppressed: "analytics_not_in_plan"`; the Next-steps panel shows the guided-tour offer
alone. Unknown or allowed → the handoff proceeds. The skill still handles the denial itself (§5),
because the cache can be stale.

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
deliberately does not detach it), working directory the current directory. Its environment gets the
same pin as the background child — `KCAP_CONFIG_DIR` and `KCAP_PROFILE` set to this run's config
directory and saved profile, `KCAP_URL` removed — because every `kcap mcp …` server and `kcap whoami`
the agent spawns inherits that environment, and an ambient `KCAP_URL` or a repository binding would
otherwise point the skill's queries at a server other than the one the handoff file names. Nothing
else in the environment changes. Setup blocks on `WaitForExit`; **"Setup complete" is deferred until
the agent exits**, and the picker says so ("setup will finish after you close the agent").

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

**Session-id grammar.** One anchored grammar covers every importer's normalized id space — dashless
GUIDs from Claude, Codex, Cursor, Copilot, Gemini, Kiro and Pi, Antigravity's ids, and OpenCode's raw
`ses_…` ids (`OpenCodeImportSource.cs:77`, deliberately not GUID-normalized):

```
^[A-Za-z0-9_-]{1,128}$
```

The analytics MCP takes SQL text with no parameter binding, so this grammar is also the SQL safety
boundary: every id the skill puts in an `IN (…)` list is validated against it first and emitted as a
single-quoted literal. The character class excludes quotes, backslash, whitespace, `;` and every
other character that could end the literal or the statement, so no escaping is needed and none is
attempted; an id that fails the grammar is never placed in SQL. A hyphen run such as `--` *is*
accepted, and is safe: inside a single-quoted literal whose contents cannot contain a quote it is
data, not a comment. Fixtures include a `ses_…` id, an id containing `--` (accepted, emitted
verbatim, queried), an id at the length bound, and hostile ids (embedded quote, `;`, whitespace,
non-ASCII, empty, over-length), each of which must degrade the file per layer B below and never
reach a query.

**Correlation source — the handoff file.** Validation is layered; a failure in a lower layer never
re-opens selection:

- **Layer A — locator** (may this file be *selected*?): well-formed JSON; known `schema_version`;
  `run_id` matches the GUID-N grammar (and equals the prompt token for a run-id reference);
  `written_at` parses as ISO-8601 and is within 24h; `handoff_offered` is a boolean. The 2026-08-21
  draft's cwd-repository match is gone: the run spans repositories. Failure → not selectable.
- **Layer B — cohort payload** (may its ids drive *exact data*?): `cohort`, `background`,
  `foreground_certainty` and `handoff_suppressed` are known enum values (or null where allowed);
  every entry of `session_ids` and `foreground_succeeded_ids` matches the session-id grammar;
  `unattributed_count` is a non-negative integer. Failure → data drops to heuristic, disclosed; the
  file stays selected.
- **Layer C — link payload**: an invalid `server_url` (not http/https) cuts file-sourced links **and**
  makes the binding below unverifiable, so data drops to heuristic too. `background_log`, when shown,
  must be a plausible path (no control characters, bounded length) rendered as plain text, never a
  link; otherwise omitted.

An unknown future `schema_version` fails layer A. Unvalidated values are never spliced into SQL,
paths or links.

**Locating the files.** The skill reads `import-handoff-*.json` from `KCAP_CONFIG_DIR` when that
variable is set in its environment, else `~/.config/kcap` — the same rule `ConfigRoot` applies.

**Server binding.** The analytics MCP queries the active profile's server, so the skill runs
`kcap whoami` and compares its server URL to the file's under canonicalization: scheme and host
lowercased, the scheme's own default port elided (80 for http, 443 for https, so `http://host:443`
stays distinct), trailing slash trimmed, path compared as-is. A mismatch or a `whoami` failure over a
*valid* `server_url` keeps file-sourced links with heuristic data, disclosed.

**Resolution, first action of the skill:**

1. A `(run: <id>)` line in the invoking prompt: the token must match the GUID-N grammar **before**
   being used as a filename component. **A valid run id binds the skill to that run and only that
   run.** Its file, when locator-valid, is selected. When the file is missing or fails layer A (for
   example the write failed), the skill says so and enters heuristic mode *for that run* — it never
   falls through to another run's file, because disclosure would not make another cohort's results
   belong to the requested run. If the selected file is a no-handoff file (`handoff_offered: false`),
   branch on `handoff_suppressed` — a layer-B-valid file is required to make any claim; otherwise
   close as "its record is unreadable" — and close with the closing block:
   `"no_new_sessions"` → "nothing to watch — that import found no new sessions" (a running background
   here is replay work); `"analytics_not_in_plan"` → the plan sentence below — the import ran and no
   retry is suggested; `"import_failed"` → "that import did not get running", naming the
   `kcap import --all --yes` retry and the `background_log` when displayable. A malformed run token
   is treated as no token.
2. No run id: among `import-handoff-*.json`, the newest locator-valid file with
   `handoff_offered: true`. More than one → the skill says it picked the newest and names the others.
   Matches disagreeing on `server_url` with no provable binding → the newest supplies links, the
   ambiguity is disclosed, data is heuristic.
3. Nothing qualifies → **heuristic cohort**. A selected file with `cohort: "unknown"` routes its
   *data* here; its layer-C fields still serve the links.

**Cohorts.**

- **Exact**: the file's `session_ids` — the run candidate set, so sessions the background child lands
  before the skill's first snapshot are counted, and sessions imported concurrently by anything else
  are not. Completed eval rows already present at the first snapshot count.
- **Partial-exact**: identical mechanics over the listed 500; the skill opens by saying it watches the
  500 most recent sessions of this import and that older ones may land and evaluate unobserved.
  Omitted candidates are invisible to the bounded queries and are never labelled unrelated.
- **Heuristic**: the skill says it is watching recent activity rather than a specific import —
  completed-eval arrivals after the first snapshot plus runs with `evaluated_at` in the ten minutes
  before start, tenant-wide. Another user's sessions can be counted — accepted and disclosed.

**Plan denial.** An analytics response of HTTP 403 `analytics_not_in_plan` is a terminal degrade
recognised on sight, not a poll failure: the skill closes immediately with the closing block and says
that Insights is not in this tenant's plan, that the import continues regardless, and that evals
appear in the server UI. No polling, no retry, no wait.

**Data contract — every query under `scope: 'global'`.** The `kcap-analytics` MCP defaults to the
cwd repository and refuses to widen silently (`McpAnalyticsServer.BuildQueryBody`,
`McpAnalyticsServer.cs:275`), so the skill passes `scope: 'global'` on every call: the cohort spans
repositories and includes repo-less sessions, whose `repo_hash` is null.

- Cohort arrivals: `SELECT session_id, repo_hash FROM v_an_sessions WHERE session_id IN (…)` over a
  batch of ids — presence is membership. `repo_hash` is kept per session for the links.
- Cohort completions: `SELECT session_id, eval_run_id, evaluated_at, overall_score, judge_model
  FROM v_an_eval_summaries WHERE session_id IN (…)`, same batching.
- Displayed import progress: `SELECT COUNT(*) FROM v_an_sessions` — one row, shown as a total, never
  used for identification.
- Heuristic mode: `v_an_eval_summaries` ordered by `evaluated_at` descending — the window read
  described under "Batching and truncation"; new rows enter at the top, so a capped read still
  surfaces them.
- Per-category detail: `v_an_eval_scores` (`session_id`, `category`, `question_id`, `score`) joined
  to summaries on `session_id`. Deterministic aggregation: per-category mean; strongest = highest,
  weakest = lowest, ties alphabetical by category; up to two lowest-scoring questions ordered by
  `score` then `question_id`. A session with no score rows is summarized by `overall_score` alone.
  Titles may be enriched through the `kcap-sessions` MCP when present, else the id suffices.
- There is deliberately no repo-wide id diff and no unlisted-arrival narration: identifying non-cohort
  rows would need exactly the unbounded scan this contract forbids.

**Batching and truncation — two policies.** The server clamps every query to its own configured row
maximum, and a capped result is a *successful* response flagged `truncated: true` (the MCP appends a
warning trailer). The two cohort modes treat that differently, because they ask different questions:

- **Exact and partial-exact batches fail closed.** A membership or summary batch asks "which of
  these ids are present"; a truncated answer is incomplete and is never committed. Each batch
  requests `max_rows` equal to its size; the initial size is 100. On truncation the skill halves the
  batch size (floor 10) and re-issues within the same logical poll. If a batch of 10 still
  truncates, the poll fails, the skill says the server's row cap is below what exact watching
  needs, and the run drops to heuristic mode from the next poll. The chosen batch size persists
  across polls.
- **The heuristic read is a window, and truncation is expected.** It asks "what completed most
  recently", ordered by `evaluated_at` descending with `max_rows` at the nominal 100; a truncated
  answer is the newest N completions and is committed as such. Baseline = the rows in the first
  snapshot; a completion is new when its `eval_run_id` was not seen in any earlier committed poll;
  the skill notes the window size whenever the response is truncated. A window narrower than the
  activity between two polls can miss completions — that is the disclosed imprecision of heuristic
  mode, not a failure.
- The progress `COUNT(*)` is one row and cannot truncate.

**Logical-poll atomicity.** One poll = every required query: in exact and partial-exact mode all
membership and summary batches, each non-truncated; in heuristic mode the window read, truncated or
not. Any required-query failure — an error, or an exact-mode truncation the halving could not
clear — discards the whole poll: no baseline, dedup or completion state commits from it, and it
counts as one error toward the two-failure stop. Each successful poll recomputes cohort state from
its own full results. Enrichment queries are optional: their failure degrades summary content, never
poll success or stop logic.

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

**Links.** `server_url` with any trailing slash trimmed; `repo_hash` (16 hex) and session ids (the
grammar above) need no encoding.

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
- The import's own HTTP traffic refreshes the cached `X-Kcap-Plan` entitlement, so by step 6 the
  cache reflects the server setup just talked to; a stale or missing entry reads as allowed.

## Error handling

- Foreground pass: totalized (§2); a throw becomes `Incomplete`, setup warns as today, spawns the
  child, writes `cohort: "unknown"` when the candidate set is untrusted.
- Background `Failed`: warn with the exit code when there is one and `kcap import --all --yes`;
  continue. `ExitedZero` is reported without claiming completeness.
- Handoff-file write failure: warn, continue; a later run-id reference enters heuristic mode for that
  run.
- Agent launch failure (no executable, or non-zero exit within 2000ms): warn, paste block, continue.
- Skill: analytics MCP absent → immediate close with the block; `analytics_not_in_plan` → immediate
  close with the plan sentence; failed binding → file links, heuristic data, disclosed; two
  consecutive failed polls → early stop with the block.

## Testing

**Ordering** (`BuildImportChains`, routed sort): descending by max `ChainTimestamp`; within-chain
ascending preserved with `MinValue` first; slug-less merge; tie-breakers; identical output on a
repeated corpus; a newer routed session still lands in phase two. **Candidate comparator**: a mixed
corpus of chain members, routed sessions and probe errors, including `MinValue` and equal timestamps,
yields one deterministic order; the 500 cut over a >500 mixed corpus is stable across runs.

**Selection** (`SelectForeground`): whole chains to the cap; boundary-chain overshoot; eligible
routed units taken whole (parent plus children); importable ≤ cap selects everything; all-watermarked
selects nothing; newest sessions `AlreadyLoaded` → newest importable selected; replay rows never
selected; **mixed-status units**: an `AlreadyLoaded` parent with a `New` child is ineligible — neither
runs in the foreground, the child is remainder and the child process imports it through the parent;
a `New` parent with an `AlreadyLoaded` child is selected with the parent counted, the child carried
into the plan, absent from every partition list and from the cohort; selection computed after capture
scope, before reconcile and before any import (seam ordering); a selected Cursor parent never imports
a child outside its unit and no child in the plan lacks its parent.

**Terminal partition**: a Cursor child imported inline by its parent lands in `SkippedIds`; a
quarantined session lands in `SkippedIds`; own-call `Loaded`/`Resumed` land in `SucceededIds`;
`Complete ⇒ Selected == Succeeded + Skipped + Failed`; the per-id partition is taken from the raw
outcome and disagrees with nothing the Done grid counts.

**Outcome totalization**: throw before classification → `Incomplete`, `RunCandidateIds == null`,
spawn, `cohort: "unknown"`; throw after classification → `Incomplete` with ids; partial foreground
success → `Succeeded + Skipped + Failed < Selected`; nothing escapes to setup.

**Spawn decision and status** through `IBackgroundImportSpawner`'s fake: importable remainder →
spawn; routed replay rows alone (all-watermarked Cursor corpus) → spawn; file-based `AlreadyLoaded`
alone (all-watermarked Claude/Codex corpus) → **no** spawn; probe errors alone → spawn; failures
alone → spawn; clean full pass with an empty remainder → no spawn; each of the four statuses prints
its pinned line and lands its file value; `ExitedZero` output claims no completeness; the child's
argv is `import --all --yes --skip-title`; the contributed environment carries `KCAP_CONFIG_DIR`,
`KCAP_PROFILE`, `KCAP_IMPORT_DETACHED_LOG` and `KCAP_IMPORT_DEFAULT_VISIBILITY` with the chosen
profile when it differs from the active one, and `KCAP_URL` is removed from a parent environment
that carries it (the removal is the behaviour under test; the repo's `.envrc` is what makes the
parent carry it).

**Child env contract**: log variable set → log file written, non-TTY output, detach called, and
`KCAP_IMPORT_DEFAULT_VISIBILITY` applied as `defaultVisibility` for each value setup can save; log
variable unset → behaviour identical to today, and `KCAP_IMPORT_DEFAULT_VISIBILITY` ignored; parent
closes all three streams. **Visibility parity**: for every saved default, the stamp the foreground
pass sends and the stamp the child sends are the same value.

**Agent env pin**: the launched agent's `ProcessStartInfo` carries `KCAP_CONFIG_DIR` and
`KCAP_PROFILE` for the saved profile and no `KCAP_URL`, against a parent environment carrying
`KCAP_URL` and a cwd whose repository binding names another profile.

**Handoff file**: written iff the foreground pass ran (accepted prompt → file; declined, skipped,
unauthenticated and `--no-prompt` → no file); per-run filename under `KCAP_CONFIG_DIR` when set;
atomic write leaves no parseable partial; §3 shape; `handoff_suppressed` is `"no_new_sessions"` with
empty exact `session_ids` for the replay-only run, `"import_failed"` for a failed background with zero
successes, `"analytics_not_in_plan"` for a cached denial over a successful pass, and `null` whenever
`handoff_offered` is true; >500 candidates → first 500 in candidate order, `partial_exact`; two
concurrent runs → two files; >7-day files pruned on write; write failure warns and continues.

**Handoff gating and picker**: `Succeeded ≥ 1` → offered; `Running`/`ExitedZero` with a non-empty
cohort → offered; empty exact cohort → not offered whatever the background; `Failed` with zero
successes → not; `--no-prompt` → no handoff, unbounded import, no spawn, no file; cached analytics
denial → no picker and no paste block, guided tour alone; eligibility per vendor (detected, skill
present, executable resolves vs not); IDE-only Kiro and Antigravity print the paste block; every
recipe's argv pinned per vendor with the two-line prompt as a single element; Skip and cancel print
the paste block; launch failure (non-zero < 2000ms) falls back; zero exit < 2000ms and any exit ≥
2000ms proceed silently.

**Step 6 sequence**, end to end through the fakes: discovery → figures → prompt → foreground →
spawn decision → handoff file → picker/paste → agent wait → summary, including each failure branch;
the "no origin remote" run now imports; the browser-answered run is untouched.

**Prompt pinning**: exact `Follow my kcap import`; panel item composition; verbatim presence in the
skill's frontmatter description; `eval-watch` in `SourceNames` and `help-plugin.txt`.

**Skill acceptance** (scripted fixtures behind recorded `query_analytics` responses): run-id
resolution including non-GUID rejection, a missing named file beside a newer unrelated file
(heuristic for that run, the other file untouched), and a custom `KCAP_CONFIG_DIR`; bare-phrase
newest-match with disclosure; no-handoff files by `handoff_suppressed` value, including the
`analytics_not_in_plan` file that must not suggest a re-import; the layered validation cases including
the `ses_…`, `--` and hostile-id fixtures; `scope: 'global'` on every query; repo-less members counted
and linked without a repo hash; partial-exact over 500 with a listed arrival a capped scan would miss;
exact-mode truncation with a server cap below 100 (halving within the poll, floor-10 failure to
heuristic) and heuristic-mode truncation committed as a window with the size disclosed;
`analytics_not_in_plan` from the server closing immediately without polling; poll atomicity and recovery;
each stop rule at its boundary; deterministic first-three under ties; the whoami-failure no-link
closing variant; the `unattributed_count` sentence.

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
- Making plain `kcap import` stamp the profile's `default_visibility` itself instead of leaving an
  omitted stamp to the server. Setup's two passes are made consistent here without touching that;
  the general question is its own change.
