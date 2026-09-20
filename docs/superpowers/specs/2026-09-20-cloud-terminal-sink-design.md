# Cloud terminal sink: decouple the cloud mirror from local terminal output

Issue: #1022

## Problem

`AgentOrchestrator.ReadAgentOutputAsync` is the one loop that drains an agent's PTY. For an agent
launched from the server it delivers each chunk to the local sinks and then **awaits**
`ServerConnection.SendTerminalOutputAsync`, which enqueues into a single `TerminalOutputSender`
shared by every agent: one 2,000-chunk bounded channel, one consumer, one SignalR send at a time.

When that queue fills, the await parks the read loop. The PTY stops being drained, so the local
sinks stop receiving output too — `kcap agent attach` and the desktop app freeze, including the
echo of what the user types. The queue is shared, so output from *other* agents is enough to stall
a quiet one.

The non-blocking path exists but is selected by `IsLocalSpawned`, which records where the agent was
launched, not who is watching it. A server-launched agent with a local client attached still takes
the blocking path. The non-blocking path has its own defect: on a full queue it drops the chunk,
and nothing repairs the mirror afterwards. The server appends what it receives to a 2 MB ring and
broadcasts it; it has no resynchronisation message, so a dropped chunk is a permanent gap in a
cursor-addressed redraw stream.

## Goals

- The PTY read loop never waits on the cloud mirror, for any agent, whoever launched it.
- One agent's cloud backlog cannot delay another agent's local output or cloud lane.
- Memory stays bounded per agent.
- The mirror never silently loses a chunk: every loss is followed by an explicit
  resynchronisation that leaves viewers and the server's ring with a coherent screen.
- No server, web or mobile change.

## Non-goals

- A dedicated reset message in the hub protocol (the server replacing its ring instead of
  appending a reset to it). Worth a follow-up issue; this design does not depend on it.
- The Unix PTY thread-pool starvation tracked in #1023.

## Scope: which agents get a cloud sink

Every agent **registered with the server** — server-launched, or started with `kcap agent start`
without `--private`. That includes agents whose visibility is owner-only: they are registered and
streamed, and are reachable from `kcap agent attach`, the web UI and the desktop app at once.

An agent started with `--private` (`AgentInstance.IsPrivate`) is unregistered and is never streamed
to the server. It has no cloud lane and gets no sink.

## Design

### The read loop

Fan-out happens in one place, under `AgentInstance.SinksLock`:

1. `OutputBuffer.Append(data)`
2. `TryEnqueue` on each local sink
3. `CloudSink?.TryEnqueue(data)`

Every step is non-blocking. The loop has no cloud await, so the `IsLocalSpawned` branch and the
`sendCts` linked token are removed. The read loop was the only reader of
`AgentInstance.IsLocalSpawned`, so the property and its one assignment go too.

The cloud sink is held as `AgentInstance.CloudSink`, not added to `LocalSinks`: that list feeds the
local dims clamp and must stay local-only.

### `CloudTerminalSink`

New type in `src/Capacitor.Cli.Daemon/Services/CloudTerminalSink.cs`, implementing
`ITerminalSink`. One instance per registered agent, created at the top of `ReadAgentOutputAsync`
(which covers both launch sites) and completed in its `finally`.

Constructor dependencies: the agent id, the agent's `SinksLock` and `OutputBuffer`, a send delegate
(`ServerConnection.SendTerminalOutputAsync`), an `isConnected` delegate
(`ServerConnection.IsConnected`), a logger and a `TimeProvider`. Tunables with defaults: backlog
budget, retry delay (500 ms), connected-failure attempts (5), drain deadline (30 s).

**Queue.** An unbounded single-reader channel of `byte[]`, with a byte counter beside it. The
chunks are the same arrays the ring holds, so a backlog costs references, not copies. Base64
encoding happens in the pump, at send time.

**Budget.** 2 MB of raw bytes — `TerminalOutputBuffer.MaxBytes`, exposed as a constant so the two
cannot drift. Past the ring's size a replay is cheaper than delivering the backlog, which is what
makes this the natural bound. Worst-case memory per agent beyond the ring itself: one budget of
queued references plus one ring's worth held by a replay in progress.

**States.** `Synced`, `Desynced`, and a `Completed` flag orthogonal to both. Every transition is
made under `SinksLock`; the pump reads the flags without it.

| Event | Synced | Desynced |
|---|---|---|
| Chunk arrives, within budget | queue it | ignore — the ring has it |
| Chunk arrives, would exceed budget | → Desynced | ignore |
| Send fails, transport down | hold the chunk, retry after the delay, indefinitely | same, for the reset and replay chunks |
| Send fails while connected, attempts exhausted | → Desynced | stay Desynced; retry the resync after the delay |
| Pump observes Desynced | — | resync (below) |
| Desynced set again during a replay | — | abandon the replay; resync from a fresh snapshot |

After `Complete()`, arriving chunks are ignored in both states; the pump keeps draining and
resyncing until the queue is empty or the drain deadline passes.

A chunk superseded by a desync — queued, held in retry, or part of an abandoned replay — is
discarded, never sent: the reset that follows makes it meaningless.

**Resync.** Under `SinksLock` the pump drains its queue, zeroes the byte counter, takes
`OutputBuffer.GetAll()`, and clears `Desynced`. Outside the lock it sends `ESC c` (RIS, bytes
`1B 63`) as one chunk, then the retained chunks in order, then returns to the live queue. The
fan-out holds the same lock, so every chunk is either in the snapshot or queued after it — no gap,
no duplicate. This is the same pairing local attach uses for its replay.

The replay is sent as the ring's original chunks rather than one flattened buffer, so message sizes
stay what the transport already carries.

A chunk arriving on an empty queue is always accepted, whatever its size. An overflow can
therefore only happen while the queue is non-empty, so the pump is always awake, or about to be,
when `Desynced` is set by the producer; no wake-up sentinel is needed. The pump checks the flag
before each send and after each held send lands.

**Logging.** One Warning per Synced→Desynced transition, naming the agent, the cause (budget or
connected send failure) and the running count. One Information when a resync completes. Retry
attempts log at Debug, as today.

### Transport

`ServerConnection.SendTerminalOutputAsync(agentId, base64, ct)` becomes the raw hub send —
`_hub.SendAsync("SendTerminalOutput", …)` — and stays `virtual` so test fakes can gate it. It
throws on failure; the sink owns retry.

Each pump has at most one send in flight. SignalR serialises concurrent `SendAsync` calls and
back-pressures them through the transport pipe, so ordering is preserved per agent, which is the
only ordering the mirror needs, and a flooding agent costs a quiet one at most one in-flight chunk
per other agent on the cloud lane — and nothing locally.

Removed: `TerminalOutputSender`, `ServerConnection.TrySendTerminalOutput`, the `_terminalSender`
field with its task, CTS, `TerminalSenderCtsForTests` seam and dispose steps.

### Lifecycle

- **Start:** created and its pump started at the top of `ReadAgentOutputAsync`, when the agent is
  not `IsPrivate`. The pump's token is the daemon shutdown token.
- **Agent end:** the read loop's `finally` calls `Complete()` before `FinalizeAgentRunAsync`, which
  does not wait for the drain. The pump delivers what is queued — resyncing first if desynced, so
  the final screen reaches the mirror — and stops when the queue is empty or 30 s after
  `Complete()`, whichever comes first, so a dead agent's pump cannot outlive an outage.
- **Daemon shutdown:** the shutdown token cancels every pump at once, including one holding a
  chunk. `AgentOrchestrator.DisposeAsync` awaits the pump tasks so no send races hub disposal.
- **Reconnect:** unchanged — re-registration does not replay. A short outage is absorbed by the
  held chunk and the queue, in order and without loss. An outage long enough to exceed the budget
  ends in one reset + replay once the transport is back.

### What viewers see

A resync reaches every viewer as ordinary terminal output: the screen clears and repaints from the
ring. The server's ring then holds the pre-gap bytes, the reset, and a coherent replay, so a viewer
that subscribes later replays into the same final state. The web UI renders with xterm.js and the
desktop app with XTerm.NET; both must honour RIS, and the desktop side is pinned by a test.

RIS also clears modes (alternate screen, mouse tracking, bracketed paste). They are restored only
if the sequences that set them are still inside the 2 MB ring — the same limit a local reattach
already has.

## Invariant

Add to the `CLAUDE.md` invariants:

> **The PTY read loop never awaits a consumer.** Local sinks and the cloud sink take chunks through
> a non-blocking `TryEnqueue` under `SinksLock`, and each drains on its own pump. A consumer that
> falls behind is cut off and replayed from the ring — a local client by reattaching, the cloud
> mirror by an in-band terminal reset — never allowed to back-pressure the PTY, which would freeze
> every other surface of that agent.

## Testing

**`CloudTerminalSinkTests`** (replaces `TerminalOutputSenderTests`; fake time, gated send delegate):

- chunks arrive in order, base64 of the original bytes;
- transport down: the head chunk is held and retried, later chunks follow in order, nothing lost;
- budget exceeded: queued chunks are discarded, and the next sends are RIS then the ring's
  chunks then live output — with appends racing the resync, no chunk missing or duplicated;
- connected send failure past the attempt budget desyncs instead of dropping;
- a second desync during a replay abandons it and restarts from a fresh snapshot;
- a failing resync retries after the delay without logging a Warning per attempt;
- `Complete()` drains, resyncs if desynced, and stops at the drain deadline while the transport is
  down; cancellation stops a pump that is holding a chunk.

**Orchestrator regression** (`SeedAgentForTest` / `ReadAgentOutputForTest`, a runtime fake that
emits on demand, a `ServerConnection` fake whose `SendTerminalOutputAsync` blocks on a gate):

- a server-launched agent with a local sink attached keeps delivering to that sink, and keeps
  draining the runtime, for far more output than the budget while the cloud send is blocked;
- with agent A's cloud lane blocked and over budget, agent B's local sink receives B's output
  without delay;
- after the gate opens, the mirror receives RIS, the ring, then live output, in order;
- stopping the agent while the cloud is blocked finalizes promptly;
- a `--private` agent never calls `SendTerminalOutputAsync`.

**Desktop:** feeding `ESC c` to the XTerm.NET terminal behind `RemoteTerminalViewModel` clears
previously written content.

**Existing suites:** `ServerConnectionDisposeTests` loses its sender-drain cases; fakes overriding
`SendTerminalOutputAsync` keep compiling.

## Docs

`docs/CHANGES.md` entry. No CLI surface changes, so `README.md` and the help files are untouched.
