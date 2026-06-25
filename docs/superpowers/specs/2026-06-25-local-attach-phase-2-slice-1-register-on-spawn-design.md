# Local terminal attach — Phase 2 / Slice 1: register-on-spawn (continue-from-web core)

**Issue:** AI-972 (sub-issue of AI-862).
**Decomposition:** `docs/superpowers/specs/2026-06-25-local-attach-phase-2-decomposition.md` (Slice 1).
**Parent design:** `docs/superpowers/specs/2026-06-13-local-attach-hosted-agent-design.md` (Phase 2).

## Problem

Phase 1 (AI-860, PR #155) made a locally-launched agent live in the persistent daemon,
attachable from the terminal over a local socket. But that agent is **`PrivateLocal`**:
`IsPrivate = true`, every per-agent server call suppressed, no SignalR sink, no
`KCAP_AGENT_ID`. So you cannot see or drive it from your own web UI — the moment you close
the laptop, it's gone from anywhere but that machine.

Slice 1 delivers **continue-from-web**: a locally-launched agent **registers exactly like a
UI-launched (hosted) agent**, so the owner sees and drives it from their own web UI
immediately, and the recorded session links the normal way. This is the keystone of Phase 2;
sharing (AI-974), resize aggregation (AI-973), and Windows (AI-975) build on it.

## Goal

`kcap run-agent <vendor> -- …` spawns an agent that, by default, **is a hosted agent in
every respect** — registered, streamed, permission-bridged — except for three deliberate
deltas (work location, local auth, local-socket attach). A `--private` opt-out preserves the
Phase 1 unregistered behavior.

### Non-goals (owned by other slices)

- **Web + local resize min-clamp aggregation** — Slice 2 / AI-973 (needs a new server
  contract). Slice 1 keeps Phase 1's local-only clamp and the existing agent-level web resize.
- **Sharing / pairing to teammates** — Slice 3 / AI-974 (existing web-UI "share"; verify-only here).
- **Windows** — Slice 4 / AI-975.
- **A bespoke web permission *dialog* beyond what bridge-mode reuse already gives** — none
  needed; see decisions.

## Confirmed decisions

| Decision | Choice | Rationale |
|---|---|---|
| Registration | A non-`--private` local agent sets `IsPrivate = false` and runs the **same registration sequence as `HandleLaunchAgent`** (`AgentRegisteredAsync` + dims + `AgentRunStarted`, then the per-agent server lifecycle: status, heartbeat, end-session, reconnect re-register). | "Register exactly like a UI launch" — reuses the existing hosted-agent server contract, so owner-only-until-shared and web→daemon input handlers apply unchanged. |
| `KCAP_AGENT_ID` | **Set** in spawn env. | Recorded session links to the hosted agent (`agent_host_id` tag) the normal way. |
| Streaming | **Eager.** With `IsPrivate = false`, the read loop's existing per-chunk `SendTerminalOutputAsync` fires from the first chunk — no code to add, it falls out of the gate. (Initial dimensions are sent once by the registration sequence in section a, not the read loop.) | Server accumulates its own buffer from byte one; the first web subscriber replays via the server's existing `SubscribeToTerminal`. **No new server contract.** |
| First-web-subscribe replay (item 4) | **No daemon code** — satisfied by the server's existing replay because we stream eagerly from spawn. Covered by a verification test. | The reconnect-replay-skip (`AgentOrchestrator.cs:946-954`) is about daemon *rebind*, not first subscribe; eager streaming closes the gap the parent spec worried about. |
| Permissions | **Bridge mode, exactly like hosted:** set `KCAP_RENDERED_AGENT=1` **and** `KCAP_DAEMON_URL = _permissionBridge.BaseUrl`. | User decision: match hosted's web permission dialog. The bridge already runs as a daemon hosted service; the PTY prompt still mirrors and remains answerable by keystroke (`SendInput` → PTY) from local or web. |
| Work location | Default **borrowed cwd** (in-place); `--worktree` opts into an owned worktree. **Unchanged from Phase 1.** | Local agents work in the user's checkout by default. |
| Cleanup safety | **Top invariant, unchanged:** never `Directory.Delete` / `git worktree remove` / `git branch -D` a borrowed cwd; `Prepare()` skips repo-mutating steps for a borrowed cwd. | Deleting the user's working dir on exit would be catastrophic. |
| Auth | Keep the user's `ANTHROPIC_API_KEY` (local-interactive auth), unlike hosted's daemon-managed auth. **Unchanged from Phase 1.** Orthogonal to the recorded-event provider-key scrub policy (`ProviderApiKeyPolicy` / `KCAP_USE_PROVIDER_API_KEY`). | A local agent uses the user's own login. |
| Opt-out | Keep **`--private`** (`kcap run-agent --private`): the Phase 1 unregistered path — `IsPrivate = true`, deny-all, no `KCAP_AGENT_ID` / `KCAP_RENDERED_AGENT` / `KCAP_DAEMON_URL`, native-terminal permissions, no cloud streaming. | Cheap (path already exists); preserves a purely-local mode and keeps the deny-all guarantee testable. |
| Dims to server | Send the **actual local PTY dims** at registration, and re-send on local clamp change (registered agents only), so web read-only viewers lock to the real size. | Hosted sends fixed `HostedPtyCols/Rows`; a local agent's size is client-driven and can change. Full local+web aggregation is Slice 2. |

## Implementation

All references in this repo.

### a) Daemon — register on the local spawn path

`AgentOrchestrator.HandleLocalSpawnAsync` (`Services/AgentOrchestrator.LocalIpc.cs:19-84`) is
the seam. Today it builds a local-only env (`:58-61`), creates the instance with
`IsPrivate = true` (`:65-69`), then fires `ReadAgentOutputAsync` + `AttachClientLoopAsync`.

Changes for the **registered** path (default; skipped when `--private`):

1. **Env (`:58-61`)** — build the hosted env instead, *plus* keep local auth:
   - `KCAP_RENDERED_AGENT = "1"`, `KCAP_AGENT_ID = agentId`, `KCAP_URL = _config.ServerUrl`,
     `KCAP_DAEMON_URL = _permissionBridge.BaseUrl` (mirror `AgentOrchestrator.cs:348-363`).
   - Keep `ANTHROPIC_API_KEY` re-add (`:60-61`) — local auth survives the headless scrub.
2. **Instance (`:65-69`)** — `IsPrivate = false` for the registered path (true for `--private`).
3. **Registration sequence** — after `_agents[agentId] = agent` and before/around starting the
   read loop, run the same calls `HandleLaunchAgent` makes at `AgentOrchestrator.cs:380-399`:
   - `await _server.AgentRegisteredAsync(agentId, prompt: null, model: "", effort: null, repoPath: cwd)`
   - `await _server.SendTerminalDimensionsAsync(agentId, cols, rows)` (best-effort; **use the
     spawn `cols`/`rows`, not `HostedPtyCols/Rows`**)
   - `_ = _server.AppendAgentRunEventAsync(agentId, new AgentRunStarted(null, "", null, cwd, worktree.Path, vendor))`
   - the repo-path persist/announce that follows at `AgentOrchestrator.cs:399+`.
   - **Extract a shared helper** (e.g. `RegisterAgentAsync(agent, ushort cols, ushort rows)`)
     used by both `HandleLaunchAgent` and `HandleLocalSpawnAsync`, so the two paths can't
     drift. (Hosted passes `HostedPtyCols/Rows`; local passes the client dims.)

Everything else — status changes, heartbeat, end-session, reconnect re-register, terminal
output streaming — already keys off `!IsPrivate` (`AgentOrchestrator.cs` gate sites
:511/:524/:530/:588/:594/:620/:927/:928/:995/:1061), so it activates automatically.

`prompt`/`model`/`effort` are empty for a local agent — the web UI shows it by its **agent id**
(the random string), which is the normal display for an agent with no task metadata. Fine for
Slice 1, confirmed.

### b) Daemon — re-send dims on local clamp change

`ClampPtyLocked` (`AgentOrchestrator.LocalIpc.cs:189-195`) resizes the PTY to the local
min-clamp. For a **registered** agent, after a clamp change also call
`_server.SendTerminalDimensionsAsync(agentId, clampedCols, clampedRows)` (best-effort) so web
viewers re-lock. No-op for `--private`. (Web *input* resize aggregation stays Slice 2.)

### c) Wire `--private` through the Spawn frame

The Spawn frame (`Core/LocalIpc/FrameCodec.cs:66-99`) currently encodes
`work(1) | cols(2) | rows(2) | vendor(lp) | cwd(lp) | argCount(4) | args…`. Add a **`private`
flag byte** immediately after `work`:

- `Spawn(...)` encoder (`:66-74`): write the byte after `ms.WriteByte((byte)work)`.
- `ParseSpawn` (`:82-99`): bump the leading `Require(p, o, 5)` to `6`, read the flag byte.
- The `Spawn(LocalFrame)` tuple (`:77-78`) gains a `bool isPrivate` field; update the
  destructuring at `HandleLocalSpawnAsync:20`.
- CLI/daemon ship together, so no wire-compat shim is needed; a codec round-trip test covers it.

### d) CLI — `--private` flag

`RunAgentCommand` (`src/Capacitor.Cli/Commands/RunAgentCommand.cs`) parses kcap flags before
`--`. Add `--private` (consumed by kcap, never forwarded), pass it into the `Spawn` frame
builder. `attach` is unaffected (privacy is fixed at spawn). Help text (`help-*.txt`) + README.

### e) Privacy test re-scope

The Phase 1 strict-mock privacy test (a mock `ServerConnection` that fails on **any**
per-agent call) currently asserts a local agent makes no server calls. Re-scope it to the
**`--private`** path. Add a complementary test that a **default (registered)** local agent
*does* make the expected registration calls (`AgentRegisteredAsync`, `AgentRunStarted`, dims),
and that its spawn env sets `KCAP_AGENT_ID` + `KCAP_RENDERED_AGENT` + `KCAP_DAEMON_URL` while
still carrying `ANTHROPIC_API_KEY`.

## Server behavior — verified, no server change required for Slice 1

Concern was that `AgentRegistered` is, for hosted agents, the daemon **confirming** a record the
**server already created** (UI mints the id → server pushes `LaunchAgent` → daemon registers),
whereas a local agent registers an id the server has **never seen**. Traced through
`../kcap-server/src/Capacitor.Server`:

- **Owner attribution works for a daemon-originated id.** `CapacitorHub.AgentRegistered`
  (`CapacitorHub.cs:837-918`) sets `effectiveOwner = daemonRegistry.GetOwnerByConnectionId(...)`
  — the daemon's **authenticated** SignalR owner (set at `DaemonConnect`) — and stamps it into
  the **in-memory** `agentRegistry.Register(...)` entry (`:885`). `pending` is null for a
  daemon-originated id, so the SQLite `WriteVisibility` at `:906` is skipped, leaving
  in-memory `visibility = null`.
- **The live web-UI agent list is in-memory, not the SQLite read model.**
  `AgentStoreDataService.GetAgentInstancesAsync(currentUserId, …)` iterates
  `agentRegistry.GetAll()` (`AgentStoreDataService.cs:47-51`) and filters with
  `VisibilityService.IsVisible(owner, mode, defaultVisibility: "private", …)` (`:66-76`) — a
  **null visibility defaults to owner-only ("private")**. So the agent shows to its owner and
  **not** to anyone else: exactly owner-immediate, owner-only.
- **First-web-subscribe replay** is served from the server's own in-memory buffer
  (`SubscribeToTerminalAsync`, `AgentStoreDataService.cs:101-125`), confirming item 4 needs no
  daemon code given eager streaming.

**Conclusion:** Slice 1's continue-from-web + owner-only goal works with **no server change** —
the in-memory registry is owner-scoped via the daemon's authenticated connection and defaults
to private. Still smoke-test it manually (below) to confirm end-to-end.

**Deferred (not Slice 1):** the persistence gap — `agent_runs.owner_user_id`/`visibility`
stay NULL for a daemon-originated id because `WriteVisibility` (`CapacitorHub.cs:906`) is gated
on `pending`. This doesn't affect the live experience but does affect persistent agent-run
history and the **sharing** path (Slice 3 / AI-974), which is where a small server change —
persist owner + default-private on a `pending`-less `AgentRegistered` — should land. Tracked
as a note on AI-974, not built here.

## Lifecycle & edge cases

- **Agent exit / detach / daemon death** — unchanged from Phase 1; cleanup still runs the
  borrowed-cwd guard. End-session now fires (registered), so the server marks the run complete.
- **`--worktree` + registered** — owned worktree, prepared and cleaned up exactly as a hosted
  agent; the closest-to-hosted configuration.
- **Web user resizes** — handled by the existing agent-level `HandleResizeTerminal`
  (`OnResizeTerminal`); min-clamping it against local clients is Slice 2.
- **Bridge-mode local answering (validation risk)** — for hosted agents there's no local
  terminal; here there is. Whether a *local* terminal keypress can resolve a permission while
  the bridge hook is blocking is uncertain (the prompt mirrors, but interactivity is unverified).
  **Manual-test item**, not a code change — answering via `SendInput`/"Send to Claude" is the
  guaranteed path either way.

## Testing

- **Unit:** Spawn-frame codec round-trips the new `private` flag (incl. a `--worktree` +
  `--private` combination). Shared `RegisterAgentAsync` helper invoked by both launch paths.
- **Privacy (strict mock), re-scoped:** `--private` agent → **no** per-agent server call across
  its full lifecycle (launch → run → heartbeat → exit/finalize → cleanup, incl. simulated
  reconnect); spawn env omits `KCAP_AGENT_ID` / `KCAP_RENDERED_AGENT` / `KCAP_DAEMON_URL`,
  keeps `KCAP_URL` + `ANTHROPIC_API_KEY`.
- **Registration (new):** default local agent → `AgentRegisteredAsync` + `AgentRunStarted` +
  initial dims fire; spawn env sets `KCAP_AGENT_ID` + `KCAP_RENDERED_AGENT` + `KCAP_DAEMON_URL`
  and keeps `ANTHROPIC_API_KEY`; a clamp change re-sends dims.
- **In-place safety (unchanged, still asserted):** a borrowed-cwd registered agent writes no
  files into the user's checkout/global config and never deletes the cwd or its branch on exit.
- **Integration:** trivial PTY program over the socket — registered path streams to a mock
  server sink and replays to a late local attacher; `--private` path streams to neither server.
- **Manual:** real `claude` — (1) registered local agent appears (by agent id) + is drivable in
  the owner's web UI and **not** visible to a different user (end-to-end confirmation of the
  verified owner-only finding); (2) a permission prompt is answerable from the web; (3)
  `--private` records as a plain local session, prompts natively.
- Per `CLAUDE.md`: `dotnet publish -c Release` and grep for IL3050/IL2026 after changes (the
  Spawn codec edit is hand-rolled binary — AOT-safe — but verify).

## Docs (same PR, per `CLAUDE.md`)

`README.md` must change in the same PR:
- **Getting started** — note that `kcap run-agent` now makes the agent **visible/drivable in
  your web UI** by default (continue-from-anywhere), owner-only until shared.
- **CLI commands → `run-agent`** — document the new default (registers) and the `--private`
  opt-out (purely local, not visible on the web).
- Update `src/Capacitor.Cli.Core/Resources/help-*.txt` too (not sufficient on its own).
