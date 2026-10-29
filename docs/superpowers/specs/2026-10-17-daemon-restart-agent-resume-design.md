# Restart the daemon under running agents, then resume them

## Problem

A daemon picks up a new binary only when it is idle: no hosted agent and no eval in flight. The
check is a passive wait that never refuses new work, so a daemon that is kept busy never upgrades.
In practice the operator stops launching hosted agents and waits for the fleet to drain just to get
the restart through, which is exactly the flow hosted agents were meant to enable.

Every restart path kills every agent. Graceful shutdown terminates each process, reports it to the
server as failed with `daemon_shutdown`, ends its session, removes its worktree and deletes its
records. The next daemon kills anything that survived and deletes any worktree it does not
recognise. A crash, an OOM kill or a reboot loses the fleet the same way, with uncommitted work in
the worktrees.

Nothing a successor could use to bring an agent back is on disk: the vendor session id, the launch
arguments, the worktree, the requester and the consent decision live only in the orchestrator's
memory.

## Goals

- A restart no longer waits for an idle daemon. When the binary changes, or an operator asks, the
  daemon stops each interactive hosted agent cleanly, restarts, and brings every one of them back
  by resuming its vendor session with a nudge prompt. No question is asked and no option is
  offered.
- Where the vendor has no resume, or a resume fails, kcap's own continue brings the agent back as
  a fresh session attached to the old session's work items and plans, carrying a recap.
- The same recovery runs on every boot, so a crash, an OOM kill, a reboot and the Windows
  `kcap update` then `kcap daemon restart` sequence all end with the fleet running again.
- A resumed agent keeps its agent id and its session id whenever the server still holds the entry
  live.
- No uncommitted work is lost: the worktree of a resumable agent survives the restart.
- The states are visible: the app and the CLI show an agent as resuming or interrupted, with the
  reason.

## Non-goals

- Keeping a running turn alive across the restart. That needs a process that outlives the daemon
  and owns the PTY or pipes; see *Alternatives*. The ledger built here is the groundwork it would
  share.
- Flow participants through the resume path. Their settlement is keyed by agent id and daemon
  epoch, and carrying one across incarnations needs a migration nobody has designed. They take the
  existing park path or end as today (§6).
- Detecting a binary change on Windows. The poll stays a no-op there, and `kcap update` keeps
  telling a Windows operator to run `kcap daemon restart`, which now resumes the fleet.
- Persisting pending permission or consent prompts. A resumed agent re-issues the tool call and the
  prompt is raised again.
- Changing how hooks and MCP servers pick up the binary on disk.

## Design overview

Three pieces, in the order they run:

1. **The resume ledger.** A durable per-agent record, written at launch and kept current, that
   holds everything a relaunch needs.
2. **Restart for resume.** The outgoing daemon stops agents gracefully, leaves their server entries
   live and their worktrees in place, and exits. The busy definition shrinks to the things a
   restart cannot carry.
3. **Resume on boot.** The successor reaps any surviving processes, claims the agents on connect,
   spares their worktrees, and relaunches each one through its vendor's resume, falling back to
   kcap continue.

Everything below applies to interactive hosted agents: launched by the server or a local client,
private or not, and not a flow participant.

## 1. The resume ledger

A new store beside the PID records: `<daemons>/<name>/resume/<sha256(agentId)>.json`, written
atomically (temp file then rename) like `AgentPidRecordStore`, unparseable files quarantined the
same way. The PID record stays what it is, a reap ledger; its semantics (prior-epoch records are
killed and deleted at boot) would fight resume intent, so the two are separate files.

Fields:

- `schema_version` (1), `agent_id`, `daemon_id`, `created_at`, `updated_at`.
- `vendor`, `transport` (`pty`, `app-server`, `acp`, `pi-rpc`, `muse`, `antigravity`).
- `session_id`: the kcap session id in canonical form, once known.
- `vendor_session`: what the vendor's resume takes. The session id for Claude, Codex, ACP vendors,
  Muse and Antigravity; the session file path as well for Pi (`get_state` reports `sessionFile`).
- The launch descriptor: prompt, model, effort, repo path, cwd, worktree path and ownership
  (owned, borrowed, external), git-relative cwd, sandbox, approval and permission settings, MCP
  config path, title, posture, `private`, launch origin (server or local), requester user id, and
  a reference to the consent decision that admitted the launch.
- `state`: `running`, `resuming` or `interrupted`; `attempts`; `last_error`;
  `predecessor_instance_id` once a restart has marked it.
- `inherited_agent_ids`: the agents a chain of kcap continues replaced, whose attachment directories
  the launch prompt still names; the boot sweep keeps them for as long as the entry lives.

Lifecycle:

- Written when a launch is admitted, before the process starts. Updated when the session id is
  learned: at the handshake for ACP, Pi, Codex app-server, Muse and Antigravity; at discovery for
  Claude and Codex PTY.
- Deleted when the agent ends normally or is stopped by a user.
- Readers tolerate unknown fields and missing optional ones, the additive convention the PID
  record already follows. A record whose `schema_version` is newer than the reader is reported as
  interrupted with reason `ledger_newer_than_daemon`, never guessed at.

Pinning ids up front. Claude accepts a caller-chosen `--session-id <uuid>`, so the launcher mints
one and the ledger is complete before the first turn; discovery stays as the fallback. Pi, Codex
app-server, Muse, Antigravity and the ACP vendors report their id at the handshake, before any
turn, so nothing is pinned for them. Codex PTY has no such flag and keeps discovery as its only
path.

## 2. Restart for resume

### 2.1 Busy

`RestartCoordinator.IsBusy` becomes: an eval in flight, or a launch between admission and its
ledger write, or a non-resumable flow participant whose round is in flight (bounded, §6).
Interactive hosted agents no longer make the daemon busy.

The restart modes keep their names and change their meaning only where agents are involved:

- `kcap daemon restart` (and `now` over IPC) proceeds with agents running: it stops them for
  resume at once and restarts. It no longer refuses while agents run. An operator who asks for a
  restart gets one now; the turn-boundary wait below belongs to the unattended trigger only.
- `--when-idle` keeps today's meaning: wait until no agents at all.
- `--force` skips the flow-participant wait (§6).

### 2.2 The turn-boundary wait

When the binary-change poll makes a restart due, the daemon waits until every interactive agent is
at a turn boundary, then fires. A boundary is `AgentActivityClock.AwaitingInput`, which gated
runtimes (ACP, Codex app-server, Muse, Pi, Antigravity) set on the falling turn edge and the Claude
and Codex PTY hooks relay through `/input-wait`, with the 8-second quiet heuristic as the PTY
fallback. The wait is bounded by `KCAP_RESTART_TURN_WAIT_SECONDS`, default 300: long enough for a
typical tool call to finish, short enough that an upgrade is never far away. New launches are
admitted during the wait; a launch mid-admission at the deadline is waited for, since it takes
seconds. The wait never applies to a stop the supervisor initiates (§2.5): a SIGTERM from launchd
or systemd comes with that supervisor's exit budget, which the graceful stop fits and a wait would
not.

### 2.3 Stopping an agent for resume

For each interactive agent, in parallel, with the shutdown budget widened to cover a graceful stop:

1. Mark the ledger `resuming` with the daemon's instance id as `predecessor_instance_id`.
2. Close local attach connections and stop the cloud sink. No new IPC frame: the app's reattach
   loop and `kcap agent attach` reconnect when the successor is up (§5).
3. Persist the terminal ring to `<sha256(agentId)>.ring` beside the ledger (2 MB cap, the ring's
   own size) so the first attach after the restart replays what the user was looking at.
4. Stop the process gracefully through the runtime's existing graceful path, then the hard path:
   the PTY quit sequence followed by SIGTERM to the group; stdin EOF for Codex app-server; the
   RPC or MSP terminal path for Pi and Muse; stdin EOF for ACP children. Five seconds of grace,
   then `ProcessTree.Kill`. The grace is what lets a vendor flush its session file and release its
   lock, which is the difference between the probe's SIGKILL results and a planned restart (§4.4).
5. Skip `ReportAgentsEndedForShutdownAsync` for these agents. Their server entries stay live, their
   sessions are not ended, nothing is unregistered. A terminal entry on the server cannot be
   revived, so this step is what makes same-id resume possible.
6. Skip worktree removal, attachment and MCP-config cleanup for them. Keep the PID record: the
   successor's reaper needs it to prove the old process is gone before a resume starts.

The daemon is not the only process that ends a PTY session. A Claude quit fires Claude's own
SessionEnd hook, and the `kcap watch` sidecar, which is not in the agent's process tree, posts a
session end within seconds of the agent pid dying. Both run the CLI binary with `KCAP_AGENT_ID` in
their environment, so both consult the resume ledger first: when the entry for that agent id is
`resuming`, the hook does not post the SessionEnd event and the watcher exits without posting its
end. The resumed process starts a fresh watcher through its own hooks, and the session continues
under the same id without an ended-then-reactivated flap, which would otherwise race the server's
orphan reaper against the successor's claim. A session that does reach the server as ended still
relinks on a `SessionStart` with the resume source, which is how a terminal user's `claude --resume`
already behaves; a server test pins it (§7).

Everything else about shutdown is unchanged, including private agents, which are stopped the same
way and resumed without server claims.

### 2.4 Stop versus restart

A restart keeps agents; a stop ends them. `kcap daemon stop` and `kcap daemon service stop` end
every hosted agent and delete its ledger entry, as they do today, so a daemon and its fleet can
still be stopped for good and the README's description of `stop` stays true. Both gain
`--keep-agents`, which stops the agents for resume instead and leaves the ledger `resuming`, for
an operator who needs the daemon down for a while without losing the fleet.

The intent travels over IPC. A new client-to-daemon frame, `Shutdown`, carries `end-agents` or
`keep-agents` plus whether the daemon should exit for a supervisor relaunch (`RestartRequested`)
or for good (0, which `KeepAlive SuccessfulExit=false` and `Restart=on-failure` do not relaunch).
The CLI sends it before any signal or `bootout`; the daemon acks when its agents are stopped and
exits. Only when the daemon does not answer does the CLI fall back to today's signal path, and in
that fallback it marks every ledger entry `interrupted` with reason `stopped_by_operator` itself,
so a daemon that was killed because it was wedged does not bring its fleet back as a crash on the
next start.

### 2.5 Supervisor-initiated stops

A SIGTERM the daemon did not ask for, from a logout, a reboot or an operator's `launchctl` or
`systemctl`, is treated as a restart: agents are stopped for resume within the supervisor's exit
budget (launchd's default 20 seconds, systemd's 90), with no turn-boundary wait. Ledger entries end
up `resuming`, and the next boot brings the fleet back, which is what a reboot should do. The
Windows task scheduler ends the process without a signal; its entries stay `running` and resume as
a crash. No unit file changes.

### 2.6 Reload and `service refresh`

`daemon service refresh` and the app's Reload today ask for a `now` restart, accept only an
immediate `restarting` ack, and then boot the job out while the daemon is exiting. With a supervised
daemon that relaunches on `RestartRequested`, booting out would kill a successor that has already
begun resuming. Both paths therefore send `Shutdown keep-agents, exit 0`, wait for the daemon
process to exit (the stop budget plus grace, not the 5-second ack window), and only then rewrite and
bootstrap the unit. The app's lane shows the stop in progress; its evidence predicate is the
daemon's exit followed by the successor's hello, never the ack alone.

## 3. Resume on boot

The order in `DaemonRunner` is: lock, `OrphanReaper`, read the ledger, connect with claims,
worktree cleanup with a keep-set, then the resume lane.

### 3.1 What the ledger says

- `resuming`: a planned restart. Resume.
- `running`: the predecessor died without stopping. Resume, once the reaper has settled the
  process.
- Last alive more than `KCAP_RESUME_MAX_AGE_SECONDS` ago (default 86400): mark `interrupted` with
  reason `too_old`. A laptop that comes back after a weekend should not relaunch Friday's fleet
  unattended. "Last alive" is the later of the entry's `resuming` stamp and the daemon's heartbeat:
  the daemon touches one file, `<name>.heartbeat`, every 60 seconds while it runs, and an agent
  whose entry still exists was alive at the last beat, since a normal end deletes the entry. One
  write a minute per daemon, none per agent.
- `interrupted`: left alone until `kcap agent resume <id>` or `kcap agent stop <id>`, or 30 days.
- `daemon.auto_resume = false`: every candidate is marked `interrupted` with reason
  `auto_resume_off` and nothing launches.

`interrupted` is a local state. The server never learns the word: when a candidate ends up
interrupted, the daemon ends its server entry as it would any failed launch, with status `Failed`
and reason `resume_failed:<reason>`, so a remote viewer sees a failure with the cause rather than
an agent that vanished. The server-side claim expiry (§7) is only the safety net for a daemon that
dies again before it can say so. A later `kcap agent resume` on that entry takes the new-id branch
of §3.6.

### 3.2 The reaper first

A resume must never run beside the process it replaces. The existing `OrphanReaper` pass runs
before anything else and keeps its rule that ambiguity never kills. For a ledger entry whose PID
record resolves to a live, proven-ours process, the reaper kills it as today, then the resume
proceeds. For one that resolves to `Ambiguous`, the entry becomes `interrupted` with reason
`predecessor_process_unresolved` and is not resumed.

### 3.3 Claims on connect

`DaemonConnectArgs` and `DaemonStatusReport` gain `ResumingAgents`: a list of
`{ AgentId, PredecessorInstanceId, SessionId }`, one per ledger entry about to be resumed, private
agents excluded. The server side (§7) spares them from the reconnect reap and lets them inherit
across the instance fence. An id leaves the list when its resume has registered (it is then in
`LiveAgents`) or has failed.

### 3.4 Worktrees

`CleanupOrphanedAsync` receives the ledger's worktree paths as the keep-set. Nothing else about
cleanup changes.

### 3.5 The resume lane

Resumes go through a new orchestrator entry that factors the post-consent body of the admitted
launch path. It keeps the admission fence and the `MaxAgents` check, reuses the ledger's agent id,
and runs at most `KCAP_RESUME_CONCURRENCY` (default 2) launches at a time, so a fleet does not
reload its context in one burst.

Consent is treated as granted: the resume is the same requester's same launch, and the ledger
carries the decision that admitted it. The consent log gets a line per resume naming that decision.
This is the one place a launch bypasses `LaunchConsentGate`, and it is confined to ledger entries
the daemon itself wrote. Policy `prompt` with no UI attached would otherwise fail every unattended
recovery closed.

For each agent the lane tries, in order:

1. **Native resume** (§4), when the vendor's factory declares `ResumeSupport` for the transport
   and the ledger has a vendor session reference.
2. **Kcap continue** (§4.6), when there is no native resume, the native resume fails, or the
   server refuses the session identity.
3. **Interrupted**, with `last_error`, when continue fails too.

A native resume is judged failed when the process exits before its handshake, when the vendor
refuses the session, or when the first turn is not accepted within the launch readiness budget.

### 3.6 Identity

The agent id and the session id travel together. A native resume keeps both; kcap continue mints
both.

- Native resume, server entry still live: the resumed process registers under the existing agent
  id. `AgentRegistered` overwrites a same-slot live entry and re-stamps the instance id, which is
  today's behaviour, and the session relinks through the usual status and claim messages under the
  same session id. For ACP, a same-agent-id bind returns `AlreadyBoundSame` and keeps its cursors,
  so the forwarder resumes from the server's acknowledged position with replay suppressed, and no
  event is duplicated.
- Native resume, server entry already terminal (a long outage, or a server that reaped the agents
  before the claim arrived): the resumed process registers under a new agent id, and the vendor
  session keeps its id. A hook-recorded session reactivates the server's read model on its first
  event. An ACP session whose binding is terminal cannot be reopened; that case falls through to
  kcap continue.
- Kcap continue: always a new agent id and a new session. Once the successor has stayed alive
  through the readiness window, the daemon ends the old pair explicitly,
  `AgentStatusChanged(Completed)` and `EndAgentSession` with the new reason `superseded`, so nothing
  is left active forever and the server never sees a live agent change its session id. An older
  server maps the unknown end reason to `Other` and still ends the session, so the `superseded`
  label waits on the server change but the end itself does not. Until then
  the old entry keeps the worktree, and a successor that dies inside the window leaves it
  interrupted. The continue-session design's choice stands: no continuation link is written; the
  work items and plans are the shared state, and `SessionTakeover` attaches them.

A new agent id registered by the daemon has no server-side launch intent, and the server attributes
such a registration to the daemon's owner. The ledger keeps the original requester, and the new
agent's title names them. For a daemon used by one person this changes nothing. For a team daemon
it means a teammate's agent comes back owned by the daemon's owner after a long outage or a
continue; carrying the requester through `AgentRegistered` is a server follow-up, not part of this
change.

## 4. Native resume per vendor

`IHostedAgentRuntimeFactory` gains `SupportsRestartResume(transport)`. A restart resume sets
`RuntimeStartContext.ResumeSessionId`, the field a parked-reviewer relaunch already uses, to the
ledger's vendor session, and replaces the prompt with the nudge; the two cases mean the same thing
to a factory, "continue this vendor session", so they share the field. A factory that declares no
support is never asked. Support is declared only after a live probe has passed for that vendor and
transport; an advertisement is never enough, as the ACP reconnect work established.

### 4.1 The nudge

The first turn after a resume is a new user message, never a re-send of the interrupted one. The
ACP reconnect design's rule stands: an interrupted turn is surfaced, not replayed, because a
re-send can duplicate a side effect. The nudge says, in the builder's words: this session was
interrupted by a kcap daemon restart at a given time; the previous turn may have stopped mid-way,
so a command may not have finished and its result may be missing; check the working tree and the
last steps before continuing; do not repeat an action that can be verified to have happened.

### 4.2 Claude (PTY)

`claude --resume <session id>` with the launcher's usual flags and the nudge as the trailing
prompt. The hook posts under the same session id, so the kcap session continues.

### 4.3 Codex

- PTY: `codex resume <thread id> <nudge>` with the launcher's configuration overrides. The PTY
  route's refusal of a resume request is removed; it existed because a request that fell through
  to a fresh session would silently fork the transcript, and that cannot happen once the route
  honours it.
- App-server: `thread/resume`, already wired.

### 4.4 ACP vendors

A new start path sends `session/load { sessionId, cwd, mcpServers }` instead of `session/new`,
with the same replay suppression as the in-process reconnect. It is gated per vendor by a
`SupportsRestartResume` flag on the descriptor, separate from `SupportsReconnectResume` and from
the `KCAP_ACP_RECONNECT` switch, because the two paths are enabled on different evidence.

The reconnect probe measured vendors after a SIGKILL. The restart path stops them gracefully, so
each vendor is re-measured with a graceful-stop variant of `acp_c0_probe.py` (stdin EOF, then
SIGTERM, then the kill) before its flag flips. Expected starting points: Cursor and Copilot pass as
they did after a SIGKILL; Kiro's stale-owner lock and Gemini's unpersisted session may clear with
a clean exit; OpenCode and Grok have never been probed. A vendor that fails stays on kcap continue.

### 4.5 Pi, Muse, Antigravity

- Pi: `--mode rpc --session <session file>`, then the nudge as the first `prompt`.
- Muse: `session/resume`, already wired; the id is a GUID kcap minted.
- Antigravity: the next turn's process starts with `--conversation <id>` as every turn after the
  first already does, carrying the nudge.

### 4.6 Kcap continue

A fresh session launched through the ordinary path with the ledger's descriptor under a new agent
id; the old pair ends as `superseded` (§3.6) once it passes readiness. Its prompt is the original
launch prompt, a note that it continues an interrupted session, the old session's summary from
`GET /api/sessions/{id}/summary` when the fetch succeeds within its timeout, and the nudge. Once
the new session id is known, the daemon attaches it to the old session's work items and unfinished
plans with `SessionTakeover`, which moves from `Capacitor.Cli` to `Capacitor.Cli.Core` so the
daemon can call it; the liveness check is satisfied by the reaper's proof that the old process is
gone.

## 5. Local clients during a restart

Attach connections close when the outgoing daemon stops the agent. The desktop app's reattach loop
and `kcap agent attach` reconnect by agent id once the successor answers, bounded by the existing
attach timeout, and the first replay is the persisted ring followed by a marker line that says the
daemon restarted and the session resumed. `kcap agent attach` on an agent that ends up
`interrupted` prints the reason and exits.

## 6. Flow participants

A review-flow or PR-review agent never takes the resume path.

- A resumable reviewer (Codex app-server transport) is parked through the existing
  `ParkReviewerAsync` path with its durable server ack, whatever its idle time. The server
  relaunches it later with `ResumeSessionId` as it does today.
- A non-resumable reviewer makes the daemon busy while its round is in flight, up to the
  turn-boundary bound, then is ended as today with `daemon_shutdown`. The flow's liveness-based
  supervision reports the loss.

## 7. Server changes (kcap-server, deployed first)

- `DaemonConnectArgs` and `DaemonStatusReport` carry `ResumingAgents`.
- `DaemonConnect` adds the claimed ids to the reconcile survivor set.
- `RebindDaemonConnectionAsync` and `RebindCurrentIfSlotMatches` let a claimed entry inherit when
  its stamped instance id equals the claim's `PredecessorInstanceId`: rebind the connection, keep
  it live, re-stamp the new instance.
- `InteractiveLaunchIntentStore` moves the owner instance of a claimed agent from the predecessor
  to the successor during `DaemonConnect`, so `AgentRegistered` passes the ownership fence.
- A claim expires when the id leaves the daemon's reports or after a server-side bound (my call:
  15 minutes), after which the entry is reconciled as absent.
- `EndAgentSession` and the agent-run stop accept the reason `superseded`.
- `AgentRegistered` answers the daemon with its outcome, so a registration the registry refuses
  because it retained a recorded failure is visible to the caller. That answer is what lets the
  daemon take the new-id branch of §3.6 instead of assuming the same-id registration landed. Until
  the server answers, the daemon treats every same-id registration as accepted.
- A test pins that a `SessionStart` carrying the resume source relinks a session the server holds
  as ended, with the agent entry still live, since that is the path a session takes when a hook or
  watcher ended it before the ledger said `resuming`.
- An older daemon sends no claims and sees today's behaviour. An older server ignores the field;
  the daemon then finds its entries reaped and takes the new-id branch of §3.6.

## 8. Surface

- `kcap daemon restart`: default and `--force` semantics per §2.1. `kcap daemon stop` and
  `kcap daemon service stop` gain `--keep-agents` (§2.4). `kcap daemon status` lists resuming and
  interrupted agents with reasons.
- IPC: the `Shutdown` frame and its ack are appended to `FrameType`, advertised as `shutdown/1` in
  `LocalControlCapabilities`, and the CLI uses them only when the hello lists the capability.
- `kcap agent list` shows `resuming` and `interrupted` states. New verb `kcap agent resume <id>`
  retries an interrupted entry through the lane.
- Config: `daemon.auto_resume` (bool, default true) in `DaemonSettings`, settable and unsettable
  through `kcap config`, shown by `kcap profile`. Bounds are env knobs in the daemon's existing
  `ParseSecondsEnv` style.
- Desktop app: `SessionStatusDots` gains `Resuming` (in-flight, `KcapPurple*`) and `Interrupted`
  (attention, `KcapWarning*`). Reload copy and the update disclosure stop promising that running
  agents keep running or that the daemon waits for idle, and say instead that agents are stopped
  and resumed on the new daemon. The "update pending" indicator keeps working and now clears
  within the turn-boundary bound.
- `README.md` (the daemon section's restart and update paragraphs, the `kcap agent` section),
  `help-daemon.txt`, `help-agent.txt` and `help-update.txt` change in the same PR.

## Alternatives considered

- **A per-agent host process** that owns the PTY or pipes, the ring and the death signal, so the
  daemon can restart under a running turn. It is the only way a turn survives and the only way
  agents survive on Windows, and it also covers daemon crashes. It costs a process per agent and a
  frozen host protocol. Resume was chosen first because it is the smaller build, covers crashes and
  reboots with the same mechanism, and the interruption is acceptable. The ledger here is what a
  host would adopt from.
- **Two daemons under one name**, new launches on the newer one. Rejected: the per-name lock, the
  server's name-in-use rejection, the instance fence, the socket path and the single command lane
  all exist to prevent two incarnations under one identity, and a crash would still lose the
  fleet.

## Testing

Daemon unit tests:

- Ledger store: write at admission, update on id discovery and handshake, delete at end,
  quarantine of a corrupt file, unknown fields tolerated, newer schema reported as interrupted.
- Busy definition: interactive agents do not block; an eval, a launch mid-admission and a
  non-resumable reviewer mid-round do; `--when-idle` and `--force` keep their meanings.
- Turn-boundary wait: fires when every agent is awaiting input, fires at the bound otherwise,
  applies to the binary-change trigger only.
- Stop for resume: the ended report, worktree removal and record deletion are skipped for
  interactive agents and still happen for a non-resumable reviewer; the ring is persisted.
- Stop versus restart: `Shutdown end-agents` ends agents and deletes their entries;
  `keep-agents` leaves them `resuming`; the signal fallback marks them `interrupted`; a SIGTERM
  the daemon did not request stops for resume with no wait.
- Hook and watcher suppression: a SessionEnd for an agent whose entry is `resuming` is not posted;
  the watcher exits without ending the session; a `running` or absent entry posts as today.
- Heartbeat: the file is touched on the tick; the age check reads the later of the stamp and the
  beat.

CLI tests: `daemon stop` and `service stop` send `Shutdown` with the right intent and fall back
to the signal path only on no answer; `service refresh` waits for the exit before rewriting the
unit; `kcap update` no longer promises a restart "once it is idle".
- Boot order: reaper before resume; an ambiguous predecessor interrupts instead of resuming; the
  keep-set reaches `CleanupOrphanedAsync`; `running` entries resume; stale entries interrupt;
  `auto_resume = false` interrupts everything.
- Lane: concurrency cap, same-id registration, new-id branch after a server refusal, the fallback
  ladder and the terminal `interrupted` state, the consent-log line.
- Argv for Claude, Codex PTY and Pi resume, pinned like the existing launcher argv tests; the
  Codex PTY refusal is gone.
- ACP start-with-load against the fake ACP server, with replay suppressed and the gate honoured.
- `SessionTakeover` tests move with the type.

App tests: status mapping for the two new states; copy changes.

Live checks, gated like the existing certs: the graceful-stop probe variant for each ACP vendor;
one end-to-end restart per vendor family with a real binary swap on macOS and Linux; a crash
recovery (SIGKILL the daemon) for Claude PTY and one ACP vendor.

Server tests in kcap-server: survivors include claims; inheritance across the fence on a matching
predecessor only; launch-intent owner adoption; claim expiry; an older daemon's payload unchanged.

## Risks

- **Two processes on one vendor session.** Mitigated by running the reaper first and refusing to
  resume on ambiguity.
- **A resumed turn repeats a side effect.** The nudge asks the agent to verify before repeating,
  and no interrupted turn is ever re-sent.
- **Token burst and rate limits** when a fleet reloads context. Mitigated by the concurrency cap;
  the bound is tunable.
- **Vendor resume quirks.** A skills probe saw Claude refuse a repeated question on a resumed
  session; resumes are monitored and a vendor that misbehaves can be switched to continue by
  withdrawing its `ResumeSupport`.
- **Server skew.** Server first; the daemon degrades to new agent ids rather than losing agents.
- **Stale fleets after a long outage.** The age cap.
