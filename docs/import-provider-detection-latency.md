# Import provider (PR/MR) detection latency

Tracking: [#232](https://github.com/kurrent-io/kcap-cli/issues/232) (AI-1122).

## Question

`ImportCommand`'s bulk loop calls `RepositoryDetection.DetectRepositoryAsync` per session/cwd,
which can spawn a live `gh pr view` / `glab api` per unique repo (bounded by a ~2 s best-effort
cap each). The per-host memo in `GitProviderRouter` stops the `gh auth status` **probe** from
multiplying, but the **detection call** still runs per cwd — and GitLab's `glab api` is a network
round-trip rather than gh's local resolution. Does this hurt large multi-repo imports, and if so,
should we cache per `(repo, branch)` or skip detection during import?

## Finding: the PR/MR detection is *dead work* on the import paths

The decisive observation is not a latency number — it's that **every bulk-import path discards the
PR fields**, so the `gh`/`glab` round-trip produces a result that is never used:

- **Scope-picker resolution** and the **"Scanning repositories"** step use only `Owner`/`RepoName`.
- **Per-session ingest** (`ImportCommand`) builds its `repository` node manually with
  `user_name`/`user_email`/`remote_url`/`owner`/`repo_name`/`branch` — **no `pr_*` fields**.
- **Classification** (`TranscriptFileClassification`) uses the detected repo only for the
  owner/repo exclusion key.
- The **Copilot / Pi / Kiro** import sources use their repo detector only for the same exclusion
  key.

The **live/hook path** (`EnrichWithRepositoryInfo` → `BuildRepositoryNode`) *does* emit
`pr_number`/`pr_title`/`pr_url`/`pr_head_ref`, so PR detection genuinely matters there — just not
during import.

## Decision

Add a `detectPullRequest` flag (default `true`, preserving every live/hook caller) to
`DetectRepositoryAsync` and pass `detectPullRequest: false` on the bulk-import paths. This removes
the provably-unused `gh pr view` / `glab api` round-trip per cwd while still resolving base repo
info from local git. It is **behaviour-preserving**: imported payloads never carried PR fields on
those paths, so nothing observable changes — only the discarded network calls are dropped.

Because the result is discarded, this is a stronger justification than a wall-clock measurement:
the work is unnecessary regardless of its per-call cost. A representative "large multi-repo import"
benchmark was **not** run — it needs live `gh`/`glab` authentication, many real repositories with
open PRs/MRs, and network access that isn't reproducible in the dev/CI sandbox — and would not
change the conclusion.

### Exception: Cursor import keeps PR detection

`CursorImportSource` is the one import source whose synthetic `session-start/cursor` payload carries
a `repository` node built via `RepositoryDetection.BuildRepositoryNode`, which **does** emit `pr_*`
when populated (AI-1152). It therefore keeps live PR detection (its default detector is *not*
flipped) so imported Cursor sessions retain their PR tagging.
