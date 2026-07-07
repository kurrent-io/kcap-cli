# SessionStart facts injection — trim the hook critical path

## Problem

The Claude `SessionStart` hook is supposed to inject "facts from Capacitor"
(server-side guideline clusters + the team-memory index) as
`hookSpecificOutput.additionalContext`. In practice users rarely see it.

Evidence (from live transcripts under `~/.claude/projects/.../*.jsonl`, the
`hook_success` attachments):

- Across 6 recent sessions, `kcap hook --claude` ran at `SessionStart` in only
  **1**. In the other 5 there is no attachment at all — the hook exceeded
  Claude Code's **5 s** `SessionStart` timeout and was killed (killed hooks
  leave no attachment; 0 hook errors were recorded).
- In the one session it did run: `durationMs: 3629`, exit 0, **empty stdout** —
  the internal `HookBudget` (5 s ceiling − 1.5 s safety) was exhausted before
  the POST could emit, so nothing was injected.

The CLI logic is correct: invoked with a valid payload it emits the full
envelope (guidelines + team memory). The failure is **latency**, not logic.

## Root cause

The server is fast. Measured against `kurrent.kcap.ai`:

- `POST /hooks/session-start` — 130–250 ms
- `GET /api/memories/index` — 120–190 ms

The 3.6 s is spent **client-side**, as a chain of network round-trips the hook
makes before it can emit facts:

| Step | Cost | Needed to emit facts? |
|---|---|---|
| `GET /auth/config` provider discovery | ~150–250 ms | No — and it is re-fetched on **every** hook (cached only in a per-process static) |
| token refresh (near expiry) | variable | No |
| **`gh pr view` live PR detection** | **~600 ms** | **No** — facts scope by owner/repo (git, ~10 ms); the watcher backfills PR info independently |
| `POST /hooks/session-start` | ~150 ms | Yes |
| `GET /api/memories/index` (parallel) | ~150 ms | Yes |

The two avoidable costs — `gh pr view` and the repeated `/auth/config` fetch —
are what push a fresh hook process past 5 s. Neither touches the daemon, so the
fix works even for users who never run the daemon.

`RepositoryDetection`'s own comment already flags the PR round-trip as "pure
wasted latency" when PR fields aren't needed (import passes
`detectPullRequest: false`, AI-1122). And `WatchCommand` runs its own
`DetectRepositoryAsync` (with PR detection) — so the hook's pre-POST PR probe is
redundant for the session record; the watcher provides PR info anyway.

## Approach

Trim the critical path rather than cache the symptom. Two focused changes; both
speed up **every** hook event (and the watcher start), not just facts injection.

### Change A — the hook never does live PR detection

The watcher owns PR detection. The hook only needs owner/repo (git).

- `RepositoryDetection.EnrichWithRepositoryInfo` gains a
  `bool detectPullRequest = true` parameter, threaded to
  `DetectRepositoryAsync` (default preserves existing behavior for other
  callers).
- All `ClaudeHookCommand` enrichment calls pass `detectPullRequest: false`
  (session-start, session-end, subagent-stop, and the inline "other commands"
  path).
- `RepoExclusion.IsExcludedAsync` and `ClaudeHookCommand.TryResolveRepoHashAsync`
  pass `detectPullRequest: false` — both need only owner/repo.

Behavioral effect: the session-start event reaches the server without
`pr_number/pr_title/pr_url`; the watcher's own detection backfills them within
seconds. Base repo info (owner/repo/branch/host/user) is unchanged.

### Change B — persist `/auth/config` discovery to disk

`DiscoverProviderAsync` currently caches only in a per-process static, so every
fresh hook process re-fetches `/auth/config`.

- New `AuthProviderCache` (Capacitor.Cli.Core): a small JSON store at
  `cache/auth-providers.json` mapping `baseUrl → { provider, fetched_at }`, with
  a 24 h TTL. Pure `Read`/`Upsert` helpers (unit-tested without disk) plus thin
  fail-open disk wrappers (`TryGet`/`Set`). JSON via `JsonNode` (AOT-safe, no
  reflection).
- `DiscoverProviderAsync`: on a static miss, consult the disk cache before the
  network; on a successful network discovery, write both the static and the disk
  cache. The unreachable→token fallback stays uncached (as today).

## Non-goals (YAGNI)

- No local facts cache / staleness layer — with a lean path the live round-trip
  fits comfortably in 5 s.
- No daemon involvement.
- Other vendor hooks (Codex/Cursor/…) keep current behavior; this change is
  scoped to the Claude hook path where the problem was measured.

## Testing

- `DetectRepositoryAsync(..., detectPullRequest: false, run)` never issues a
  `gh`/`glab` command (injected `CommandRunner` records commands); the
  contrasting `true` case does. Base fields still resolve.
- `AuthProviderCache.Read`/`Upsert` pure-function tests: hit within TTL, miss
  when expired, upsert replaces per-baseUrl, malformed store → null.
- Existing `ClaudeHookStdoutTests` / `SessionStartVisibilityTests` continue to
  pass (envelope + visibility unaffected).

## Verification

- `dotnet publish -c Release` with no new IL3050/IL2026 warnings.
- Re-measure a real `kcap hook --claude` end-to-end: expect ≈400–600 ms
  (down from 1.9–3.6 s).
