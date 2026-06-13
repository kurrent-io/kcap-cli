# Local terminal attach for hosted agents (co-driven sessions)

## Problem

The hosted-agents story today is entirely **server-orchestrated and headless**. The
server pushes a launch command to the daemon; the daemon spawns the agent CLI via
`forkpty` into an isolated git worktree (`UnixPtyProcess.Spawn`,
`src/Capacitor.Cli.Daemon/Pty/Unix/UnixPtyProcess.cs:47`) and mirrors the PTY to a
single client — the web UI "Terminal" tab — over SignalR
(`AgentOrchestrator.ReadAgentOutputAsync` → `ServerConnection.SendTerminalOutputAsync`,
`src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs:431`). Input flows the other
way as composed messages and discrete special keys (`HandleSendMessage` `:682`,
`HandleSendSpecialKey` `:687`, `HandleResizeTerminal` `:784`).

That web terminal is the *only* way a human touches a hosted agent, and its UX is the
source of the issues currently being chased. Two capabilities are missing:

1. **Pair programming** — let a human at their own terminal drive the agent directly,
   while teammates can also see and interact via the web UI.
2. **Start local, continue remotely** — start an agent from the terminal, walk away,
   and keep driving it from the web (Claude-remote-control style).

A naive "just run `claude` in a pipe and forward stdin" approach fails the user's core
worry — with an anonymous-pipe redirect the user's keyboard is disconnected. But the
daemon already uses a **PTY**, and a PTY master is not single-owner: multiple input
sources can write to it and its output can fan out to multiple consumers (this is how
`tmux`/`screen`/`tmate` work). The capability is therefore feasible on top of the
existing PTY plumbing — what's missing is a local client and a local launch trigger.

## Scope

Generalize the daemon from **one client** to **N clients across two transports**, and
add a local launch path:

```
kcap run-agent <vendor> [kcap flags] -- [agent args passed verbatim]
kcap attach <agent>
kcap ls
```

- The **agent always lives in the persistent daemon** (never the terminal). That is
  what makes "close the terminal, keep going, drive from the web" work — the terminal
  is an *attachable/detachable client*, never the owner.
- The **local terminal** attaches over a **new local IPC socket** (low latency,
  detach/reattach). The local socket is for *latency* — keystrokes don't round-trip
  through the cloud — **not** an offline-operation claim; the daemon still requires
  `ServerUrl` + SignalR as today (see *Server dependency* in the decisions table).
- **Teammates / remote control** attach over the **existing SignalR** channel.
- Both are interchangeable *clients* of one PTY: output is fanned to all of them,
  input is **merged with no arbitration (free-for-all)**, and a newly-attached client
  gets the existing per-agent `OutputBuffer` replayed so its screen is populated.

```
                         ┌─────────────── daemon (persistent) ───────────────┐
  local terminal ──UDS/pipe──▶ local socket listener ─┐                       │
  (kcap run-agent)  ◀──────────                        ├─▶ AgentOrchestrator   │
                         │                              │     owns IPtyProcess  │
  web client ───SignalR──▶ ServerConnection ───────────┘     N-client fan-out  │
  (teammate)    ◀────────                                    free-for-all input │
                         │                                    OutputBuffer replay│
                         └────────────────────────────────────────────────────┘
```

### Confirmed design decisions

| Decision | Choice |
|---|---|
| Process owner | The **daemon** owns the PTY/agent always; terminals are clients. |
| Work location | **Configurable per launch.** Default **in-place** (agent works in your cwd); `--worktree` opts into today's isolated-worktree behavior. |
| Arg passing | **Thin-wrapper passthrough** with a `--` boundary. kcap flags before `--`; everything after is handed to the agent CLI verbatim. |
| Concurrent input | **Free-for-all.** All clients' input feeds the one PTY master; humans coordinate socially (like pairing in tmux). No driver lock. |
| Transport | **Hybrid.** Local terminal over a new local socket; teammates + continue-remotely over existing SignalR. |
| Server dependency | **Phase 1 adds no new server *contract*, but is not offline.** The daemon still requires `ServerUrl` + a SignalR connection at startup exactly as today; local attach rides alongside it. True offline-daemon operation is a possible later enhancement, not in scope. |
| Remote control (Phase 2) | **Write by default once shared**, mirroring current hosted-agent run permissions. Observe-only is served by the existing read-only session-sharing path, not by attach. |
| Resize | **Min-clamp the PTY across attached *local* clients** (tmux semantics) in Phase 1; clamping web clients too needs a Phase 2 server-side resize contract (SignalR has only one agent-level resize today). |
| Terminal stream | **No silent partial-stream corruption.** Never `DropOldest`; an overflowing sink is force-detached and re-syncs via a clean replay on rejoin (bounded by the 2 MB `OutputBuffer`) rather than rendering a corrupted partial stream (see plumbing). |
| Vendor passthrough | **Launcher-agnostic.** Part of the `IHostedAgentLauncher` contract; both `run-agent claude` and `run-agent codex` work in v1. |

### Out of scope (explicitly deferred / rejected)

- **Driver-lock / turn-taking / request-control.** Free-for-all chosen for v1.
- **Offline-daemon mode (no `ServerUrl`).** The daemon still requires the server at
  startup exactly as today; running fully cloud-less is a possible later enhancement, not
  this design.
- **Cross-machine local attach.** The agent runs on *your* daemon; the only "remote"
  control is the web/SignalR path. There is no local socket across machines.
- **A server-side screen model for pixel-perfect replay.** Raw `OutputBuffer` replay
  plus a resize-triggered repaint is sufficient.
- **Windows polish beyond "named pipe compiles and basically works"** if users are
  Unix-first (revisit if Windows becomes a target).

## Command surface

```
kcap run-agent <vendor> [kcap flags] -- [agent args passed verbatim]
    # ensures the daemon is running, asks it to spawn the agent, then attaches
    # your terminal. Blocks until you detach or the agent exits.
    #
    # kcap flags (before --):  --worktree (default: in-place),
    #                          --name <id>      select which daemon (existing convention),
    #                          --detached       spawn without attaching this terminal
    #                                           (start in background, attach later)
    # everything after --:     handed to the `claude`/`codex` CLI as-is

kcap ls
    # lists daemon-hosted agents (local + server-initiated): id, status, attached clients

kcap attach <agent>
    # re-attaches your terminal to a running agent (after detach, or to join one
    # you started earlier on this machine)

# detach without killing: a configurable tmux-style prefix sequence (e.g. Ctrl-q d).
# The local client intercepts it before it reaches the agent.
```

Example:

```
kcap run-agent claude --worktree -- --model opus --resume "fix the bug"
       └─ kcap ─┘  └────── verbatim to the `claude` CLI ──────┘
```

`run-agent` joins the top-level command switch in `src/Capacitor.Cli/Program.cs`. It
ensures a daemon is running for the resolved name (auto-starting one if needed) and
connects to it over the local socket. The daemon still requires `ServerUrl` + SignalR to
start (`DaemonConfig.Validate` `DaemonConfig.cs:58`, gated at `DaemonRunner.cs:122`;
`ConnectAsync` at `DaemonRunner.cs:244`), so `run-agent` is **not** an offline command and
is **not** added to the `offlineCommands` list. `--worktree` is consumed by kcap (never
forwarded). Args after `--` are forwarded verbatim via a **launcher-agnostic passthrough contract**
on `IHostedAgentLauncher` (see *Vendor passthrough* under Plumbing), not by re-deriving
each flag — so `run-agent codex -- …` works too, not just Claude.

## Plumbing

### a) Local IPC channel (the one genuinely new subsystem)

Today there is **no local IPC** between the CLI and the daemon — `kcap daemon` commands
talk to a running daemon only through PID/lock files and signals (`DaemonLockPaths`,
`src/Capacitor.Cli/Commands/DaemonCommands.cs`). This adds a length-prefixed binary
framing over:

- **Unix domain socket** on Unix — path under `DaemonLockPaths.Directory`, e.g.
  `<name>.sock`, mirroring the per-daemon-name file convention.
- **Named pipe** on Windows.

Hand-rolled frames (no reflection-based serializer) keep it **AOT-friendly**. Frame
types:

| Direction | Frame | Payload |
|---|---|---|
| client→daemon | `Spawn` | vendor, work-location kind (owned-worktree \| borrowed-cwd), cwd, verbatim agent args, initial cols/rows |
| client→daemon | `Attach` | agentId |
| client→daemon | `Stdin` | raw bytes |
| client→daemon | `Resize` | cols, rows |
| client→daemon | `Detach` | — (graceful; agent keeps running) |
| daemon→client | `Attached` | agentId, current `OutputBuffer` snapshot (replay) |
| daemon→client | `Stdout` | raw PTY bytes |
| daemon→client | `Exited` | exit code |
| daemon→client | `Error` | message (spawn failed, unknown agent, etc.) |

The socket file is created with **owner-only permissions (0600)**: anything that can
open it can spawn processes and stream a terminal, so it sits at the same trust boundary
as the daemon PID/lock files and is locked to the OS user. The socket file is cleaned up
alongside the PID/lock files in the existing daemon-file lifecycle.

### b) Daemon changes

1. **Socket listener** — a new hosted service that accepts local connections, parses
   frames, and routes them to `AgentOrchestrator`. This is the only net-new subsystem.
2. **N-client output fan-out (per sink, no partial-stream corruption)** — `ReadAgentOutputAsync`
   (`AgentOrchestrator.cs:431`) currently calls `SendTerminalOutputAsync` for the single
   web sink. Refactor to push each PTY chunk to a **list of registered sinks**: the
   SignalR sink (existing `TerminalOutputSender`) plus zero or more local-socket sinks.
   Each sink keeps its **own bounded, ordered, *lossless* queue — never `DropOldest`**.
   `TerminalOutputSender` (`TerminalOutputSender.cs:17-38`) documents why: `DropOldest`
   silently discarded chunks under back-pressure and desynced Claude's cursor-addressing
   redraw stream (AI-844), so it now uses `BoundedChannelFullMode.Wait`. The per-client
   design must hold two things at once:
   - **No silent partial-stream corruption** — a sink must never render a stream with a
     hole in it; one dropped or reordered chunk desyncs every later repaint.
   - **No cross-client coupling** — one slow/stalled sink must not stall the shared PTY
     read loop (and thus the agent and every *other* client), which is exactly what
     naive `Wait` back-pressure on a shared producer would cause.
   Resolution: the fan-out enqueue is **non-blocking per sink**; when a single sink's
   bounded queue overflows, that **one client is force-detached and marked dirty**, and on
   reattach it gets a fresh `OutputBuffer` replay + repaint — re-syncing from a clean frame
   (bounded by the 2 MB `OutputBuffer`: a long stall may lose scrollback, but the live view
   is never corrupted) rather than consuming a partial stream. Other sinks and the PTY
   loop are unaffected. (Implementation-plan detail: whether the existing web/SignalR sink
   keeps its current agent-back-pressure-on-tunnel-stall behaviour or also adopts
   force-detach is settled in the Phase 1 plan, biased toward **not** coupling local
   terminal responsiveness to a remote tunnel stall.)
3. **Input merge + resize arbitration** — local `Stdin` frames call the same
   `agent.Process.WriteAsync` the web handler uses (`:682`); free-for-all = no
   arbitration, all writers hit the one master fd. Raw local Ctrl-C flows through as byte
   `0x03`; the PTY line discipline turns it into SIGINT for the agent (no special
   handling, unlike the web `SendInterrupt` path). **Resize is the exception to
   free-for-all.** In **Phase 1** the daemon min-clamps the PTY to the smallest cols ×
   rows across the **local socket clients** (each reports its dimensions via `Resize`,
   re-clamped on attach/detach/resize) — tmux semantics, so two local terminals of
   different sizes don't corrupt each other. The existing web `ResizeTerminal` (`:786`)
   stays agent-level as today. **Phase 2 caveat:** SignalR carries only one agent-level
   `ResizeTerminalCommand` (`ServerConnection.cs:29,102`) with no per-web-client
   attach/dimensions, so clamping local *and* web together needs server-side aggregate
   dimensions or a new per-client resize contract — tracked under Phase 2, not assumed here.
4. **Local-launch path: passthrough, work-location, server-privacy** — a `Spawn` frame
   runs the orchestrator launch (vendor `Prepare()`, `forkpty`) with three deviations from
   the server path:
   - **Passthrough args** — `LaunchArgs` from the verbatim post-`--` args (see *Vendor
     passthrough*), not structured fields.
   - **Work-location kind** — `--worktree` → an **owned worktree** the daemon created (safe
     to remove on cleanup, as today); default in-place → a **borrowed cwd the user owns**.
     For a borrowed cwd, vendor `Prepare()` MUST skip its repo-mutating steps — it must not
     write into the user's checkout or global vendor config. Today `Prepare()` overlays
     settings, writes `.mcp.json`, edits `.claude/settings.local.json` + `~/.claude.json`
     trust (`ClaudeLauncher.cs:37,43,325`), and for Codex overlays `.codex`, enforces
     hooks, and trusts the cwd in `~/.codex/config.toml` (`CodexLauncher.cs:18-54`) — all
     to make a *fresh worktree* mirror the source repo. The user's own checkout is already
     a trusted, configured repo, so the only allowed pre-launch effects for a borrowed cwd
     are read-only/idempotent ones (e.g. Codex's hooks preflight *check*, which fails fast
     without writing). A test asserts an in-place launch creates/modifies no repo or
     global-config files.
   - **Private-local server state** — a locally-launched agent starts **`PrivateLocal`**:
     the orchestrator suppresses *all* server interaction — no `AgentRegisteredAsync`
     (`AgentOrchestrator.cs:327`), no run-started/status events (`:329`), no
     `SendTerminalOutputAsync` (`:455`), no `AgentUnregisteredAsync`. The SignalR sink is
     simply **not attached** to this agent's fan-out; the agent exists only on the local
     socket until an explicit share (Phase 2) transitions it.
   `CleanupAgentAsync` (`AgentOrchestrator.cs:888`) today unconditionally calls
   `WorktreeManager.RemoveAsync(agent.Worktree)` (`:900`) — `Directory.Delete(path,
   recursive)` for standalone or `git worktree remove --force` + `git branch -D` otherwise
   (`WorktreeManager.cs:54-67`). **For a borrowed cwd that is catastrophic** — it would
   delete the user's working directory and branch on exit. Cleanup MUST skip all
   worktree/branch removal for borrowed-cwd agents. This is the top safety invariant, with
   an explicit test (below).
5. **Conditional env handling** — `UnixPtyProcess.Spawn` currently scrubs
   `ANTHROPIC_API_KEY` and unsets `CLAUDECODE`/`CLAUDE_CODE_ENTRYPOINT`
   (`UnixPtyProcess.cs:58`). That is correct for headless hosted launches but wrong for
   an interactive agent the user started themselves — it should keep their normal local
   auth. Env scrubbing becomes **conditional on launch type** (headless-hosted vs
   local-interactive). This interacts with the provider-API-key-scrub policy
   (`ProviderApiKeyPolicy` / `KCAP_USE_PROVIDER_API_KEY`).
6. **Share = `PrivateLocal`→`Shared` transition (Phase 2 only)** — an explicit `kcap
   share` flips the agent from `PrivateLocal` to `Shared`: it attaches the SignalR sink,
   runs the deferred `AgentRegisteredAsync` + run-started event, and begins streaming, so
   teammates see the agent in the web UI as if it were server-initiated. Today launches
   flow server→daemon; this adds a daemon→server "I started agent X" registration. **This
   is the one place the server contract changes** — which, with all web involvement, is why
   it is deferred to Phase 2.

### c) Local terminal client (`kcap run-agent` / `kcap attach`)

A thin foreground process — the dumb-pipe end of the socket:

1. **Raw mode** — `tcgetattr`/`tcsetattr` to put the real terminal into raw mode, and
   **restore on every exit path** (detach, agent exit, signal, crash) via a guaranteed
   cleanup. This is **new CLI-side native interop** — the daemon has PTY interop, but the
   *client* tty handling is separate. The detached-stdio-hang history (commit
   `a76fdfdd6`) is the cautionary precedent.
2. **Two pumps** — stdin → `Stdin` frames; `Stdout` frames → stdout.
3. **Detach interception** — the client scans its stdin stream for the configurable
   detach prefix **before** forwarding, so the sequence detaches instead of reaching the
   agent.
4. **Resize** — a `SIGWINCH` handler reads the new size and sends a `Resize` frame.
5. **Replay + repaint on attach** — render the `Attached` buffer snapshot, then send one
   `Resize` to nudge the TUI's alternate-screen buffer to repaint cleanly (raw scrollback
   replay alone can leave cursor artifacts; a resize-triggered repaint is the cheap fix).

### d) Vendor passthrough (launcher-agnostic)

`<vendor>` is generic, so passthrough is part of the `IHostedAgentLauncher` contract — not
Claude-specific. Today `ClaudeLauncher.BuildArgs` (`ClaudeLauncher.cs:57`) and
`CodexLauncher.BuildArgs` (`CodexLauncher.cs:56`) each *construct* argv from structured
server fields. Add a passthrough mode each launcher implements: emit only the **mandatory
daemon-level flags** the launcher must always set, then append the user's verbatim
post-`--` args.

- **Claude** — nothing mandatory (cwd is set via `forkpty` chdir); append verbatim.
- **Codex** — must still inject `--cd <cwd>` and `--no-alt-screen` (the terminal mirror and
  buffer replay depend on the primary screen) and run its hooks preflight in `Prepare()`;
  its sandbox/approval defaults are emitted unless the user overrides them in the verbatim
  args.

Conflict policy: a user-supplied flag that collides with a non-mandatory daemon default
wins; the mandatory flags (`--cd`, `--no-alt-screen`) are always enforced.

## Lifecycle & edge cases

- **Daemon ensure.** `run-agent` reuses the existing start path (`DaemonCommands`) to
  auto-start the daemon if no live one is found for the resolved name, then connects.
- **Agent exit while attached.** Daemon sends `Exited`; client restores the terminal,
  prints and returns the exit code (so `kcap run-agent … && …` chains sensibly).
- **Detach while running.** Agent keeps running in the daemon; `kcap ls` shows it;
  `kcap attach` rejoins. This is the "continue later" path even without the web side.
- **Multiple local attachers.** Two local terminals on one agent are just two socket
  sinks — falls out of the N-client fan-out, no extra work. Free-for-all applies.
- **Daemon dies / socket drops.** Client restores the terminal and reports the
  disconnect. The agent dies with the daemon (it is a daemon child) — consistent with
  hosted agents today. No auto-resurrection.
- **Stale socket file.** Cleaned up alongside the PID/lock files in the existing daemon
  file lifecycle.

## Security, visibility & control

- **Local socket = owner-only (0600).** Same trust level as the daemon PID files,
  locked to the OS user.
- **Private until shared (Phase 2).** A locally-started agent is reachable only over the
  owner's local socket by default. It becomes visible to teammates only via an
  **explicit** share/announce action (`kcap share <agent>` or `run-agent --share`) — it is
  never auto-exposed by merely launching it.
- **Visibility = account-scoped, never public.** Once shared, the agent follows the
  existing account/tenant visibility model (per commit `b77e9f97c`). No new visibility
  concept.
- **Remote control defaults to write — by design.** Once shared, a teammate attaching via
  the web UI gets the same **write/input** control today's hosted agents grant. This is
  deliberate: a *view-only* attach would be a strictly worse version of the existing
  read-only session-sharing feature, so **observe-only is served by sharing, and attach is
  for control**. We therefore do **not** gate input behind a separate control-grant; the
  permission model mirrors hosted-agent runs exactly.
- **Accepted trade-off (noted for reviewers).** This intentionally widens the *blast
  radius* versus today's hosted agents, though not the *permission model*: a hosted agent
  runs in an **isolated worktree under daemon-managed auth**, whereas a default in-place
  local agent runs in the **user's real checkout with the user's personal credentials**.
  The accepted mitigations are (a) private-until-explicitly-shared, (b) account-scoped
  (never public) visibility, and (c) `--worktree` for users who want isolation. A tighter
  per-session boundary is out of scope unless later required.

## Phasing

- **Phase 1 — "tmux for your agent" (no web involvement, no new server contract).** Local
  socket + framing, daemon N-client fan-out refactor (per-sink, no partial-stream
  corruption), resize
  min-clamp, `run-agent`/`attach`/`ls`, raw-mode client, detach/reattach, persistence, the
  **owned-vs-borrowed cleanup guard**, conditional env handling. Delivers: start local →
  detach → reattach → survives terminal close. The daemon still connects to the server as
  it does today — Phase 1 is *not* offline; it simply adds no new server endpoints and the
  local-attach feature involves no web client. De-risks all the new infra before touching
  the server.
- **Phase 2 — pairing & continue-from-web.** An **explicit** daemon→server announce
  (`kcap share <agent>` / `run-agent --share`; private to the launching user until then)
  + web clients attaching/injecting via the existing SignalR fan-out (now just another
  sink), with **write control by default once shared** (see *Security, visibility &
  control*). Delivers both headline goals: pair programming and continue-from-anywhere.

## Testing

- **Unit:** frame codec round-trips; N-client fan-out (a slow/dead sink is force-detached
  and re-syncs via clean replay on rejoin — **never renders a corrupted partial stream and
  never stalls the shared PTY loop or other sinks**; replay bounded by the 2 MB
  `OutputBuffer`); resize min-clamp across two *local* clients of different dimensions;
  detach-sequence scanner (including a prefix split across reads); launcher-agnostic
  passthrough assembly for **both** Claude and Codex (incl. Codex's mandatory `--cd` /
  `--no-alt-screen` injection) + `--worktree` consumption.
- **In-place safety (the critical one):** for a borrowed-cwd agent, (a) `Prepare()` writes
  **no** files into the user's checkout or global vendor config (`.mcp.json`,
  `settings.local.json`, `~/.claude.json`, `~/.codex/config.toml` untouched), and (b) exit
  + daemon `DisposeAsync` leave the cwd **and** its git branch fully intact (no
  `Directory.Delete` / `git worktree remove` / `git branch -D`). Conversely an owned-
  worktree agent is still prepared and cleaned up exactly as today.
- **Privacy:** a `PrivateLocal` agent makes **zero** server calls (no `AgentRegistered`,
  status, run events, terminal output, or unregister) across its whole lifecycle until an
  explicit share; only after share do registration + streaming begin.
- **Integration:** spawn a trivial PTY program (e.g. a tiny echo / `cat`) over the
  socket; assert stdin→stdout round-trip, resize, detach-leaves-running, and
  reattach-replays-buffer. TUnit + existing PTY test patterns.
- **Manual:** a real `claude` raw-mode session — repaint-on-attach, and the terminal
  restored on every exit path.

## Docs

Per `CLAUDE.md`, any change to the user-facing CLI surface updates `README.md` in the
same PR — both the quick-start (`## Getting started`) and a new per-command section for
`run-agent` / `attach` / `ls` under `## CLI commands`. Updating only the `help-*.txt`
resources is not sufficient.
