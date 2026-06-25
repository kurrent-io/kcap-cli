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
| Streaming | **Eager, but local-first (non-blocking).** With `IsPrivate = false`, the read loop streams every PTY chunk to the server from the first byte. For a **local-spawned** agent (`IsLocalSpawned`) the server enqueue is **non-blocking** (`TrySendTerminalOutput`/`TryEnqueue`: drop+count on a full backlog) so a stalled tunnel never freezes the PTY loop or the live local terminal; **hosted** agents keep the lossless awaiting back-pressure (server is their only consumer). (Initial dimensions are sent once by the registration sequence in section a, not the read loop.) | Server accumulates its own buffer from byte one; the first web subscriber replays via the server's existing `SubscribeToTerminal`. The local terminal is the local agent's primary surface, so its responsiveness must not couple to a remote tunnel stall (web mirror re-syncs on reconnect). **No new server contract.** |
| First-web-subscribe replay (item 4) | **No daemon code** — satisfied by the server's existing replay because we stream eagerly from spawn. Covered by a verification test. | The reconnect-replay-skip (`AgentOrchestrator.cs:946-954`) is about daemon *rebind*, not first subscribe; eager streaming closes the gap the parent spec worried about. |
| Permissions | **Bridge mode, exactly like hosted:** set `KCAP_RENDERED_AGENT=1` **and** `KCAP_DAEMON_URL = _permissionBridge.BaseUrl`. The **web UI is the authoritative approval surface** for a registered agent; `--private` is the native-terminal-approval path. | User decision: match hosted's web permission dialog. With `KCAP_RENDERED_AGENT=1` the hook routes the decision to the bridge (`PermissionRequestCommand.cs:37,68`), **not** native terminal approval — so a local-terminal keypress resolving the prompt is **unverified** (see Permission UX below), not a guaranteed property. |
| Work location | Default **borrowed cwd** (in-place); `--worktree` opts into an owned worktree. **Unchanged from Phase 1.** | Local agents work in the user's checkout by default. |
| Cleanup safety | **Top invariant, unchanged:** never `Directory.Delete` / `git worktree remove` / `git branch -D` a borrowed cwd; `Prepare()` skips repo-mutating steps for a borrowed cwd. | Deleting the user's working dir on exit would be catastrophic. |
| Auth | Keep the user's `ANTHROPIC_API_KEY` (local-interactive auth), unlike hosted's daemon-managed auth. **Unchanged from Phase 1.** Orthogonal to the recorded-event provider-key scrub policy (`ProviderApiKeyPolicy` / `KCAP_USE_PROVIDER_API_KEY`). | A local agent uses the user's own login. |
| Opt-out | Keep **`--private`** (`kcap run-agent --private`): the Phase 1 unregistered path — `IsPrivate = true`, deny-all, no `KCAP_AGENT_ID` / `KCAP_RENDERED_AGENT` / `KCAP_DAEMON_URL`, native-terminal permissions, no cloud streaming. | Cheap (path already exists); preserves a purely-local mode and keeps the deny-all guarantee testable. |
| Dims to server | Store the agent's **current PTY dims on `AgentInstance`** and send them at registration, on local clamp change, **and on reconnect re-register**. | Hosted sends fixed `HostedPtyCols/Rows`; a local agent's size is client-driven and can change. The current reconnect path resends the `HostedPtyCols/Rows` constant (`AgentOrchestrator.cs:941`) — wrong for a local agent, and reconnects are a known recurring condition (tunnel/WebSocket drops), so reconnect must resend the stored per-agent dims (hosted's stored value = `HostedPtyCols/Rows`, so no behavior change for hosted). Full local+web aggregation is Slice 2. |

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
   Add **current-dims fields** to `AgentInstance` (e.g. `CurrentCols`/`CurrentRows`), initialized
   to the spawn `cols`/`rows` (hosted initializes them to `HostedPtyCols/Rows`). These are the
   single source of truth for every dims send (registration + reconnect), updated by **every**
   resize path — local clamp *and* web resize (see section b).
3. **Registration sequence** — after `_agents[agentId] = agent` and before/around starting the
   read loop, run the same calls `HandleLaunchAgent` makes at `AgentOrchestrator.cs:380-399`:
   - `await _server.AgentRegisteredAsync(agentId, prompt: null, model: "", effort: null, repoPath: cwd)`
   - `await _server.SendTerminalDimensionsAsync(agentId, agent.CurrentCols, agent.CurrentRows)`
     (best-effort; **the stored per-agent dims, not the `HostedPtyCols/Rows` constant**)
   - `_ = _server.AppendAgentRunEventAsync(agentId, new AgentRunStarted(null, "", null, cwd, worktree.Path, vendor))`
   - the repo-path persist/announce that follows at `AgentOrchestrator.cs:399+`.
   - **Extract a shared helper** (e.g. `RegisterAgentAsync(agent)`) used by both
     `HandleLaunchAgent` and `HandleLocalSpawnAsync`, reading `agent.CurrentCols/Rows`, so the two
     paths can't drift.

Status changes, heartbeat, end-session, and terminal output streaming already key off
`!IsPrivate` (`AgentOrchestrator.cs` gate sites :511/:524/:530/:588/:594/:620/:995/:1061), so
they activate automatically. **Reconnect re-register is the exception:** `ReRegisterAgentsAsync`
already filters `!IsPrivate` (`:928`) but resends the `HostedPtyCols/Rows` **constant** (`:941`)
— change it to resend `agent.CurrentCols/CurrentRows` so a registered local agent re-locks web
viewers to its real size after a reconnect (no behavior change for hosted, whose stored dims
equal the constant).

`prompt`/`model`/`effort` are empty for a local agent — the web UI shows it by its **agent id**
(the random string), which is the normal display for an agent with no task metadata. Fine for
Slice 1, confirmed.

### b) Daemon — keep `CurrentCols/Rows` current on **every** resize path

`CurrentCols/Rows` is the source of truth resends rely on, so **every** path that resizes the PTY
must update it — there are two in Slice 1:

1. **Local clamp** — `ClampPtyLocked` (`AgentOrchestrator.LocalIpc.cs:189-195`) resizes to the
   local min-clamp. When it changes, update `agent.CurrentCols/CurrentRows` and, for a
   **registered** agent, call `_server.SendTerminalDimensionsAsync(agentId, agent.CurrentCols,
   agent.CurrentRows)` (best-effort) so web viewers re-lock. No server send for `--private`.
2. **Web resize** — `HandleResizeTerminal` (`AgentOrchestrator.cs:900-906`) resizes the PTY
   directly from a web client's agent-level `ResizeTerminalCommand` and today touches **no**
   stored field. It must also update `agent.CurrentCols/CurrentRows`, otherwise a web resize
   followed by a daemon reconnect would resend the **stale** pre-resize dims. Re-sending dims to
   the server here is redundant (the web client initiated the resize, so the server already knows)
   — storing is the required part.

(Min-clamping web *against* local clients — so the two don't fight, last-writer-wins today — is
Slice 2.)

### c) Wire `--private` through the Spawn frame

The Spawn frame (`Core/LocalIpc/FrameCodec.cs:66-99`) currently encodes
`work(1) | cols(2) | rows(2) | vendor(lp) | cwd(lp) | argCount(4) | args…`.

**Wire-compat constraint (corrected):** the CLI and daemon ship in one binary, but the daemon is
**persistent** — `run-agent` reuses an already-running daemon (`RunAgentCommand.cs:127` only
checks it can connect to the socket; there's no version handshake). So an upgraded CLI can talk
to an **older running daemon** (and vice-versa). Inserting a byte mid-payload would make the
other side **misparse** every following field. Instead, **append the `private` flag as a
trailing byte after the args**, which both sides can evolve safely because `ParseSpawn`
(`:82-99`) returns after reading `argCount` args **without** requiring the payload be fully
consumed (it already tolerates trailing bytes):

- `Spawn(...)` encoder (`:66-74`): the new CLI **always** writes the trailing flag byte after the
  arg loop.
- `ParseSpawn` (`:82-99`): after the arg loop, read the trailing byte **if present**; **absent →
  default `private = true`** (an old CLI sent no byte → preserve its Phase-1 unregistered
  behavior — the conservative default).
- The `Spawn(LocalFrame)` tuple (`:77-78`) gains a `bool isPrivate` field; update the
  destructuring at `HandleLocalSpawnAsync:20`.
- **Graceful degradation, no corruption:** new CLI → old daemon = old daemon ignores the trailing
  byte and does Phase-1 (the agent isn't registered/web-visible — degraded but not broken); old
  CLI → new daemon = no byte → treated as `--private`. The registered default only lights up when
  both sides are new. A codec round-trip test (incl. the missing-byte default) covers it.
- *(Optional future hardening, out of scope: a version field in the local IPC so `run-agent` can
  warn when the running daemon is too old to honor registration, rather than silently degrading.)*

### d) CLI — `--private` flag

`RunAgentCommand` (`src/Capacitor.Cli/Commands/RunAgentCommand.cs`) parses kcap flags before
`--`. Add `--private` (consumed by kcap, never forwarded), pass it into the `Spawn` frame
builder. `attach` is unaffected (privacy is fixed at spawn). Help text (`help-*.txt`) + README.

### e) Privacy: close the `LiveAgentIds` leak, then re-scope the test

A `--private` agent must be invisible to the server, but today there's an **asymmetry**:
`ReRegisterAgentsAsync` correctly filters `!IsPrivate` (`AgentOrchestrator.cs:928`), yet
`GetLiveAgentIds` (`:187-191`) returns **all** Starting/Running ids with no filter, and that list
is shipped to the server in `DaemonConnect` (`ServerConnection.cs:312`). So a `--private` agent's
**id leaks** on every (re)connect. **Fix:** add `&& !kvp.Value.IsPrivate` to `GetLiveAgentIds`.
(Pre-existing since Phase 1, where all local agents are private — Slice 1 is where the privacy
contract is formalized, so it lands here.)

Privacy is a two-way boundary. As well as the outbound deny-all + the `LiveAgentIds` filter,
the **inbound** server→daemon control handlers (`HandleStopAgent`, `HandleSendInput`,
`HandleSendSpecialKey`, `HandleResizeTerminal`) must **ignore commands for a private agent**
(defence-in-depth: the server shouldn't know a private agent's id, but a leaked/guessed id must
not let it stop, drive, or resize one). Add an `IsPrivate` guard to each.

The Phase 1 strict-mock privacy test (a mock `ServerConnection` that fails on **any** per-agent
call) currently asserts a local agent makes no server calls. Re-scope it to the **`--private`**
path, and extend it to assert a `--private` agent's id **never appears in `DaemonConnect`'s
`LiveAgentIds`** (incl. across a simulated reconnect). Add a complementary test that a **default
(registered)** local agent *does* make the expected registration calls (`AgentRegisteredAsync`,
`AgentRunStarted`, dims), appears in `LiveAgentIds`, and that its spawn env sets `KCAP_AGENT_ID` +
`KCAP_RENDERED_AGENT` + `KCAP_DAEMON_URL` while still carrying `ANTHROPIC_API_KEY`.

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
- **Permission UX (bridge mode) — authoritative surface is the web permission dialog.** With
  `KCAP_RENDERED_AGENT=1`, the hook posts the request to the daemon bridge
  (`PermissionRequestCommand.cs:68`), which awaits `RequestPermissionAsync`
  (`LocalPermissionBridge.cs:212`); the **guaranteed** resolution is the web **permission dialog
  → `RespondToPermission`** (`../kcap-server CapacitorHub.cs:1484`), which flows back via
  `PermissionResolved` and unblocks the hook's HTTP response — exactly like hosted. **Unverified
  (validation item):** whether the prompt can *also* be resolved by ordinary PTY input — a
  *local terminal keypress*, or a web client's `SendUserInput`/"Send to Claude"
  (`CapacitorHub.cs:1430`, ordinary input, **not** the permission-response channel) — while the
  bridge hook is blocking. The user has anecdotally seen "Send to Claude" '1' unblock a hosted
  agent, but that is not the designed path, so treat it as unverified, not guaranteed. If
  ordinary-input resolution does **not** work, the documented behavior for a registered agent is
  "approve via the web permission dialog," and a local driver who wants native terminal approval
  uses **`--private`** (no bridge). A deliberate consequence of the bridge-mode choice, not a
  defect; does not block Slice 1.

## Testing

- **Unit:** Spawn-frame codec round-trips the new `private` flag (incl. a `--worktree` +
  `--private` combination). Shared `RegisterAgentAsync` helper invoked by both launch paths.
- **Privacy (strict mock), re-scoped:** `--private` agent → **no** per-agent server call across
  its full lifecycle (launch → run → heartbeat → exit/finalize → cleanup, incl. simulated
  reconnect); spawn env omits `KCAP_AGENT_ID` / `KCAP_RENDERED_AGENT` / `KCAP_DAEMON_URL`,
  keeps `KCAP_URL` + `ANTHROPIC_API_KEY`; and its id is **absent from `DaemonConnect`'s
  `LiveAgentIds`** (initial connect and a simulated reconnect).
- **Registration (new):** default local agent → `AgentRegisteredAsync` + `AgentRunStarted` +
  initial dims fire; appears in `LiveAgentIds`; spawn env sets `KCAP_AGENT_ID` +
  `KCAP_RENDERED_AGENT` + `KCAP_DAEMON_URL` and keeps `ANTHROPIC_API_KEY`.
- **Dims (new):** a clamp change updates `CurrentCols/Rows` and re-sends dims; a **web resize**
  (`HandleResizeTerminal`) updates `CurrentCols/Rows`; a simulated reconnect
  (`ReRegisterAgentsAsync`) resends the agent's **stored** dims, not `HostedPtyCols/Rows`. Assert
  both: (a) a registered local agent with non-default dims re-sends those exact values on
  reconnect, and (b) a **web-resize-then-reconnect** resends the post-web-resize dims, not stale ones.
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
