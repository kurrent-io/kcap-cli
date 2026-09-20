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

Two further losses are silent today and are closed by the same design. The sender keys on the raw
hub state, so after a reconnect it can send before the agent is re-registered, and the hub drops
output whose connection id does not match the agent's. And a send that completed locally just
before the transport died may never have reached the server; nothing reports it.

## Goals

- The PTY read loop never waits on the cloud mirror, for any agent, whoever launched it.
- One agent's cloud backlog cannot delay another agent's local output, and cannot consume another
  agent's backlog budget.
- Memory stays bounded per agent.
- Every loss the daemon can know about — overflow, a send that keeps failing, a connection
  change — is followed by an explicit resynchronisation.
- No server, web or mobile change.

## Non-goals

- A dedicated reset message in the hub protocol (the server replacing its ring instead of
  appending a reset to it). Worth a follow-up issue; this design does not depend on it.
- Acknowledged delivery. Without it the daemon cannot detect a server that silently rejects one
  agent's output — the case where a per-agent re-registration gave up after its retries. That
  window exists today and remains.
- Cloud-lane fairness. All pumps share one hub transport; a flooding agent still competes with a
  quiet one *for the cloud lane*. What is isolated is local output and the per-agent backlog.
- Decoupling finalization from the hub. `FinalizeAgentRunAsync` awaits server calls on the same
  transport, so under real transport back-pressure it stalls as it does today.
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
`ITerminalSink`. One instance per registered agent.

Constructor dependencies: the agent id, the agent's `SinksLock` and `OutputBuffer`, a send delegate
(`ServerConnection.SendTerminalOutputAsync`), an eligibility delegate (`ServerConnection.IsReady`),
a logger and a `TimeProvider`. Tunables with defaults: backlog budget, retry delay (500 ms),
failing-send attempts (5), drain bound (2 s), warning interval (1 min).

Surface: `TryEnqueue(byte[])` (caller holds `SinksLock`), `RequestResync()` (takes `SinksLock`),
`StartPump()`, `StopAsync(TimeSpan drainBound)`.

**Queue.** An unbounded single-reader channel of `byte[]`, with a byte counter beside it. The
chunks are the same arrays the ring holds, so a backlog costs references, not copies. Base64
encoding happens in the pump, at send time.

**Budget.** 2 MB of raw bytes — `TerminalOutputBuffer.MaxBytes`, exposed as a constant so the two
cannot drift. Past the ring's size a replay is cheaper than delivering the backlog. Only queued
chunks count; the chunk the pump has dequeued and is sending does not. A chunk arriving on an empty
queue is always accepted, whatever its size. Worst-case memory per agent beyond the ring itself:
one budget of queued references plus one ring's worth held by a replay in progress.

**Synchronisation contract.**

- `_desynced`, `_abortReplay` and `_completed` are `volatile` flags. They are *set* only under
  `SinksLock`. `_desynced` and `_abortReplay` are *cleared* only by the pump, under `SinksLock`,
  inside the resync step. The pump reads all three without the lock.
- The byte counter is changed only through `Interlocked`: the producer adds under `SinksLock`, the
  pump subtracts as it dequeues, without the lock. The pump is the only dequeuer, and the resync
  step — which drains the queue and zeroes the counter — runs on the pump under `SinksLock`, so
  neither a producer add nor another dequeue can interleave with it.
- Every set of `_desynced` also writes a zero-length sentinel to the channel, so a pump waiting on
  an empty queue always wakes. The sentinel is never sent and never counted. A sentinel write that
  fails because the channel is completed is ignored: a completed sink does not resync.

**Desync causes.**

| Cause | Set by | Interrupts a replay in progress |
|---|---|---|
| Queued bytes would exceed the budget | producer, in `TryEnqueue` | no |
| A send keeps failing while eligible, attempts exhausted | pump | yes — the replay cannot continue past a missing chunk |
| `RequestResync()` after a re-registration | orchestrator | yes — the connection the replay started on is gone |

While desynced, `TryEnqueue` ignores chunks; the ring keeps recording them.

**Pump loop.** One iteration, in this order of precedence:

1. Cancelled → exit.
2. `_desynced` set → if `_completed`, exit; otherwise run a resync (below) and start over.
3. A chunk can be read → skip it if it is a sentinel; otherwise subtract its size from the
   counter, send it, and start over.
4. The channel is completed and empty → exit.
5. Wait for the channel.

Pending resync work always comes before waiting on, or exiting from, the channel.

**Sending** — live chunks, the reset, and replay chunks all go through one routine:

1. Wait until eligible (`IsReady`), re-checking after each retry delay. Nothing is sent to a
   connection that has not finished re-registering. A chunk that is waiting or held is given up —
   a live chunk when `_desynced` is set, a replay chunk when `_abortReplay` is set — and control
   returns to the loop: the resync that follows supersedes it.
2. Call the send delegate with the pump's token.
3. On an exception while *not* eligible: hold the chunk and go back to step 1, indefinitely.
4. On an exception while eligible: retry after the delay; when the attempts are exhausted, set
   `_desynced` and `_abortReplay` and return to the loop. The chunk is not dropped silently — the
   resync that follows replaces it.

**Resync.**

1. Wait until eligible, so the snapshot is fresh when sending starts.
2. Under `SinksLock`: drain the queue, zero the counter, take `OutputBuffer.GetAll()`, clear
   `_desynced` and `_abortReplay`.
3. Send `ESC c` (RIS, bytes `1B 63`) as one chunk, then the retained chunks in order.
4. Between replay chunks, check `_abortReplay`; if set, stop this replay and return to the loop,
   which starts a fresh resync. A budget overflow during the replay sets only `_desynced`, so the
   replay **runs to completion** and the next resync starts after it.

The fan-out holds `SinksLock` for its append + enqueue, so every chunk is either in the snapshot or
queued after it — no gap, no duplicate. This is the same pairing local attach uses for its replay.
The replay is sent as the ring's original chunks rather than one flattened buffer, so message sizes
stay what the transport already carries.

**Stale chunks.** A send that had already started when the desync happened may land before the
reset; so may the one chunk the pump sends between its last flag check and the producer setting the
flag. Nothing queued before a desync is ever sent *after* that desync's reset: the resync step
drains the queue before the reset goes out.

**Sustained overload.** When an agent produces faster than the transport carries, the queue
overflows during every replay. Because a replay runs to completion, each cycle still delivers a
complete snapshot: viewers see reset + full repaint, lagging by about one replay. Resets are paced
by the transport — at most one per replay sent — and when the load subsides the next replay is
followed by a queue that stays within budget, and the sink stays synced.

**Logging.** A Warning on a Synced→Desynced transition, naming the agent, the cause and the running
count — at most one per agent per warning interval; transitions inside the interval are counted and
reported by the next Warning. Resync completion and retry attempts log at Debug.

### Transport

`ServerConnection.SendTerminalOutputAsync(agentId, base64, ct)` becomes the raw hub send —
`_hub.SendAsync("SendTerminalOutput", …, ct)` — and stays `virtual` so test fakes can gate it. It
throws on failure and must honour `ct`, including while a write is blocked inside the transport;
the sink owns retry.

Each pump has at most one send in flight, and sends for one agent are issued strictly in order.

**Ordering dependency, unchanged by this design.** `SendAsync` orders the daemon's writes, not the
server's handling of them, and the server allows one client's invocations to run in parallel. The
mirror therefore relies — as the shared sender does today — on the server appending and
broadcasting one connection's sequential `SendTerminalOutput` messages in arrival order. This
design adds no new reliance on it: per agent, the send sequence is exactly as serial as before.

Removed: `TerminalOutputSender`, `ServerConnection.TrySendTerminalOutput`, the `_terminalSender`
field with its task, CTS, `TerminalSenderCtsForTests` seam and dispose steps.

### Lifecycle

**Ownership.** The read loop owns its sink: it creates the sink and starts the pump at the top of
`ReadAgentOutputAsync` (covering both launch sites) when the agent is not `IsPrivate`, and stops it
in `finally`, **before** `FinalizeAgentRunAsync`. The orchestrator also tracks every live sink in
its own registry, keyed by the sink rather than the agent, from pump start until `StopAsync`
returns — so the record survives the agent's removal from `_agents`.

**`StopAsync(drainBound)`.** Marks the sink completed and completes the channel, then waits up to
`drainBound` for the pump to deliver what is queued, including a replay already in progress. A
completed sink never *starts* a resync — the pump exits instead: the server deletes an agent's
terminal buffer when the agent unregisters, so a replay begun at the end of a run is worthless. When the bound passes, `StopAsync` cancels the
pump's own token — which reaches a send blocked inside the transport, a retry delay and a channel
wait alike — and awaits the pump. If the pump still has not ended one retry delay later (a send
delegate ignoring its token), it is abandoned with its faults observed, and a Warning is logged; a
misbehaving transport must not hold finalization. A drain cut short logs at Debug with the bytes
left unsent.

**Agent end.** The read loop calls `StopAsync(2 s)`, then finalizes. No terminal output is sent
after the agent unregisters, because the pump has ended. In the healthy case the queue is empty or
nearly so and the stop returns immediately; with the cloud blocked it costs at most the drain bound
before finalization starts.

**Daemon shutdown.** The pump's token is linked to the daemon shutdown token, so shutdown cancels
every pump at once. The read loop's `finally` calls `StopAsync(TimeSpan.Zero)` on that path.
`AgentOrchestrator.DisposeAsync` awaits `StopAsync(TimeSpan.Zero)` for every sink still in the
registry before it returns, so no terminal send races the hub disposal that follows in
`DaemonRunner`. A sink cannot be registered once shutdown has begun.

**Reconnect.** The pump sends nothing until `IsReady`, which is restored only after
`DaemonConnect` and per-agent re-registration. `ReRegisterAgentsAsync` calls
`agent.CloudSink?.RequestResync()` after each agent's successful `AgentRegisteredAsync`, so every
connection change — a transport reconnect, a heartbeat slot displacement, a server restart that
wiped the server's ring — ends in one reset + replay per registered agent once the daemon is ready.
That repairs any chunk written to the old connection that never arrived. Cost: up to 2 MB per
registered agent per reconnect, idle agents included.

This is safe where the earlier reconnect replay was not: that one appended the ring on top of the
live screen with no reset and raced the read loop's live sends. Here the reset comes first, and the
replay travels on the same ordered per-agent lane as live output.

### What a resync guarantees

After a resync the mirror is what a client gets by attaching locally at that moment: a fresh
terminal fed the daemon's retained ring, then live output with no gap. It is not a reconstruction
of the screen. The ring is a byte suffix, not a frame boundary, so:

- it can begin mid-escape-sequence or mid-UTF-8 sequence, which renders as stray characters until
  the agent next repaints;
- anything painted before the ring's horizon and never repainted is absent;
- terminal modes (alternate screen, mouse tracking, bracketed paste) come back only if the
  sequences that set them are still in the ring.

These are the limits local reattach already has. A viewer that subscribes later replays the
server's ring, which — as today — is whatever 2 MB suffix the server retains; the reset may have
been evicted from it, which does not matter to a terminal that starts empty.

The web UI renders with xterm.js and the desktop app with XTerm.NET; both must honour RIS, and the
desktop side is pinned by a test.

## Invariant

Add to the `CLAUDE.md` invariants:

> **The PTY read loop never awaits a consumer.** Local sinks and the cloud sink take chunks through
> a non-blocking `TryEnqueue` under `SinksLock`, and each drains on its own pump. A consumer that
> falls behind is cut off and replayed from the ring — a local client by reattaching, the cloud
> mirror by an in-band terminal reset — never allowed to back-pressure the PTY, which would freeze
> every other surface of that agent.

## Testing

**`CloudTerminalSinkTests`** (replaces `TerminalOutputSenderTests`; fake time, a send delegate that
can be gated, made to throw, and that observes its token; a settable eligibility flag):

- chunks arrive in order, as base64 of the original bytes;
- not eligible: nothing is sent, including when the hub would accept it; on becoming eligible the
  held chunk and its successors follow in order;
- budget exceeded: queued chunks are discarded and the sends that follow are the reset, the ring's
  chunks, then live output — with appends racing the resync step, no chunk missing or duplicated;
- a send already gated when the overflow happens may land before the reset; nothing queued before
  the overflow is sent after it;
- failing sends past the attempt budget desync instead of dropping, including when the failing
  chunk was the last one and the queue is empty (the pump must not park);
- an overflow during a replay does not interrupt it; the next resync follows the completed replay;
- sustained overload: every cycle delivers a complete replay, and once production stops the sink
  ends synced with live output flowing;
- `RequestResync` on an idle sink with an empty queue wakes the pump and produces reset + replay;
  during a replay it aborts that replay and starts a fresh one;
- a failing resync retries after the delay, with Warnings limited to one per interval;
- `StopAsync`: drains a synced queue; exits at once when desynced; cancels a send blocked on its
  gate when the bound passes, without the gate being released; abandons a delegate that ignores its
  token; a stop during a replay delivers the rest of the replay and the queued tail when they fit
  in the bound, and the pump then exits instead of waiting on the completed channel.

**Orchestrator regression** (`SeedAgentForTest` / `ReadAgentOutputForTest`, a runtime fake that
emits on demand, a `ServerConnection` fake whose `SendTerminalOutputAsync` blocks on a gate and
whose `IsReady` is settable):

- a server-launched agent with a local sink attached keeps delivering to that sink, and keeps
  draining the runtime, for far more output than the budget while the cloud send is blocked;
- with agent A's cloud lane blocked and over budget, agent B's local sink receives B's output
  without delay, and B's sink stays within its own budget and synced;
- after the gate opens, the mirror receives at most one stale chunk, then the reset, the ring, and
  live output, in order;
- stopping an agent while the cloud is blocked: the read loop leaves its fan-out at once and enters
  finalization within the drain bound, with no `SendTerminalOutputAsync` call after
  `AgentUnregisteredAsync`. This replaces
  `Stopping_an_agent_releases_a_read_loop_blocked_on_a_full_terminal_queue`, whose contract — the
  read loop parked inside the send until its token cancels — no longer exists;
- `ReRegisterAgentsAsync` requests a resync for each agent it re-registers, and none for an agent
  whose re-registration gave up;
- disposing the orchestrator with a send still gated completes, and the gated send observed
  cancellation before disposal returned;
- a `--private` agent never calls `SendTerminalOutputAsync`.

**Desktop:** feeding `ESC c` to the XTerm.NET terminal behind `RemoteTerminalViewModel` clears
previously written content.

**Existing suites:** `ServerConnectionDisposeTests` loses its sender-drain cases; fakes overriding
`SendTerminalOutputAsync` keep compiling, and `CaptureServerConnection`'s gate now blocks the
sink's pump rather than the read loop.

## Docs

`docs/CHANGES.md` entry. No CLI surface changes, so `README.md` and the help files are untouched.
