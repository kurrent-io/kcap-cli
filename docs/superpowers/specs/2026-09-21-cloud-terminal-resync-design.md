# Automatic cloud terminal recovery

Status: independently reviewed by Claude; clean in round 6 of Capacitor flow
`97742ef86d9646f7aab416ee3f9f87e9`. Ready for staged implementation and its
qualification gates. This worktree contains no production changes; the diagnostic
regression intentionally fails on its baseline. An earlier attempt is reconciled below.

Issue: [kcap-cli #1022](https://github.com/kurrent-io/kcap-cli/issues/1022),
[AI-2962](https://linear.app/kurrent/issue/AI-2962).

## Outcome and milestones

Local desktop and `kcap agent attach` terminals keep receiving PTY output when
cloud delivery is slow or unavailable, regardless of launch origin. Memory stays
bounded. Cloud journal overflow automatically recovers the screen and retained
scrollback, then continues in order. Unlimited historical output is not promised.
The user selected automatic recovery; stopping the mirror is an intermediate or
legacy behavior, not completion of this issue.

| Milestone | Repository and deliverable | Dependencies / exit |
| --- | --- | --- |
| M0 | CLI nonblocking journal/scheduler plus server and viewer mirror-status support | No engine dependency; local regression and remote overflow-status tests pass |
| M1 | CLI: managed state/VT serializer plus published-daemon streaming spike | Parallel with M0; fidelity, allocation, latency, reference-type streaming and AOT gates before M2 contract freeze |
| M2 | Server: framed publication, bounded storage/replay, ownership and negotiation | Wire contract fixed; enable after M1 qualification |
| M3 | CLI desktop and server web/mobile: framed receivers, generation fences and recovery status | M2 contract and M1 fixtures; all three clients tested |
| M4 | CLI: enable automatic recovery and qualify end to end | M0–M3; deploy M2 before enabling M4 |

The user chose to RELEASE ONLY WITH AUTOMATIC SNAPSHOT RECOVERY. M0 is independent
to implement and test, not a standalone production rollout. Ship its nonblocking
fanout together with M4, after the server/web protocol is deployed and desktop/
mobile receiver changes are merged or released on their own trains; do not wait
for installed-client or app-store adoption. Keep #1022 open until M4 passes.
An updated stack must recover after server restart rather than permanently pause
a published incarnation. The legacy/M0 limitations below are compatibility and
intermediate-test behavior, not an approved interim release. A spec-only change is not a
separately merged implementation PR. #1023 and upgrading the desktop terminal
engine remain outside this work. Local attach's raw-tail replay is a separate
follow-up; its IPC format is unchanged.

Prior work exists on branch `capacitor/agent-2875d7df78ad44`, inspected at
`e81588ce`. Its `CloudTerminalSink` decouples output and handles reconnect/stop,
but recovery sends RIS followed by `TerminalOutputBuffer.GetAll()`, an arbitrary
raw tail. It does not satisfy this design's checkpoint/acknowledgment contract.
Before M0 implementation, port or adapt its useful isolation, lifecycle and test
work against the current baseline rather than create a competing sink. Replace
the raw-tail recovery behavior; do not merge the entire divergent branch.

## Evidence and scope

The orchestrator fans out locally, then awaits the shared cloud sender for hosted
agents. In the production regression, a gated send plus eight other agent IDs
fills the queue: the first local chunk arrives; the second times out. A temporary
nonblocking enqueue passes; it was reverted. Seven existing sender tests pass.
Daemon and server raw 2 MiB tails are not checkpoints: they can start inside a
control sequence and cannot restore missing modes or bytes never sent.

Both hosted and locally spawned nonprivate agents already publish. Preserve that
eligibility. `IsPrivate` is immutable per incarnation: private agents allocate no
mirror. No private-to-shared transition is added; a future transition requires a
known terminal baseline, not parsing from the middle of a running stream.

Web and mobile use browser xterm; mobile is in `kcap-server/src/Capacitor.Mobile`
and is read-only. Desktop uses XTerm.NET 1.2.0, special-key input and viewport
reports. Preserve input and sizing policy. The headless model sends no replies or
input into the PTY.

## Wire checkpoint and engine qualification

The checkpoint is **UTF-8 VT reconstruction bytes**, dimensions, profile version
and stream position, not an engine-private dump. An updated viewer creates a
fresh surface and decoder, plays the checkpoint and applies subsequent ordered
deltas. Writing RIS into a possibly unfinished OSC is not an adequate reset.
Desktop needs receiver changes, not a package upgrade or private-state importer.

XTerm.NET is a starting candidate, not a qualified serializer. M1 requires
explicit read-only export of both buffers and semantic state needed to generate
VT. No reflection. Any extension uses a distinct package/assembly identity
referenced only by the daemon, avoiding a transitive desktop upgrade through
central package versions. Capacitor maintainers own the extension and version
pin. Before distribution M1 records the exact upstream commit, license/notice
requirements, patch list and upstream contribution plan. Publish CLI/daemon
trim- and AOT-clean, and record artifact growth; added daemon artifact <=10 MiB.
A failed qualification requires revising the design before enabling M2/M4.

### Complete-token cloud profile

Raw PTY bytes reach local terminals unchanged. Framed cloud output passes through
one bounded VT tokenizer/UTF-8 decoder before both headless state and journal.
Publish only complete UTF-8 characters and control tokens. Record boundaries are
parser-ground boundaries. An unfinished OSC/DCS/CSI or UTF-8 character remains
solely in the tokenizer; checkpoint the last completed record even when the PTY
is quiet inside a token. On completion, publish the token as the next record.
There is no parser-state import, indefinite wait for ground, or guessed prefix.
The tokenizer implements the engine's escape/cancellation grammar and is tested
at every split point; it is not a regex filter.

Canonicalize accepted tokens before BOTH model and journal: 7-bit ESC introducers,
ESC-backslash as string terminator, normalized supported parameter/intermediate
forms, and whole-token omission for cancelled/malformed forms. The manifest pins
C1 decoding, CAN/SUB, embedded ESC, BEL terminators and DEL handling. Embedded
executable controls are emitted as explicit canonical operations in their observed
order when admitted by the profile. Parser parity is tested across all three
viewer versions; matching only the daemon grammar is insufficient.

Control storage is at most 64 KiB including the canonical introducer/terminator. On overflow, discard
the entire token and scan in finite discard state until its grammatical
terminator/cancellation. Never publish a truncated prefix. Invalid UTF-8 has the
same deterministic replacement before model and journal. Strip graphics
protocols, query/reply-producing controls, clipboard operations, notifications
and BEL from live and replayed cloud output. Strip DECSET 2026 delimiters so an
open synchronization block cannot freeze a recovering mirror. These operations
remain unchanged locally. Fidelity is defined against uninterrupted playback of
this explicit cloud profile, not raw bytes containing omitted operations.

Omit sixel, Kitty and iTerm image payloads and their graphics-specific cursor
effects; retain subsequent ordinary text/cursor commands. Ignore shell-integration
and working-directory metadata and unsupported private controls. Show an
unsupported-content indicator. Title is bounded, idempotently restored state.
OSC 8 hyperlink IDs/targets are interned within a charged pool. Ignore an entire
oversized title/link/control operation consistently before model and journal.

The required serialized set includes:
- Normal and alternate screens, retained history, text, SGR attributes,
  RGB/indexed colours, hyperlinks, cell widths and palette/default colours.
- Soft-wrap flags, pending wrap, cursor position/style/visibility, saved cursor
  and attributes, margins, origin/insert/autowrap/reverse-wrap modes.
- Tab stops, G0–G3 and GL/GR shifts, last printed character for REP, and
  alternate-screen transition semantics.
- Supported input modes (cursor keys, keypad, bracketed paste, mouse/focus,
  keyboard flags), restored without emitting input or replies. Unsupported input
  protocols add no new remote input path.

M1 commits a versioned feature/width manifest for actual desktop/web/mobile engine
versions. Width, combining behavior and reflow must agree on admitted features,
including emoji/ZWJ. Absolute cursor moves alone cannot fix width mismatch.
Prove parity or define deterministic normalization before model and all viewers;
never silently mark a failed feature supported. Do not advertise the capability
without the manifest and passing fixtures.

For each viewer engine compare uninterrupted cloud-profile playback against
reset + checkpoint + suffix: cells/attributes/widths/links, both buffers, retained
history, cursor/saved cursor, palette, modes and wrap metadata. Distinguishing
suffixes include REP, saved-cursor restore, alternate exit, text and resize/reflow.
Use recorded Claude Code and Codex TUI captures plus synthetic cases, checkpoint
at every raw byte in sampled ranges, and explain every omission by the profile.

## Daemon ownership, CPU and memory

Each admitted nonprivate PTY incarnation owns a tokenizer, model, journal, epoch
and cursor. Capture begins before its first output even if cloud is disconnected.
Private agents allocate none.

Local replay-plus-fanout remains atomic under `SinksLock`. Then feed the mirror
synchronously in bounded slices without awaiting transport, capacity, retries,
encoding or acks. This spends local CPU; arbitrary-rate input cannot be processed
at zero cost. Bound engine work by retained geometry/history rather than numeric
CSI counts. Huge REP must compute the same retained result using bulk operations,
not loop that count or incorrectly clamp scrolling semantics. The same applies
to IL/DL/SU/SD/ECH and erase/DECALN floods.

Use immutable/copy-on-write lines and a bounded root table for capture. Serialize
outside the feed boundary; never copy the whole text store under the local-sink
lock. The successful runtime resize and its dimension record share the output
observation ordering gate; mirror application follows that order. This defines
observed order, not when the child generated bytes already in the kernel.
No ordering lock spans encoding or network work.

| Resource | Hard default / policy |
| --- | --- |
| Journal | Framed: 2 MiB/agent, 16 MiB/daemon; M0 legacy: 8 MiB/agent, 64 MiB/daemon; record overhead included |
| Live state | 16 MiB per agent, 256 MiB per daemon; incremental charged reservations |
| Geometry / cells | 500×200; 262,144 cells across both screens and history |
| Variable text | 64 UTF-8 bytes per grapheme; charged shared string/link pool, not a 64-byte array per cell |
| Tokenizer | 64 KiB token storage and finite counters/discard state |
| Capture/encode | Two global permits; each reserves 16 MiB COW retention, 32 MiB encoding, 4 MiB scratch |
| Checkpoint | 32 MiB encoded cap; stream into charged pooled segments, no second whole/base64 copy |
| Transport | 4 MiB unacknowledged raw bytes/daemon, 1 MiB/agent; 16 MiB charged total for wire/encoded/queued copies |
| Scheduling | One ready token per admitted agent; metadata charged to its budget |

Charge object overhead, retained capacities and live/in-flight ownership, not just
payload bytes. Framed mirror retention is <=392 MiB (256 + 16 + 2×52 + 16), plus fixed
bounded bookkeeping. The daemon-wide journal ledger permits at most 64 MiB if
legacy and framed lanes overlap during transition, making that mixed maximum
440 MiB. Do not give each mode an independently additive journal budget. Existing local replay remains 2 MiB per agent and is
separately included in whole-daemon measurements. These are qualification gates,
not claims about today's engine. Pooled storage remains charged until released.

Trim history before screen cells. Normalize excess combining content away without
changing the base character/width before model and journal. Reserve incrementally: minimum bytes are the charged two-screen storage at the
current dimensions plus fixed model/tokenizer/tables from the M1 allocation
ledger, not 16 MiB for every small terminal. Reserve each later allocation before
performing it. No other agent may steal that minimum. At growth pressure, trim
this agent's history first. If its screen/semantic minimum still cannot fit
(for example a resize to 500×200), apply the local resize normally, mark this
incarnation capacity-unavailable and release/stop its cloud model. Never stall
local output, reorder resize, or resume from an empty model midstream. Admission
and model-resource exhaustion are distinct from journal overflow; automatic
cloud-overflow recovery covers retained valid models.

For a server-launched PTY, reserve the initial minimum before spawning and fail
launch with a capacity reason if it cannot fit, because cloud may be its only UI.
For a locally started PTY, local use continues with explicit capacity-unavailable
status. Later growth failure in either case leaves the agent running with that
visible status; no automatic restoration is promised without a known baseline.
Test both hosted admission failure and a full-pool maximum resize.

When the total journal budget binds, discard the largest retained journal,
oldest last-selected as tie-break, and mark it for checkpoint outside the budget
lock. One pending flag coalesces further work. Capture permits are FIFO; an agent
rejoins the back after each transfer, preventing indefinite permit retention.
Alternate bounded chunks between ready transfers and limit delta bytes per turn.
No task per PTY chunk and no lock spanning transport.

## Ordered protocol and recovery progress

The additive `terminal-mirror-v1` contract carries agent ID, registry-approved
incarnation, epoch and revision. Lifecycle registration establishes the accepted
incarnation; a random client ID cannot supersede it. Check ownership at each
operation and again at its atomic acceptance/commit boundary.

Revision is a uint64 record sequence, increasing across epochs in one incarnation.
An immutable record is at most 64 KiB of canonical complete-token output or a
fixed-size resize (header <=128 bytes). Assign its ID
and boundaries once; batching never changes them. Large records may span
transport fragments, but acceptance and acks occur only on complete records.
Drop a duplicate batch prefix and accept a contiguous suffix; return the current
cursor on a gap. Conflicting bytes for a retained ID are a protocol error.
Counters never wrap. Epoch 0 is an empty baseline at registered initial size.

1. Streaming sends bounded batches after the acknowledged revision.
2. Needs-checkpoint schedules capture through completed revision R in a new epoch.
3. Installing uploads indexed chunks under an immutable transfer ID with epoch,
   R, dimensions, total length and digest. Staging is invisible until commit.
4. Resuming **always commits the in-flight checkpoint even if post-R deltas
   overflow**. Replay contiguous retained deltas, or queue a newer checkpoint.

New output never cancels the active snapshot. Ownership loss, incarnation teardown,
permanent validation failure or shutdown can cancel it. Duplicate chunks/commits
are idempotent; keep the last committed transfer identity after freeing staging.
Resolve lost acks by querying/retrying that transfer. Reject uncommitted-epoch
deltas and stale epochs. Quiet agents recover without another PTY read.

If transfers repeatedly outlast the journal, reduce history in the next snapshot
to zero if needed. Keep both screens and semantic state: a valid minimum snapshot
can exceed 2 MiB. Under sustained overload deliver periodic committed snapshots
with increasing revisions. Do not promise lossless deltas or a bandwidth-independent
lag bound. Positive per-agent bandwidth b and snapshot size S imply time
proportional to S/b plus scheduling/commit latency. After output subsides, converge
automatically to the final revision. Zero bandwidth implies no cloud progress.

Reconnect negotiates again, rebinds ownership and queries committed/staged state.
Resume retained records/chunks or checkpoint. Storage is ephemeral: server restart
or routing to an instance without state reports missing-state; a SignalR backplane
does not imply terminal-storage replication. Do not blindly replay the raw buffer.
Stop releases local work promptly; cloud drain has a separate 2-second best-effort
budget. This is not a guarantee of delivering a final screen on a blocked link.
At the deadline revoke publication and release the mirror without cancelling
another agent's shared send. Observe outstanding tasks; shutdown owns transport
cancellation. Teardown discards work by incarnation, not reused string agent ID.

## Transport and server storage

Use one ordered client-to-server SignalR upload stream per daemon connection,
with a single reader applying frames in order. Frames multiplex agent records,
transfer begin/chunks/commit and lifecycle fences. Server emits cumulative
accepted-record cursors and transfer byte-offset/commit acknowledgments; do not
await a network round trip per fragment. Upload the next bounded frame while
window credit remains. This avoids relying on ordering of concurrent hub calls.

Raw fragments <=12 KiB, serialized frames <=24 KiB; a record <=64 KiB plus its
header is assembled before acceptance. The unacknowledged window has ceilings of 1 MiB per agent and 4 MiB per daemon,
including checkpoint and delta traffic. Those ceilings are not fill targets.
Start with 65 KiB globally. After each observation interval use
min(4 MiB, max(65 KiB, acknowledged raw bytes/second × max(250 ms, 2×baseline RTT))).
Use a conservative moving rate estimate, limit upward growth to 2× per baseline
RTT, and shrink the target immediately when observed RTT inflates or progress
falls. Stop offering while outstanding bytes exceed the new target; already
offered bytes cannot be recalled. Divide credit fairly among ready agents within
the 1 MiB ceiling. Window/rate accounting uses unencoded payload plus record
headers, not base64/serialized wire size. Admit each delta record by reserving
its ENTIRE payload plus <=128-byte header before sending the first fragment.
The 65 KiB floor accommodates one maximal record. Interleave fragments only
among fully admitted records; retain their credit reservation through completion,
including after a target shrink. FIFO whole-record admission across agents must
not divide scarce credit among unfinished, unadmitted records. Checkpoint
fragments reserve their own byte credit, released by staging-offset acks.
Whole-record and checkpoint-fragment admissions share ONE FIFO: no fragment may
bypass a waiting record at its head. Each agent offers at most one admission
request at a time, so a transfer cannot flood this FIFO. Test
eight maximal-record producers with two active checkpoint transfers at
startup/floor and during window shrink; every
agent must progress without depending on another producer finishing its record. Retain
unacknowledged records/checkpoint segments until accepted or superseded. Charge
copies in the 16 MiB transport ledger rather than treating channel dequeue as
delivery. Global FIFO/round-robin scheduling interleaves agents per fragment.
Application upload channel capacity is one frame; server StreamBufferCapacity is
10 bounded frames. No server-side reorder buffer is needed for this single
ordered reader; old connection generations are rejected.

Send cumulative acknowledgments at least every 64 KiB or 20 ms, including
progress on staged snapshots. Publication advances the log and signals subscriber pumps; the reader never
waits for viewer I/O or credit. A failure
to reserve storage returns a capacity response immediately instead of blocking
the stream reader and hence other hub traffic. Window credit is released only
by accepted bytes/cursors; refusal reconciles credit for explicitly discarded
frames, without pretending a rejected record was committed.

Reader execution is bounded synchronous work per frame: in-memory registry
ownership lookup, budget checks, incremental digest update on <=12 KiB, and a
per-agent publication gate. Compute no whole-checkpoint digest at commit; commit
validates the accumulated digest/length and swaps a root reference in O(1).
Reclaim old segments outside the reader. Subscribe takes immutable references
under the same per-agent gate, then replays outside it; no global replay lock.
None of these paths await network, I/O, viewer work or a queue permit.

Store the latest cursor/transfer offset/error per admitted agent in a bounded
coalescing table, charged to server metadata. A separate single acknowledgment
pump PER CONNECTION sends bounded batches; the reader never awaits it. A slow
daemon cannot hold another connection's acknowledgment pump or credit. Daemon ack handlers only
update cursors/credit and signal scheduling, never serialize snapshots or send
network work on the receive callback. A per-agent/transfer protocol error fences
only that publication or returns needs-checkpoint and continues reading other
agents. Only an undecodable envelope or a connection-level violation ends the
shared stream. Test a gated ack pump/subscribe replay and one malformed agent
while other streams and control calls continue. A deliberately gated reader must
demonstrate the underlying SignalR backpressure; production tests then prove
none of these listed operations can introduce that gate.

The existing main hub allows 100 concurrent invocations and unlimited receive
size. The terminal path still validates fragment lengths before base64 decoding
and charges assembly/storage. Retention caps do not claim to bound arbitrary
hostile JSON allocations in that pre-existing shared transport. SignalR streaming
support and its AOT/serialization path must pass a real published-daemon test.

Control work has priority before offering another terminal frame. One frame may
already be flushing under SignalR's shared connection lock; do not claim control
can preempt a physically stalled socket. Keep that frame small and the feeder
bounded. A 5-second no-progress deadline, active only with outstanding frames, marks
cloud offline and stops offering new frames; it does not inject per-frame
cancellation into a shared write or start an overlapping stream. Renewed valid
acknowledgment progress resumes scheduling with a freshly reduced adaptive
window. If no progress returns, the existing connection liveness/reconnect
policy replaces the connection; do not create a second upload to probe it. Connection lifecycle owns transport cancellation;
reconnect replaces the upload only after the old publisher generation is fenced.
The current daemon uses SignalR client 10.0.12 with ordinary
WithAutomaticReconnect; no WithStatefulReconnect opt-in is present, and the
server hub has no AllowStatefulReconnects opt-in. Keep stateful reconnect disabled
for this protocol. Each reconnected logical connection gets a fresh publisher
generation; changing to stateful reconnect later requires a separate protocol
review. Use injected time/gates for deadlines, stop budgets and stage expiry.

Healthy transport qualification runs both 1 MiB and 8 MiB large-history baselines,
including refresh capture CPU: eight agents each at 256 KiB/s (2 MiB/s total),
150 ms round trip and >=10 MiB/s available payload bandwidth for 60 seconds.
Require no overflow/gap-triggered checkpoints and exactly-once accepted records.
Routine baseline compaction is expected at the server delta cap and is counted
separately: provision that overhead within the stated bandwidth and verify no
catch-up cycles. At 2 MiB/s the 150 ms bandwidth-delay product is about 307 KiB,
well within the 4 MiB window. Verify control call p99 adds <=50 ms relative to the
same healthy connection without terminal traffic. Repeat with steady 512 KiB/s
and 1 MiB/s links at 300 ms RTT and output above capacity: after adaptation,
added control latency must be <= max(250 ms, 2×baseline RTT) + one frame/link rate
+ 100 ms scheduling allowance, using the <=24 KiB serialized frame
(about 747 ms at 512 KiB/s), not the 12 KiB raw fragment. Include an abrupt
bandwidth reduction: first drain at most the previously offered window, then
meet the steady bound. Do not claim the adaptive policy can recall those bytes. At zero bandwidth report
offline rather than promising a control-response deadline.

Server hard per-stream caps are 32 MiB baseline, 64 MiB deltas and 32 MiB
replacement staging. For current baseline size S, the active delta allowance is
min(64 MiB, max(2 MiB, 2S)); request routine refresh at max(1.5 MiB, S).
This leaves headroom even at S=32 MiB. For stable-size baselines, routine snapshot
bytes are at most approximately the intervening delta bytes: <=2× total payload
amplification, excluding headers/retries/initialization. Baseline growth has a
one-off cost and reserves the next allowance before committing. A smaller new
allowance takes effect only with the new baseline, not by truncating an old
baseline's required prefix. Replay is bounded by baseline plus allowance
(<=3S when S>=1 MiB; <=S+2 MiB for smaller baselines). Charge ACTUAL baseline/delta bytes and metadata; do not
reserve 66 MiB for every stream. At transfer begin reserve the declared encoded
length plus assembly overhead and any net committed-baseline growth. Reject
over-limit declarations before allocation. Commit reclassifies the reservation
and releases superseded baseline/deltas atomically.

Global terminal ledger defaults are 1 GiB per tenant and 4 GiB per server process,
configured to fit deployment memory. Within those totals, keep 64 MiB/tenant and
256 MiB/process for transient replacement staging, and 64 MiB/tenant and
256 MiB/process for server-side viewer delivery; remaining space holds baselines,
deltas and metadata. Those are partitions, not additional budgets. Stage permits
are FIFO with at most one queued request per agent. If a transfer cannot reserve
staging/net growth, return capacity-retry with 1–30 second jittered backoff,
preserving the committed baseline plus recovering/capacity status. Release on
commit, cancel, unregister or two minutes of stage inactivity; active progress
refreshes expiry. No baseline or required delta prefix is freed to make room before commit.

Capacity qualification targets 128 streams per tenant and 1,024 per process at
1 MiB baselines plus 512 KiB deltas, across multiple daemons, including charged
metadata and concurrent replacements. These are synthetic targets, not measured
current workloads. Test both typical admission and worst-case cap refusals.
Reserved staging headroom prevents ordinary baselines consuming all replacement
space; it does not make unbounded baseline growth fit. At exhausted committed
capacity, recovery waits with visible capacity status until storage is released.
The positive-bandwidth progress claim also requires an available capacity grant.

At the size-dependent refresh threshold coalesce one request per agent. At its
active delta allowance refuse
further deltas with `needs_checkpoint`; the daemon uses its cursor/journal.
Allow one outstanding refresh per agent, at most one new request per second.
Match the existing registry lifetime: unregister and reconciliation removal clear
terminal data and release reservations. Do not introduce a new five-minute
ended-stream retention rule. A live daemon can rebuild state lost on restart;
an ended unregistered agent cannot. Preserve completed entries' existing policy
when they remain in the registry; no durable terminal archive is added.

Keep explicit transfer IDs/staging/commit. A single combined record stream would
still require bounded staging, disconnect cleanup and atomic publication.

## Viewers, authorization and rollout

Commit, acceptance and subscribe replay share one ordered publication boundary.
Frames carry incarnation/epoch/revision and subscription generation. Send reset
metadata, checkpoint fragments, contiguous records, then a replay/live boundary.
Queued JS/dispatcher work checks generation when executing. Replace the old
surface and decoder, cancel its callbacks, and install the checkpoint before
post-R records. Normalized checkpoints and deltas exclude one-shot effects.

Every framed terminal grid uses checkpoint dimensions and subsequent ordered
resize records exactly. Fit the outer viewport by scaling or scrolling, never by
independently resizing the terminal grid. Viewport reports still request a daemon
resize through the existing clamp; until its resize record arrives, keep the
old stream dimensions. Suppress resize feedback caused by installing the recorded
dimensions. Test different viewport sizes, snapshot replacement and clamp races.

Every subscription has a cursor over immutable baseline segments, then contiguous
delta records and the live tail. A separate subscriber pump sends only when that
viewer grants credit. The 2 MiB viewer/circuit limit bounds IN-FLIGHT delivery,
not total replay size. A 96 MiB replay may therefore pass through a 2 MiB window.
Credits return incrementally per fragment after parser/decoder consumption
(xterm write callback, desktop dispatcher feed completion, or equivalent mobile
JS acknowledgment), not merely network receipt or JS invocation submission.
Fragment byte credit is separate from the replay record cursor, which advances
only after the whole record. Partial decoder/parser state is bounded by the
qualified profile. Live publication advances the log
and signals pumps; it never pushes a complete replay or awaits viewer credit.
Each viewer has at most one fresh terminal surface within its 96 MiB state/render
budget; mobile renders one remote terminal per view.

Pins on the current immutable log reuse the stream's existing charge. If commit
supersedes a pinned root, charge its uniquely retained bytes to the viewer-delivery
partition; do not double-charge shared segments. A subscription may pin at most
32 MiB of UNCONSUMED superseded segments at or after its cursor for at most
60 seconds, always within the tenant/process viewer partition. Use segment-scoped
leases and release consumed segments as the cursor advances; retaining an entire
old root while only charging its suffix is forbidden. Network awaits retain only
a bounded fragment copy, not an already-consumed root. Test supersession when
a large replay is 95% consumed: charge only the remaining suffix. If any limit would be exceeded, invalidate lagging subscriptions
and release their pins before completing the replacement; do not delay publisher
progress. Subscriber cursors behind an evicted log restart from the newest
baseline. Superseded callbacks/acks are fenced by generation. Other reasons to
invalidate are authorization changes, malformed frames or lifecycle teardown,
not that replay totals exceed 2 MiB.

Blazor circuit caches and JS-interop buffers each obey the same <=2 MiB in-flight
credit gate and server viewer-delivery partition; no per-subscriber copy of a
whole checkpoint. Coalesce retries with 1–30 second backoff. Viewer requests never
directly trigger daemon capture; server refresh remains independently limited.
Viewer admission starts with 64 KiB credit, charged at actual allocated/in-flight
bytes plus metadata; it does not reserve a full 2 MiB per subscription. Grow
credit up to 2 MiB only as the partition permits and shrink when idle. Fair grant
scheduling keeps established viewers from monopolizing all growth credit.
If even initial admission cannot fit, return capacity status. Capacity
qualification targets 256 concurrent subscriptions per tenant and 2,048 per
process (two per target stream), at idle/low rates plus rotating active readers.
The separate 1 MiB/s replay gate applies to an active reader with sufficient
bandwidth/partition capacity, not every subscriber simultaneously.

Qualification subscribes web, desktop and mobile to a 32 MiB baseline plus its
full 64 MiB delta allowance while output continues. With 1 MiB/s effective
viewer transport AND parsing, 64 KiB/s new output, and adequate retention/capacity,
require reaching the live edge within 150 seconds of a stable baseline (including
allowed restarts on initial supersession). Use injected pacing and gates; a
zero-output 96 MiB replay must complete without queue-size invalidation. If the
viewer cannot consume faster than production, or newer baselines replace its
source before it can catch up within pin bounds, exact live convergence is
impossible: show lagging/recovering, keep bounded delivery and retry from the
latest checkpoint. When output slows sufficiently or becomes quiet it converges
without further PTY output. This condition is distinct from a design-induced
replay retry loop on an otherwise fast-enough viewer.

Keep Full visibility and publisher ownership checks. Revocation increments the
generation, detaches handlers and clears staged/rendered state. Old queued work
cannot render afterward. A fresh authorized subscription may legitimately contain
retained history preceding revocation; this is not per-byte historical deletion.

Show `Cloud mirror recovering` until the live edge. Capacity, unsupported-profile
and upgrade-required states persist until resolved or incarnation end. M0/legacy
overflow reports structured mirror status through an additive optional field on
agent updates, with a sticky badge/banner on remote web/mobile and desktop plus
daemon/local agent status. This is separate from the agent's running/completed
lifecycle; never mark a healthy agent failed to explain a mirror failure. M0's
implementation includes the small status-only server/viewer path; production
release waits for M4's snapshot path as selected above.
During disconnection show existing offline state; deliver the sticky overflow
reason upon rebind before any further terminal publication.

The M0 journal is 8 MiB/agent and 64 MiB total: test eight agents at 256 KiB/s
through a 25-second offline-before-publication interval (including metadata),
then replay at 150 ms RTT and >=10 MiB/s available payload bandwidth. The M0
legacy path must sustain >=4 MiB/s aggregate while producers continue at
2 MiB/s, drain fairly and avoid overflow. This is a measured qualification gate for the mixed-version legacy lane in
the combined M4 release.
This is a reference buffering allowance, not an exactly-once reconnect promise:
legacy SendAsync confirms local flush, not server acceptance. On a connection
generation change after any attempted publication, delivery is in doubt; pause
with status rather than resend or skip uncertain bytes. The same applies to a
failed attempted send with uncertain outcome. Offline-before-first-publication
has no such ambiguity and can replay on first readiness. M0 has no model and,
after a gap/overflow, remains paused until incarnation end; a binary upgrade
cannot reconstruct that run's missing state. Only an M4 incarnation whose model
has existed from its beginning can checkpoint after server capability returns. An older server/client cannot render the new field;
its legacy local/log warning and remote offline/blank behavior is an explicit
mixed-version limitation, not the updated-stack release acceptance target.

Negotiate viewer capability/profile every subscription. Old daemons retain legacy
delivery. Old viewers can read legacy streams; framed streams refuse them through
the subscription failure path with an upgrade-required server reason. Old
installed clients may only show their existing generic offline/blank state;
only updated clients are guaranteed the specific explanatory message. Never send framed
bytes to an old handler or attempt RIS-based down-conversion. Release server/web
together and desktop/mobile updates before enabling framed publication; installed
old clients receive no framed output.

Negotiate daemon capability on every reconnect. A framed incarnation reconnecting
to a legacy server pauses cloud publication with upgrade-required status, retains
its bounded model and automatically checkpoints when capability returns. Do not
append a fresh legacy stream to unknown server state. A new incarnation on an
old server may use bounded nonblocking legacy delivery from its beginning;
overflow stops it until an upgraded connection supports recovery or it ends.

## Required verification

- M0: real orchestrator regression, both launch origins, attach while congested,
  privacy, local ordering, stop/shutdown, and unrelated noisy agents. Reconcile
  and adapt the existing CloudTerminalSink attempt first. Remote overflow banners,
  in-doubt legacy-send behavior and the 25-second buffering/drain gate qualify
  the mixed-version legacy lane in the combined M4 release, not a standalone M0 rollout.
- M1: the fidelity comparator, real-agent corpus and every-byte splits around
  UTF-8, SGR, OSC/DCS/CSI, saved cursor, alt exit, wrap, REP, links and resize.
  Check identical normalization on uninterrupted and restored paths.
- Allocation: unterminated OSC/DCS, combining floods, image payloads, maximum
  geometry/history; assert charged budgets and no retained growth after GC in
  a ten-minute repeating workload. Test nine journals, incremental admission,
  hosted capacity refusal, full-pool maximum resize and server concurrency targets.
- Latency: eight agents at 256 KiB/s each for 60 seconds on the same supported
  reference machine, then a separate maximum-geometry control-flood corpus.
  Compare mirror-disabled, healthy and gated-cloud runs: added local p99 <=10 ms,
  added maximum <=50 ms; capture boundary <=2 ms. Record hardware and allocation.
  Also satisfy healthy throughput/RTT at both baseline sizes, adaptive-window
  constrained-link control gates, and reader/ack/error isolation tests above.
  These are controlled-benchmark thresholds, not arbitrary OS-pause guarantees.
  Failure blocks M1/M4 qualification and production release; M0 development/tests
  can still proceed independently.
- Below the journal limit deliver normalized records once and in order. Hold
  upload/commit while output/resize continue and overflow again; lose acks,
  repeat chunks/commits, replace incarnation, reconnect empty and recover quiet.
- Sustained overload: snapshot upload takes longer than filling the journal;
  require three increasing committed revisions within the computed transport/
  fairness bound, then final convergence after output stops. Test two active
  transfers plus waiting agents and show every admitted ready agent advances.
- Server/viewer caps, expired staging, process restart and mobile replay budgets.
  Exercise pings, launch and permission calls during gated terminal uploads.
- Subscribe/replay/live and revocation races in SignalR, in-process Blazor,
  desktop dispatcher and mobile JS. No stale generation writes or replayed effects.
- All old/new daemon/server/viewer combinations, downgrade and later automatic
  return to framed recovery. Verify recovery/status messages.
- Warning-free rebuilds, CLI/daemon NativeAOT publishes, artifact size gate and
  affected daemon/core/remote-client/server suites before release.

## Sources checked

- [SignalR client upload streaming](https://learn.microsoft.com/en-us/aspnet/core/signalr/streaming?view=aspnetcore-10.0):
  ordered upload primitive; application cursor/credit protocol remains our design.

- CLI: `AgentOrchestrator.cs`, `AgentOrchestrator.LocalIpc.cs`,
  `ServerConnection.cs`, `TerminalOutputSender.cs`.
- Desktop: `RemoteTerminalViewModel.cs`, `ServerConnectionService.cs`,
  `XtermTerminalSurface.cs`.
- Server: `Program.cs` hub limits, `CapacitorHub`, `AgentInstanceRegistry`,
  `TerminalBuffer`, `TerminalSubscriptionCache`; mobile `TerminalView` and
  `Resources/Raw/terminal/terminal-interop.js`.
- [XTerm.NET API](https://github.com/tomlm/XTerm.NET/blob/5987a9aa995eb287ef595e164daeb18e641411f7/src/XTerm.NET/Terminal.cs):
  candidate with private state, not a qualified checkpoint provider.
- [xterm.js headless overview](https://github.com/xtermjs/xterm.js#nodejs-support)
  and [serialization limitations](https://github.com/xtermjs/xterm.js/issues/4470):
  visible repaint alone does not establish correct continuation.
