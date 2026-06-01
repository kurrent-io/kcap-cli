# AI-669 — Cursor IDE realtime ingest via Cursor hooks

**Status:** design proposed (rev 6 — vendor-agnostic `kapacitor hook --cursor` surface, harmonization follow-ups AI-732 / AI-733 filed), supersedes the watch-based proposal
**Linear:** [AI-669](https://linear.app/kurrent/issue/AI-669/cursor-ide-realtime-ingest-cursor-hooks-integration) (umbrella) → [AI-730](https://linear.app/kurrent/issue/AI-730/cli-cursor-hooks-dispatcher-and-setup-wiring) (CLI), [AI-731](https://linear.app/kurrent/issue/AI-731/server-cursor-hooks-ingest-routes-and-transcript-line-dedup) (Server) — must ship together
**Related:** [AI-661](https://linear.app/kurrent/issue/AI-661/cursor-session-ingest-milestone-a-post-hoc-cli-import) (post-hoc SQLite import), [AI-680](https://linear.app/kurrent/issue/AI-680/cursor-cli-daemon-support-for-hosted-agents), [AI-682](https://linear.app/kurrent/issue/AI-682/acp-support-for-live-agent-integrations)
**Supersedes:** `docs/superpowers/specs/2026-05-25-ai-669-cursor-watch-design.md` (foreground SQLite/WAL poll watcher)

## Problem

Cursor IDE Agent sessions only reach Capacitor today via the manual `kapacitor import --cursor` (AI-661), so the live dashboard/eval/review loop lags Claude and Codex which stream via hooks.

The earlier AI-669 design proposed a foreground `kapacitor cursor watch` that polled Cursor's SQLite/WAL state on a 2-second interval. Cursor has since shipped a [hooks API](https://cursor.com/docs/hooks) **and** a per-session JSONL transcript file in Claude's content-block format. The combination eliminates the entire SQLite/WAL poll model.

## What real Cursor invocations look like (validated 2026-06-01)

Empirical capture against Cursor 3.5.17 confirmed the following contract:

### Hook invocation

- Cursor invokes the literal `command` string from `hooks.json`. Argv past the script name is whatever we put in the config; Cursor does not inject argv.
- Hook runs from `~/.cursor/` (user-scope), so relative paths in `command` resolve there.
- The JSON payload arrives on **stdin** and includes `hook_event_name`. This matches the Claude/Codex convention — one CLI dispatcher entry can branch on `hook_event_name` exactly like `CodexHookCommand` does.
- Cursor exports a rich env: `CURSOR_PROJECT_DIR`, `CURSOR_WORKSPACE_LABEL`, `CURSOR_USER_EMAIL`, `CURSOR_VERSION`, `CURSOR_LAYOUT`, `CURSOR_EXTENSION_HOST_ROLE`. We mostly don't need these because the same data is in the JSON payload.

### Payload shape (representative `sessionStart`)

```json
{
  "hook_event_name": "sessionStart",
  "session_id":      "8c3276c2-c8f7-43ce-9889-8c2becf5240a",
  "conversation_id": "8c3276c2-c8f7-43ce-9889-8c2becf5240a",
  "generation_id":   "",
  "model":           "default",
  "composer_mode":   "agent",
  "is_background_agent": false,
  "cursor_version":  "3.5.17",
  "workspace_roots": ["/Users/alexey/dev/eventstore/kapacitor-web"],
  "user_email":      "alexey@ubiquitous.no",
  "transcript_path": null
}
```

`session_id` and `conversation_id` are the same dashed UUID. `transcript_path` is null on `sessionStart`, populated on subsequent events.

### Transcript file

Cursor maintains a JSONL transcript per session at:

```
~/.cursor/projects/<sanitized-workspace>/agent-transcripts/<session_id>/<session_id>.jsonl
```

The file uses Anthropic content-block format — essentially the same shape as Claude Code's transcript:

```jsonl
{"role":"user","message":{"content":[{"type":"text","text":"..."}]}}
{"role":"assistant","message":{"content":[
  {"type":"text","text":"..."},
  {"type":"tool_use","name":"Glob","input":{...}}
]}}
```

This is the key architectural finding. **For live ingest we no longer need SQLite at all.** The transcript file is the authoritative on-disk record; hooks tell us when to read it.

## Goals

- Live ingest of Cursor IDE Agent sessions without a long-running kapacitor process.
- No new user-facing CLI command. Setup writes Cursor's hook config; the CLI is invoked by Cursor as a hook handler, same shape as Claude/Codex.
- Reuse the existing transcript-parsing path used by the Claude integration for backfill and per-turn forwarding.
- Preserve the existing privacy posture: payload allowlist, size caps, no raw DB dump.

## Non-goals

- No foreground watcher, no SQLite/WAL polling loop, no fingerprinting, no rescan cadence.
- No daemon-hosted IDE watcher.
- No remote control of Cursor IDE sessions (permission/elicitation needs ACP — AI-682).
- No Cursor CLI hosted-agent support (AI-680).
- No VSCode/Cursor extension.
- No periodic safety-net reconciliation in this milestone. If hook delivery proves lossy in practice, a scheduled `kapacitor import --cursor` covers it later; not in scope here.

## Cursor hook event coverage

| Cursor hook | Payload (key fields) | Server route (proposed) |
|---|---|---|
| `sessionStart` | `session_id`, `conversation_id`, `is_background_agent`, `composer_mode`, `model`, `workspace_roots[]`, `user_email`, `cursor_version` | `POST /hooks/session-start/cursor` |
| `sessionEnd` | adds `reason`, `duration_ms`, `final_status`, `transcript_path` | `POST /hooks/session-end/cursor` |
| `beforeSubmitPrompt` | `prompt`, `attachments[]` (`{type, file_path}`), `transcript_path` once populated | `POST /hooks/user-prompt/cursor` |
| `afterAgentResponse` | `text` | `POST /hooks/agent-response/cursor` |
| `afterAgentThought` | `text`, `duration_ms` (new capability vs. SQLite path) | `POST /hooks/agent-thought/cursor` |
| `preToolUse` | `tool_name`, `tool_input`, `tool_use_id`, `cwd`, `agent_message` | `POST /hooks/pre-tool-use/cursor` |
| `postToolUse` | `tool_output`, `duration` | `POST /hooks/post-tool-use/cursor` |
| `postToolUseFailure` | `error_message`, `failure_type`, `duration` | `POST /hooks/post-tool-use-failure/cursor` |

`sessionEnd` is an explicit terminal signal — the original watcher design called this "best-effort only".

## Architecture

### CLI dispatcher

Add `CursorHookCommand` as a sibling of `src/Kapacitor.Cli/Commands/CodexHookCommand.cs`. A single vendor-agnostic `hook` command in `Program.cs` dispatches to the per-vendor handler via a flag:

```csharp
case "hook":
    if (args.Contains("--cursor")) return await CursorHookCommand.Handle(baseUrl!, Console.In);
    // future: --codex, --claude flags route here too once those are migrated
    Console.Error.WriteLine("kapacitor hook requires a vendor flag (e.g. --cursor)");
    return 1;
```

The dispatcher reads stdin, parses JSON, branches on `hook_event_name`, and POSTs to the matching server route. Same internal pattern as Codex.

**Why not `kapacitor cursor-hook`.** The existing codebase already has two inconsistent vendor-hook conventions: Claude uses per-event top-level commands (`kapacitor session-start`, `session-end`, etc., listed in `Program.cs:15`), Codex uses a single vendor-suffix command (`kapacitor codex-hook`, `PluginCommand.cs:10`). Adding `kapacitor cursor-hook` makes it three. The `kapacitor hook --vendor` form is vendor-agnostic — adding a new vendor adds a flag and server routes, not a new top-level command — and lets the URL construction live in one place (vendor flag → URL prefix segment). Codex migration to `kapacitor hook --codex` is a separate follow-up; see "Follow-ups" below.

For every event the dispatcher must:

1. Read stdin, parse JSON, return 0 on malformed input (never crash Cursor).
2. Normalize `session_id` (dashless), matching the server's `AgentSession-{dashless}` stream convention used by `CursorImportSource`.
3. Inject `home_dir` and `agent_host_id` (when `KAPACITOR_AGENT_ID` is set), matching the Codex/Claude dispatchers.
4. Honor `DisabledSessions.IsDisabled(sessionId)`.
5. POST to the matching server route under the **shared 2s budget defined below**.
6. Emit nothing on stdout. Cursor reads stdout as control output (e.g. `permission` decisions); silence is the safe default for ingest-only hooks.

`hooks.json` writes the bare `kapacitor hook --cursor` command — no argv tail, no resolved absolute path. Cursor's hooks.json key is already the source of truth for the event type, and the dispatcher reads `hook_event_name` from the stdin payload.

**Why bare PATH lookup and not an absolute path.** Kapacitor is distributed via `npm install -g @kurrent/kapacitor`, which installs a node launcher (`<npm-prefix>/lib/node_modules/@kurrent/kapacitor/bin/kapacitor.js`) and a `kapacitor` symlink in the npm global bin (e.g. `/opt/homebrew/bin/kapacitor`). The launcher resolves the platform-specific package (`@kurrent/kapacitor-<os>-<arch>`) and exec's the AOT native binary inside it. Writing an absolute path into `hooks.json` would have to point at one of:

| Option | Problem |
|---|---|
| The native AOT binary (what `Environment.ProcessPath` returns from inside the running process) | Bypasses the launcher's platform-resolution. Path is version- and platform-package-specific. Breaks on `npm update -g` if the platform package directory layout changes. |
| The node launcher (`kapacitor.js`) | Requires a node shebang resolver; depends on `/usr/bin/env node` being present in Cursor's PATH anyway. Adds quoting complexity for paths with spaces. |
| The npm symlink (`/opt/homebrew/bin/kapacitor`) | Stable, but the AOT binary has no clean way to discover the symlink that points at it — it would need to shell out to `which kapacitor` or `npm prefix -g`. |

Bare PATH lookup sidesteps all three. It relies on Cursor inheriting a PATH that contains the npm global bin, which empirical probe data confirms for Cursor 3.5.17 on macOS — the captured hook env included the user's full shell PATH (`/opt/homebrew/bin`, `~/.local/bin`, `/usr/local/bin`, etc.). If a future Cursor build is observed launching hooks with a minimal PATH that excludes the npm bin, the fallback is to teach the npm postinstall script to symlink into `/usr/local/bin` as well, or to teach setup to write the resolved npm-symlink path — but neither is needed today.

Setup behavior:

- Verify `kapacitor` is resolvable on PATH (`which kapacitor` equivalent) before writing `hooks.json`. If not, surface a setup error rather than write a config that will silently fail.
- The `command` value is `kapacitor hook --cursor` verbatim. No quoting concerns since the string contains no shell metacharacters.

### Dispatcher time budget (whole hook path)

**The 2-second wall-clock budget covers the entire dispatcher path**, not just transcript replay. From hook entry to hook exit:

1. Hook-event POST (e.g. `POST /hooks/session-start/cursor`) — **must not** use `HttpClientExtensions.PostWithRetryAsync` with its 30-second default (`src/Kapacitor.Cli.Core/HttpClientExtensions.cs:113`). Use a short per-call timeout (~1s) via an explicit `CancellationTokenSource`.
2. Watermark GET (if backfill applies) — short timeout (~500ms).
3. Transcript-line POSTs (if backfill applies) — short per-call timeout (~1s each), looping until the shared budget is exhausted.

A `Stopwatch` started at the top of `Handle` arms a `CancellationTokenSource` with a 2-second deadline that is linked into every outbound HTTP call. Once the deadline fires, any in-flight call is cancelled and the dispatcher exits 0 with whatever partial progress was made. No phase has an independent budget — the dispatcher itself owns the clock.

Add a `HttpClientExtensions.PostOnceAsync(timeout, ct)` overload (or just inline `HttpClient.Timeout` + a linked CTS at the call sites) so neither the hook POST nor the transcript-line POSTs reuse the 30-second retry-wrapper default. The retry wrapper is unsafe in any hook-path call: idempotency on the server (canonical-event identity + watermark) makes the next-hook invocation a free retry. In-hook retries just consume the budget.

### Hook config registration

Setup writes `~/.cursor/hooks.json` invoking the PATH-resolved `kapacitor` binary:

```json
{
  "version": 1,
  "hooks": {
    "sessionStart":       [{ "command": "kapacitor hook --cursor" }],
    "sessionEnd":         [{ "command": "kapacitor hook --cursor" }],
    "beforeSubmitPrompt": [{ "command": "kapacitor hook --cursor" }],
    "afterAgentResponse": [{ "command": "kapacitor hook --cursor" }],
    "afterAgentThought":  [{ "command": "kapacitor hook --cursor" }],
    "preToolUse":         [{ "command": "kapacitor hook --cursor" }],
    "postToolUse":        [{ "command": "kapacitor hook --cursor" }],
    "postToolUseFailure": [{ "command": "kapacitor hook --cursor" }]
  }
}
```

Setup wiring:

- Detect Cursor by **user-dir presence**, not by PATH. The existing `AgentDetector.IsInstalled` only probes PATH (`src/Kapacitor.Cli/Commands/AgentDetector.cs:38`) and would silently miss Cursor IDE users who never installed the `cursor` shell command. Add `CursorPaths.IsInstalled()` that returns true when any of: `~/.cursor/` exists, `~/Library/Application Support/Cursor/User/` exists (macOS), `%APPDATA%\Cursor\User\` exists (Windows), `~/.config/Cursor/User/` exists (Linux). Surface it as `Cursor: CursorPaths.IsInstalled()` on the `DetectedAgents` record.
- Add `CursorPaths.UserHooksJson` returning `~/.cursor/hooks.json`.
- Add `InstallCursorHooks` to `CodingAgentsStep.Installers`, parallel to `InstallCodexHooks`.
- Add a JSON merger that preserves user-authored hooks (mirror `CodexHooksParser`).
- Add `--skip-cursor-hooks` argv and a Cursor section in the Step 4 prompts/help.

User-scope (`~/.cursor/hooks.json`) is the default. Per Cursor's hook precedence (Enterprise > Team > Project > User), org-managed config can override, which is acceptable. No project-scope writes from setup.

### Canonical-event ownership — hook vs. transcript dedup contract

Hook events and transcript lines carry overlapping content. The contract must be settled before either sub-issue starts work.

**Rule: the transcript JSONL is the source of truth for turn content. Hooks are the source of truth for lifecycle and metadata.**

Concretely:

| Canonical event kind | Source of truth | Why |
|---|---|---|
| `session.started` | `sessionStart` hook | Transcript file is empty / nonexistent at this point. |
| `session.ended` | `sessionEnd` hook | Transcript may have no terminal marker; hook carries `reason`, `final_status`, `duration_ms`. |
| `turn.user` (prompt text) | Transcript `role:"user"` line | Hook carries `attachments[]` (rules, files) the transcript may not fully list. Server merges per the algorithm in "Attachments merge" below. |
| `turn.assistant` (text + tool_use blocks) | Transcript `role:"assistant"` line | Hook `afterAgentResponse.text` is a dropped-on-the-floor cross-check. Server records hook arrival as a heartbeat but does not emit a canonical event from it. |
| `turn.assistant.reasoning` | `afterAgentThought` hook | Currently absent from the transcript file. Hook owns it until/unless Cursor adds it to the JSONL. |
| `tool.call` / `tool.result` | Transcript content block | `preToolUse` / `postToolUse` / `postToolUseFailure` hooks are dropped on the floor for content. The hook arrival is logged for diagnostics (latency, failure rate) but does not produce a canonical event. |
| User prompt attachments | `beforeSubmitPrompt.attachments[]` | Not in the JSONL. Hook owns it; merged onto the matching `turn.user` per the algorithm in "Attachments merge" below. |

### Attachments merge

Cursor's `beforeSubmitPrompt` carries `attachments[]` (e.g. `{type:"rule", file_path:"CLAUDE.md"}`), but the transcript JSONL `role:"user"` line does not. The server must stitch them together without an explicit join key in the transcript.

Algorithm (pinned, server-side):

1. **Per-session FIFO.** Each session has a server-side queue of pending `(generation_id, attachments[])` entries from `beforeSubmitPrompt`.
2. **Enqueue.** On a `beforeSubmitPrompt` POST with non-empty `generation_id` and non-empty `attachments[]`, append to the queue. Empty `generation_id` → drop the attachments-canonical-event entirely (log telemetry; do not enqueue). Empty `attachments[]` → no-op.
3. **Consume.** When a transcript `role:"user"` line is accepted, dequeue the head of the queue (if any) and stamp its `attachments[]` onto the canonical `turn.user` event. The dequeued `generation_id` becomes the `turn.user`'s `generation_id`.
4. **Empty queue at consume time.** Transcript line accepted before the hook arrived (out-of-order). The `turn.user` is emitted without attachments. **No retro-attach.** Late-arriving `beforeSubmitPrompt` after the transcript line was consumed lands in an empty queue; its attachments are dropped, logged as a known v1 limitation.
5. **Queue size cap.** Bounded at 16 pending entries per session. Oldest entries evict on overflow (also a known limitation; in practice the queue is 0–1 deep because Cursor fires the hook synchronously before writing the transcript line).

This means attachments are best-effort under reordering, but the common case — hook fires immediately, transcript line appended shortly after — produces perfect matching. Out-of-order is observable as a `turn.user` without attachments + a telemetry counter for dropped late attachments.

### Canonical-event identity

Transcript-sourced events use `(session_id, line_index)`. `line_index` is 0-based and matches the server's existing `last_line_number` convention used by the Claude import path (see `src/Kapacitor.Cli/Commands/TranscriptFileClassification.cs:131`). Server stores `last_line_number` = highest accepted index; CLI resumes from `last_line_number + 1`.

Hook-sourced events use **stable, deterministic IDs from payload fields**. Never arrival timestamps — those break retry idempotency.

| Hook | Canonical event | ID tuple |
|---|---|---|
| `sessionStart` | `session.started` | `(session_id, "session.started")` — singleton per session |
| `sessionEnd` | `session.ended` | `(session_id, "session.ended")` — singleton per session |
| `beforeSubmitPrompt` | merged into `turn.user` | `(session_id, "attachments", generation_id)` for the per-session FIFO entry; once consumed by a transcript `turn.user` line the attachments live on that canonical event, not as a standalone one. Empty/missing `generation_id` → drop. See "Attachments merge" below. |
| `afterAgentThought` | `turn.assistant.reasoning` | `(session_id, "reasoning", generation_id, sha256(text)[:16])` — multiple thought blocks per turn are observed in real captures; content hash dedupes repeated deliveries while letting distinct thoughts coexist within one `generation_id` |
| `afterAgentResponse`, `preToolUse`, `postToolUse`, `postToolUseFailure` | telemetry only | no canonical event; server records hook arrival for latency/failure metrics keyed by `(session_id, generation_id, hook_event_name, tool_use_id?)` |

The server enforces idempotency on `(session_id, canonical_event_id)`. Replays via the next hook invocation or via legacy `kapacitor import --cursor` produce the same canonical events and the same telemetry-keyed dedup.

This means the CLI dispatcher's job for `afterAgentResponse` / `preToolUse` / `postToolUse` / `postToolUseFailure` is essentially **fire-and-forget telemetry**, not content forwarding. The transcript replay path carries the actual turn data. `afterAgentThought` is the one exception where the hook is content-bearing.

### Mid-session adoption — bounded resumable backfill

When the dispatcher sees an event whose payload has a non-null `transcript_path` it hasn't fully ingested, it backfills. **The backfill must never block Cursor's agent loop.** `HttpClientExtensions.PostWithRetryAsync` defaults to 30s per request (`src/Kapacitor.Cli.Core/HttpClientExtensions.cs:113`); N lines × 30s would be catastrophic.

Rules:

1. **Do not construct `transcript_path`.** Use only the value Cursor provides in the payload. If it's null (e.g., on `sessionStart` before the first turn), do nothing — the next event will carry it. This avoids inventing a sanitizer for `<sanitized-workspace>` paths that's bound to drift against Cursor's actual scheme on edge cases (multi-root, symlinks, remote workspaces, Windows path encoding).
2. **No pending-drain registry.** If a session's hooks stop firing before the transcript is fully drained, the leftover lines are recovered by either (a) the user re-running `kapacitor import --cursor` for that session, or (b) a future out-of-band drainer (see rule 5). The dispatcher does **not** maintain a local list of (session_id, transcript_path) pairs to resume later. The earlier draft suggested "the next session's `sessionStart` re-checks pending sessions" — that was inconsistent with rule 1 (which forbids constructing paths from `sessionStart` where `transcript_path` is null) and is removed.
3. **Shared 2s budget.** The same wall-clock budget that governs the hook-event POST and watermark GET also governs transcript-line replay (see "Dispatcher time budget"). The dispatcher loops `for line in transcript from last_line_number+1; until budget exhausted or EOF; POST line, advance watermark`. Partial progress is durable on the server.
4. **Fail-open semantics.** On watermark-API failure, transcript-line POST failure, or budget exhaustion: log, return 0, do not block Cursor. The transcript file remains on disk, so next-hook resumption from the advanced watermark is a free retry. Hook-event POST failures are handled by the spool below — **not** by next-hook retry, because the failed hook payload would otherwise be lost forever (the transcript file does not carry sessionStart, sessionEnd, attachments, or reasoning).
5. **Optional out-of-band drainer (deferred).** If practical experience shows per-hook drainage is insufficient (large catch-ups that never complete because hooks stop firing), a follow-up issue can add a periodic `kapacitor cursor-drain` invoked from cron/launchd that reads `~/.cursor/projects/*/agent-transcripts/*/*.jsonl` and drains pending lines without involving Cursor. Not in scope here.

This makes the hook a hard real-time component: bounded latency, fail-open, no Cursor-blocking work.

### Hook-event spool (lifecycle/content hooks only)

Transcript lines survive any POST failure because they live on disk. Hook events do not — `sessionStart`, `sessionEnd`, `beforeSubmitPrompt.attachments`, and `afterAgentThought.text` exist only as in-flight payloads. A transient server outage during `sessionEnd` would permanently lose the terminal signal for that session. Mitigation: a tiny local spool.

Spool layout:

```
~/.cursor/kapacitor-pending/<dashless-session-id>.jsonl
```

One JSON line per pending payload, in arrival order:

```json
{"hook_event_name":"sessionEnd","payload":{...full normalized payload...}}
```

Spool semantics:

1. **Eligibility.** Only the four canonical-event-bearing hooks are spooled on POST failure: `sessionStart`, `sessionEnd`, `beforeSubmitPrompt`, `afterAgentThought`. Telemetry hooks (`afterAgentResponse`, `preToolUse`, `postToolUse`, `postToolUseFailure`) are accepted lossy — the spool is for canonical events only.
2. **Write path.** When a canonical-event hook POST fails (timeout, network error, 5xx, budget expiry mid-flight), append the normalized payload to the per-session spool file. Fsync optional; a crash here only loses one event we were already losing.
3. **Drain path.** Top of every `CursorHookCommand.Handle` invocation, **before** the incoming event is processed, drain the spool for `session_id` under the dispatcher budget. FIFO order. Each successful POST removes that line; on first failure, stop draining (leave the rest for next time) and proceed to the incoming event.
4. **Bounds.** Per-session spool capped at 1 MB. New lines that would exceed the cap evict the oldest. Maximum ~10k pending events per session — far above any realistic outage.
5. **Cleanup.** After a successful `sessionEnd` POST (either fresh-arrived or drained from the spool), delete the spool file for that session. Stale files older than 30 days are pruned at startup of any `kapacitor` invocation.
6. **Cross-session interleaving.** A given hook invocation only drains its own `session_id`'s spool, not other sessions'. Cursor-side hooks for other sessions will drain their own spools when they next fire. Permanently abandoned sessions (Cursor closed before kapacitor caught up) get their spool reaped at the 30-day cleanup.
7. **What it does not solve.** A session that ends while the server is unreachable and Cursor is then never reopened: the spool file sits on disk forever (until the 30-day reaper) and that session's terminal signal is lost. v1 accepts this. A future drainer (rule 5) or a startup-time spool-drain in any `kapacitor` invocation can close this gap.

The transcript-reader parsing primitives are structurally close to Claude's path — confirm during implementation whether to factor a shared "Anthropic-format transcript reader" out of the Claude integration.

### `CursorImportSource` (SQLite) — demoted to legacy fallback

`CursorImportSource` stays in the codebase for now:

- Backfill of sessions older than the transcript-file era (Cursor introduced the JSONL transcripts in a recent version — confirm cutoff during implementation).
- Recovery if a user wipes `~/.cursor/projects/` but composers still exist in SQLite.

It is **not** on the live ingest path. The hook dispatcher does not call `CursorImportSource`. `kapacitor import --cursor` keeps working unchanged for historical imports.

A follow-up issue can deprecate `CursorImportSource` entirely once we're confident no users depend on pre-transcript-file sessions.

### Privacy and size caps

The JSONL transcript shape is essentially Claude's. Reuse Claude's payload allowlist and per-message size caps; do not add a Cursor-specific allowlist. Hook event payloads (e.g. `postToolUse.tool_output`) get the same per-event cap applied at POST time.

## CLI surface delta

User-facing CLI: **no change**. No new subcommand. The only public surface is what `kapacitor setup` writes into `~/.cursor/hooks.json`. `kapacitor import --cursor` is unchanged.

Internal new entry: `kapacitor hook --cursor`, undocumented, invoked only by Cursor.

## Sub-issues

This design is implemented across two Linear sub-issues under AI-669 that **must ship together**:

- **AI-730 — CLI**: `CursorHookCommand` dispatcher, hooks-config installer, time-budgeted resumable transcript backfill, setup wiring.
- **AI-731 — Server**: per-event hook ingest routes, transcript-line ingest route, watermark API, canonical-event ownership/dedup enforcement.

## Server side (AI-731)

The CLI side (AI-730) commits the server (AI-731) to:

- Per-event ingest routes for the 8 hooks (table above). Hook arrivals for `afterAgentResponse`, `preToolUse`, `postToolUse`, `postToolUseFailure` are recorded as telemetry (latency, failure rate) but do **not** produce canonical events; transcript lines do.
- A transcript-line ingest route accepting one Anthropic content-block JSONL line at a time with `(session_id, line_index)` identity. `line_index` is 0-based and matches the existing Claude convention used by `TranscriptFileClassification.cs:131` (server stores `last_line_number`; CLI resumes from `last_line_number + 1`).
- A watermark API per `session_id` returning `last_line_number` (the highest accepted `line_index`, or absent if no lines have been accepted yet). Response budget: <500ms for the CLI's hook-path watermark check.
- Idempotent dedup enforced on `(session_id, canonical_event_id)` using the stable hook-sourced IDs defined in the "Canonical-event identity" section above (singletons for start/end, `generation_id`-keyed for attachments, `generation_id + sha256(text)[:16]` for thoughts). **No arrival-time component.**
- Per-session attachments FIFO consumed by the next accepted transcript `turn.user` line (see "Attachments merge"). Bounded at 16 entries per session; oldest evicted on overflow. No retro-attach for out-of-order delivery in v1.
- Backwards compatibility with the existing `/hooks/cursor-import` (SQLite backfill) route; the two backfill paths must converge on the same canonical-event stream.

## Testing

### Unit

- `CursorHookCommand` dispatches each `hook_event_name` to the right server route.
- `session_id` is normalized to dashless before POST.
- `home_dir` and `agent_host_id` injected on every payload.
- Malformed JSON returns 0, emits nothing on stdout.
- `DisabledSessions.IsDisabled(sessionId)` suppresses POSTs.
- Null `transcript_path` does not trigger backfill; populated `transcript_path` does.
- Transcript replay respects the 2-second wall-clock budget and exits with partial progress when exhausted; next invocation resumes from the advanced watermark.
- Transcript-line POSTs use the short HTTP timeout, not the default 30s.
- Watermark-API failure / unreachable server: dispatcher returns 0 (fail-open) and does not block.
- Hook config writer merges into existing `~/.cursor/hooks.json` without clobbering user-authored hooks.
- `CursorPaths.IsInstalled()` detects each supported platform's Cursor user dir; PATH probe is not used.
- `afterAgentResponse` / `preToolUse` / `postToolUse` POST telemetry-only payloads; the dispatcher does not attempt to emit canonical events for them.
- Hook-event spool: `sessionStart` / `sessionEnd` / `beforeSubmitPrompt` / `afterAgentThought` POST failures append to `~/.cursor/kapacitor-pending/<sid>.jsonl`; telemetry hook failures do not.
- Spool drain: next hook invocation drains FIFO under budget before processing the new event; stop draining on first failure; on success remove the line.
- Spool cap: per-session file capped at 1 MB; new entries evict oldest.
- Spool cleanup: successful `sessionEnd` deletes the per-session spool file; startup-time reaper prunes files older than 30 days.
- Setup precheck: `kapacitor` is verifiable on PATH before writing `hooks.json`; missing-on-PATH surfaces a setup error instead of writing broken config.

### Integration

- WireMock fixture for each `/hooks/.../cursor` route, drive `CursorHookCommand` end-to-end.
- Verify snapshot the outgoing payload shape per event.
- Watermark-driven transcript replay test against a fixture JSONL.
- Slow-server simulation: per-line POST takes >1s; dispatcher exits within budget with the right number of lines advanced.
- Hook arrival + transcript line carrying the same content: server emits exactly one canonical event (cross-tested with the AI-731 server fixture).

### Manual smoke

Against a real Cursor install:

1. `kapacitor setup` registers Cursor hooks.
2. Start a new Cursor Agent-mode composer → `sessionStart` POSTed, transcript backfill runs (empty), session appears live in Capacitor.
3. Send a prompt that triggers a tool → corresponding hook events POSTed, matching transcript lines POSTed, no duplicates.
4. End the conversation → `sessionEnd` POSTed.
5. Install kapacitor mid-session → on next event, transcript backfill catches turns 1..N, subsequent hooks append cleanly.
6. `kapacitor disable <sessionId>` mid-session → subsequent POSTs are suppressed.

## Resolved / open questions

| # | Question | Status |
|---|---|---|
| Q1 | argv/env contract for Cursor hooks | **Resolved** — `hook_event_name` is in stdin JSON; dispatch on payload, not argv. |
| Q2 | `session_id` vs. composer ID | **Superseded** — transcript file uses `session_id` directly; SQLite composer ID is no longer on the live path. |
| Q3 | `composer_mode` / `is_background_agent` filtering | **Resolved** — both in payload on every event. Ingest everything; filter server-side. |
| Q4 | Cursor version that introduced JSONL transcripts | **Open** — verify during implementation; gates whether `CursorImportSource` can be deprecated. |
| Q5 | Transcript-line ingest server-side schema | **Resolved (contract)** — `(session_id, line_index)` identity, one line per POST, idempotent on the server. Implementation owned by AI-731. |
| Q6 | Reconciliation between hook events and transcript lines | **Resolved (contract)** — see "Canonical-event ownership" section. Transcript owns turn content; hooks own lifecycle, attachments, reasoning, and telemetry. Server enforces idempotency on `(session_id, canonical_event_id)`. Implementation owned by AI-731. |
| Q7 | Cursor detection on machines without the `cursor` shell command | **Resolved** — detect by Cursor user-dir presence (`~/.cursor/`, OS-specific `User/`), not by PATH. |
| Q8 | Backfill blocking Cursor's agent loop | **Resolved** — 2s wall-clock per-hook budget, fail-open, short per-POST timeout, no in-hook retries, watermark-driven resumption. |

## Rollout

Additive. Existing `kapacitor import --cursor` users are unaffected. Setup gains a Cursor section behind `--skip-cursor-hooks`. No migration story for the deleted watch design — it was never shipped. `CursorImportSource` stays as legacy SQLite backfill until Q4 is resolved.

## Follow-ups (separate issues)

Two harmonization tickets, both gated on AI-669 / AI-730 landing first so the `kapacitor hook --<vendor>` pattern is proven before migrating existing surfaces:

- **AI-732 — Migrate Codex from `kapacitor codex-hook` to `kapacitor hook --codex`.** Replace `CodexHookCommand` constant in `PluginCommand.cs:10` with the unified form. Extend `CodexHooksParser.EntryReferencesKapacitorCodexHook` (`CodexHooksParser.cs:32`) to match both the new and legacy command strings so re-running setup after migration cleans up old entries. Keep the legacy `codex-hook` command alias for one release for backward compatibility with un-migrated user hooks.json files.
- **AI-733 — Migrate Claude from 7 per-event top-level commands to `kapacitor hook --claude`.** Replace the command list in `Program.cs:15` with a single dispatcher behind `kapacitor hook --claude` that branches on Claude's `hook_event_name`. Update `.claude/settings.local.json` (or user-scope settings.json) writer to emit the new command. Keep the per-event commands as aliases for one release; ensure `KAPACITOR_SKIP=1` recognition (`Program.cs:44`) still works under the new form.

Both follow-ups are pure refactors with no behavior change — the same hooks fire, the same payloads are POSTed to the same server routes, only the CLI surface changes. They unblock the long-term goal of one vendor-agnostic `kapacitor hook --<vendor>` surface across the project.
