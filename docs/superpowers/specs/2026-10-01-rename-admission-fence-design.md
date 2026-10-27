# Rename admission fence

Linear: AI-2704. Follow-up to the desktop daemon Settings rename (#886).

## Problem

A rename retires the old daemon's service (`ServiceVerify.RetireAsync` → bootout), which kills every
agent and evaluation the old daemon runs. Settings checks "zero active agents" from a status snapshot,
then awaits the profile write, the mutation lane and the CLI. Work admitted anywhere in that gap is
killed by the retirement. The snapshot also misses work the daemon has admitted but not yet published
(consent prompt, worktree creation, runtime handshake), local `kcap agent start` spawns, and
evaluations being prepared.

## What creates daemon-owned work

Exactly three entry points; everything else runs inside work they already admitted.

| Entry | Trigger | Admission point |
|---|---|---|
| `HandleLaunchAgentCore` | server `LaunchAgent` (default, review, review-flow, parked-reviewer resume), on the serial command lane | core entry |
| `HandleLocalSpawnAsync` | local `Spawn` frame, on the socket handler | handler entry |
| `EvalRunner.HandlePrepareAsync` | server `PrepareEval` | handler entry; the run then lives in `EvalContextCache` until finalize, cancel or the 30-minute idle sweep |

ACP reconnect and Antigravity per-turn children respawn inside an existing `AgentInstance`, which the
busy check below already counts. Startup reap and quarantine retry only kill; reconnect only
re-registers. The what's-done generator is a detached summary job whose loss is harmless.

## Design

### Daemon: one admission boundary

`AdmissionFence` (in-memory) owns a lock, an in-flight count, and the fence state.

- **Admission.** Each entry point above enters the fence first. Under the lock: a fenced daemon
  refuses; otherwise the in-flight count goes up, and comes down when the entry returns — by then an
  admitted agent is in `_agents`, or an admitted evaluation is in the cache.
- **Busy** = in flight > 0, or `EffectiveCount` > 0 (published and quarantined agents), or the
  evaluation cache is non-empty.
- **Acquire.** Under the same lock: busy refuses; otherwise the daemon becomes *fenced*. The idle check
  and the admission stop are one atomic step.
- **Refusals.** A server launch fails with `daemon_retiring: …` (a semantic rejection, so the server
  does not treat it as a transient full daemon); a local spawn gets an `Error` frame naming the rename;
  a prepare fails the way an unprepared run does today.
- A launch queued on the serial lane but not yet at core entry when the fence is acquired is refused
  when it reaches core entry. A launch already inside the consent prompt is in flight, so a rename
  during it is refused as busy.

### Control frame: acquire, then acknowledged commit

New frames, capability `fence/1`:

- `AdmissionFenceAcquire` (Text = `{ "expected_name": … }`) → `AdmissionFenceAck`
  (`{ ok, reason, pid, instance_id }`). `expected_name` must equal the configured name
  (`identity_mismatch`); busy answers `busy`. The ack carries the daemon's pid and instance id so the
  CLI can bind the fence to the process it will retire.
- On success the connection stays open and the fence is *held*. A held fence is released when the
  client closes the connection or the read fails. Unix-domain sockets close when the client process
  dies, so a crashed, timed-out or early-returning CLI releases its own fence and nothing else.
- `commit` on the same connection makes the fence *committed*. The daemon writes a retiring marker
  (`{ instance_id, committed_at }`) **atomically** — temp file, flush, rename into its state directory,
  the same pattern as its consent store — and answers `committed` only after the rename. The CLI boots
  out only after reading that answer.
- `abort` on the same connection, while it is still open, undoes a commit: the daemon deletes the
  marker, releases and answers `aborted`. It is idempotent — on a fence that was never committed it
  just releases — so the CLI sends it on any failure before bootout, including a commit whose answer it
  never read.
- A committed fence whose connection closes without `abort` stays committed — the CLI may have died
  mid-bootout, and the daemon cannot tell. It ends when the process exits, or when a fixed **2-minute**
  lease runs out. Bootout and stop confirmation run synchronously inside the CLI within its own retire
  budget (20s), so no retirement is still in progress after the lease; a daemon alive at that point
  was not retired, and reopening admission is correct. The daemon then deletes the marker.
- **Accepted exception.** If the CLI process dies after `commit` and before bootout, the connection
  breaks so `abort` cannot be delivered, or `aborted` does not arrive within the CLI's wait, the outcome
  is indeterminate: the daemon cannot tell any of these from a death during bootout, and the fence
  stays for at most the lease. This is a deliberate exception to "release on failure
  before retirement": releasing on a broken connection would reopen admission while a bootout may be in
  progress, which is the bug this fixes. The refusal tells the user to retry after a couple of minutes.
- **A replacement process inherits it.** On startup the daemon reads the marker before its host starts,
  so before the control socket, the server connection or evaluations can admit anything. A marker
  inside its lease starts the daemon committed-fenced until the lease ends; an expired one is deleted.
  A marker that exists but cannot be read or parsed fails closed: it counts as committed at the file's
  modification time (or now, if that is unreadable too). A crash before the rename leaves no marker,
  but then no `committed` answer was sent and the CLI never boots out.
- The fence and the marker are separate from the consent policy, so a tray pause or any other consent
  rule is untouched. The marker lives in the old name's state directory, which the renamed daemon does
  not use.

### CLI: every refusal before commit

`InstallVerifiedAsync` with `--retire`:

1. New id's lock, viability (as today).
2. Old id's lock, held from here through stop confirmation (moved out of `RetireAsync`). Read the old
   unit: absent → plain install, no fence; unreadable / foreign profile → refuse (as today).
3. Query the old label. `Unknown` → `retire_reason=fence_unavailable`. Not loaded → no service-run
   daemon the bootout could kill; no fence. Loaded without a job pid (starting, or between instances)
   → `fence_unavailable`: fail closed rather than boot out a daemon that cannot be fenced.
4. Loaded with job pid P → connect to the old name's socket, hello, require `fence/1`, acquire:
   capability missing → `fence_unsupported` (fail closed: the running daemon predates the fence;
   restarting it updates it); unreachable / no reply → `fence_unavailable`; `busy` → `agents_active`;
   ack pid ≠ P → `fence_unavailable`.
5. Leftover-marker recovery and the target-free checks (as today). Their refusals close the
   connection, which releases the held fence.
6. `commit`, wait for `committed`, then bootout and stop confirmation. No answer, or any failure
   before bootout starts → `abort` (waiting briefly for `aborted`), then `fence_unavailable`, no
   bootout. Once bootout starts nothing releases the fence.

Every refusal exits 30 with `retire_reason=<token>` before commit. Before commit, nothing has changed
for the old daemon or the new name; leftover-marker recovery only reconciles a previous crashed
attempt's residue for the new id, exactly as every install already does before its own refusals.

### App

- `SettingsViewModel.RenameAsync`: `agents_active`, `fence_unsupported` and `fence_unavailable` (with
  `target_occupied` / `target_unknown`) restore the saved name, keep editing open and say nothing was
  changed, with the action: wait for the work to finish; restart the daemon; try again in a couple of
  minutes. All are emitted before bootout, so the old service is still installed and the restored name
  is the one it runs under.
- `DaemonMutationLane` records the old id as retired after every outcome except `cli_unsupported`, so a
  refusal that changed nothing still blocks the next rename with `daemon_renamed_restart_app`. Those
  "nothing changed" refusals must not mark it retired.
- Outcomes after commit are unchanged: the name stays saved and the app asks for a restart. Whether
  that matches the service state after a failed bootout is a pre-existing question this change does
  not alter.
- The Settings idle check stays as the fast path; the daemon fence is the guarantee.

## Tests

Deterministic, with barriers rather than eventual results:

- Daemon fence: a launch held at core entry makes acquire busy (launch wins); acquire first, then a
  queued launch reaches core entry and is refused (fence wins); a launch held in the consent gate keeps
  the daemon busy; same for a local spawn and for a prepare; a cached evaluation keeps it busy.
- Daemon frame: close while held releases; commit answers only after the marker is on disk; abort
  after commit deletes the marker and releases; close after commit without abort keeps it fenced; the
  lease lifts it and deletes the marker; a daemon starting with a live marker starts fenced, with an
  expired one starts open and deletes it, with an unreadable one starts fenced; `identity_mismatch`; ack
  carries pid and instance id; hello advertises `fence/1`.
- CLI: idle old daemon → acquire, commit acknowledged, bootout, success; busy, unsupported,
  unavailable, loaded-without-pid and pid mismatch refuse before commit with the old unit untouched; a
  target refusal after acquire releases the fence; no commit answer refuses without bootout; a failure
  after `committed` and before bootout sends `abort`; a commit whose answer is lost on a still-open
  connection is aborted and the daemon admits again; a commit stalled past the abort wait leaves the
  daemon fenced until the lease, and the CLI reports `fence_unavailable`; label not loaded retires without a fence.
- App: each "nothing changed" refusal restores the name and keeps editing open; it does not mark the id
  retired, so a second rename runs; a queued rename behind another mutation acquires only when it runs.
