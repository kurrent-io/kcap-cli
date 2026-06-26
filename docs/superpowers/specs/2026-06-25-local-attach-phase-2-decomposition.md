# Local terminal attach — Phase 2 decomposition

**Issue:** AI-862 (Local terminal attach — Phase 2: continue-from-web & pairing)
**Parent design:** `docs/superpowers/specs/2026-06-13-local-attach-hosted-agent-design.md` (Phase 2 section)
**Status of Phase 1:** shipped (PR #155 / AI-860). Foundations below are live.

## Why this doc

Phase 2's eight scope items in AI-862 are **not one plan**. They span two repos and have
distinct dependencies, so building them as a single unit would couple a low-risk daemon
change to a cross-repo server contract and a large Windows port. This doc splits Phase 2
into separately-shippable slices, marks the repo boundary of each, draws the dependency
graph, and names the slice to build first. Each slice then gets its own brainstorm → spec
→ plan → implementation cycle.

**Repo legend:** 🟦 this repo (daemon/CLI) · 🟨 server + web-UI repo (separate) · ⬜ no code here (verify only)

## What Phase 1 already gives us (the seams Phase 2 builds on)

All file:line references are in this repo unless noted.

- **Single deny-all gate:** `AgentInstance.IsPrivate` (`Services/AgentOrchestrator.cs:66`).
  When `true`, every per-agent server call is skipped (registration, terminal output,
  status, run events, heartbeat, end-session, reconnect re-register). Local agents spawn
  with `IsPrivate = true` (`Services/AgentOrchestrator.LocalIpc.cs:65`).
- **N-client lossless fan-out:** `AgentInstance.LocalSinks` + `SinksLock`, append+enqueue
  atomic with attach-snapshot (`AgentOrchestrator.cs:51-52`, read loop `:519-522`). Each
  sink has its own bounded, never-`DropOldest` queue; an overflowing sink is force-detached
  and re-syncs on rejoin.
- **`OutputBuffer`** — 2 MB ring with `Snapshot()` for one-time replay (`AgentOrchestrator.cs:73-105`).
- **SignalR terminal sink (web path), already wired for hosted agents:**
  `ServerConnection.SendTerminalOutputAsync` (`Services/ServerConnection.cs:565`) via the
  bounded, in-order `TerminalOutputSender`; web input handlers `HandleSendMessage` /
  `HandleSendSpecialKey` / `HandleResizeTerminal`; reconnect deliberately **skips** buffer
  replay because the server keeps its own per-agent buffer across a daemon rebind
  (`ServerConnection.cs:946-957`).
- **Registration contract (reusable as-is):** `AgentRegisteredAsync` (`ServerConnection.cs:441`),
  run-started/ended events, `AgentStatusChangedAsync` (`:452`) — all already drive the web
  UI for hosted agents.
- **Spawn env + scrubbing:** `UnixPtyProcess.Spawn` unsets `CLAUDECODE`,
  `CLAUDE_CODE_ENTRYPOINT`, `ANTHROPIC_API_KEY`, `KCAP_AGENT_ID`, `KCAP_RENDERED_AGENT`,
  `KCAP_DAEMON_URL`, then applies `extraEnv` (`Pty/Unix/UnixPtyProcess.cs:58-73`). Hosted
  env set at `AgentOrchestrator.cs:348-367`; local env at `LocalIpc.cs:58-61` (sets
  `KCAP_URL`, keeps `ANTHROPIC_API_KEY`; omits `KCAP_AGENT_ID` / `KCAP_RENDERED_AGENT` /
  `KCAP_DAEMON_URL`). **Env is fixed at `execvp` — it can never change after spawn.**
- **Work-location safety:** `AgentInstance.Work` (BorrowedCwd | OwnedWorktree),
  `CleanupAgentAsync` skips worktree/branch removal for a borrowed cwd (`AgentOrchestrator.cs`
  cleanup path) — the top safety invariant.
- **Resize min-clamp (local only):** `ClientDims` dict + `ClampPtyLocked`
  (`LocalIpc.cs:189-195`), re-clamped on attach/detach/resize.
- **Local IPC:** UDS listener `Services/LocalControlServer.cs` (0600); frame codec
  `Core/LocalIpc/FrameCodec.cs`; frame types `Core/LocalIpc/FrameType.cs`; client raw-mode
  `Local/TerminalRawMode.cs`, `Local/LocalAgentClient.cs`; detach scanner
  `Core/LocalIpc/DetachScanner.cs`. **Unix-only today.**

## The slices

### Slice 1 — Continue-from-web core (register-on-spawn + first-web-subscribe replay) · 🟦 (this repo only — verified)

**Covers issue items 1, 2, 3, 4, 7.** Build this first. Streaming decision: **eager** (chosen),
permissions: **bridge mode** (chosen). Server behavior verified against `../kcap-server`: a
daemon-originated `AgentRegistered` is owner-scoped in-memory via the daemon's authenticated
connection and defaults to owner-only ("private"), and the live UI list is in-memory — so the
owner-immediate, owner-only continue-from-web works with **no server change**. See the Slice 1
spec for the trace. (Persistent owner attribution in SQLite is a separate gap → Slice 3.)

Flip a locally-launched agent from `PrivateLocal`/deny-all to **registered exactly like a
UI-launched agent**, so the owner sees and drives it from their own web UI immediately:

- `IsPrivate → false` for the local-registered path, so the existing per-agent server calls
  fire (`AgentRegisteredAsync` + run-started, status, end-session, heartbeat, reconnect
  re-register). This is the one server-contract *change* — and it is a **reuse**, not a new
  endpoint: registering identically to a hosted agent means the server's existing
  owner-only-until-shared model and the existing web→daemon input handlers
  (`HandleSendMessage` / `HandleSendSpecialKey` / `HandleResizeTerminal`) apply with no
  server change. **Item 2 (owner-immediate, owner-only) falls out for free.**
- Attach the SignalR terminal sink to the fan-out as just another `ITerminalSink` alongside
  the local-socket sinks (**item 3**).
- Set `KCAP_AGENT_ID` in the spawn env so the recorded session links to the hosted agent the
  normal way (no "tag-and-link a pre-registered agent").

**Two design decisions — both resolved** (detail in the Slice 1 spec):

1. **Streaming: eager** (chosen). Attach the SignalR sink at spawn like a hosted agent — falls
   out of the `!IsPrivate` gate; the server buffers from byte one and the first web subscribe
   replays via its existing mechanism, so **item 4 is a verification test**. No server contract.
2. **Permissions: bridge mode** (chosen). Set `KCAP_RENDERED_AGENT` + `KCAP_DAEMON_URL` exactly
   like hosted, so a registered local agent gets the same web permission dialog; the PTY prompt
   still mirrors and stays answerable by keystroke from local or web.

**Touch points:** `AgentOrchestrator.LocalIpc.cs` (spawn path: `IsPrivate`, hosted env, the
shared `RegisterAgentAsync` call) and the `IsPrivate` gate sites in `AgentOrchestrator.cs`
(streaming activates automatically); the `--private` opt-out adds a Spawn-frame flag.

**Strict-privacy regression risk:** the Phase 1 privacy test asserts a strict mock
`ServerConnection` is **never** called for a private agent. A registered local agent is no
longer private, so that test must be re-scoped (the deny-all guarantee still applies to any
*remaining* unregistered path, e.g. a `--private`/offline variant if one is kept).

*Depends on: nothing. Every other slice depends on this.*

### Slice 2 — Web + local resize aggregation · 🟦 + **new 🟨 contract**

**Covers issue item 6.** SignalR carries only one agent-level `ResizeTerminalCommand` with
no per-web-client dimensions, so Phase 1 min-clamps **local** clients only (`ClampPtyLocked`,
`LocalIpc.cs:189`). To min-clamp local **and** web together (tmux semantics, so a small web
viewer doesn't corrupt a large local terminal or vice-versa) needs server-side aggregate
dimensions or a per-web-client resize contract. This is the most server-coupled slice and
the only one that needs a genuinely new 🟨 contract.

*Depends on: Slice 1. Independent of Slices 3 and 4.*

### Slice 3 — Sharing / pairing · ⬜ this repo (verify only) · **small 🟨 server change** + 🟨 web-UI

**Covers issue item 5.** Once Slice 1 registers the agent owner-only, sharing to teammates is
the **existing web-UI "share"** on the existing hosted-agent mechanism (write control,
mirroring hosted-agent permissions) — **no kcap-specific share logic**. But this slice carries a
**small server-repo change**: a daemon-originated `AgentRegistered` never persists
owner/visibility to SQLite (`CapacitorHub.cs:906` `WriteVisibility` is gated on a UI-launch
`pending`), so persistent ownership/grants must be written on a `pending`-less registration
before sharing can attach grants and before agent-run *history* is owner-attributed. (Slice 1's
*live* owner-only view does not need this; see the Slice 1 spec's verified server trace.) The
kcap-cli work is to *verify* the share path on a registered local agent (notably: a shared
in-place agent gives teammates write control over a process in the owner's checkout under the
owner's creds — a conscious, owner-initiated trade-off, with `--worktree` available for
isolation; see the parent spec's Security section). The `kcap share` CLI convenience is already
tracked separately as **AI-861**.

*Depends on: Slice 1.*

### Slice 4 — Windows support · 🟦 large, self-contained

**Covers issue item 8.** Named pipe in place of the Unix domain socket
(`LocalControlServer.cs` / `LocalSocketPaths`), plus a Windows raw-mode console + PTY
equivalent for the client (`Local/TerminalRawMode.cs` is Unix `tcgetattr`/`tcsetattr`/libc
`read`/`write`) and the daemon PTY (`UnixPtyProcess`). This is a transport/tty port that is
**orthogonal to the continue-from-web semantics** — it touches the Phase 1 infra rather than
the server contract, so it can proceed in parallel with the slices above. Largest single
chunk; lowest server coupling.

*Depends on: nothing (parallel to Slices 1–3).*

## Dependency graph

```
Slice 1 (core: register + replay)
   ├── Slice 2 (resize aggregation — needs new server contract)
   └── Slice 3 (sharing — verify existing web-UI path)
Slice 4 (Windows — independent, anytime)
```

## Recommended build order

1. **Slice 1 — continue-from-web core.** The keystone: nothing else is meaningful without a
   registered agent, and it is almost entirely *reuse* of the hosted-agent contract — high
   value, low risk, **this repo only (verified — no server change)**. Decisions settled: eager
   streaming + bridge-mode permissions; see the Slice 1 spec (AI-972).
2. **Slice 3 — sharing.** Near-zero work in this repo (verify the existing web-UI share on a
   registered local agent); completes the "pairing" half of the issue title cheaply.
3. **Slice 2 — resize aggregation.** Deferred because it needs a new server contract;
   schedule with the server/web-UI team.
4. **Slice 4 — Windows.** Anytime; parallelizable, but the largest chunk and lowest priority
   if users are Unix-first (per the parent spec's "Windows polish" out-of-scope note).

## Cross-references

- Parent design (full Phase 2 narrative): `docs/superpowers/specs/2026-06-13-local-attach-hosted-agent-design.md`
- AI-860 — Phase 1 (shipped, PR #155)
- AI-861 — `kcap share` CLI convenience (the CLI half of Slice 3's sharing)
