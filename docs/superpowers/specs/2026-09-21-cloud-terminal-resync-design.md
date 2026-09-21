# Automatic cloud terminal recovery

Status: proposed design for review. Implementation has not started; the diagnostic
regression test intentionally fails against the current production code.

Issue: [kcap-cli #1022](https://github.com/kurrent-io/kcap-cli/issues/1022),
[AI-2962](https://linear.app/kurrent/issue/AI-2962).

## Outcome

Local desktop and `kcap agent attach` terminals must keep receiving PTY output when
cloud delivery is slow or unavailable, regardless of where the agent was launched.
Memory must remain bounded. When output outgrows the retained cloud backlog, the
remote terminal must recover automatically from a valid terminal checkpoint and
continue with subsequent output in order.

The user selected automatic snapshot/resynchronization over permanently pausing
the cloud mirror on overflow. Recovery restores the current screen and bounded
retained scrollback. It does not promise an archive of every historical redraw or
unlimited scrollback during an arbitrarily long outage.

The Unix PTY scheduling work in #1023 remains independent.

## Evidence

`AgentOrchestrator.ReadAgentOutputAsync` appends a chunk to its replay buffer and
local sinks, then awaits cloud enqueue for a server-launched agent. The shared
sender can therefore stop the next local chunk behind unrelated agents' output.

The regression in `AgentOrchestratorTerminalCongestionTests` runs that production
fanout with a local sink and the production `TerminalOutputSender`. A gated send
plus queued output from eight other agent IDs fills the cloud queue. The first
local chunk arrives; the second times out after two seconds. A temporary change
from awaited enqueue to nonblocking enqueue makes the same test pass. That
diagnostic production change was reverted: it isolates the cause but does not
provide safe recovery. The seven existing sender tests pass.

Both daemon and server currently retain a raw 2 MiB byte tail. Neither is a screen
checkpoint: its first byte can be inside an escape sequence, and missing earlier
cursor moves, modes or alternate-screen transitions cannot be reconstructed from
the tail. The server's `SendTerminalOutput` only appends and broadcasts; subscriber
replay cannot recover bytes the server never received.

## Approach and alternatives

Use a managed headless terminal state per PTY agent, a bounded per-agent output
journal, and an acknowledged checkpoint-and-delta transport. The state consumes
every PTY byte locally, even when the cloud falls behind. Dropping journal entries
then loses transport history, not the state needed to rebuild the remote screen.

A bounded disk spool only postpones overflow and introduces disk exhaustion and
write latency into this path. Raw-tail replay and a forced resize are also
insufficient: a repaint is application-dependent and does not reconstruct the
terminal's parser and modes. A Node helper using xterm.js is an alternative, but
would add a runtime/process prerequisite to the native daemon. Prefer a managed
engine with explicit snapshot support.

## Terminal checkpoint prerequisite

The desktop already uses XTerm.NET 1.2.0. Its public cells and buffers make it a
candidate engine, but that version does not expose a complete checkpoint API.
The active parser and input-handler state are private. Copying visible cell text
and a cursor would not make subsequent raw output safe.

The first implementation milestone is a managed checkpoint implementation and
round-trip qualification, before changing production fanout. Use XTerm.NET as the
starting candidate; expose the required state through an explicit engine API in
a pinned package build if the public API is insufficient. Do not use reflection
against private fields, silently approximate missing state, or upgrade the
desktop's terminal dependency as part of this fix. A required upstream/package
change is a dependency of daemon integration, not something the transport can
compensate for. If this candidate cannot meet the qualification, revise this
design before integrating an alternative runtime.

The checkpoint must restore enough state that feeding its subsequent raw bytes
produces the same supported terminal state as uninterrupted playback. This covers
normal and alternate buffers, retained scrollback, cell attributes and widths,
cursor and saved cursor, margins, wrapping, character sets, palette, and modes.
The checkpoint also accounts for incomplete UTF-8 and escape/control sequences.
A checkpoint cannot just wait indefinitely for a parser to return to ground.

Qualification compares interrupted/restored playback with uninterrupted playback
using an independent browser xterm renderer as well as the managed engine. Cases
include a checkpoint at every byte of multibyte text and ANSI sequences; active
SGR followed by plain text; alternate-screen exit; saved-cursor restore; wide and
combining characters; pending wrap; partial OSC/DCS; and resize during output.
Bound parser accumulation and graphics state too. Unsupported graphics must have
an explicit, tested omission policy; they must not consume unbounded memory or
make text continuation invalid.

The headless instance must never answer terminal queries into the PTY. It models
output only; the existing attached terminal remains responsible for replies and
input. Snapshotting must not cause clipboard, title, notification or other
one-shot effects to be executed again by a remote viewer.

## Daemon ownership and bounded buffering

Introduce an agent-owned terminal mirror that owns the headless state, ordered
output/resize revision, bounded delta journal, and delivery cursor. Its lifetime
matches the runtime incarnation, not a reused string agent ID.

The PTY loop preserves the existing atomic local replay-plus-fanout boundary. It
then advances terminal state and offers cloud work without awaiting transport,
queue space, retry delay, checkpoint upload or acknowledgment. Apply the same
policy to locally and remotely launched agents. Private agents never publish
terminal content.

Dimension changes enter the same terminal-state order as output. A snapshot
records its dimensions and revision together. Feed state incrementally; copy a
checkpoint under a short state boundary and serialize the immutable copy outside
it. Neither the local-sink lock nor the state lock may span network work.

Start with a 2 MiB delta journal per agent and a 16 MiB aggregate delta budget.
Count actual retained bytes, not just chunks. The scheduler holds one ready token
per agent, and at most one immutable in-flight checkpoint per agent. Geometry is
bounded by the existing 500-column/200-row contract. Bound scrollback, cell
content, parser strings and encoded checkpoints as well: at most 262,144 cells
across the two screens and history, 64 UTF-8 bytes per cell's grapheme content,
64 KiB per accumulated control string, and 32 MiB per encoded checkpoint. Exclude
image payloads from the first checkpoint format and restore a visible placeholder
for retained image cells. Preserve text positioning around that placeholder.
Trim oldest scrollback first; never trim the active screen to admit history.
These are proposed hard limits, to be checked against actual engine allocations
during qualification. Revise the design if the engine cannot enforce them.
The total memory calculation must include both live state and in-flight copies.

On journal overflow, discard superseded deltas and mark that agent as needing a
checkpoint. Further output keeps advancing its bounded local state and coalesces
the pending cloud work. Use a fair ready-agent scheduler so one verbose agent
cannot consume every turn. A blocked shared transport can delay cloud mirrors,
but it cannot delay local fanout or create one waiting task per chunk.

## Ordered recovery protocol

Add an additive, versioned terminal-mirror capability and hub methods. Preserve
the legacy method for old daemons. New messages carry agent ID, runtime
incarnation, stream epoch and monotonically increasing revision. Output and
resize records share that order.

The state machine is:

1. **Streaming:** send bounded batches after the server's acknowledged revision.
   The server deduplicates retries and rejects a gap instead of appending it.
2. **Needs checkpoint:** entered after overflow, a missing server stream, or an
   unrecoverable delivery gap. The PTY continues locally. Capture state through
   revision R and advance the stream epoch.
3. **Installing checkpoint:** upload bounded chunks under a transfer ID. The
   server stages one transfer per agent with a size cap and expiry. Nothing is
   visible until a commit validates completeness and current ownership.
4. **Resuming:** after checkpoint commit is acknowledged, send deltas strictly
   after R. If those deltas overflowed while the snapshot was in flight, capture
   a newer checkpoint instead. Recovery must not depend on another PTY chunk
   arriving; a quiet agent with pending work is still scheduled.

A committed checkpoint replaces the server baseline atomically. A lost commit
acknowledgment is retried idempotently using the same epoch/transfer ID. A stale
batch or stale checkpoint cannot overwrite a newer epoch. Connection ownership
is checked on every upload, batch and commit, including after daemon rebind.
Snapshot acquisition does not remove post-R bytes until they are acknowledged or
superseded by a newer checkpoint.

Reconnect re-establishes the current owner and asks the server for its cursor. If
the server has retained that cursor, continue from it. If its process restarted
or its state is absent, install a fresh checkpoint. Merely reconnecting does not
blindly append a second copy of the daemon replay buffer.

On agent stop, release local read/capture work promptly. Cloud draining has a
separate bounded shutdown window and cancellation token; it cannot hold agent
cleanup open indefinitely. Discard scheduled work by incarnation at teardown so
a retry cannot target a later run with the same ID.

## Server storage and viewer delivery

The server stores a checkpoint baseline plus contiguous deltas, not an arbitrary
tail starting midway through terminal state. When its delta budget approaches
the limit it requests a newer checkpoint; it never silently evicts the prefix
required by its current baseline. Staged snapshot bytes are separately bounded.

Checkpoint commit, delta acceptance, and subscribe replay participate in one
ordered per-agent delivery path. A new subscriber gets dimensions, a reset and
checkpoint, then only deltas after that checkpoint. Existing subscribers receive
the same reset before post-checkpoint deltas. No old output can appear after the
reset, including output already queued in Blazor JS interop or an Avalonia
dispatcher.

Use a versioned viewer frame carrying reset/snapshot/delta and stream position.
Update the web terminal cache, remote desktop lane/view model, and mobile remote
terminal receiver together. A reset clears pending old writes and the UTF-8
decoder, restores the checkpoint at its recorded dimensions, and installs the
new revision before live deltas resume. Viewer-side gaps trigger a resubscribe.
Bound viewer queues; an overloaded viewer requests a new checkpoint instead of
growing its pending write list indefinitely.

Retain existing terminal authorization: daemon ownership on publication and the
Full visibility floor on subscription and replay. Revocation invalidates staged
viewer work as well as clearing its screen. A snapshot cannot bypass the current
subscription generation or make previously revoked bytes reappear.

## Compatibility and rollout

Land and deploy the additive server and viewer protocol before enabling it in
the daemon. Old daemons keep using the legacy stream. A new daemon negotiates
support before publishing framed output; do not mix legacy chunks into a framed
stream.

For an older server, retain nonblocking local operation and bounded legacy
delivery. If its backlog overflows, stop its cloud stream and report that server
support is needed for recovery; never resume arbitrary bytes after the gap.
That mixed-version fallback is explicit, not the selected steady-state behavior.
Automatic overflow recovery requires the updated daemon and server pair.

The local IPC format and terminal input path do not need to change. The desktop
terminal engine upgrade and #1023 are outside this work.

## Required verification

- The real orchestrator regression turns green with a blocked transport and
  output from other agent IDs; exercise both launch origins, attaching while
  already congested, private agents, local ordering, stop and daemon shutdown.
- Below the journal limit, disconnect and reconnect deliver byte-identical output
  once, in order. Above it, rendered state after recovery matches uninterrupted
  playback and later raw output remains correct.
- Hold snapshot upload/commit while producing output, resizing, overflowing
  again, cancelling, and replacing the runtime incarnation. Verify no local
  stall, duplicated bytes, stale reset, unbounded allocation or orphaned worker.
- Lose batch and checkpoint acknowledgments; repeat commits; reconnect to an
  empty server; restore a quiet agent without waiting for new output.
- Race subscribe/replay/live output and revoke access during checkpoint delivery
  in both SignalR and in-process Blazor paths. Verify decoder and UI write queues
  reject stale generations.
- Exercise old/new daemon, server and viewer combinations explicitly.
- Benchmark eight producing agents under a gated cloud transport. Measure local
  delivery latency, PTY draining, checkpoint CPU time and retained memory; confirm
  snapshot serialization itself does not introduce another local stall.
- Rebuild touched projects without warnings and publish the CLI and daemon with
  NativeAOT. Run the daemon, core, affected remote-client, and server test suites.

## Sources checked

- Current CLI fanout and sender: `AgentOrchestrator.cs`, `ServerConnection.cs`,
  `TerminalOutputSender.cs`.
- Current server path: `CapacitorHub.SendTerminalOutput` and
  `SubscribeToTerminal`, `AgentInstanceRegistry`, `TerminalBuffer`,
  `TerminalSubscriptionCache`.
- [Pinned XTerm.NET terminal API](https://github.com/tomlm/XTerm.NET/blob/5987a9aa995eb287ef595e164daeb18e641411f7/src/XTerm.NET/Terminal.cs)
  and its parser/input-handler source: candidate engine, not a qualified
  checkpoint provider.
- [xterm.js headless and serialization overview](https://github.com/xtermjs/xterm.js#nodejs-support):
  established reconstruction approach, with a runtime tradeoff for this daemon.
- [Upstream serialization completeness discussion](https://github.com/xtermjs/xterm.js/issues/4470):
  reason to test continuation semantics rather than equate a visible repaint
  with a complete terminal checkpoint.
