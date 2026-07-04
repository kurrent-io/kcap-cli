# Multi-provider PR/MR detection design

**Date:** 2026-07-01
**Status:** Draft for review
**Linear:** (to be filed — GitHub issue too, per repo policy)

> **Update 2026-07-03 (AI-1121 / #231):** The nested-group fast-follow (§3, §6b,
> Open decision #2) has **landed**. GitLab nested groups
> (`group/subgroup/project`) are now supported end-to-end: `GitUrlParser` and
> `PrRefParser` parse a multi-segment owner (owner = all segments before the last;
> project = last segment), and the server URL-**decodes** the owner/repo route
> values before hashing. **The §6b `%2F` worry was disproved empirically** (.NET 10
> Kestrel spike): `%2F` in a named route segment is *accepted* (HTTP 200), is *not*
> split, and arrives *still-encoded* in the route value, so a single
> `Uri.UnescapeDataString` recovers the slash — **no route-template change was
> needed**. `repo_hash` stays host-agnostic (Open decision #1 unchanged).

## Problem

kcap's session recording tags each session with the pull request it belongs to,
so the review features can group sessions by PR. Today that PR is discovered by
shelling out to the GitHub CLI (`gh pr view`) and the explicit `kcap review <ref>`
path only understands `github.com` URLs. As a result, PR/MR detection only works
for GitHub.

The server has moved user authentication to WorkOS. Tenants can now be GitHub,
GitHub Enterprise, GitLab.com, or self-hosted GitLab organizations. The PR
detection surface has not kept up.

## Key findings (why this is smaller than it looks)

Investigated the CLI (`kcap-cli`) and the server (`../kcap-server`). The important
facts:

1. **The PR is only a grouping key.** The server derives the "PR view" (files,
   transcripts, tests) from recorded kcap sessions grouped by `owner/repo/pr#`
   (`session_file_changes` JOIN `session_prs`). It never fetches a real diff.
   There are **zero** calls to a provider API for PR data anywhere in the server.

2. **The server has no provider API access for tenants — by design.** WorkOS
   supplies identity only. There is a global GitHub App, but it is used for
   auth/installation checks, not to read repos or PRs. So resolving "branch → PR"
   on the server would mean rebuilding the per-tenant provider integration that
   the WorkOS migration deliberately removed. **Out of scope.**

3. **The CLI supplies `pr_number`.** `RepositoryDetectedEvent` carries
   `pr_number/pr_title/pr_url/pr_head_ref`; the server stores whatever the CLI
   sends and never resolves a branch itself. So the *only* GitHub-locked piece is
   the CLI's client-side discovery of the PR/MR number.

4. **Account linking (AI-967) does not help.** It is "identity only, not auth" —
   it captures the user's GitHub id/login/avatar, not a repo-scoped token, and is
   GitHub-only. It cannot resolve PRs and is orthogonal to this work.

5. **The grouping key is host-agnostic.**
   `RepoHashHelper.ComputeRepoHash(owner, repo) = SHA256("{owner}/{repo}".ToLowerInvariant())[..16]`.
   The host is not part of the hash, so `github.com/foo/bar` and
   `gitlab.com/foo/bar` collide. **Correcting this is not a cheap re-projection:**
   `repo_hash` is embedded in *event stream names* — `JudgeFacts-repo-{repoHash}-{category}`,
   `JudgeFactEmbeddings-repo-{repoHash}-{category}`, `RepoSettings-{repoHash}`,
   `RepoGuidelineSettings-{repoHash}`, `JudgeFactCuration-{repoHash}` (written via
   `SessionWriter.Writes.cs` / `RepoSettingsService`) — and carried inside those
   events' `ScopeId`/`RepoHash` fields. A read-model re-projection rebuilds SQLite
   but does **not** rename streams, so changing the hash orphans all retained judge
   facts, promotions, mutes, and per-repo settings. So this effort treats the
   collision as a documented limitation, not something to fix here (see §6a).

**Conclusion:** resolution stays **client-side**, and because the PR is just a
label, the server endpoints and MCP review tools already work for any provider
once the CLI sends a correct label.

## Goals

- `kcap` auto-detects the PR/MR for the current branch on **GitHub.com and
  GitLab.com**, and `kcap review <url>` accepts both providers' URLs.
- GitHub Enterprise keeps working (it already does — `gh pr view` auto-targets the
  remote's host).
- **No provider tokens are managed by kcap.** We delegate to the vendor CLIs
  (`gh`, `glab`), which own their own auth exactly as `gh` does today.
- Detection stays **best-effort**: a missing/unauthenticated CLI, unknown host, or
  timeout results in an untagged session, never an error.

## Non-goals

- Server-side PR resolution or any per-tenant provider API integration.
- Fetching real PR diffs from the provider (the review model is derived from
  sessions, unchanged).
- Explicit per-profile `host → provider` configuration. Self-hosted GitHub
  Enterprise / GitLab work only opportunistically, when the vendor CLI is already
  authenticated to that host (see "Custom / self-hosted hosts" below). Forcing a
  provider for an unauthenticated custom host is left as a clean extension point.
- Bitbucket or any provider beyond GitHub / GitLab.
- Depending on AI-967 account linking.

## Design

### 1. Provider routing (which tool for which host)

`RepositoryDetection.DetectRepositoryAsync` is the single detection engine and the
only place `gh`/`glab` is spawned, so all detection changes live behind it. Note
its blast radius is wide: besides hook enrichment (`EnrichWithRepositoryInfo`) and
the MCP review server (`McpReviewServer.DetectPrFromGitAsync`), it is called by
every vendor hook (Claude/Codex/Gemini/Copilot/Kiro/Pi/OpenCode), the sessions and
flows MCP servers, `RecapCommand`, `CurateCommand`, `WatchCommand`, `RepoExclusion`,
and `ImportCommand` (including a **per-session loop over a bulk import**). Only
`ClaudeHookCommand` passes a time budget; the rest use the default caps. This
matters for latency (see Error handling) — a slow provider probe multiplies across
a bulk import.

After parsing the remote into `(host, owner, repo)`, choose a detector by host:

| Remote host        | Detector                    |
|--------------------|-----------------------------|
| `github.com`       | GitHub (`gh`)               |
| `gitlab.com`       | GitLab (`glab`)             |
| any other host     | probe (see §2)              |

Introduce a small seam — an `IPrDetector` with `TryDetectAsync(host, owner, repo, branch, cwd, budget)` returning the
`(prNumber, prTitle, prUrl, prHeadRef)` tuple or null — with two implementations:

- **`GitHubCliPrDetector`** — runs the existing
  `gh pr view --json number,title,url,headRefName` (unchanged). `gh` auto-targets
  the remote's host, so GitHub Enterprise works with no extra code.
- **`GitLabCliPrDetector`** — runs
  `glab api --hostname <host> "projects/{Uri.EscapeDataString(owner/repo)}/merge_requests?source_branch={Uri.EscapeDataString(branch)}&state=opened"`,
  parses the JSON **array**, and selects the MR per the rules below. Both the
  project path **and** the branch must be `Uri.EscapeDataString`-encoded: git
  branch names legally contain `/`, and can contain `&`/`+`/`%`, which would
  otherwise corrupt the query and select the wrong MR (unit-test an encoded branch,
  e.g. `feat/a&b`).

**MR selection (critical correctness).** GitLab's `source_branch` filter is
*ignored when empty*, returning **all** open MRs — so a naïve "take the first
element" mis-tags the session on a detached HEAD or when `git branch --show-current`
returns empty. The detector must therefore:

1. **Abort if the branch is empty/unknown** — no branch, no GitLab detection
   (`gh pr view` has no analogous failure because it takes no branch argument).
2. **Filter the returned array to MRs whose `source_branch` exactly equals the
   local branch** (defense in depth even though the query already filters).
3. If more than one remains, pick the **most recently updated** (`updated_at`);
   log nothing (best-effort). This mirrors `gh pr view`'s single-PR behavior.
   `draft`/`work_in_progress` MRs are eligible (kcap tags them like any other).

**External-tool surface (verified on installed tooling, gh 2.93.0 / current glab):**
`gh auth status --json hosts` is a stable flag keyed by hostname; note `--json`
forces exit 0 even on auth failure, so the custom-host probe must inspect the JSON
payload, not the exit code. `glab api` is a real JSON passthrough (default
`--output json`), `--hostname` exists, and MR objects carry `iid`/`id`/`title`/
`web_url`/`source_branch`/`updated_at`/`draft`. We build the URL-encoded project
path (`owner%2Frepo`) ourselves rather than using glab's `:fullpath` placeholder
**because `:fullpath` is resolved from the current directory's repo and would not
respect an explicit `--hostname`/owner** — hand-building keeps the two consistent.

The process-spawn helper (`RunCommandAsync`) is factored so the detector can be
unit-tested with a fake command runner instead of shelling out.

#### GitLab field mapping

| GitLab MR field           | → `RepositoryDetectedEvent` |
|---------------------------|------------------------------|
| `iid` (per-project MR #)  | `pr_number`                  |
| `title`                   | `pr_title`                   |
| `web_url`                 | `pr_url`                     |
| `source_branch`           | `pr_head_ref`                |

Use `iid` (the per-project number shown in MR URLs), **not** the global `id`.

### 2. Custom / self-hosted hosts

This routing branch **is** part of the first pass — it is cheap and, crucially,
preserves today's GitHub Enterprise behavior (the current code runs `gh pr view`
unconditionally, so a GHE host already works; host routing must not regress that).

For a host that is neither `github.com` nor `gitlab.com`, detection probes rather
than guesses:

- If `gh auth status --json hosts` (inspect the JSON, not the exit code) lists the
  host → it is GitHub → use the GitHub detector (`gh` targets it automatically).
  This keeps GitHub Enterprise working with zero configuration for anyone whose
  `gh` is authenticated to that host.
- Otherwise, attempt the GitLab detector best-effort. On an unknown/unauthenticated
  host `glab api` does not silently no-op — it prints an error and exits non-zero,
  which `RunCommandAsync` already maps to `null` (untagged session). The caveat is
  **latency**: the attempt may incur DNS/connect time before failing, so it must
  run under the detection budget (see Error handling), and this probe path should
  be skipped entirely for unbudgeted bulk callers (see below).
- Otherwise, no detection (session untagged).

No custom domains are hardcoded and no config map is required — self-hosted works
opportunistically whenever the matching vendor CLI is already authenticated to
that host. What is **out of scope** is an *explicit* per-profile `host → provider`
map (for forcing a provider when the CLI is not authenticated); it can be layered
on this same routing step later if ever wanted.

### 3. Remote parsing — host extraction (first pass); nested groups deferred

Detection needs the **host** to route, which the current parsing does not surface.
**First pass:** extract the host by reusing `RemoteMatcher.NormalizeRemoteUrl`
(which yields `host/owner/path`), so parsing stays in one place. Owner/repo
continue to come from the existing single-level parse
(`GitUrlParser.ParseRemoteUrl`), which already handles `gitlab.com/group/project`.

`GitUrlParser.ParseRemoteUrl` captures exactly two path segments, so GitLab
**nested groups** (`gitlab.com/group/subgroup/project`) fail to parse and yield no
owner/repo — such repos are simply **not auto-detected in the first pass**
(graceful: untagged session). Generalizing the parser to multi-segment namespaces
is **deferred to the nested-group fast-follow** alongside the server route change
(§6b); doing it earlier would tag sessions that `kcap review` cannot read (the
routes are still `/{owner}/{repo}/pulls/{n}`). Until §6b lands, nested-group
remotes are **consistently unsupported** across detection, `PrRefParser` (§4), and
`RepoMatcher` (§5) — one scope, everywhere.

**Cache.** `DetectRepositoryAsync` caches `RemoteUrl`/`Owner`/`RepoName` per cwd
for 1 hour (`GitCacheEntry`) and reuses the cached `owner`/`repoName` *without
reparsing* (`RepositoryDetection.cs:91`). Because host/provider routing is new,
two changes are required: (1) the cache must also carry the **host** (routing needs
it), and (2) pre-upgrade entries were produced by the old logic (no host; e.g. a
nested-group repo cached with null owner), so **bump the `GitCacheEntry` schema
version to invalidate stale entries on upgrade** — otherwise a repo can stay
misparsed/unroutable for up to the TTL. **Requirement:** add `host` to
`GitCacheEntry` and bump its schema version so pre-upgrade entries are discarded.
This keeps today's cache-hit path untouched (no parsing on a hit). The alternative
of caching only `RemoteUrl` and re-deriving on every load was rejected: it moves
parsing (and the "not a git repo" / null-owner handling) onto the cache-**hit**
path, where parsing never runs today, on every session across the wide
unbudgeted-caller set (§1).

### 4. Explicit PR/MR reference parsing (`PrRefParser`)

`PrRefParser.TryParse` must accept, in addition to today's forms:

- GitHub PR URL on any host: `https?://<host>/<owner>/<repo>/pull/<n>` (drop the
  literal `github.com`), keeping the existing trailing-suffix tolerance
  (`(?:[/?#].*)?$`).
- GitLab MR URL: `https?://<host>/<owner>/<repo>/-/merge_requests/<n>` with the
  **same trailing-suffix tolerance** as the GitHub pattern, so browser-copied URLs
  ending in `/diffs`, `/commits`, `?query`, or `#note_…` fragments still parse
  (GitLab users commonly copy these). **Single-level `owner/repo` only** in the
  first pass — nested-group (multi-segment owner) MR URLs are rejected until §6b
  lands, consistent with §3.
- Shorthand `owner/repo#123` stays **single-level and unchanged**. The existing
  regex deliberately forbids `/` in the repo group (a slash there would produce a
  malformed API path segment); relaxing it reopens exactly that hazard.

Because the URL now determines the provider implicitly and the server key is
`owner/repo`, `TryParse` continues to return `(owner, repo, prNumber)` — all
single-segment in the first pass.

### 5. Daemon repo matching (`RepoMatcher`)

`RepoMatcher.FindAsync` builds `target = $"github.com/{owner}/{repo}"` and compares
against `RemoteMatcher.NormalizeRemoteUrl(origin)` (which is `host/owner/path`).
The hardcoded `github.com` prefix must go.

**Decision: match on the `owner/repo` suffix only** — strip the leading host
segment from the normalized remote (`host/owner/path`) and compare the remaining
`owner/repo` against the requested `owner/repo`, ignoring host. This is consistent
with the host-agnostic `repo_hash` (§6a) the rest of the system already uses, and
with the single-level scope of §3 (nested-group remotes simply won't match until
that scope is lifted). Note this is *not* free: the normalizer emits the host, so
it must be stripped before comparing.

The "most correct" alternative — threading the real host through the match — is
**out of scope and larger than it looks**: the daemon request
(`FindRepoForRemoteRequest`) carries no host, the server builds it from owner/repo
only, and the server's `repositories` table has **no host column**. Host-aware
matching would require host on the event, host projected into `repositories`, a new
request field, and daemon changes — the same chain that §6a's hash change would
need. Deferred with the collision limitation.

### 6. Server-side considerations (`../kcap-server` — separate repo/PR)

These are dependencies, not CLI work, but block correct end-to-end GitLab support.

**a. Host-agnostic `repo_hash` collision — accepted as a known limitation.**
`ComputeRepoHash(owner, repo)` omits the host, so two repos with the same
`owner/repo` on different providers share a `repo_hash` and therefore share review
data. Fixing it properly means a **stream rekey**, not a re-projection: `repo_hash`
names event streams (`JudgeFacts-repo-{repoHash}`, `RepoSettings-{repoHash}`, …),
so rebuilding read models would leave those streams orphaned (see Key findings §5).
The raw event does carry `remote_url` (host is derivable), so read models *could*
be rebuilt host-aware — but the streams could not, without a dedicated rekey
migration. The only existing rekey (`JudgeFactRekey`) is a *user-identity* re-key
that re-projects columns while leaving repo-hash stream names intact — so there is
**no precedent for renaming a `repo_hash`-keyed stream**, which underscores rather
than eases the cost. **Decision:** do not change the hash in this
effort; document the collision (realistically rare — it needs the identical
`owner/repo` on two providers both recorded to the same tenant). Revisit only if a
collision is actually hit.

**b. Nested-group routing.** Review routes are
`/api/review/{owner}/{repo}/pulls/{n}` — single path segments. A GitLab `owner`
containing a slash cannot be a single segment. The CLI already URL-encodes
owner/repo (`ReviewCommand`, `McpReviewServer`, via `Uri.EscapeDataString`), but
whether an encoded `%2F` survives ASP.NET routing to this endpoint is **unverified
and must be tested empirically against this server** (it is commonly rejected by
default). The file-context route already uses a `{**filePath}` catch-all, so a
catch-all or dedicated project-path parameter is a proven pattern if `%2F` fails.
This is a server-side routing change — but note the daemon `RepoMatcher` (§5) is a
separate client-side consumer of `owner/repo` that also needs the multi-segment
handling, so the change is not purely server-side.

**Scoping choice for the first pass:** to avoid the server routing change entirely,
the initial release restricts GitLab auto-detection and `kcap review` to
**single-level namespaces** (`owner/repo`), which fit the existing routes. Nested
groups are a fast-follow once §6b is resolved. (See Open decision #2.)

### 7. Docs

- `README.md` — Getting started prerequisites and the `kcap review` command
  section: document `glab` as a soft dependency for GitLab users (same shape as
  `gh` for GitHub), and that `kcap review` accepts GitLab MR URLs. (Repo policy:
  README must change in the same PR as any user-facing CLI surface change.)
- `ReviewCommand` usage text (currently shows only the GitHub URL/shorthand) — add
  the GitLab MR URL example.
- `src/Capacitor.Cli.Core/Resources/help-*.txt` as applicable.

These user-facing GitLab doc changes (README + help + `ReviewCommand` usage) land
with the **auto-detector** PR, not the parser groundwork — so the docs never
advertise GitLab review before it works end-to-end (see Open decision #3). The
parser-only PR, if merged first, is internal groundwork and adds no user-facing
GitLab surface, so it triggers no README change on its own.

## Error handling / degradation

Preserve the current best-effort contract in `DetectRepositoryAsync`:

- Unknown host, missing `gh`/`glab`, unauthenticated CLI, non-git dir, malformed
  JSON, or timeout → return without PR fields; the session is recorded untagged.

**Budget is not "unchanged" — it must be re-sliced.** Today one `ghCap` is carved
from the budget remaining after the git probes and spent on a single `gh pr view`.
The new flow can spend up to three sub-steps — an optional `gh auth status` probe
(custom hosts only), then the provider detector, and for GitLab a network round
trip. The model is an **effective provider budget** that always exists:

- Define an **effective provider budget** for all provider work (auth probe +
  detector) combined, not per step. When the caller passes an explicit budget
  (only `ClaudeHookCommand` does), it is the post-git remainder. When the caller
  passes none — most surfaces: MCP servers, `RecapCommand`, `CurateCommand`,
  `ImportCommand` — it defaults to the historical fixed cap (today's 2s `ghCap`).
  So GHE probing and GitLab detection run on **every** surface; "no explicit
  budget" means "use the default cap," **not** "skip detection." If the effective
  budget is exhausted, skip the remaining provider work.
- The `gh auth status` probe runs only for **non-SaaS hosts** (skipped for
  `github.com`/`gitlab.com`, which route directly) and is charged against the same
  effective budget.
- **Sole exception — the `ImportCommand` bulk loop:** it must reuse a cached
  host→provider decision per repo (or skip the custom-host probe) so a slow probe
  cannot multiply across many sessions in one import.
- Child processes are killed on timeout (existing behavior, extended to `glab`).

## Testing

Unit tests (pure, no network):

- `PrRefParser` — GitHub PR URLs on `github.com` and other hosts; single-level
  GitLab MR URLs, including browser-copy suffixes (`/diffs`, `/commits`, `?query`,
  `#note_…`); shorthands; and rejections. **Nested-group MR URLs must be rejected**
  in the first pass (asserts the deferred scope, §3/§6b).
- `GitUrlParser` — single-level namespaces, SSH and HTTPS, `.git` suffix; and that
  a nested-group remote yields no owner/repo (deferred). Host extraction via
  `RemoteMatcher.NormalizeRemoteUrl`.
- `RemoteMatcher` / `RepoMatcher` — host-agnostic `owner/repo`-suffix matching
  (host stripped), GitLab remotes.
- Provider routing — host → detector selection, including the custom-host probe
  (assert it inspects the `gh auth status` JSON, not the exit code), via a fake
  command runner (no real `gh`/`glab`).
- Detection cache — a pre-upgrade `GitCacheEntry` (no host) is invalidated by the
  version bump / re-derived from `RemoteUrl`, not served stale.
- `GitLabCliPrDetector` request building — assert the project path and an
  `&`-containing branch are `Uri.EscapeDataString`-encoded in the `glab api` args.
- `GitLabCliPrDetector` field mapping — feed a captured GitLab MR JSON payload and
  assert the `iid → pr_number` mapping.
- `GitLabCliPrDetector` selection/guard cases (the Critical finding): empty/unknown
  branch → no detection (must NOT tag from an all-MRs response); a payload of
  multiple open MRs → the one matching `source_branch` is chosen, and the
  most-recently-updated wins on ties; an MR whose `source_branch` ≠ local branch is
  rejected.

AOT: run `dotnet publish -c Release` and confirm no new IL3050/IL2026 warnings
(new JSON parsing must use the source-generated `JsonSerializerContext`, and any
`JsonArray` built by hand must use the constructor, not a collection expression).

## Open decisions (resolve in planning)

Each carries a recommended default so a plan can proceed if unchallenged.

1. **`repo_hash` host inclusion.** Include host (a stream **rekey** migration, not a
   cheap re-projection — see §6a) vs. keep host-agnostic and accept the
   cross-provider collision. Drives §5 and §6a. *Recommendation:* keep it
   host-agnostic and accept the (rare) collision; the rekey cost far outweighs the
   risk. Match the daemon (§5) to this by comparing `owner/repo` suffix only.
2. **GitLab nested groups.** Support now (needs §6b server routing) vs. single-level
   `owner/repo` only in the first pass. *Recommendation:* single-level first to
   avoid the Kestrel `%2F` routing change; add nested groups as a fast-follow.
3. **Delivery sequencing.** The pure-string wins (`PrRefParser` + `RepoMatcher` +
   `GitUrlParser`/host extraction) and the `GitLabCliPrDetector` auto-detector can
   land in separate PRs. *Recommendation:* split for **engineering** reasons — the
   pure-string PR is low-risk, dependency-free groundwork that can merge first — but
   **the first user-visible GitLab release must include auto-detection.** Parse-only
   is *preparatory, not shippable GitLab support*: with no auto-detector, no GitLab
   session is ever tagged, so the server has no review context, and
   `kcap review <gitlab-url>` parses the URL but then **404s** on `ReviewCommand`'s
   existing review-context check (`GET /api/review/{owner}/{repo}/pulls/{n}`). Do
   not announce GitLab support, or document the `kcap review` GitLab example (§7),
   until the auto-detector ships.
