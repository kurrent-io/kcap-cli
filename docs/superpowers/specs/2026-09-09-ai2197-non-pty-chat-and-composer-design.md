# Chat and composer for non-PTY hosted sessions (AI-2197, AI-2625)

Slice of the desktop shell (parent AI-2171). Today the workspace renders a Chat
tab only for the two PTY-hosted vendors, Claude and interactive Codex, by
tailing the transcript file the vendor writes. Every other harness the app
offers — Cursor, Copilot, Gemini, Kiro, OpenCode (ACP), Antigravity (NDJSON per
turn), Pi (JSONL-RPC) and Codex on app-server — produces its transcript as a
live `AcpEventEnvelope` stream that the daemon forwards to the server and
nothing else consumes. The daemon reports those agents, the rail and Home list
them, but their workspace has no Chat tab, no Terminal tab, and no way to send
a follow-up. AI-2625 is the user-facing report of that; AI-2197 is the slice
that was always meant to close it.

The design goal, settled with the owner: **a non-PTY session should work as
identically to a PTY session as the transport allows.** This spec is the first
of two slices toward that. It delivers the transcript and the composer for all
eight non-PTY vendors. The second slice (permission and question cards for
these vendors) is scoped at the end and is not designed here.

## Decisions

Settled with the owner during brainstorming, 2026-09-09:

1. **The transcript reaches the app as a daemon-written envelope journal on
   disk, read through the existing tail.** The chat is already envelope-based:
   the PTY path projects vendor records to canonical events and maps those to
   `AcpEventEnvelope`s before rendering. Non-PTY runtimes emit that very type
   natively. So the daemon appends each envelope as one JSON line to a
   per-agent file and reports it as the agent's `transcript_path`; the app's
   `JsonlTail`, `SwitchPath` and renderer work unchanged. The complete persisted
   history on open, survival across app restarts, a file that outlives the
   agent, and a file `kcap` and tests can read. Rejected: an in-memory journal replayed over a
   new long-lived `TranscriptSubscribe` frame (a bounded buffer with a
   truncation story, a replay/live splice, a second app-side source, and
   history that dies with the daemon — more code for a worse parity result);
   server history plus live frames (two sources to align, and the app's chat
   is deliberately local-daemon-only).
2. **The journal is fed at each runtime's emit site, not by a second consumer
   of its channel, and it never does file IO on the runtime's thread.** Every
   envelope-emitting runtime funnels its writes through one method; the journal
   is handed the envelope there, after the channel accepts it and under the
   same serialization the channel write uses, so the journal's own queue
   receives the envelopes the runtime wrote, in the order it wrote them — less
   only what the journal's own counted overflow sheds, and what a crash leaves
   unpersisted (§1). The hand-off is a non-blocking enqueue onto the journal's
   bounded queue; a single writer task drains it to disk. So the forwarder's channel and its
   backpressure and loss policy are untouched, and a slow or hung disk can
   never stall an ACP read loop, Pi's pump, Antigravity's worker or Codex's
   read loop — it costs journal lines, never protocol liveness. The journal is
   a constructor dependency, because the runtimes' pumps start in their
   constructors and the initial prompt's envelopes are emitted before the
   factory returns. Rejected: a tee channel between runtime and forwarder (it
   cannot reproduce `CodexForwardBuffer`'s split policy — canonical waits with
   a watchdog, ephemeral drops — and it doubles the queued capacity before the
   watchdog can fire); synchronous appends at the emit site (a hung
   filesystem would hold the ACP read loop under `_aggregationLock`, exactly
   what that runtime's non-blocking transcript design exists to prevent).
3. **Input rides one new one-shot frame, `SendText`, routed to the same delivery
   core the server-origin `SendInput` uses.** The raw `Stdin` attach path is
   unsupported for these runtimes by design (`SendRawInputAsync` throws); every
   runtime implements `SendUserInputAsync`. PTY keeps its `Stdin` path
   untouched in this slice; the app chooses by `has_terminal`.
4. **Which reader a session gets is stated on the wire, not inferred.**
   `AgentStatusDto` gains a trailing `transcript_format` that a current daemon
   always emits — `"vendor"` for a PTY runtime, `"envelopes"` for an
   envelope-emitting one — so null means an older daemon and nothing else.
   Same wire rule AI-2196 set: trailing nullable members, no new frame family
   for reading, every member always emitted.
5. **Parity amendments the PTY comparison surfaced.** Discovery sets
   `agent.SessionId` for PTY agents, so an envelope-sourced agent gets it at
   construction, in the canonical form the server uses; PTY transcript
   discovery and the Codex turn probe are gated to PTY runtimes so neither can
   replace or watch a journal.
6. **Retention is 30 days**, mirroring Claude's own default transcript cleanup,
   because these files are kcap-owned where the PTY files are the vendor's.
7. **No vendor display rules on the journal path.** `ClaudeChatRules` and
   `CodexChatRules` exist to drop or rewrite noisy vendor records; the ACP
   translators already emit the clean vocabulary the web renders verbatim. The
   per-vendor `IChatDisplayRules` hook under `Harness/<Vendor>/` stays available
   if one ever needs it.
8. **The tab-strip "no terminal" note goes away.** It explained an empty pane;
   the pane is no longer empty, and the work-context header already names the
   transport.

## 1. Daemon: the envelope journal

### What is journaled

Every envelope an envelope-emitting runtime's transcript channel **accepts**,
except ephemerals (`Ephemeral == true`: Codex app-server live chunks, which the
server never persists either). Kinds the chat does not render (`usage`,
`token_usage`, `plan`, `session_title`, `assistant_thinking`) are still
written: the file is the session's envelope record, and the renderer already
ignores kinds it does not handle, exactly as thinking is projected and dropped
on the PTY path. An envelope the runtime itself declines — written after its
channel completed at teardown, or after `CodexForwardBuffer` has stalled — is
not in the journal, because it was never in the transcript. The journal's own
queue can also overflow when the disk has hung (§ `TranscriptJournal`); that
loss is counted and marked in the file with a gap note whenever the writer
survives to persist it.

Line order is the order; the `seq` a line carries is the runtime's placeholder
`0`, because the forwarder assigns the real sequence on its own dequeue and the
chat never reads it.

### Where

`<daemon state dir>/transcripts/<sha256(agentId)>.jsonl`, where the state dir
is `DaemonStore.StateDirectory(config.Name)` — so two daemons never collide,
and the directory is already 0700. The filename is the same lowercase SHA-256
hex `AgentPidRecordStore` uses for its records, and for the same reason it
documents: the agent id crosses the SignalR wire unconstrained, so
interpolating it into a path would let `..` or a separator escape the
directory. The derivation moves out of `AgentPidRecordStore` into a shared
`AgentFileNames.For(agentId)` so the two stores can never disagree; the sweep
below joins them by that name. One envelope per line, encoded by
`EnvelopeJournalFormat` (§3) over `CapacitorJsonContext`: snake_case, the same
bytes the server receives.

### `TranscriptJournal`

One per launch, constructed by the orchestrator from the agent id **before the
runtime factory runs** and carried to the factory on a new
`RuntimeStartContext.Journal` member.

It is a bounded queue plus one writer task. `Record(envelope)` is what the
runtimes call: it skips ephemerals and otherwise does one non-blocking
`TryWrite` onto a bounded channel — no IO, no await, never throws. The channel
is `BoundedChannelOptions(4096) { FullMode = Wait, SingleReader = true,
SingleWriter = false }`: `Wait` is what makes `TryWrite` return false on a
full channel so the drop can be counted (`DropWrite` would return true while
discarding, the trap `AntigravityHostedAgentRuntime` documents and avoids the
same way), and `SingleWriter = false` because Pi and Antigravity record from
two threads. The writer task drains the channel in order and appends each
**item** — its gap-note line, when it carries one, and its envelope line,
encoded into one buffer — as one open-seek-to-end-write-flush-close with
`FileMode.Open`, `FileAccess.Write` and `FileShare.ReadWrite | FileShare.Delete`,
so an orderly writer never stops between a note and its envelope, no handle is
held between items, the app's `JsonlTail` never contends with one on Windows,
and the sweep can delete a file the daemon last wrote a moment ago. A regular
file write is not transactional: a crash or an IO fault mid-write can leave a
prefix of the buffer, which for a gap-bearing item can be its complete note
line followed by a torn envelope; `JsonlTail` then renders the note and drops
the torn tail. That is the crash model below, and the guarantee here is only
that *cancellation* never splits an item.
Envelopes arrive at conversational rates with tool-call bursts in the tens per
second; the queue absorbs any burst a healthy disk will see.

**One writer per path at a time, enforced, not assumed.** .NET's `FileStream`
has no cross-handle append primitive — `FileMode.Append` snapshots the length
at open and writes at that cached offset — so two handles on one journal could
overwrite each other, and the one way two can exist is an abandoned writer
(below) alongside a same-id relaunch's journal. Every file operation on a
journal therefore runs under a process-wide per-path `SemaphoreSlim`
(`JournalPathLocks`, keyed by the normalized absolute path under
`PlatformPaths.Comparer` — which moves from the app's `TrayModels.cs` to its own
file in Core (`Capacitor.Cli.Core.PlatformPaths`, one type per file) so the
daemon and the app share one platform path rule; the app's five call sites
re-point — so two state directories that hash the same agent id to the same
file name never share a lock, and one path never has two): each
writer item, `Open`'s header write, the failure path's delete and the sweep's
delete. Entries are reference-counted leases: `Acquire(path, timeout, ct)`
creates or finds the entry and increments its count before waiting; a wait that
succeeds returns a lease whose `Dispose` releases the semaphore and then
decrements; a wait that times out, is cancelled or throws decrements on the
same path **without** releasing a semaphore it never held; and the entry leaves
the map only when the count reaches zero — every increment, decrement and
removal under the map's own lock, so a waiter can never be left holding an
entry the map no longer knows (how one path would end up with two locks) and a
failed wait can never leave a phantom count behind. The map therefore holds
exactly the paths with a live owner or waiter, never one per historical
session or per timed-out attempt. A writer stuck inside
a filesystem call holds its path's lock for as long as the call lasts, which is
exactly the property wanted: nothing else can write the file until that call
has returned and the handle is closed. `Open` and the two deletes take the
lock with a 2 s bound and treat a miss as their failure case (`IsOpen` false
for `Open`; skipped for a delete) — a path whose lock cannot be had is one
whose disk is hung, and the same hang would have taken them down anyway.

**Loss is explicit, local and placed where it happened.** If the queue is full
— the disk has hung or is far behind — `Record` drops the envelope and counts
it in a `pendingGap` counter. The count is not flushed as the writer's next
line, which would put the note ahead of everything already queued before the
loss. Instead the channel's item type is `JournalItem(AcpEventEnvelope
Envelope, int GapBefore)`: the next `Record` that finds room takes the counter
into the item it enqueues (one `TryWrite`, one slot, no two-slot reservation),
and the writer emits the `system_note` "N envelopes were not recorded to this
journal" line and then the envelope's line, so the note sits exactly between
the last envelope that made it and the first one after the gap. A gap still
pending when the channel completes is written by the writer as the final line
once the queue has drained; a gap pending when the writer is abandoned is
part of the abandonment Warning. The server is unaffected: it reads the
forwarder's channel, not this one. A write failure (including the file having
been deleted underneath the writer) latches the journal off after one Warning;
anything still queued is counted and logged with it.

**A crash loses the backlog.** Lines are durable once the writer has appended
them; what is still queued when the daemon process dies — up to the channel's
4096 envelopes plus any pending gap count — is lost with no marker, because no
writer remains to write one. That is the price of keeping file IO off the
runtime threads, and it bounds "full history" to "every envelope the writer
reached"; a healthy disk keeps that backlog at zero.

`Open(cwd, model)` is **synchronous and does its IO on the calling thread** —
the launch path, before the runtime factory runs, where IO is already routine.
It creates the directory, opens or creates the file (recording `CreatedFile`),
writes the header line and flushes it, closes the handle, and only then sets
`IsOpen` and starts the writer. So when the orchestrator publishes
`TranscriptPath` the file exists with its first line, `JsonlTail` never sees
`Missing` for a journal that opened, and a failure to create or write the
header is an open failure: `IsOpen` stays false, no writer starts, `Record` is
a no-op. The header is a `session_started` envelope built with
`AcpEventTranslator.BuildSessionStarted` from what is known at that moment (the
worktree path as `cwd`, the requested model, no raw session id; the file's
identity is the agent id in its name).

`Complete()` is the one lifecycle call, and its owner is the orchestrator:
`CleanupAgentAsync` calls it after `Runtime.DisposeAsync()` (the runtime can
emit nothing further by then), the pre-publish failure path calls it before
deciding whether to delete the file, and daemon shutdown reaches it through the
same cleanup. It closes the channel's writer side and awaits the writer task
for at most 2 s. A healthy writer drains what is queued and exits. A writer
stuck inside a filesystem call cannot be interrupted, so on expiry `Complete`
cancels the writer's token (observed only between items, never inside one),
logs one Warning naming the in-flight item, how many queued items and how
large a pending gap count are being abandoned, latches the journal, and
returns without awaiting further; the abandoned task holds at most the one
handle of the item it is on, finishes that item's write when the call returns,
observes cancellation and exits. `Complete` reports whether
the writer exited (`Drained`) or was abandoned, and the pre-publish failure
path deletes a `CreatedFile` journal only when `Drained` — an abandoned writer
still holds the path lock inside its call, so the delete would not get the lock
anyway, and left alone the sweep reaps the file in 30 days. A same-id
relaunch's `Open` takes the same path lock: while the abandoned call lasts,
`Open` misses its 2 s bound and the new incarnation runs with `IsOpen` false
(Waiting in the app), which is the truthful state of a journal whose disk is
hung; once the call returns and the lock is free, the late line has already
been written at the true end of the file under the lock, and any later `Open`
appends after it. No two handles are ever open on one journal. A journal never
completed (the process died) simply stops with the process. `Path`, `IsOpen`
and `CreatedFile` are readable by the orchestrator.

The header is written at **every** open, so a relaunch that reuses an agent id
which already has a journal (an envelope-sourced rebind through the source
claim) appends a second header that marks the new incarnation, and the
transcript spans incarnations the way the server's canonical stream does. A
relaunch under a new agent id starts a new file. The header is deliberately
independent of the forwarder's own `session_started`, which is still built and
sent to the server exactly as today; whether a bind is fresh or a rebind is
only learned from the source-claim response, and the journal must not wait for
it.

That is the file's story. **The app's story for a same-id relaunch is
unchanged by this slice**: `WorkspaceViewModel.Accumulate` carries
`SessionEnded` forward once set, and `OpenSession` keeps an already-open
workspace for the same id, so a workspace that watched an incarnation end stays
ended until the user closes it; a workspace opened after the relaunch tails the
same file from byte zero and sees both incarnations. Whether an ended
workspace should come back to life on a same-id relaunch is a session-model
question the PTY path has not answered either, and it is out of scope here.

### Constructor injection at the emit site

Each envelope-emitting runtime has exactly one place its transcript channel is
written: `AcpHostedAgentRuntime.EmitEnvelope`, `PiRpcHostedAgentRuntime.Write`,
`AntigravityHostedAgentRuntime`'s transcript write, and
`CodexForwardBuffer.Emit`. Each gains an optional `TranscriptJournal?`
constructor parameter (null in every existing direct-construction test site).
The factory calls `journal.Open(...)` before constructing the runtime and
passes it in.

Why the constructor and not a later assignment: `PiRpcHostedAgentRuntimeFactory`
sends the initial prompt and awaits the handshake before returning, the ACP and
Antigravity factories enqueue the initial prompt in `StartAsync`, and every
pump starts inside its constructor — envelopes are in the channel before the
orchestrator ever sees the runtime. The `ActivityClock` post-construction
assignment carries exactly the race the Pi factory documents; the journal
cannot accept it, because the first `user_message` is the line that matters
most.

**Order and admission.** The channel accepting an envelope and the journal
recording it are one serialized operation per runtime, and the record happens
only on acceptance:

- `AcpHostedAgentRuntime.EmitEnvelope` already writes under `_aggregationLock`;
  `Record` joins it after a successful `TryWrite`.
- `PiRpcHostedAgentRuntime.Write` and Antigravity's transcript write are
  reached from two threads (the pump and the send-time `user_message`; the turn
  worker and the queue-full notice from `EnqueueTurn`), which is why their
  channels are `SingleWriter = false`. Each gains a small write lock around
  `TryWrite` plus `Record`. The activity-clock advance stays where it is,
  before the lock. `TryWrite` never blocks on these `DropOldest` channels, so
  the lock only ever covers the channel write and the journal queue's own
  `TryWrite` — never any IO.
- `CodexForwardBuffer.Emit` has a single writer (the read loop) and needs no
  lock. `WriteCanonicalBlocking` changes from `void` to `bool accepted`: true
  only when the envelope entered the channel; false when the stall watchdog
  fired, when shutdown cancelled the wait (today it swallows that cancellation
  and returns as if nothing happened), and when the channel was already
  completed (`ChannelClosedException`, caught and logged at Debug as the
  ordinary teardown race the other runtimes already treat it as, instead of
  escaping the read loop). `Record` follows a successful `TryWrite` or an
  accepted blocking write. A stalled buffer, a completed channel, a cancelled
  wait and a dropped ephemeral are all "not written" and are not journaled.
  Antigravity's queue-full system note is journaled exactly when
  `EmitDaemonNotice` returns true, so the file and the transcript agree.

Recording after acceptance means a `DropOldest` eviction costs the journal
nothing: when the runtime channel is full, `TryWrite` still succeeds and evicts
the oldest envelope, which the journal already received when it was accepted —
the journal's own overflow and the crash backlog are the only losses, and both
are the journal's, not the runtime's. `CodexForwardBuffer`'s canonical
wait, ephemeral drop and 30-second stall watchdog are untouched because nothing
sits between the buffer and the forwarder. `Record` is one channel `TryWrite`
on the emitting thread; the disk is only ever touched by the journal's writer
task, so no runtime thread can block on it.

### Antigravity's user turn

Three of the four envelope-emitting runtimes already put the user's prompt in
the transcript: Pi emits a `user_message` in `SendUserInputAsync`, the ACP
runtime's turn worker emits `AcpEventTranslator.BuildUserMessage` when it
dequeues a turn, and the Codex mapper translates the app-server's own user
item. Antigravity does not: `EnqueueTurn` queues the text, the worker hands it
to the child as an argument, and `AntigravityNdjson.ToEnvelopes` maps only
what the child prints. The web shows Antigravity user turns from the server's
own dispatch record, which a local send never reaches.

Antigravity therefore adopts the ACP worker's pattern: `RunTurnWorkerAsync`
emits `AcpEventTranslator.BuildUserMessage(seq: 0, NowIso(), turn.Text)`
through the transcript write after it dequeues a turn and takes `_turnGate`,
before it spawns the turn child. The helper takes a timestamp and Antigravity
has no clock today, so its constructor gains an optional `TimeProvider`
(default `TimeProvider.System`, the same shape `AcpHostedAgentRuntime` and
`CodexAppServerHostedAgentRuntime` already take) and a `NowIso()` over it;
tests inject a fixed provider so the journal and server bytes are pinned. The
single worker is what orders it ahead of that turn's output — emitting from
`EnqueueTurn` would not, since the worker can dequeue and the child can print
before the enqueuing thread runs again. The initial prompt and every follow-up
take the same path; a turn the queue refuses emits nothing but the existing
"message not delivered" note. The server maps a `user_message` envelope to
`UserMessageReceived` through its attribution service for the other three
vendors already, so Antigravity joins an existing server path rather than
adding one.

### Status snapshot

`HostedRuntimeStart` is in hand before the `AgentInstance` is constructed, so
the envelope-sourced fields are set in the initializer, **before
`PublishAgent`** and therefore before the first snapshot that can carry this
agent (today `PublishAgent` precedes `RegisterAgentAsync`, whose server call
can stall for the length of an outage):

- `SessionId = start.Transcript is { } t ? SessionIds.Canonical(t.AcpSessionId) : null`.
  `SessionIds.Canonical` is a new Core helper mirroring the server's
  `CanonicalSessionId.Normalize` exactly: a value that parses as a GUID becomes
  its 32-hex `N` form, anything else is returned unchanged. It is not
  `PermissionWire.Canonical`, which returns null for a non-GUID and exists to
  match permission requests, not to name sessions. ACP session ids are opaque
  strings on the wire (existing tests use `sess-1` and
  `fixed-conversation-id`), so the GUID-only form would leave those agents with
  no session id at all.
- `TranscriptPath = start.Transcript is null ? null : journal.IsOpen ? journal.Path : null`.
- `Journal = journal` (a new `AgentInstance` member, null for PTY), so cleanup
  and the failure path below can reach it.

`SnapshotAgentsForStatus` emits `transcript_format` derived from the runtime,
not stored: `a.Runtime is IAcpTranscriptSource ? "envelopes" : "vendor"`. It is
therefore present and correct in the first snapshot, whether or not the journal
opened. PTY agents keep null `SessionId`/`TranscriptPath` until discovery, as
today.

Every later `AgentStatusChanged` for an envelope-sourced agent now carries the
canonical session id instead of null — which is what makes the web UI render a
stopped Pi agent as "Agent failed to start" today. The rest of that report (the
SIGKILL exit and stop-as-failure mapping) stays with AI-2644.

**The same rule on the app's server reads.** `WorkContextIds.CanonicalSessionId`
today trims and strips every dash, which would turn an opaque `sess-1` into
`sess1` and miss the server; it delegates to `SessionIds.Canonical` (GUID →
`N` form, anything else trimmed and unchanged) before its existing dot-segment
validation. The PR pane passes the dto's `session_id` through untouched and
needs no change.

### PTY-only paths

- `DetectSessionIdAsync` runs unconditionally after launch and, for
  `vendor == "codex"`, would locate app-server Codex's rollout and overwrite
  `TranscriptPath` with a file the journal projection cannot read. It is
  gated on `start.Transcript is null`.
- `ArmCodexTurnProbe` samples `TranscriptPath`'s length as a rollout baseline;
  it is gated on `agent.Runtime.EmitsTerminalOutput`.

### Lifecycle

- **Daemon restart.** The daemon does not re-adopt a prior epoch's children:
  `OrphanReaper` reaps them at boot. Their journals stay on disk and readable
  in the app (the rows are gone, so the workspace shows the session as ended)
  until the sweep.
- **Agent exit.** The file stays; the chat keeps rendering; `SessionEnded`
  comes from the snapshot; the composer reads `Ended`.
- **Launch failure before `PublishAgent`** (the factory threw after
  `journal.Open` ran): the orchestrator deletes the file **only if
  `CreatedFile` is true and `Complete` drained** — this launch made it,
  everything in it is this launch's, and no abandoned writer can recreate it. A
  rebind whose factory fails leaves the prior incarnation's bytes untouched; an
  abandoned writer leaves the file to the sweep. Nothing else deletes a journal: not agent exit, not worktree
  teardown (the file is outside the worktree), not a failure after publish.
- **Sweep.** `TranscriptJournalSweep` deletes `transcripts/*.jsonl` whose last
  write is older than 30 days and for which no PID record `agents/<same
  name>.json` exists, taking each file's path lock first (a file whose lock it
  cannot take is skipped this run). It is its own hosted singleton, a
  `BackgroundService` registered in `DaemonRunner` beside the other hosted
  services and built over a `TimeProvider`, with two entry points. `RunOnceAsync`
  is awaited by `DaemonRunner` **after** the awaited startup orphan reap and
  before the server connect — the reap is what removes a prior epoch's PID
  records, so a sweep before it would keep every journal whose agent died with
  the old daemon. `ExecuteAsync` then runs `RunOnceAsync` every 24 hours on a
  `PeriodicTimer` over the same `TimeProvider`, anchored at the first tick 24 h
  after start, until host shutdown cancels it. `RunOnceAsync` is single-flight
  (an `Interlocked` guard, the shape `ReapOrphansOnceAsync` uses, so a slow
  sweep and the next tick never overlap) and **never throws**: each file's
  metadata read, lock attempt and delete is its own try/catch logged at
  Warning, and an outer catch guards enumeration itself — a sweep fault can
  neither block the daemon's connect nor end the periodic loop. So 30 days is
  a retention policy rather than a restart-only cleanup. A record the reap left
  in place (a still-live or quarantined child) keeps its journal.

### Faults

- Open failure: `IsOpen` stays false, `Record` no-ops, one Warning;
  `TranscriptPath` is null while `transcript_format` is still `"envelopes"`, so
  the app shows Waiting — the same phase a failed PTY discovery produces, and
  distinguishable from an older daemon by the format.
- Write failure later: one Warning, the journal latches off, the path stays set
  so the app keeps what it has. Forwarding is unaffected: the journal never
  touches the channel.
- Crash: the queued backlog is lost without a marker (above); the line being
  appended may be torn, and `JsonlTail` yields complete lines only, re-reading
  an unterminated tail once its newline lands, so the file stays readable.

## 2. Local input frame

### Frames (append-only)

`FrameType.SendText = 22` (one-shot request) and `FrameType.SendTextAck = 80`
(reply). Both carry JSON text like the permission frames, encoded and decoded
in `FrameCodec` beside them:

- `SendTextDto(string AgentId, string Text)` — `{"agent_id","text"}`.
- `SendTextAckDto(bool Ok, string? Reason, string? Error, string? Outcome = null)`
  — `Reason` is one of the coded refusals below (null when `Ok`), `Error` is
  human-readable detail, and `Outcome` says what an `Ok` did: `"delivered"`
  (the runtime admitted the text) or `"stopped"` (the text was a quit command
  and the agent was stopped instead). Null when not `Ok`.

Both in a new `InputIpcJsonContext` (snake_case). Advertised as `input/1` in
`LocalControlCapabilities.Current`, added in the same change as the new `case`
in `LocalControlServer` — the capability list is assembled beside the routing
switch and nothing is advertised without a live handler.

**Compatibility contract: a client never sends `SendText` to a daemon that
does not advertise `input/1`.** An older daemon cannot answer it: its
`FrameCodec.Decode` throws `InvalidDataException` on the unknown type before
routing, `HandleConnectionAsync` logs and closes, and the client sees EOF.
`SendTextAsync` maps EOF and any transport fault to `Ok = false, Reason =
"transport"`; that mapping, not an `Error` frame, is what the client tests pin.

### Handler

`AgentOrchestrator.HandleLocalSendTextAsync(string payload, Stream stream,
CancellationToken ct)` in `AgentOrchestrator.LocalIpc.cs`. Every outcome is an
ack, never an `Error` frame and never an escaped exception, so the composer can
word it. Checks run in this order, each answered before the next runs:

| `reason`              | when                                                                          |
|-----------------------|-------------------------------------------------------------------------------|
| `malformed`           | payload is not JSON, is not an object, or lacks `agent_id` or `text` (missing or null). Source-generated STJ leaves a missing member null, so this is an explicit structural check, the way `PermissionWire.IsPendingStructurallyValid` guards the permission frames |
| `text_empty`          | `text` is present but empty or whitespace                                     |
| `too_large`           | `text` over 256 KiB of UTF-8                                                  |
| `no_such_agent`       | id not in `_agents`                                                           |
| `protected_kind`      | a review or flow participant; `Error` carries the `ProtectionReason` text the read-only attach uses |
| `not_running`         | status is Starting, Completed or Failed                                       |
| `reaper_claimed`      | the delivery core refused because the reaper claimed the agent (the existing `SendInputDropReason` token) |
| `reaper_claimed_late` | the reaper's claim landed inside the delivery section (likewise the existing token) |
| `queue_full`          | the runtime refused to admit the text (its pending-turn queue is full or it is terminal) |
| `stop_failed`         | the text was a quit command and the owner's stop did not succeed             |
| `delivery_failed`     | the runtime threw anything else; `Error` carries the message                  |

The reason vocabulary is the server-origin `SendInputDropReason` set plus the
local-only refusals; no existing token is renamed. Private (locally spawned)
agents are accepted: a request on the owner's 0600 socket is the owner's, the
rule `kcap agent stop` already follows. The size cap exists because a single
JSON frame is one buffer where PTY input streams through the PTY's own flow
control; no prompt approaches it.

### Admission is explicit

`Ok = true` means **the runtime's own delivery task completed without a
fault** — or, for a quit command, that the agent was stopped (`Outcome` says
which). What that task's completion means is the runtime's existing contract,
unchanged by this slice:

| runtime               | `SendUserInputAsync` completes when                                        |
|-----------------------|----------------------------------------------------------------------------|
| ACP vendors           | the turn is queued for the single turn worker (immediately)                |
| Antigravity           | the turn is queued for the turn worker (immediately)                       |
| Codex app-server      | the dispatcher has started or steered a turn with the text                 |
| Pi                    | the prompt command has been written to Pi's stdin                          |
| PTY (Claude, Codex)   | the bracketed paste has been written; the delayed Enter is best-effort (skipped when the process has exited or the pipe closed) — the runtime's existing contract, and PTY is not on this frame in this slice |

Today the ACP and Antigravity runtimes refuse a full pending-turn queue
silently for a non-acknowledging caller (`EnqueueTurn` logs and returns a
completed task), so the server-origin path cannot tell a delivery from a drop —
a residual the code already notes as accepted. This slice makes refusal
explicit and typed:

- `IHostedAgentRuntime.SendUserInputAsync` and `SendUserInputAndWaitForWriteAsync`
  gain a documented contract: a runtime that will not queue or write the text
  fails with `InputNotAdmittedException` (new, `Services/`) — thrown
  synchronously by the non-acknowledging path, and carried by the acknowledging
  path's task in place of today's `InvalidOperationException`, so the borrowed
  server-origin round (which keeps using the wait-for-write variant) maps to
  the same reason. `AcpHostedAgentRuntime.EnqueueTurn` and
  `AntigravityHostedAgentRuntime.EnqueueTurn` do this on a full queue and on a
  completed channel; Antigravity keeps emitting its "message not delivered"
  system note first.
- `CodexTurnInputDispatcher.EnqueueAsync` has the same gap in a different
  shape: it enqueues unconditionally even after `FaultAll` has set `_faulted`,
  and the dispatch paths then refuse to settle the item, so a send that races
  teardown stays pending forever. It refuses under its `_gate` — the lock
  `FaultAll` takes — by throwing `InputNotAdmittedException` once `_faulted`
  is set, so an enqueue and a fault cannot interleave; the runtime's
  `SendUserInputAsync` lets it propagate. An item enqueued before the fault is
  faulted by `FaultAll` as today.
- Pi and PTY admit by writing and need no change.

The shared core (below) maps `InputNotAdmittedException` to `queue_full` for
both callers. The server-origin caller reports it through
`ReportInputDroppedAsync` with a new `SendInputDropReason.QueueFull =
"queue_full"`: the server's `SendInputRejected` treats the reason as opaque
text — it logs it and records it against the dispatch — so the new token needs
no server change.

**The delivery core's own behaviour does not change**, for either caller. It
holds `BorrowedSnapshotGate` for the whole delivery, uses
`SendUserInputAndWaitForWriteAsync` for a borrowed round exactly as today (the
server-origin path's review and flow participants are protected kinds, and they
keep entering the core), awaits the runtime's task to completion, and runs the
activity-clock, awaiting-input and status-report side effects after that
completion, as it does now. Nothing in this slice bounds, times out or cancels
a delivery, and the gate is never released while a write is in flight — that
gate is what keeps a retry or a reap claim from racing the same write.

**The local handler answers when the core settles, and not before.** There is
no early acknowledgement, no admission signal and no timeout: a fault answers
`queue_full`, a reaper token or `delivery_failed`; completion answers `Ok`. The
handler holds the connection open for as long as that takes, and the client's
only bound is its own cancellation.

**A lost ack is an unknown outcome — as it is for a PTY paste today.** A
one-shot exchange can lose its answer: the client cancels while the core is
still delivering, or the delivery completes and the `SendTextAck` write fails
on a closing socket. The daemon delivers each frame it received exactly once,
under the gate, whatever became of the connection; the client that lost the
answer simply does not know which way it went. This slice does not add an
idempotency key or a result ledger to close that window: the PTY composer has
had the same window since it shipped (a paste is written and never
acknowledged), the transport is a local Unix socket where the window is
vanishingly small, and the machinery to close it — client-minted keys, a
per-agent replayable ledger, a store that outlives the workspace so a
torn-down tab can still replay — is a second protocol's worth of state for a
guarantee the PTY path does not make. The composer instead tells the truth:
after a transport failure or a cancelled wait it keeps the text and says
"delivery unconfirmed — check the chat before sending again", and the user
decides. Two clients sending different texts serialize on the gate and deliver
one turn each.

Two further consequences are deliberate:

- **A slow runtime looks slow.** ACP and Antigravity answer at once; Codex
  answers when its dispatcher has started or steered the turn (input during an
  active turn is steered, so this is usually prompt, and it can wait when a
  `turn/start` is pending); Pi answers when its stdin write completes, which is
  microseconds unless the child is wedged. A wedged child keeps the composer in
  `Sending` until the reaper or a stop faults the write, at which point the ack
  is `delivery_failed` and the composer says so from the ack's own text — it
  never depends on the runtime's best-effort transcript note.
- **Cancelling the wait abandons the observation, not the delivery.** If the
  client cancels (the tab is torn down) the connection closes; the core still
  runs to settlement under the gate, delivering once, and logs the outcome;
  the client is left with the unknown outcome above.

The borrowed-snapshot refresh (up to 30 s) never reaches the local handler: it
applies only to borrowed reviewers, which `protected_kind` refuses before the
core runs.

### Delivery core, shared not copied

`HandleSendInput`'s body from the quit-command check onward becomes
`DeliverInputAsync(AgentInstance agent, string text, string[]? attachmentIds)`
returning a delivery outcome — delivered, quit requested, or a drop reason —
and both callers use it. A local send therefore gets identical behaviour to a
web send:

- a `/quit`-style command on a non-PTY runtime is recognised by the core and
  reported as *quit requested* rather than sent as a prompt (PTY runtimes still
  receive the text verbatim). **Each caller then performs the stop on its own
  lane**: the server-origin caller routes it through
  `HandleUnsequencedStopAgent` exactly as today, keeping the invariant that
  server-origin launch and stop share one serial lane, and that path's
  `HandleStopAgent` refusal of private agents stays; the local caller calls
  `StopAgentCoreAsync(agent)` directly — the owner's stop, the one `kcap agent
  stop` uses, which private agents are eligible for — and acks `Ok` with
  `Outcome = "stopped"`, or `stop_failed` when the core reports failure;
- the borrowed-snapshot gate serialises local and server inputs per agent;
- a reap-claimed agent refuses, early or late, with the existing tokens;
- a refused admission is `queue_full` for both callers;
- the activity clock advances and the awaiting-input wait clears **only on
  completion**, so a refused or failed text never drops the rail's needs-you
  pip;
- the post-send status report fires on completion;
- the Codex probe arms only for PTY Codex (§1).

The server-origin caller keeps reporting drops to the server through
`ReportInputDroppedAsync`; the local caller puts the same reason in the ack.

### Core client

`ILocalControlOps.SendTextAsync(string agentId, string text, CancellationToken
ct)` beside `StopAgentAsync`, one frame exchange per call, returning
`SendTextResult(bool Ok, string? Reason, string? Error, string? Outcome)` —
the ack's four members passed through verbatim; EOF and transport faults map to
`Ok = false, Reason = "transport", Outcome = null`. Unlike the other one-shot
exchanges it has **no reply timeout**: the ack arrives when the delivery
settles, and the caller's token is the only bound. The app reads `Outcome`
exactly once: `"stopped"` is the quit case; any other value on an `Ok`,
including one this build has never seen, is treated as delivered.

The frame is vendor-agnostic on purpose (`PtyHostedAgentRuntime.SendUserInputAsync`
is the bracketed-paste path the web already uses), but the app keeps PTY on the
attach's `Stdin` path in this slice so nothing changes for Claude and Codex.

## 3. Core: reading journal lines as chat

### One reader interface

`IChatTranscriptProjection` (Core, public) with the two members the tab already
calls on `TranscriptChatProjection`:

```csharp
public interface IChatTranscriptProjection {
    TranscriptContext CreateContext(string sessionId, string? agentId);
    IReadOnlyList<AcpEventEnvelope> Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context);
}
```

`TranscriptChatProjection` implements it unchanged. `ChatTabViewModel` and its
`TailLease` take the interface. No behaviour change for Claude or Codex.

### `EnvelopeJournalProjection`

Core, vendor-neutral, so outside `Harness/`. `Project` decodes the line as one
`AcpEventEnvelope` through `EnvelopeJournalFormat.TryRead` and returns it; a
line that does not decode throws, which the tab already catches per line and
logs once, the same as a malformed vendor record. Its context is a stateless
`TranscriptContext`: journal lines carry no ids to dedupe. Passthrough is the
whole rule (decision 7).

### `EnvelopeJournalFormat`

Core owns the line encoding both sides use: `string Write(AcpEventEnvelope)`
and `bool TryRead(string line, out AcpEventEnvelope)`, over
`CapacitorJsonContext.Default.AcpEventEnvelope`. The daemon writes with it, the
app reads with it, and one round-trip test pins the bytes.

`TryRead` decides structural validity itself rather than trusting the
deserializer: `AcpEventEnvelope` is a record struct whose `Kind` defaults to
`""`, so source-generated STJ happily produces a value from `{}` or from a
null `kind`. A line is valid only if it is a JSON object with a non-null,
non-empty `kind` and a `contract_version` this build supports; `{}`, a scalar,
an array, a missing, null or empty `kind`, an unsupported version, and
unparseable text all fail and take the per-line fault path. The supported set
mirrors the server's `AcpContractVersion` gate (today exactly `1`), for the
reason the server states: a future version may reuse a known `kind` with
changed field semantics, so a v2 line with a v1 kind must not render as v1. A
syntactically valid v1 envelope whose `kind` this build does not know is valid
— that is the additive-within-a-version rule the renderer already relies on.

### `TranscriptChat`

Gains `public static readonly IChatTranscriptProjection Journal` (an
`EnvelopeJournalProjection`). `For(vendor)` stays the PTY registry and keeps
returning null for vendors without a leaf.

### Wire: `transcript_format`

`AgentStatusDto` gains one trailing member after `AwaitingInput`:

- `string? TranscriptFormat = null`, serialized `transcript_format`, always
  emitted. A current daemon sends `TranscriptFormats.Vendor` (`"vendor"`) for a
  PTY runtime and `TranscriptFormats.Envelopes` (`"envelopes"`) for an
  envelope-emitting one, from the first snapshot the agent appears in; null can
  only come from an older daemon. The constants live beside `WorkLocationText`
  in `StatusIpc.cs`.

Serialization acceptance (extends `StatusIpcJsonTests`): old JSON without the
member deserializes to null; a value and null both serialize with the member
present, last, in the declared trailing order.

The workspace resolves the projection from the format: `"vendor"` →
`TranscriptChat.For(vendor)` (null for a vendor without a leaf, today's
`Unavailable`), `"envelopes"` → `TranscriptChat.Journal`, null → the
older-daemon phase (§4), any other value → `Unavailable` worded "Update the app
to view this session", so a future format never renders as garbage.

## 4. App: workspace and composer

### Chat for every session

`WorkspaceViewModel` builds `ChatTabViewModel` on the first dto for any vendor
(`presence.Where(p => p.Dto is not null).Take(1)`), no longer only the first
dto that passes `ShowsTerminal`. The projection is resolved from that dto's
`transcript_format` as §3 describes; because a current daemon carries the
format from the first snapshot, no later re-resolution is needed, and a null
format is the older-daemon case, not a race. The composer's input channel comes
from `has_terminal`. `ShowsTerminalTab` and the Terminal tab stay PTY-only.

`WorkspaceView.axaml` today gates three things on `ShowsTerminalTab`: the
`ChatTabButton`, the `ChatHost` (through a `MultiBinding` with `IsChatActive`)
and the `TerminalTabButton`/`TerminalHost`. The first two lose the gate
(`ChatTabButton` always visible, `ChatHost` visible on `IsChatActive` alone);
the terminal pair keeps it. The tab-strip `NoTerminalNote` binding is removed
(decision 8); `HostedHarnessCatalog.NoTerminalNote` itself stays, because
`TerminalTabViewModel` still publishes it as the hidden terminal pane's state.

`AgentPresence`, today a private nested record of `WorkspaceViewModel`, moves to
its own file as an internal record so the input channel below can observe it.

### One composer, two channels

`ChatTabViewModel` stops reading `TerminalTabViewModel` directly and takes a
`ChatInput`:

```csharp
public abstract class ChatInput : ReactiveObject, IDisposable {
    public abstract SendAvailability Availability { get; }
    public abstract bool CanAcceptText { get; }
    public abstract string Hint { get; }
    /// Completes when the channel considers the text committed: true to clear the composer.
    public abstract Task<bool> SendAsync(string text, CancellationToken ct);
    public abstract void Dispose();
}
```

`SendAsync` is where the two channels legitimately differ (see "Clear on ack"):
the terminal channel completes as soon as the paste is accepted, the frame
channel when the ack arrives.

Both implementations own long-lived subscriptions — the terminal one to the
terminal tab's availability and state, the frame one to the daemon client's
status and capabilities and to the agent's presence — so the input is
disposable and **`ChatTabViewModel` owns it**: it goes into the tab's
`_disposables`, and `TeardownAsync` cancels the lifetime token (ending any
in-flight send) before disposing it, in that order. After `Dispose` an input
raises nothing and sends nothing.

- `TerminalChatInput(TerminalTabViewModel)` wraps the terminal tab and
  reproduces today's PTY behaviour and wording exactly (`HintFor` moves here
  with its terminal-state inputs). Its `SendAsync` returns the accepted flag as
  an already-completed task and ignores the token, since the terminal path has
  nothing to cancel.
- `LocalFrameChatInput(agentId, IDaemonClientService, ILocalControlOps,
  IObservable<AgentPresence>)` sends `SendText` and derives its state from the
  daemon client: `Connecting` while not attached; `Unsupported` (one new
  `SendAvailability` member) when the attached daemon does not advertise
  `input/1`, hinted "Update the daemon to send messages from the app" — and it
  never sends in that state; `Ready` while the dto is Running; `Sending` from
  the send until its ack arrives, however long that takes, one at a time like
  the PTY gate; `Ended` once the session ends. A refused ack keeps the typed
  text and puts the coded reason in the hint ("agent is no longer running",
  "read-only participant", "the agent's input queue is full, try again
  shortly", and for `delivery_failed` the ack's own `Error` text); a transport
  failure or a cancelled wait keeps the text with the hint "delivery
  unconfirmed — check the chat before sending again" (§2). An `Ok` with
  `Outcome = "stopped"` clears the text
  and lets the session's ending, which the snapshot delivers moments later,
  speak for itself.

The composer's `ComposerHint`, `ShowsComposer` and `canSend` bindings read the
`ChatInput`. The read-only participant banner (derived from the dto's kind) is
unchanged and already vendor-neutral.

### Clear on ack

PTY clears the composer when the paste is accepted, because it never learns
more. The frame learns the outcome, so the text clears on `Ok` and stays
otherwise. `SendCommand` becomes `CreateFromTask` over `ChatInput.SendAsync`,
passing the tab's lifetime token, and on `true` clears `ComposerText` **only if
it still equals the snapshot that was sent** — the box stays editable while
`Sending`, and text typed or pasted during the round trip must not be erased.
`TeardownAsync` cancels the lifetime token, which cancels an in-flight send;
`LocalFrameChatInput` checks disposal before touching any state on completion,
so a late ack can never mutate a torn-down tab. `TerminalChatInput.SendAsync`
completes synchronously, so the PTY composer behaves exactly as today.

### The user's message comes from the transcript

No local echo, same as PTY: every non-PTY runtime emits the prompt as a
`user_message` envelope — Pi, ACP and Codex app-server today, Antigravity
through the worker change in §1 — so it appears through the journal.

### Older daemon

A dto with `transcript_format: null` can only come from a daemon that predates
the field. For a non-PTY dto (`has_terminal: false`) the chat's phase note
reads "Update the daemon to view this session" instead of Waiting forever; a
PTY dto with a null format takes today's vendor path unchanged, because an
older daemon's PTY agents are exactly what the app renders now. `input/1`
covers the composer independently.

### Unchanged

Rail and Home rows (they already flow), Chat as the default tab, the launch
auto-open, the PR pane (which starts working for these sessions because
`session_id` is now canonical; the work-context pane needs the
`WorkContextIds` change in §1 as well), vendor tiles (the monogram fallback for
kiro, pi and antigravity stays; brand marks are cosmetic and out of scope).

## 5. Compatibility matrix

| app     | daemon  | non-PTY session                                                                                     |
|---------|---------|-----------------------------------------------------------------------------------------------------|
| new     | new     | Chat renders from the journal; composer sends through `input/1`.                                    |
| new     | old     | `transcript_format` null with `has_terminal` false → "Update the daemon to view this session"; composer `Unsupported` with the same advice, and nothing is sent. |
| old     | new     | Unknown trailing member ignored; Chat is never built for these vendors, as today.                    |
| new     | newer   | Unknown `transcript_format` → `Unavailable`, "Update the app to view this session".                  |

PTY sessions are unaffected in every cell: a current daemon marks them
`"vendor"` and an older one null, and both take the path the app runs today;
the Terminal and Chat tabs, `Stdin` input and discovery are untouched.

## 6. Parity with the PTY path

What matches: a file on disk tailed from byte 0 by the same `JsonlTail`, so the
complete persisted history on open and after an app restart; the same
Waiting/Missing/Reading phases and per-line fault handling; shared-open for
Windows; when `Open` succeeds, path and session id present in the first
snapshot the agent appears in; the file outliving the agent; a relaunch under
the same agent id appending to the same file when its `Open` succeeds (the
app's view of that relaunch is the existing one, §1); no
redaction on either path (PTY files are vendor-written, envelopes are not
redacted before the server either); Chat as the default tab with the composer
on it only; one user turn per send, in the transcript, for every vendor
(Antigravity gains the synthesized turn the other three already emit); the
needs-you pip clearing when a send is admitted; the read-only participant rule; no attachments on either local path
yet; reviewers and flow participants on non-PTY vendors getting a read-only
chat as PTY reviewers do.

What differs, deliberately: kcap owns and sweeps the file (30 days) where the
vendor owns the PTY file; the path and session id are known at construction
rather than discovered by a poll; no vendor display rules; the composer answers
with coded acks instead of guessing, has no `Reattach` state (one exchange per
send) and adds `Unsupported`; the text clears on ack rather than on accept; two
web-path side effects (the post-send status report and the PTY-gated Codex
probe) ride the shared core where the PTY-local `Stdin` path does neither; a
refused admission is reported where the PTY path cannot observe one; an older
daemon gets an explicit message instead of PTY's indefinite Waiting. A lost
acknowledgement is an unknown outcome on both paths — the PTY paste has never
had one — and the composer says so rather than guessing.

What remains after this slice: permission and question cards render for PTY and
not yet for these vendors (§8).

## 7. Testing

Core (`test/Capacitor.Cli.Core.Tests.Unit/`):

- `EnvelopeJournalFormatTests`: round-trip pins exact bytes (snake_case, member
  order); `TryRead` rejects unparseable text, `{}`, a scalar, an array, a
  missing, null or empty `kind`, and `contract_version: 2` carrying a known
  display kind; it accepts a well-formed v1 envelope with an unknown future
  `kind`.
- `EnvelopeJournalProjectionTests`: passthrough of every kind, throw on a
  malformed line, stateless context.
- `SessionIdsTests`: a dashed GUID canonicalizes to its `N` form; an opaque id
  such as `sess-1` is returned unchanged; agreement with the server's
  `CanonicalSessionId.Normalize` on both shapes.
- `WorkContextIdsTests`: an opaque dashed id reaches the request path
  unchanged; a GUID reaches it in `N` form; dot-segment validation unchanged.
- `StatusIpcJsonTests`: `transcript_format` trailing, `"vendor"`,
  `"envelopes"`, null, and old JSON without it.
- `FrameCodecTests`: `SendText` / `SendTextAck` encode and decode.
- `LocalControlOpsTests`: `SendTextAsync` passes all four ack members through
  (`Outcome` included), maps EOF on the request to `Reason = "transport"`,
  waits past the other exchanges' reply timeout for a late ack, and closes the
  connection on the caller's cancellation.

Daemon (`test/Capacitor.Cli.Daemon.Tests.Unit/`):

- `AgentFileNamesTests`: `../x`, both separators, an absolute-looking id and a
  plain id all yield a fixed-length hex name; stable across calls; identical to
  the name `AgentPidRecordStore` used before the extraction.
- `TranscriptJournalTests`: header first line on open; `CreatedFile` true only
  when the file did not exist; a second open of the same agent id appends a
  second header and preserves every prior byte; ephemeral skipped; no handle
  held between writes (the file can be exclusively opened and deleted between
  two lines); `Open` returns with the file created and the header flushed
  before the writer has run, so a status snapshot taken immediately after
  publish points at an existing file with one line; a header write that fails
  leaves `IsOpen` false, starts no writer and makes `Record` a no-op; `Record`
  returns without blocking while the writer is stalled on a hung sink (a fake
  sink that never completes) and the caller's thread is never blocked;
  `TryWrite` on the full channel returns false (pinning `FullMode.Wait`), and
  with distinguishable preloaded lines the gap note lands between the last line
  before the loss and the first after it, with the right count, including when
  exactly one slot is free at the first post-gap `Record` (one item carries
  both); an item whose write hangs and is abandoned is named in the Warning
  together with the queued-item and pending-gap counts, and cancellation never
  stops the writer between a note and its envelope (a torn write is the crash
  model, pinned separately: a buffer cut after its note line renders the note
  and drops the torn envelope); `Complete`
  with a pending gap and a drained queue writes the note last, and with a full
  channel and a hung writer reports the pending gap in the abandonment
  Warning; `Complete` drains what is queued within the grace and reports
  `Drained`; `Complete` against a hung sink returns within the grace, reports
  not drained and logs the abandoned item, queued-item and gap counts; with a REAL
  `FileStream` on the host OS, a same-id `Open` while an abandoned writer holds
  the path lock misses its bound and stays `IsOpen` false, and once the writer
  is released its line is at the true end and a subsequent `Open` appends after
  it with every byte intact; the sweep and the failure-path delete skip a path
  whose lock they cannot take; the same agent id under two state directories
  takes two locks under the Core `PlatformPaths.Comparer`, so a hung writer in
  one never blocks the other's `Open`, and two spellings of one path under
  that comparer take one lock; abrupt disposal without `Complete` leaves a
  readable file containing exactly the lines the writer reached; a first append
  that fails after a good open latches after one Warning and counts what was
  queued.
- Per runtime (`AcpHostedAgentRuntimeTests`, `PiRpcHostedAgentRuntimeTests`,
  `AntigravityHostedAgentRuntimeTests`, `CodexForwardBufferTests`): a journal
  passed to the constructor records every accepted envelope in channel order —
  including under **forced concurrent writers** for Pi (pump vs send-time
  `user_message`) and Antigravity (worker vs queue-full notice), asserted by
  comparing the journal to the channel's dequeue order; an envelope evicted by
  `DropOldest` is still in the journal; an envelope written after channel
  completion is not; Codex ephemerals dropped from a full buffer are not
  journaled and canonical envelopes are journaled only after their write
  completes; a stalled buffer journals nothing further and the watchdog fires
  at the same threshold; a blocking write cancelled by shutdown and an emit
  after `Complete` are both not journaled and neither throws out of `Emit`; the
  initial prompt's `user_message` is journaled even though it is emitted before
  the factory returns; Antigravity emits exactly one `user_message` per
  admitted turn (initial and follow-up), ordered before that turn's first
  output under a forced race with the child's first line, none for a refused
  turn, and with the injected clock's timestamp so its bytes are pinned.
- `AgentStatusSnapshotTests`: `"envelopes"` format, journal path and canonical
  `session_id` for an envelope-sourced agent in the snapshot taken immediately
  after `PublishAgent`, before registration; `"vendor"` and null path for a PTY
  agent; an opaque ACP id reported unchanged.
- Orchestrator: `AgentStatusChanged` carries the canonical id for an
  envelope-sourced agent; `DetectSessionIdAsync` not started for an
  envelope-sourced Codex agent, so its `TranscriptPath` stays the journal;
  Codex probe not armed for it; a factory failure after `Open` deletes the
  journal only when `CreatedFile` is true and `Complete` drained, and leaves a
  pre-existing or abandoned-writer one intact;
  `LocalControlCapabilitiesTests` pins `input/1` beside its handler;
  `HandleLocalSendTextAsync` answers an ack for `{}`, a missing member, an
  explicit null member and invalid JSON, and for each remaining reason in the
  table, including both reaper tokens; delivery through a fake runtime;
  `InputNotAdmittedException` maps to `queue_full` on both callers, the
  server-origin caller reports it, and the awaiting-input wait is untouched —
  exercised through the REAL ACP and Antigravity runtimes with a full queue on
  both the plain send and, for a borrowed server-origin round, the
  wait-for-write path; a server-origin borrowed round still uses
  `SendUserInputAndWaitForWriteAsync` and its core does not complete before the
  write, unbounded, exactly as today; the local handler answers only when the
  core settles (a fake runtime whose task completes after a delay gets no ack
  before it, and a runtime whose task faults after returning gets
  `delivery_failed`, never `Ok`); two frames against a fake runtime whose first
  write stays pending deliver two turns in order, one each, once the write
  settles; a client that cancels its wait, and a client whose ack write faults,
  each still produce exactly one delivery;
  `CodexTurnInputDispatcher.EnqueueAsync` after
  `FaultAll` throws `InputNotAdmittedException`, and an enqueue racing
  `FaultAll` either faults its item or is refused, never left pending; a quit
  from the local frame stops a private agent through `StopAgentCoreAsync` and
  acks `Outcome = "stopped"`, a public one likewise, and a failed stop acks
  `stop_failed`; the server-origin quit still rides
  `HandleUnsequencedStopAgent`.
- `TranscriptJournalSweepTests`: age and PID-record rules joined by the shared
  name, nothing else deleted; the daemon-start sequence with an old journal and
  a prior-epoch PID record deletes the journal once the orphan reap has
  removed the record and keeps it while a still-live or quarantined record
  remains; a `FakeTimeProvider` advanced 24 h runs the sweep again without a
  restart; a directory enumeration fault and a per-file delete fault are each
  logged, neither escapes `RunOnceAsync`, and the next 24 h tick still runs; a
  tick arriving while a sweep is in progress is skipped.
- `JournalPathLocksTests`: acquire/release churn over many distinct paths
  leaves the map empty; a waiter blocked on a path keeps its entry alive until
  it too releases; a wait that times out and a wait that is cancelled each
  leave the map empty once the owner releases, and release nothing themselves;
  two spellings of one path share one entry.

App (`test/Capacitor.App.Tests.Unit/`):

- `WorkspaceViewModelTests`: Chat built for `has_terminal: false` with the
  journal projection; Terminal tab hidden; the existing "Chat is null for
  gemini/pi" pins inverted; a null-format non-PTY dto yields the older-daemon
  phase and a `"vendor"` PTY dto the vendor projection.
- `ChatTabViewModelTests`: a journal file renders user, assistant, system-note
  and tool rows; unknown format → `Unavailable` wording; older-daemon wording.
- `LocalFrameChatInputTests`: availability matrix including `Unsupported`
  (and that nothing is sent in it), `Sending` one-in-flight, `Ended`;
  clear-on-ack only when the text is unchanged, text edited during the round
  trip survives; a refusal keeps the text and sets the hint; an
  `Outcome = "stopped"` ack clears the text; a transport failure and a cancelled
  wait each keep the text and set the unconfirmed hint; cancellation via the
  lifetime token; a completion after teardown mutates nothing; `Dispose`
  detaches the daemon-status and presence subscriptions (no further property
  changes after a later emission).
- `TerminalChatInputTests`: today's `SendAvailability` states reproduced
  (existing terminal-tab composer tests re-pointed); `Dispose` detaches the
  terminal-tab subscription.
- `ChatTabViewModelTests` (teardown): `TeardownAsync` cancels an in-flight
  send before disposing the input, and the input is disposed exactly once.
- `WorkspaceViewSmokeTests`: the non-PTY layout renders the Chat tab button and
  the Chat host, no Terminal button, no tab-strip note.
- `WorkContextViewModelTests`: an opaque ACP session id is requested from the
  source unchanged.

Live: the reproduction from the investigation — a Pi and a Cursor agent on the
local daemon, instrumented app instance — before and after.

## 8. Out of scope

- **Permission and question cards for non-PTY vendors** — the second slice.
  Today `AcpInteractionBridge` and `CodexApprovalBridge` go server-only, the
  server has no hub method for a daemon to report a locally answered ACP
  interaction (PTY has `RespondToPermission`), and the app's card DTOs are
  Claude-shaped (no ACP `Options`, multi-select or free text). That slice
  routes these interactions through the `PermissionPromptBroker` first-wins
  race, extends the permission DTOs (`permission/2`), adds the server method
  and web clearing in kcap-server, and generalizes the cards. It closes
  AI-2197.
- Attachments in the composer (AI-2318; the frame's DTO takes a trailing
  field when it lands).
- Streaming partial text (ephemeral envelopes) — PTY has none either.
- Moving PTY input onto `SendText`.
- Brand marks for kiro, pi and antigravity.
- A `kcap agent send` CLI verb over the new frame.
- The SIGKILL exit and stop-as-failure mapping for Pi (AI-2644).
