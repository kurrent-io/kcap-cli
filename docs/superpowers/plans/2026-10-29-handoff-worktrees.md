# Hand-off worktrees (slice 1 of resume/hand-off) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the daemon's `WorktreeManager` the checkout shapes a hand-off needs (existing branch, fast-forwarded branch, created branch, detached head) resolved from origin before launch, and make the daemon never delete a branch it did not create.

**Architecture:** Branch ownership becomes a property of `WorktreeInfo` that every removal path honours and the resume ledger persists. Resolution (`ResolveHandoffStartAsync`) fetches and decides a `HandoffStart` (shape + exact SHA) without touching the worktree tree; creation (`CreateHandoffAsync`) re-validates that decision and materialises it, refusing if the starting point moved. Nothing calls these from a launch yet: slice 2 wires them to the launch command, the server and the app.

**Tech Stack:** .NET 10, NativeAOT, TUnit, real `git` via the existing `RunGit*` helpers in `WorktreeManager`.

**Spec:** `docs/superpowers/specs/2026-10-29-resume-ended-sessions-design.md` (Hand off: "Where it runs", "Resolving the starting code", "Completeness"; Constraints: cleanup)

## Slices of the spec

1. **This plan**: hand-off worktree shapes, branch ownership, completeness check (daemon only).
2. Hand-off end to end: kcap-server launch fields, selected PR head fields, source reservation and capability; daemon launch wiring, pre-launch probe over local control, hand-off prompt, revalidated takeover with itemised outcome; app composer strip.
3. Recording identity: machine id and recording store from the SessionStart hooks and daemon launches, through server ingestion to the listing.
4. Resume: in-place launch mode, eligibility, restart-resume protocol for a listed session, failure fallback, app Resume button.

Each later slice gets its own plan once the previous one merges.

## Global Constraints

- A pre-existing branch is never deleted, reset, rewound or force-moved by the daemon — not on normal completion, a failed start, creation rollback or daemon recovery.
- Ledger entries written before this change carry no ownership field and must read as owning their branch (today's behaviour).
- A fetch or authentication failure is an error, never "the branch is gone".
- The worktree's HEAD after creation equals the resolved SHA exactly; otherwise the creation is rolled back and refused.
- No `git worktree prune` anywhere (CLAUDE.md invariant).
- Worktrees stay under `<repo>/.capacitor/worktrees/<name>` like `CreateAsync`'s.
- Comments follow CLAUDE.md's Comments rules: scarce, no history, no spec coordinates.

## Review Focus

1. A hand-off worktree on an existing branch where the agent commits and the worktree is then removed: the commit must survive on the branch and the worktree must not be kept as "unsaved work" forever (the `--exclude=<branch>` in `UnsavedWorkReadAsync` assumes the branch dies with the worktree). Pinned in Task 1.
2. The starting point moving between resolution and creation (someone pushes, or the local branch is checked out meanwhile): creation must refuse with `handoff_start_changed`, not silently start elsewhere. Pinned in Task 3.
3. A detached worktree with a new commit on removal: kept, because the commit is on no branch. Pinned in Task 1.
4. A branch name that is a valid ref but hostile (`--upload-pack=…`, leading `-`): refused before any git call, as `CreateAsync` does. Pinned in Task 2.
5. Origin whose default branch is not `main`: resolution reads it from origin, not a hard-coded name. Pinned in Task 2.

---

### Task 1: Branch ownership on worktrees and the resume ledger

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/WorktreeManager.cs` (`WorktreeInfo` record, `RemoveAsync`, `UnsavedWorkReadAsync`)
- Modify: `src/Capacitor.Cli.Core/Resume/ResumeLaunchDescriptor.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.Resume.cs` (both descriptor writes near :381 and :451, `ResumedWorktree` near :1557)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/WorktreeManagerTests.cs`
- Test: the existing resume-ledger round-trip tests for `ResumeLaunchDescriptor` (find with `rtk proxy grep -rln "ResumeLaunchDescriptor(" test/`)

**Interfaces:**
- Produces: `WorktreeInfo.OwnsBranch` — `public bool OwnsBranch { get; init; } = true;` (init property, not a positional parameter, so every existing `new WorktreeInfo(...)` keeps compiling and keeps owning).
- Produces: `ResumeLaunchDescriptor.WorktreeOwnsBranch` — `bool? WorktreeOwnsBranch = null`, inserted before the last parameter (`AccountLabel`) per the team's parameter-order rule; null reads as `true`.

- [ ] **Step 1: Write the failing tests** in `WorktreeManagerTests`:

```csharp
[Test]
public async Task RemoveAsync_keeps_a_branch_the_worktree_does_not_own() {
    using var repo = GitRepo.Create("not-owned");
    repo.CreateFile("a.txt", "a");
    repo.CommitAll("first");
    var main = repo.CurrentBranch;
    repo.Checkout("feature", create: true);
    repo.Checkout(main);

    var path = Path.Combine(repo.Path, ".capacitor", "worktrees", "handoff");
    repo.Do("worktree", "add", path, "feature");
    var tree = GitRepo.At(path);
    tree.CreateFile("b.txt", "b");
    tree.CommitAll("agent work");
    var agentCommit = tree.Head;

    var removal = await WorktreeManager.RemoveAsync(
        new WorktreeInfo(path, "feature", repo.Path) { OwnsBranch = false }, TimeProvider.System);

    await Assert.That(removal).IsEqualTo(WorktreeRemoval.Removed);   // the commit lives on `feature`
    await Assert.That(Directory.Exists(path)).IsFalse();
    await Assert.That(repo.Do("rev-parse", "refs/heads/feature").Text).IsEqualTo(agentCommit);
}

[Test]
public async Task RemoveAsync_keeps_a_detached_worktree_holding_a_new_commit() {
    using var repo = GitRepo.Create("detached");
    repo.CreateFile("a.txt", "a");
    repo.CommitAll("first");

    var path = Path.Combine(repo.Path, ".capacitor", "worktrees", "detached");
    repo.Do("worktree", "add", "--detach", path, "HEAD");
    var tree = GitRepo.At(path);
    tree.CreateFile("b.txt", "b");
    tree.CommitAll("agent work");

    var removal = await WorktreeManager.RemoveAsync(
        new WorktreeInfo(path, "", repo.Path) { OwnsBranch = false }, TimeProvider.System);

    await Assert.That(removal.KeptBecause).IsNotNull();
    await Assert.That(Directory.Exists(path)).IsTrue();
}
```

`GitRepo` (`test/Capacitor.Tests.Helpers/GitRepo.cs`) exposes `Path`, `At`, `Do` (returns `GitResult`, read `.Text`), `CommitAll`, `Checkout`, `Head`, `CurrentBranch`; `WorktreeRemoval.Removed` / `KeptBecause` are the existing result members.

In the ledger tests, add a round-trip case asserting `WorktreeOwnsBranch: false` survives serialisation, and that an entry JSON without the field deserialises with `WorktreeOwnsBranch == null`.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/WorktreeManagerTests/*"`
Expected: FAIL — `OwnsBranch` does not exist (compile error).

- [ ] **Step 3: Implement**

- `WorktreeInfo`: add `OwnsBranch` as above; `WorktreeInfo.Borrowed` sets `OwnsBranch = false`.
- `RemoveAsync`: delete the branch only when `deleteBranch && worktree.OwnsBranch && !string.IsNullOrEmpty(worktree.Branch)`. This covers the rollback in `StripOrRollBackAsync` and `RemoveOwnedWorktreeAsync` without touching their call sites.
- `UnsavedWorkReadAsync`: only treat the worktree's branch as dying with it (the `ownBranch` positive arm and the `--exclude=` arm) when `worktree.OwnsBranch`. A not-owned branch stays in `--branches`, so its commits are saved.
- `ResumeLaunchDescriptor`: add `WorktreeOwnsBranch` as specified. Write it from `worktree.OwnsBranch` at both descriptor sites in `AgentOrchestrator.Resume.cs`; `ResumedWorktree` sets `OwnsBranch = launch.WorktreeOwnsBranch ?? true`.

- [ ] **Step 4: Run the daemon and core suites**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/WorktreeManagerTests/*"` then the ledger test class, then the full `Capacitor.Cli.Core.Tests.Unit` and `Capacitor.Cli.Daemon.Tests.Unit` suites.
Expected: PASS, including the existing `RemoveAsync_keeps_a_worktree_holding_work_that_exists_nowhere_else` matrix unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/WorktreeManager.cs src/Capacitor.Cli.Core/Resume/ResumeLaunchDescriptor.cs src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.Resume.cs test/
git commit -m "Never delete a worktree branch the daemon did not create"
```

---

### Task 2: Resolve a hand-off's starting point

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/HandoffStart.cs` (the request and result types below; one file holds the closely related records and enums)
- Create: `src/Capacitor.Cli.Daemon/Services/WorktreeManager.Handoff.cs` (`partial class WorktreeManager`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/WorktreeManagerHandoffTests.cs`

**Interfaces:**
- Produces, in `HandoffStart.cs`:

```csharp
public sealed record HandoffTarget(string? Branch, int? PullRequestNumber, bool HeadIsFork);

public enum HandoffSource { Branch, ForkPullRequest, DefaultBranch }
public enum HandoffCheckout { CreateBranch, CheckoutBranch, FastForwardBranch, Detached }
public enum HandoffDetachReason { None, CheckedOutElsewhere, LocalBranchDiverged, ForkHead, BranchAbsent }

public sealed record HandoffStart(
    HandoffSource Source, HandoffCheckout Checkout, string Sha, string? Branch,
    HandoffDetachReason DetachReason, string? LocalBranchSha);

public sealed record HandoffResolution(HandoffStart? Start, string? FetchError);
```

`Branch` is the remote branch name for `Source.Branch` (also the push target when detached), the PR head ref for a fork, the default branch name for `DefaultBranch`. `LocalBranchSha` is the local branch tip observed at resolution (null when absent) — Task 3 uses it to detect a moved starting point.

- Produces, on `WorktreeManager`: `public Task<HandoffResolution> ResolveHandoffStartAsync(string repoPath, HandoffTarget target, CancellationToken ct)`.

- [ ] **Step 1: Write the failing tests**, one per row, using the `MakeUpstreamWithSideRef`-style upstream + clone (copy the helper into the new class; it is private to `WorktreeManagerTests`):

| Test | Setup | Expected `Start` |
|---|---|---|
| `branch_on_origin_without_a_local_branch_creates_it` | branch `feature` only on upstream | `Branch / CreateBranch`, `Sha` = upstream tip, `LocalBranchSha` null |
| `local_branch_equal_to_origin_is_checked_out` | clone has `feature` at upstream tip | `CheckoutBranch` |
| `local_branch_strictly_behind_is_fast_forwarded` | clone `feature` at A, upstream at descendant B | `FastForwardBranch`, `Sha` = B, `LocalBranchSha` = A |
| `local_branch_diverged_detaches` | clone `feature` has commit C not on upstream | `Detached`, `LocalBranchDiverged`, `Sha` = upstream tip |
| `local_branch_ahead_detaches` | clone `feature` = upstream tip + 1 commit | `Detached`, `LocalBranchDiverged` |
| `branch_checked_out_in_another_worktree_detaches` | `feature` equal, checked out via `git worktree add` | `Detached`, `CheckedOutElsewhere` |
| `fork_pull_request_head_detaches_at_the_pull_ref` | upstream ref `refs/pull/7/head`; target `(Branch: "x", PullRequestNumber: 7, HeadIsFork: true)` | `ForkPullRequest / Detached / ForkHead`, `Sha` = that ref |
| `absent_branch_detaches_at_origins_default_branch` | target branch not on upstream; upstream default branch renamed to `trunk` | `DefaultBranch / Detached / BranchAbsent`, `Branch` = `"trunk"`, `Sha` = trunk tip |
| `unreachable_origin_is_a_fetch_error_not_an_absent_branch` | clone's origin URL set to a nonexistent path | `Start` null, `FetchError` non-empty |
| `hostile_branch_name_is_refused_before_git_runs` | `Branch: "--upload-pack=touch x"` | throws `InvalidOperationException` starting `invalid_base_ref:` |

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/WorktreeManagerHandoffTests/*"`
Expected: FAIL (types do not exist).

- [ ] **Step 3: Implement `ResolveHandoffStartAsync`**

- Validate `target.Branch` with the existing `IsValidRefNameAsync`; refuse as `CreateAsync` does.
- Fetch outside the metadata gate, with the same config overrides as `CreateAsync`'s fetch (`maintenance.auto=false`, `gc.auto=0`, `NoSubmoduleRecursion()`). Fork: `fetch origin refs/pull/<n>/head:<FetchedRefFor(...)>`-style per-resolution ref. Branch: `fetch origin +refs/heads/<b>:refs/remotes/origin/<b>`.
- Tell absence from failure with `git ls-remote --exit-code origin refs/heads/<b>` before fetching: exit 2 = absent; any other non-zero = `FetchError` (stderr). The default branch comes from `git ls-remote --symref origin HEAD`.
- Local state: `refs/heads/<b>` tip via `rev-parse --verify --quiet`; ancestry via `merge-base --is-ancestor`; "checked out elsewhere" from `worktree list --porcelain` (`branch refs/heads/<b>` lines). Run these inside `WithWorktreeMetadataGate` since they read worktree metadata.
- Decision order for `Source.Branch`: absent locally → `CreateBranch`; checked out elsewhere → `Detached/CheckedOutElsewhere`; equal → `CheckoutBranch`; strict ancestor → `FastForwardBranch`; otherwise → `Detached/LocalBranchDiverged`.

- [ ] **Step 4: Run to verify they pass**

Same command. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/HandoffStart.cs src/Capacitor.Cli.Daemon/Services/WorktreeManager.Handoff.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/WorktreeManagerHandoffTests.cs
git commit -m "Resolve a hand-off's starting point from origin"
```

---

### Task 3: Create the hand-off worktree from a resolved start

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/WorktreeManager.Handoff.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/WorktreeManagerHandoffTests.cs`

**Interfaces:**
- Consumes: `HandoffStart`, `WorktreeInfo.OwnsBranch` (Tasks 1–2).
- Produces: `public Task<WorktreeInfo> CreateHandoffAsync(string repoPath, HandoffStart start, string? name = null)`. Returns `OwnsBranch = true` only for `CreateBranch`; `Branch` is `""` for `Detached`. Throws `InvalidOperationException("handoff_start_changed: …")` when re-validation fails.

- [ ] **Step 1: Write the failing tests** (each resolves with Task 2's method first, then creates, then removes in `finally` via `WorktreeManager.RemoveAsync`):

- `created_branch_tracks_origin_and_is_owned`: HEAD = `Sha`; `git rev-parse --abbrev-ref HEAD` = `feature`; upstream of `feature` is `origin/feature`; `OwnsBranch` true.
- `checked_out_branch_is_not_owned_and_survives_removal`: HEAD = `Sha`; `OwnsBranch` false; after `RemoveAsync`, `refs/heads/feature` still exists.
- `fast_forward_moves_the_local_branch_to_the_resolved_sha`: local `feature` = B afterwards; `OwnsBranch` false.
- `detached_start_creates_no_branch`: for `LocalBranchDiverged`, `ForkHead` and `BranchAbsent` starts, `git symbolic-ref -q HEAD` fails, HEAD = `Sha`, and `git branch --list` is unchanged from before the call.
- `a_local_branch_that_moved_since_resolution_refuses`: resolve (`CheckoutBranch`), then commit onto local `feature`; `CreateHandoffAsync` throws `handoff_start_changed`, no directory left under `.capacitor/worktrees`, local `feature` keeps the new commit.
- `a_branch_checked_out_since_resolution_refuses`: resolve (`FastForwardBranch`), then `git worktree add` `feature` elsewhere; throws `handoff_start_changed`.
- `a_failed_checkout_rolls_back_without_deleting_the_existing_branch`: force `StripOrRollBackAsync` to fail (use the same filter-inventory failure the existing rollback tests use; find with `rtk proxy grep -n "RollBack\|rollback" test/Capacitor.Cli.Daemon.Tests.Unit/Services/WorktreeManager*.cs`); `refs/heads/feature` still exists afterwards.

- [ ] **Step 2: Run to verify they fail**

Same command as Task 2. Expected: FAIL (`CreateHandoffAsync` missing).

- [ ] **Step 3: Implement `CreateHandoffAsync`**

Inside one `WithWorktreeMetadataGate` acquisition: re-read the local branch tip and the checked-out-elsewhere set; refuse with `handoff_start_changed` if `LocalBranchSha` no longer matches or a `CheckoutBranch`/`FastForwardBranch` branch is now checked out. Then:
- `CreateBranch`: `worktree add --no-checkout -b <b> <path> <sha>`, then `branch --set-upstream-to=origin/<b> <b>`.
- `CheckoutBranch`: `worktree add --no-checkout <path> <b>`.
- `FastForwardBranch`: `update-ref refs/heads/<b> <sha> <LocalBranchSha>` (compare-and-swap; a failure is `handoff_start_changed`), then as `CheckoutBranch`.
- `Detached`: `worktree add --no-checkout --detach <path> <sha>`.

Then `StripOrRollBackAsync` with the `WorktreeInfo` carrying the right `OwnsBranch`, so a rollback cannot delete a branch it did not create. Finally verify `rev-parse HEAD` equals `start.Sha`; on mismatch remove (keepUnsavedWork: false) and throw `handoff_start_changed`.

- [ ] **Step 4: Run to verify they pass**

Same command, then the full daemon unit suite.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/WorktreeManager.Handoff.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/WorktreeManagerHandoffTests.cs
git commit -m "Create hand-off worktrees on an existing branch or detached"
```

---

### Task 4: Completeness check of the starting code

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/WorktreeManager.Handoff.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/HandoffStart.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/WorktreeManagerHandoffTests.cs`

**Interfaces:**
- Produces:

```csharp
public enum HandoffCompleteness { Complete, PartlyMissing, Unconfirmed }
public sealed record HandoffWork(HandoffCompleteness Completeness, IReadOnlyList<string> MissingCommits);
public Task<HandoffWork> CheckHandoffWorkAsync(string repoPath, string startSha, IReadOnlyList<string>? sessionCommits, bool mayHaveUnpushedWork);
```

`sessionCommits` null means the server does not know them. `mayHaveUnpushedWork` is slice 2's call (the session ended without a final clean-push signal); this method only combines.

- [ ] **Step 1: Write the failing tests**

- `all_session_commits_reachable_and_nothing_unpushed_is_complete` → `Complete`, empty list.
- `a_session_commit_missing_from_the_start_is_partly_missing` → `PartlyMissing`, list = that full SHA (commit exists locally but is not an ancestor of `startSha`).
- `a_session_commit_unknown_locally_is_partly_missing` → `PartlyMissing` (`cat-file -e` fails).
- `unknown_session_commits_are_unconfirmed` (`sessionCommits: null`) → `Unconfirmed`.
- `possible_unpushed_work_is_unconfirmed_even_when_commits_are_reachable` → `Unconfirmed`.
- A malformed SHA in `sessionCommits` (`"--all"`) is treated as missing, never passed to git as an option.

- [ ] **Step 2: Run to verify they fail** — same command; FAIL.

- [ ] **Step 3: Implement** with `merge-base --is-ancestor <c> <startSha>` per commit after validating each as 40/64 hex. `PartlyMissing` wins over `Unconfirmed`, which wins over `Complete`.

- [ ] **Step 4: Run to verify they pass** — same command; PASS. Then `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` prints nothing.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/ test/Capacitor.Cli.Daemon.Tests.Unit/Services/WorktreeManagerHandoffTests.cs
git commit -m "Check whether a hand-off's starting code holds the session's work"
```
