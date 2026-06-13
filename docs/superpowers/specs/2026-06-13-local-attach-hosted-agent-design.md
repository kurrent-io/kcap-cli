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
  offline-capable, detach/reattach).
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

### Out of scope (explicitly deferred / rejected)

- **Driver-lock / turn-taking / request-control.** Free-for-all chosen for v1.
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

`run-agent` joins the top-level command switch in `src/Capacitor.Cli/Program.cs` and
the `offlineCommands` list (`Program.cs:70`) — it ensures a *local* daemon and does not
itself require the cloud. `--worktree` is consumed by kcap (never forwarded). Args after
`--` flow into a **new passthrough branch** of `ClaudeLauncher.BuildArgs`
(`src/Capacitor.Cli.Daemon/Services/ClaudeLauncher.cs:57`), distinct from the existing
structured server-request branch in the same method.

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
| client→daemon | `Spawn` | vendor, worktree-or-in-place, cwd, verbatim agent args, initial cols/rows |
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
2. **N-client output fan-out** — `ReadAgentOutputAsync` (`AgentOrchestrator.cs:431`)
   currently calls `SendTerminalOutputAsync` for the single web sink. Refactor to push
   each PTY chunk to a **list of registered sinks**: the SignalR sink (existing
   `TerminalOutputSender`) plus zero or more local-socket sinks. **Each sink keeps its
   own bounded, drop-oldest, ordered queue** — the `TerminalOutputSender` pattern
   (`src/Capacitor.Cli.Daemon/Services/TerminalOutputSender.cs`) generalized per-client,
   so a stalled local socket cannot wedge the web stream and vice-versa.
3. **Input merge** — local `Stdin`/`Resize` frames call the same
   `agent.Process.WriteAsync` / `Resize` the web handlers already use (`:682`, `:786`).
   Free-for-all = no arbitration; all writers hit the one master fd. Raw local Ctrl-C
   flows through as byte `0x03`; the PTY line discipline turns it into SIGINT for the
   agent (no special handling, unlike the web `SendInterrupt` path).
4. **Local-launch path** — a `Spawn` frame runs the same orchestrator launch the server
   uses (worktree prep *or* in-place, vendor `Prepare()`, `forkpty`), differing in one
   branch: a **passthrough** `LaunchArgs` built from the verbatim args rather than
   structured fields.
5. **Conditional env handling** — `UnixPtyProcess.Spawn` currently scrubs
   `ANTHROPIC_API_KEY` and unsets `CLAUDECODE`/`CLAUDE_CODE_ENTRYPOINT`
   (`UnixPtyProcess.cs:58`). That is correct for headless hosted launches but wrong for
   an interactive agent the user started themselves — it should keep their normal local
   auth. Env scrubbing becomes **conditional on launch type** (headless-hosted vs
   local-interactive). This interacts with the provider-API-key-scrub policy
   (`ProviderApiKeyPolicy` / `KCAP_USE_PROVIDER_API_KEY`).
6. **Server-announce (Phase 2 only)** — so teammates see a locally-started agent in the
   web UI, the daemon registers it with the server as if it were server-initiated.
   Today launches flow server→daemon; this adds a daemon→server "I started agent X"
   registration. **This is the one place the server contract changes**, which is why it
   is deferred to Phase 2.

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

## Security & visibility

- **Local socket = owner-only (0600).** Same trust level as the daemon PID files,
  locked to the OS user.
- **Visibility (Phase 2).** A locally-started agent announced to the server follows the
  existing **account-scoped** visibility model (per commit `b77e9f97c`) — visible to
  your account/tenant by default so teammates can join, not public. No new visibility
  concept.

## Phasing

- **Phase 1 — "tmux for your agent" (local only, no server contract change).** Local
  socket + framing, daemon N-client fan-out refactor, `run-agent`/`attach`/`ls`,
  raw-mode client, detach/reattach, persistence, conditional env handling. Delivers:
  start local → detach → reattach → survives terminal close. De-risks all the new infra
  before touching the server.
- **Phase 2 — pairing & continue-from-web.** Daemon→server announce of local agents +
  web clients attaching/injecting via the existing SignalR fan-out (now just another
  sink). Delivers both headline goals: pair programming and continue-from-anywhere.

## Testing

- **Unit:** frame codec round-trips; N-client fan-out (one slow/dead sink does not stall
  others; drop-oldest under load); detach-sequence scanner (including a prefix split
  across reads); passthrough arg assembly + `--worktree` consumption.
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
