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
- **Correlated child**: a classification carrying `IsSubagentChild` whose `ParentSessionId` names a
  classification present in the full routed plan (`New`/`Partial`/`AlreadyLoaded` after capture
  scope). Whatever its own status — `New`, `Partial`, `AlreadyLoaded`, `ProbeError` or a terminal
  skip — it can only ever land under that parent's stream, so it is never a candidate, never a
  selected id and never in a partition. A child whose parent is *not* in the full plan is not
  correlated: it is an orphan and an ordinary session.
- **Run candidate set**: every top-level session id this setup run may cause to land — the
  importable set (selected and unselected) plus `ProbeError` ids, **minus every correlated child**.
  Known in full at classification time. The eval-watch cohort (§5) is this set. Replay rows are not
  candidates: they are already on the server.
- **Chain**: file-based sessions (Claude, Codex) sharing a transcript slug, ordered ascending so
  `previous_session_id` continuation links hold.
- **Routed unit**: one routed session, or a Cursor parent together with its correlated subagent
  children (`SourceMeta["SubagentChildren"]` / `IsSubagentChild` + `ParentSessionId`). Children are
  imported inline by their parent's call **under the parent's subsession stream**
  (`CursorImportSource.cs:427-438, 578-605`): they are never top-level sessions on the server, their
  own call returns `ImportOutcome.Skipped`, and they never appear in `v_an_sessions`. So a unit is
  **one server session** — the parent — and its children ride with it: they count for nothing, are
  never selected ids, never candidates, never in the partition. **Which children ride is decided by
  the existing plan, not by this spec:** `routed` holds only post-capture-scope `New`/`Partial`/
  `AlreadyLoaded` rows (`ImportCommand.cs:1305-1315`), and `ReconcileOrphanedCursorSubagentChildren`
  prunes a parent's `SubagentChildren` to ids present in `routed`, so a child that capture scope
  withheld or that classified `TooShort`, `Excluded`, `InternalSubSession` or `ProbeError` is never
  attached to its parent's call. That invariant stands unchanged. A unit is **eligible** for
  foreground selection only when its parent is `New` or `Partial`; a unit whose parent is a replay row
  is left whole to the background child, however its children are classified — the new content lands
  under the already-present parent either way. A child whose parent is absent from the full plan is
  an orphan: the reconcile makes it a standalone session, and from then on it is an ordinary routed
  unit of its own.
- **Selection unit**: a chain or an eligible routed unit.
- **Remainder**: unselected `New`/`Partial` classifications, routed replay rows that no selected unit
  carries (§2), and `ProbeError` classifications. File-based `AlreadyLoaded` rows are not remainder:
  nothing in `kcap import` runs them. A replay child carried by a selected parent is executed in the
  foreground and is not remainder either.

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
It returns `SetupImportDiscovery(ImportDiscoveryResult? Result, Exception? Fault)` and never throws:
`HandleImport`'s discovery fans out with an unguarded `Task.WhenAll` over the sources
(`ImportCommand.cs:825`), so one corrupt vendor database would otherwise abort setup before the
figures. A `Fault` prints one line — the scan failed and why — plus the `kcap import --all` hint,
and the step ends with no prompt, no import, no file: the user was promised figures before a yes,
and a blind yes is not that. Making discovery resilient per source is a `kcap import` change and a
follow-up (Out of scope). On success the step prints three figures under its rule: repositories,
sessions attributed to one, sessions on disk matching none. When the total is zero the step prints
one line and ends without a prompt.

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

**The runner's result.** `ISetupImportRunner.RunAsync` returns a `SetupImportRun` instead of a bare
exit code, and never throws:

```
SetupImportRun(
    int                 ExitCode,
    ImportRunSelection? Selection,   // published at the selection checkpoint; null if never reached
    ImportRunOutcome?   Outcome,     // published by onFinished; null if the pass did not finish
    Exception?          Fault)       // whatever escaped HandleImport, caught by the runner
```

`HandleImport` reports at two points. `onSelected(ImportRunSelection)` — a new callback, fired once
**after** selection and **before** any import work — carries the candidate and selected ids, so a
throw during execution still leaves setup holding them. `onFinished(ImportRunOutcome)` fires as
today at the end of a completed pass (`ImportCommand.cs:2042`) and carries the partition. **The
`ReportNothing` exits** (`ImportCommand.cs:658`: no source available, discovery found nothing, the
scope matched nothing) are completed passes too: when `maxSessions` is set they publish an empty
selection (`RunCandidateIds = []`, `SelectedIds = []`, `RemainderExists = false`) through
`onSelected` and an empty partition through `onFinished`, so a run whose sessions vanished between
setup's discovery scan and the import is a known-empty result, not an unknown one. Nothing else
changes about when `onFinished` fires: a throw before or during classification reaches neither
callback, and the runner's `Fault` is the only record of it.

**Foreground selection.** `HandleImport` gains an internal `maxSessions` parameter (default null;
plain `kcap import` never sets it). When set, selection happens **after classification and after
capture scope**, and **before any import work starts**, over selection units:

1. Take whole chains in the §1 descending order, accumulating their importable-session counts, until
   the total reaches `maxSessions`. Chains are never split; the boundary chain is taken whole, so the
   selection may overshoot by at most that chain's remaining length. A pathological 50-session chain
   means a 50-session foreground pass — accepted; unit integrity and determinism beat cap exactness.
2. If chains yield fewer than `maxSessions`, extend the selection with eligible routed units in
   descending order until the total is reached or the importable set is exhausted. A unit is taken
   whole and counts **one** toward the cap — its parent; children count for nothing.

**Selected ids versus executed ids.** Selected ids are the selected chain members and the selected
routed units' parents — one id per server session; they are what the partition below accounts for
and what enters the cohort. The foreground routed plan additionally *carries* a selected unit's
children **that the full routed plan already admits** (`New`/`Partial`/`AlreadyLoaded` after capture
scope, exactly the set the reconcile would leave attached), because the parent's call needs them
present in `routed` to import them inline; their own call short-circuits to `Skipped`. A child the
full plan does not admit is not carried, so the capture-scope and terminal-classification gates hold
in the foreground as they do in a plain import. Carried children are not selected ids, consume no
slot, appear in no partition list and are not cohort candidates. A unit whose parent is
`AlreadyLoaded` is never eligible, so nothing of it runs in the foreground: its `New`/`Partial`
children are remainder, and the background child's full plan imports them through the parent's
replay exactly as `kcap import --all` does today; they never become candidates either, because what
lands is the already-present parent's subsession. No replay parent ever runs in the foreground.
`AlreadyLoaded` is a classification, not a runtime discovery, so watermarked sessions never consume
selection slots: a corpus whose five newest sessions are watermarked selects the five newest
*importable* ones.

**Candidates.** The run candidate set (Terms) is every `New`/`Partial` or `ProbeError` classification
that is not a correlated child — exactly the ids that can become rows in `v_an_sessions`.

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
selection therefore partitions into **Succeeded**, **Skipped** and **Failed**, and the run records
which ids fell where. Succeeded is `Loaded` or `Resumed`, **or `Skipped` with
`ImportSessionResult.SentChildContent == true`**: a Cursor parent with no sendable root content
whose call nonetheless posted a carried child (`CursorImportSource.cs:578-625`) has landed real work
under its own session, and that is exactly what the flag exists to report. Skipped is `Skipped` with
nothing sent. `ResolveRoutedOutcomeForCounting` (`ImportCommand.cs:522`) keeps governing the
Done-grid *counts* unchanged, and the two answer different questions: the grid says what this call
sent for its own stream, the partition says whether the server session landed work. They differ on
exactly one shape — a `New` parent whose only content was a carried child, which the grid counts as
skipped today (`IsSkippedChildContentOverride` promotes only an `AlreadyLoaded` parent) and the
partition counts as succeeded. The test pins that divergence rather than hiding it; aligning the
grid is a plain-import display change and a follow-up (Out of scope).

**Reporting the selection and the partition.** Two records, one per checkpoint:

```
ImportRunSelection(                          // onSelected, after selection, before execution
    IReadOnlyList<string> RunCandidateIds,   // Terms "Run candidate set", candidate order (§1)
    IReadOnlyList<string> SelectedIds,
    bool                  RemainderExists)   // Terms "Remainder"

ImportRunPartition(                          // on ImportRunOutcome, via onFinished
    IReadOnlyList<string> SucceededIds,      // Loaded, Resumed, or Skipped with SentChildContent
    IReadOnlyList<string> SkippedIds,        // Skipped with nothing sent
    IReadOnlyList<string> FailedIds)
```

`ImportRunOutcome` (`ImportCommand.cs:664`) gains a nullable `Partition`. Both are populated only
when `maxSessions` was set. Ids are the same normalized session ids the server receives.

**Totalized outcome.** From the `SetupImportRun`, setup builds a `ForegroundImportOutcome` and never
lets an exception escape the step:

```
ForegroundImportOutcome {
  Certainty:        Complete | Incomplete,   // Complete iff Fault is null, Outcome non-null AND Selection non-null
  Selected:         int,                     // Selection.SelectedIds.Count, 0 when Selection is null
  Succeeded:        int,
  Skipped:          int,
  Failed:           int,
  RemainderExists:  bool,                    // Selection.RemainderExists; true when Selection is null
  RunCandidateIds:  string[] | null          // Selection.RunCandidateIds; null when Selection is null
}
```

On `Complete`, `Selected == Succeeded + Skipped + Failed`: every selected session's own call ended
in exactly one of the three. On `Incomplete`, `Outcome` is null — `onFinished` fires only at the
end of a completed pass, and this design adds no mid-run partition checkpoint — so all three counts
are zero and every selected session is treated as not landed; the background child covers them, and
the server watermark makes re-sending what did land a no-op. A `Fault` after the selection checkpoint
keeps `RunCandidateIds`; a `Fault` before it yields `null` and `RemainderExists = true`.

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
  `KCAP_IMPORT_DETACHED_LOG=<config dir>/import-{run_id}.log` (the file setup has already created,
  §3), and set `KCAP_IMPORT_DEFAULT_VISIBILITY` to the exact value the foreground pass stamped. Setup persists the
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
- Child-side contract: when `KCAP_IMPORT_DETACHED_LOG` is present, `kcap import` opens that
  **existing** file for append (`FileMode.Open`, `FileShare.ReadWrite`; a missing file is a startup
  failure, never a create), points its console output there, treats output as non-TTY line mode,
  applies `KCAP_IMPORT_DEFAULT_VISIBILITY` as above, and calls
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

**Owner-only on disk, created by setup, never through a pre-existing path.** The file lists up to
500 session ids, the tenant URL and a local log path, and the detached log carries repository and
path diagnostics. Names are per-run GUIDs (`import-handoff-{run_id}.json`, `import-{run_id}.log`),
not timestamps. The mode comes from `TokenStore`'s pattern (`FileStreamOptions.UnixCreateMode =
UserRead | UserWrite`, `TokenStore.cs:139`); the publication does **not** copy `TokenStore`'s
overwrite (`FileMode.Create` + `File.Move(…, overwrite: true)`), because a pre-existing path here is
a fault, not a stale file to replace. The exact algorithm:

1. Handoff: create a unique temp `import-handoff-{run_id}.json.{guid}.tmp` with `FileMode.CreateNew`
   and the owner-only mode; write; flush; close. Publish with `File.Move(temp, final)` **without**
   `overwrite`, which fails when the final name already exists. Either failure → the write failure
   path (warn, delete the temp, continue), and nothing is written through whatever was at the name.
2. Log: create `import-{run_id}.log` with `FileMode.CreateNew` and the owner-only mode, then close
   it; the child later opens it with `FileMode.Open` for append. A failed create → spawn `Failed`
   with that reason.

The config directory is the trust boundary, exactly as it is for `tokens.json`: a `KCAP_CONFIG_DIR`
pointing at a directory other users can write to is outside what the CLI protects, for tokens today
and for these files. On Windows both files get what `tokens.json` gets, the containing directory's
ACLs. The observable guarantees, which the tests pin: the final handoff and the log carry 0600 on
Unix; a file or symlink already at the final handoff name or the log name is left untouched and
unfollowed; no temp file is left behind on any path.

```json
{
  "schema_version":           1,
  "run_id":                   "<guid-n>",
  "written_at":               "<utc iso-8601>",
  "handoff_offered":          true,
  "handoff_suppressed":       null,
  "foreground_certainty":     "complete | incomplete",
  "server_url":               "<profile server_url, no trailing slash>",
  "profile":                  "<saved profile name>",
  "scope":                    "all",
  "cohort":                   "exact | partial_exact | unknown",
  "session_ids":              ["<run candidate set, candidate order>"],
  "foreground_succeeded_ids": ["<ImportRunPartition.SucceededIds, verbatim>"],
  "unattributed_on_disk":     1552,
  "background":               "not_needed | running | exited_zero | failed",
  "background_log":           "<path or null>"
}
```

- `cohort: "exact"` — `session_ids` is the complete run candidate set in candidate order (§1).
- `cohort: "partial_exact"` — the candidate set exceeded **500**; `session_ids` holds the first 500 in
  candidate order, so the cut is deterministic across chains, routed sessions and probe errors alike.
  The skill watches exactly those and says so.
- `cohort: "unknown"` — `Certainty == Incomplete` with `RunCandidateIds == null`; `session_ids` is
  empty and meaningless; the skill queries nothing and closes with links.
- `foreground_succeeded_ids` is the completed pass's `ImportRunPartition.SucceededIds`, verbatim —
  `Loaded`, `Resumed`, and `Skipped` with child content sent (§2 "Terminal partition") — so a Cursor
  parent that landed only through its children is listed. Parents and chain members only, never a
  correlated child. Empty on an `Incomplete` pass, where no partition exists. Its consumer is the
  skill's opening snapshot ("N sessions were imported before you were handed off"); nothing else
  reads it, and `Succeeded` in §4's table is this list's length.
- `profile` is the saved profile name the two child processes are pinned to, so the skill can name it
  in a remediation (§5 "Server binding").
- `unattributed_on_disk` is discovery's figure: sessions found on this machine with no repository
  match, counted before classification, capture scope or selection, so it says nothing about how many
  imported. The skill renders it as exactly that — "N sessions on disk had no repository match" —
  never as a count of imported or watched sessions.
- `handoff_offered` records §4's gating outcome. When it is `false`, `handoff_suppressed` names why
  with one value from the closed set §4 defines; when `true` it is `null`. The skill branches on this
  field rather than inferring the reason.

Write failure is best-effort: warn, skip the file, continue. The skill has a documented fallback.

### 4. Agent handoff (setup only)

**When — one decision, evaluated top to bottom after the foreground pass ran.** The first row that
matches decides; `--no-prompt` never reaches this table.

| # | Condition | `handoff_offered` | `handoff_suppressed` |
|---|---|---|---|
| 1 | `Certainty == Incomplete` and `Succeeded == 0` | false | `import_failed` |
| 2 | background `Failed` and `Succeeded == 0` | false | `import_failed` |
| 3 | `RunCandidateIds` known and empty | false | `no_new_sessions` |
| 4 | `Succeeded == 0` and background `NotNeeded` (every selected call was `Skipped`, nothing left) | false | `nothing_landed` |
| 5 | cached plan denies analytics (below) | false | `analytics_not_in_plan` |
| 6 | no vendor is eligible (below) and at least one is detected | false | `skill_not_installed` |
| 7 | no vendor is detected | false | `no_agent_detected` |
| 8 | otherwise (`Succeeded ≥ 1` or background ∈ {`Running`, `ExitedZero`}, with a watchable cohort) | true | `null` |

The import's own outcome outranks the plan gate, so a denied plan never masks a failed import and the
skill's retry advice is only ever given when a retry is warranted. Rows 1–4 print nothing beyond what
the step already said; rows 5–7 print one line naming the reason; row 8 proceeds to the picker. Rows
1–7 add no eval-watch item to the Next-steps panel, which keeps exactly the items it has today — the
server-setup item, and the guided-tour item when eligible.

**Plan gate (row 5).** The eval-watch skill reads analytics, which the server denies to Free tenants
(`analytics_not_in_plan`). Setup consults the cached entitlement the CLI already keeps from the
`X-Kcap-Plan` response header (`PlanEntitlementStore.Get(serverUrl, …).Allows(PlanEntitlements.Analytics)`).
Unknown or allowed passes the row. The skill still handles the denial itself (§5), because the cache
can be stale.

**Who is eligible (rows 6–8).** `HarnessRegistry.Identities` in registry order, filtered to vendors
that are `Detected(id)` **and** whose skills location holds the eval-watch skill after step 4 — the
same on-disk oracle `ShouldOfferGuidedTour` (`SetupCommand.cs:1111`) applies to the guided-tour
skill, evaluated per vendor: Claude through the registered plugin marketplace path, Kiro and
Antigravity through their own skills directories, every other vendor through the shared
`~/.agents/skills` tree. A vendor without the skill would receive a prompt nothing answers, so when
the user declined step 4 or its install failed there is no paste block either: row 6 prints one line
naming `kcap plugin install` (with the vendor flag) as the way to get the skill, and setup ends as
today.

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
search path is listed and prints the paste block on selection. The paste block is only ever printed
for an eligible vendor — one whose skill is installed — so what it asks the user to paste is always
answerable by the agent they paste it into.

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
- **Layer B — cohort payload** (may its ids drive *data*?): `cohort`, `background`,
  `foreground_certainty` and `handoff_suppressed` are known enum values (or null where allowed);
  every entry of `session_ids` and `foreground_succeeded_ids` matches the session-id grammar;
  `unattributed_on_disk` is a non-negative integer. Failure → **no data**: the skill issues no query
  and closes with links, disclosed; the file stays selected.
- **Layer C — link payload**: an invalid `server_url` (not http/https) cuts file-sourced links **and**
  makes the binding below unverifiable, so no query runs. `background_log` and `profile`, when shown,
  must be plausible (no control characters, bounded length) and are rendered as plain text, never as
  a link or a command to run; otherwise omitted.

An unknown future `schema_version` fails layer A. Unvalidated values are never spliced into SQL,
paths or links.

**Locating the files.** The skill reads `import-handoff-*.json` from `KCAP_CONFIG_DIR` when that
variable is set in its environment, else `~/.config/kcap` — the same rule `ConfigRoot` applies.

**Server binding — fail closed.** The analytics MCP queries whatever server the agent's own `kcap`
environment resolves, and a handoff file cannot repoint a running MCP. So before any query the
skill runs `kcap whoami` and compares its server URL to the file's under canonicalization: scheme
and host lowercased, the scheme's own default port elided (80 for http, 443 for https, so
`http://host:443` stays distinct), trailing slash trimmed, path compared as-is. On a match, exact
data is allowed. On a mismatch, or when `whoami` fails, **the skill issues no analytics query at
all**: querying the wrong tenant and labelling it heuristic would still summarize someone else's
sessions under this import's links. It closes with the closing block, file-sourced links, and a
remediation that names the file's `profile`: start the agent from a shell where `kcap whoami` reports
the file's server — for example with `KCAP_PROFILE=<profile>` set and no `KCAP_URL`, and with
`KCAP_CONFIG_DIR` set when kcap uses a custom config directory — then re-prompt. A launched agent
(§4) never hits this path, because setup pinned its environment; the paste-block path can, and this
is what it gets instead of wrong data.

**Resolution, first action of the skill:**

1. A `(run: <id>)` line in the invoking prompt: the token must match the GUID-N grammar **before**
   being used as a filename component. **A valid run id binds the skill to that run and only that
   run.** Its file, when locator-valid, is selected. When the file is missing or fails layer A (for
   example the write failed), the skill says so and stops *for that run* — it never falls through
   to another run's file, because disclosure would not make another cohort's results belong to the
   requested run; with no file there is no cohort, so it closes with the block and no query. If the selected file is a no-handoff file (`handoff_offered: false`),
   branch on `handoff_suppressed` — a layer-B-valid file is required to make any claim; otherwise
   close as "its record is unreadable" — and close with the closing block:
   `"no_new_sessions"` → "nothing to watch — that import found no new sessions" (a running background
   here is replay work); `"nothing_landed"` → "the sessions that import selected were skipped at
   import time", pointing at `kcap import --all` for the per-session reasons, no retry implied;
   `"analytics_not_in_plan"` → the plan sentence below — the import ran and no retry is suggested;
   `"skill_not_installed"` / `"no_agent_detected"` → the import ran, watching was not offered because
   no agent had this skill, and the user has evidently got it now, so the skill **continues as if
   offered** with the file's cohort; `"import_failed"` → "that import did not get running", naming
   the `kcap import --all --yes` retry and the `background_log` when displayable. A malformed run
   token is treated as no token.
2. No run id: among `import-handoff-*.json`, the newest locator-valid file that is watchable —
   `handoff_offered: true`, or `handoff_suppressed` ∈ {`skill_not_installed`, `no_agent_detected`}.
   More than one → the skill says it picked the newest and names the others.
   Matches disagreeing on `server_url` → the newest is selected and the binding check decides, as
   for any file: bound → its cohort is watched; not bound → links, remediation, no query.
3. Nothing qualifies → the skill says it found no import to follow, closes with the closing block
   (links from `kcap whoami` when it succeeds) and issues **no query**. A selected file with
   `cohort: "unknown"` closes the same way, with its layer-C fields serving the links: without a
   candidate list there is nothing the skill can watch that is provably this user's.

**The cohort is the only thing the skill ever queries.** Every query the skill issues names session
ids taken from the selected file's `session_ids`; there is no tenant-wide read of any kind. Those ids
are sessions this user imported from their own disk moments ago, so the skill can never surface
another user's session — not a row, not a count, not a link. The analytics surface itself is
repo-scoped and org-visible by design (it is aggregate telemetry gated by tenant plan, not by
per-viewer session visibility), and the skill does not depend on that either way: it confines itself
to its own cohort.

- **Exact**: the file's `session_ids` — the run candidate set, so sessions the background child lands
  before the skill's first snapshot are counted, and sessions imported concurrently by anything else
  are not. Completed eval rows already present at the first snapshot count.
- **Partial-exact**: identical mechanics over the listed 500; the skill opens by saying it watches the
  500 most recent sessions of this import and that older ones may land and evaluate unobserved.
  Omitted candidates are invisible to the bounded queries and are never labelled unrelated.
- **Unknown**, or no file: no query, links and the closing block only.

**Plan denial.** An analytics response of HTTP 403 `analytics_not_in_plan` is a terminal degrade
recognised on sight, not a poll failure: the skill closes immediately with the closing block and says
that Insights is not in this tenant's plan, that the import continues regardless, and that evals
appear in the server UI. No polling, no retry, no wait.

**Data contract — every query under `scope: 'global'`.** The `kcap-analytics` MCP defaults to the
cwd repository and refuses to widen silently (`McpAnalyticsServer.BuildQueryBody`,
`McpAnalyticsServer.cs:275`), so the skill passes `scope: 'global'` on every call: the cohort spans
repositories and includes repo-less sessions, whose `repo_hash` is null.

- Cohort state, **one query per batch** carrying arrival and completion together:
  `SELECT s.session_id, s.repo_hash, e.eval_run_id, e.evaluated_at, e.overall_score, e.judge_model
  FROM v_an_sessions s LEFT JOIN v_an_eval_summaries e ON e.session_id = s.session_id
  WHERE s.session_id IN (…)`. A row means the session arrived; a non-null `eval_run_id` means it
  completed. `repo_hash` is kept per session for the links.
- Displayed import progress: the number of cohort ids present over the number listed — derived from
  the batch rows, no separate query, never a tenant-wide count.
- Per-category detail, one session per query and **aggregated server-side** so no row cap can slice
  it: `SELECT category, AVG(score) AS mean FROM v_an_eval_scores WHERE session_id = '<id>' GROUP BY
  category` (one row per category) and `SELECT question_id, score FROM v_an_eval_scores WHERE
  session_id = '<id>' ORDER BY score ASC, question_id ASC LIMIT 2`. Strongest = highest mean,
  weakest = lowest, ties alphabetical by category. A session with no score rows is summarized by
  `overall_score` alone. Titles may be enriched through the `kcap-sessions` MCP when present, else
  the id suffices.
- There is deliberately no repo-wide or tenant-wide read and no unlisted-arrival narration:
  identifying non-cohort rows would need exactly the scan this contract forbids.

**Batching, truncation and the request budget — fail closed.** The server clamps every query to its
own configured row maximum, and a capped result is a *successful* response flagged `truncated: true`
(the MCP appends a warning trailer). The server also admits at most 60 analytics query starts per
user per minute (`AnalyticsQueryOptions.RequestsPerMinute`), rejected starts included, answering 429
with `Retry-After`. Both bounds shape the batching:

- A cohort batch asks "which of these ids are present and which completed"; a truncated answer is
  incomplete and is never committed. Each batch requests `max_rows` equal to its size.
- **Budget: at most 20 cohort queries per poll, including the first, issued one at a time.** With a
  poll every 30 seconds that is 40 starts a minute, leaving room for the enrichment queries (at most
  six over the whole run) and a retry. The smallest batch the budget allows is
  `floor = ceil(N / 20)` where `N` is the cohort size — 25 for 500 ids, 10 for 200, 5 for 100.
  **Serial dispatch is mandatory:** the server also caps queries in flight per user
  (`AnalyticsQueryOptions.MaxConcurrentPerUser`, default 2, configurable down to 1) and answers the
  excess with a 429 whose detail carries no `retry after` phrase. The skill therefore never has more
  than one analytics call outstanding — each batch, and each enrichment query, waits for the previous
  one to return — so a healthy server never rejects a poll for concurrency.
- **No probing.** The first poll runs at batch size `floor` exactly, so it spends at most 20
  queries. Every successful query body carries the server's effective `max_rows` (the same field the
  MCP reads for its truncation trailer), so after the first poll the skill knows the cap. **The cap,
  not the truncation flag, decides:** `truncated` is set only when a batch actually produced more
  rows than the cap, and an immediate first snapshot is usually sparse — a 25-id batch with three
  arrived sessions returns `truncated: false` with `max_rows: 10`. So when any first-poll body
  reports `max_rows < floor`, the skill fails closed at once, whatever the flags: it says the
  server's row cap is below what watching this many sessions needs, and never buys completeness by
  spending past the budget. Otherwise it raises the batch to `min(100, cap)` for later polls. Any
  actually truncated batch still fails its poll. The `get_analytics_schema` tool cannot supply the cap
  up front: the MCP returns the schema text and drops the envelope's `max_rows`.
- **429.** The MCP surfaces a 429 as `Error: HTTP 429 — <detail>`, with no headers. The
  rate-limit detail reads `Rate limit: N queries/min; retry after Ns.`, so the skill parses
  `retry after (\d+)s` when present and otherwise waits the full 60-second window; the next poll
  starts after the later of that delay and the 30-second cadence. A 429 fails the poll; two in a row
  stop the watch like any other failure. Exposing `retry_after_seconds` structurally through the MCP
  is a follow-up (Out of scope).
- Enrichment queries are bounded by construction (one row per category; `LIMIT 2`); should one still
  come back truncated, that session's detail is omitted and it is summarized by `overall_score`
  alone, disclosed.

**Logical-poll atomicity.** One poll = every cohort batch, each non-truncated. Any required-query
failure — an error, a 429, or a truncated response — discards the whole poll: no baseline, dedup or completion state commits from it, and it counts as one error
toward the two-failure stop. Each successful poll recomputes cohort state from its own full results.
Enrichment queries are optional: their failure or truncation degrades summary content, never poll
success or stop logic.

**Stop rules**, evaluated after each successful poll, its state committed first:

1. Three distinct cohort sessions with completed evals → summarize the deterministic first three
   (ordered by `evaluated_at`, ties by `session_id`; the same rule for pre-snapshot rows, so more than
   three arriving at once always select the same three).
2. **All-cohort-complete**: every id in `session_ids` has a completed eval. Requires `cohort: "exact"`
   with a non-empty list; never fires in partial-exact mode, and never on an empty list. Sessions
   that never evaluate are covered by the deadline, not inferred.
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
`Start kcap guided tour` prompt; and, when `unattributed_on_disk > 0`, one sentence saying that many
sessions on disk had no repository match, so any of them that imported show without one, and that
`kcap remap` places them.

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
- Discovery fault: one line naming the failure and the `kcap import --all` hint; no prompt, no
  import, no file.
- Handoff-file write failure (including a pre-existing path at its name): warn, continue; a later
  run-id reference finds no file and closes with the block, no query.
- Log-file creation failure (including a pre-existing path at its name): the spawn is `Failed`
  with that reason; the manual retry is printed.
- Agent launch failure (no executable, or non-zero exit within 2000ms): warn, paste block, continue.
- Skill: analytics MCP absent → immediate close with the block; `analytics_not_in_plan` → immediate
  close with the plan sentence; binding mismatch or `whoami` failure → no query, file links, the
  profile remediation; no file or `cohort: "unknown"` → no query, links, the block; two consecutive
  failed polls → early stop with the block.

## Testing

**Ordering** (`BuildImportChains`, routed sort): descending by max `ChainTimestamp`; within-chain
ascending preserved with `MinValue` first; slug-less merge; tie-breakers; identical output on a
repeated corpus; a newer routed session still lands in phase two. **Candidate comparator**: a mixed
corpus of chain members, routed sessions and probe errors, including `MinValue` and equal timestamps,
yields one deterministic order; the 500 cut over a >500 mixed corpus is stable across runs.

**Selection** (`SelectForeground`): whole chains to the cap; boundary-chain overshoot; eligible
routed units taken whole (parent plus children); importable ≤ cap selects everything; all-watermarked
selects nothing; newest sessions `AlreadyLoaded` → newest importable selected; replay rows never
selected; a routed unit counts one toward the cap however many children it has; **mixed-status
units**: an `AlreadyLoaded` parent with a `New` child is ineligible — neither runs in the foreground,
the child is remainder and the child process imports it through the parent, and neither id is a
candidate; a `New` parent with `New`, `Partial` and `AlreadyLoaded` children is selected with the
parent counted and those children carried into the plan, absent from every partition list and from
the cohort; a `New` parent with an `Excluded`, a `TooShort`, an `InternalSubSession` and a
`ProbeError` child carries **none** of them, and the `ProbeError` child is **not** a candidate; an
orphaned child (parent absent from the full plan) is its own unit and, when `New`/`Partial`/
`ProbeError`, a candidate; selection computed after capture
scope, before reconcile and before any import (seam ordering); a selected Cursor parent never imports
a child outside its unit and no child in the plan lacks its parent.

**Terminal partition**: a quarantined session lands in `SkippedIds`; a routed session with no
sendable content and no children lands in `SkippedIds`; own-call `Loaded`/`Resumed` land in
`SucceededIds`; a `New` Cursor parent with no sendable own content whose admitted `New` child was
posted (`Skipped`, `SentChildContent: true`) lands in `SucceededIds`, and with no remainder that run
reaches §4 row 8, not row 4; a carried child appears in no list;
`Complete ⇒ Selected == Succeeded + Skipped + Failed`; the per-id partition is taken from the raw
outcome and the child-content flag, and the carried-child fixture pins the one known divergence
from the Done grid: the `New` parent is in `SucceededIds` while the grid still counts it as skipped.

**Discovery contract**, through the real `ISetupImportRunner` interface: a source whose
`DiscoverAsync` throws → `Fault` set, `Result == null`, the step prints the failure line and the
hint, asks nothing, imports nothing, writes nothing; a clean scan → the three figures, with the
repo-less figure labelled as on-disk.

**Runner contract and totalization**, through the real `ISetupImportRunner` interface: a throw before
classification → `Selection == null`, `Outcome == null`, `Fault` set → `Incomplete`,
`RunCandidateIds == null`, `RemainderExists`, spawn, `cohort: "unknown"`; a throw after the
selection checkpoint and before execution → `Selection` set, `Outcome == null` → `Incomplete` with
ids, counts all zero, spawn; a throw during execution → the same shape (`Outcome == null`, counts
zero) even when some selected sessions had already landed; a completed pass → `Fault == null`,
`Outcome.Partition` set, `Complete ⇒ Selected == Succeeded + Skipped + Failed`; **a `ReportNothing`
exit** — setup's discovery found sessions, then the files vanished before `RunAsync` — → empty
`Selection`, empty `Partition`, `Complete`, no spawn, row 3 of the §4 table (`no_new_sessions`), a
file with an empty exact cohort; `onSelected` fires exactly once and before the first import call
(seam ordering); nothing escapes `RunAsync`.

**Spawn decision and status** through `IBackgroundImportSpawner`'s fake: importable remainder →
spawn; routed replay rows alone (all-watermarked Cursor corpus) → spawn; file-based `AlreadyLoaded`
alone (all-watermarked Claude/Codex corpus) → **no** spawn; a single selected `New` parent with an
`AlreadyLoaded` child and nothing else → **no** spawn (the carried child is not remainder); probe
errors alone → spawn; failures alone → spawn; clean full pass with an empty remainder → no spawn; each of the four statuses prints
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
the published handoff and the log carry owner-only mode on Unix (asserted with
`File.GetUnixFileMode`); an interrupted write leaves no parseable file at the final name and no temp
behind; a pre-existing file or symlink at the handoff name → write failure, its content and target
unchanged, no temp left; a pre-existing file or symlink at the log name → spawn `Failed`, its content
and target unchanged; the child opens only the pre-created log and fails at startup when it is
absent; §3 shape including `profile` and `unattributed_on_disk`; `foreground_succeeded_ids` equals
`ImportRunPartition.SucceededIds`, and the carried-child fixture (a `New` parent landed only through
its child) lists that parent; `handoff_suppressed` takes each value of the §4 table from a fixture
built for that row, `null` whenever `handoff_offered` is true, and the precedence cases — failed
import **and** cached denial → `import_failed`; empty cohort **and** cached denial →
`no_new_sessions`; all-skipped pass with nothing left → `nothing_landed` — resolve as the table says;
>500 candidates → first 500 in candidate order, `partial_exact`; two concurrent runs → two files;
>7-day files pruned on write; write failure warns and continues.

**Handoff gating and picker**: each row of the §4 table has a fixture and the first matching row wins;
`--no-prompt` → no handoff, unbounded import, no spawn, no file; a cached analytics denial over a
successful pass → no picker, no paste block, one line, and a Next-steps panel identical to today's; no
eligible vendor with one detected (step 4 declined or failed) → no picker, no paste block, the
`kcap plugin install` line;
eligibility per vendor (detected, skill present, executable resolves vs not); IDE-only Kiro and
Antigravity with the skill installed print the paste block; every
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
(closes with no query for that run, the other file untouched), and a custom `KCAP_CONFIG_DIR`; bare-phrase
newest-match with disclosure; no-handoff files by `handoff_suppressed` value, including the
`analytics_not_in_plan` file that must not suggest a re-import; the layered validation cases including
the `ses_…`, `--` and hostile-id fixtures; `scope: 'global'` on every query; **every query names only
cohort ids** — a recorded response carrying a row for an id outside the cohort (another user's
session) is ignored and never counted, linked or summarized, and no fixture query is ever issued
without an `IN (…)` or `session_id =` bound to cohort ids; no file and `cohort: "unknown"` both
close with links and zero queries; repo-less members counted and linked without a repo hash;
partial-exact over 500 with a listed arrival a capped scan would miss; **request budget**: a 500-id
cohort's first poll runs at batch 25 and issues exactly 20 queries, no probe; with a server row cap
of 10 and a **sparse** first snapshot (every batch returns fewer than 10 rows, `truncated: false`,
`max_rows: 10`) that first poll fails closed on the reported cap and says so, having spent 20
requests and never tripping the 60-per-minute limit; with a cap of 25 the first poll succeeds in 20
queries and later polls stay at 25; with a cap of 300 later polls rise to batch 100 and 5 queries;
**serial dispatch**: a 20-batch poll against a fake enforcing a per-user concurrency limit of 1
completes with no 429, and the fixture asserts no two analytics calls overlap; a 429 whose detail says `retry
after 37s` delays the next poll 37 seconds, one without the phrase delays it 60; a truncated
enrichment response omits that session's detail; `analytics_not_in_plan` from the server closing immediately without
polling; **binding fails closed**: a `whoami` server that differs from the file's issues zero queries
and closes with links plus the profile remediation, and a `whoami` failure does the same;
`skill_not_installed` and `no_agent_detected` files continue as if offered; poll atomicity and
recovery;
each stop rule at its boundary; deterministic first-three under ties; the whoami-failure no-link
closing variant; the `unattributed_on_disk` sentence worded as an on-disk count.

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
- Making `kcap import`'s discovery resilient to one source failing (today one throwing source aborts
  the whole scan). Setup totalizes the scan as a whole; per-source resilience is its own change.
- Surfacing `retry_after_seconds` and the effective row cap structurally through the analytics MCP.
  The skill reads both from the text the MCP returns today.
- Counting a `New` Cursor parent whose only content was a carried child as loaded in `kcap import`'s
  Done grid (`IsSkippedChildContentOverride` promotes only `AlreadyLoaded` parents). Setup's
  partition treats it as landed; the grid's display is its own change.
