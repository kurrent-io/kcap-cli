# Flow participants across a daemon restart

A daemon restart (self-update, `kcap daemon restart`, `stop --keep-agents`) resumes interactive
hosted agents under their own ids. A review-flow reviewer, or any other `LaunchKind.ReviewFlow`
participant, does not: unless it is a Codex app-server reviewer, it is killed and its flow loses the
context of every round it reviewed. This design resumes it in place through the resume ledger, for
each reviewer launch shape proven to reopen its session, and says what every other shape does.

Builds on `2026-10-17-daemon-restart-agent-resume-design.md`, whose §6 kept flow participants out of
the ledger.

## What happens today

Daemon (`AgentOrchestrator.Resume.cs`, `StopAgentsForShutdownAsync`):

- Codex app-server reviewer: `ParkForShutdownAsync` → `ReportParticipantParked`; the server moves its
  hosted-session ledger row `active → parked` and records the role stopped with
  `reviewer_parked_resumable`. The next `submit_review_round` heals the role with a launch carrying
  `ResumeSessionId`, and `thread/resume` reopens the thread. A round in flight at the park ends
  `unclear` (`participant_parked`); the driver resubmits it.
- Every other participant: terminated, nothing reported (the shutdown skips unregister). The
  successor's `DaemonConnect` does not list it, so the server's reconnect reap, or the old-epoch
  absence proof once the successor reports `StartupReapComplete`, records
  `FlowParticipantStopped(participant_died)` and clears the role for relaunch. A round in flight ends
  `unclear` (`participant_died`). The next submit launches a fresh reviewer that receives only the
  follow-up prompt: the earlier rounds' context is gone.

Server parking is scoped by the hosted-session ledger row, which only the envelope-sourced Codex
app-server runtime creates (`AcpSessionSourceClaim`). Extending parking to other vendors would need
that row for every transport, plus a worktree that outlives the park: Claude `--resume` and ACP
`session/load` find a session by its cwd, and a heal launch creates a new worktree at a new path.

## Shape: flow participants take the resume ledger

A flow participant whose launch shape can reopen its session gets a ledger entry. On a keep-agents
shutdown it is stopped for resume (worktree, snapshot, MCP config, pid record and entry kept); the
successor relaunches it under the same agent id with the vendor's native resume.

What the server already does, unchanged (read from kcap-server source):

- A `ResumingAgentClaim` carries no kind restriction; the claimed participant survives the
  reconnect reap for up to 15 minutes.
- Re-registering the same id is a duplicate registration that keeps the flow pin, provided the
  participant was not recorded stopped meanwhile; `submit_review_result` checks only that the role is
  assigned to this agent id and not stopped.
- Daemon input drops carry a reason; `unknown_agent` is per-id absence proof.
- The old-epoch absence proof ignores claims: once the daemon reports `StartupReapComplete`, an
  old-epoch flow incarnation not in `LiveAgents`, `Quarantined` or `UnresolvedStartupCandidates` is
  recorded dead.
- The activity monitor latches an agent `Regressed` when its `ActivitySeq` goes backwards, and ends
  a round whose reports stop for longer than its bound (at least 3 minutes).

Each of these is a constraint below. Codex app-server reviewers keep the park path, which is proven
end to end. A participant that is not resumable keeps today's end.

### Which participants are eligible

Only participants stopped by a planned keep-agents shutdown of the immediately preceding daemon. The
entry is written `Running` at launch like an interactive one, but a successor resumes a participant
entry only when all hold:

- it is `Resuming` (written by that stop) and its `ResumingAt` is within the claim lifetime (15
  minutes);
- its `PredecessorInstanceId` equals `DaemonLock.PriorInstanceId`, which witnesses the boot just
  before this one even when that boot was a binary without this change. A null or indeterminate
  prior instance fails closed.

Any other participant entry — `Running` after a crash, stale, or written before an intermediate
daemon ran — is discarded at boot (see *When a resume fails*). A crash leaves no stop-time activity
high-water mark and the server has already reaped the participant, so there is nothing to continue.

### Per vendor

A new factory predicate, `SupportsReviewerRestartResume(transport, work)`, default false, says a
reviewer launch shape reopens its session. It is separate from `SupportsRestartResume` because a
reviewer runs under a different shape: strict MCP config, isolated config or HOME, a borrowed
snapshot. It is set true for a shape only once a live probe of that exact shape, recorded under
`docs/probes/`, has shown: the session reopens after the daemon's stop, it recalls context from an
earlier round, and it submits a result through the freshly minted result channel and tools, both
after a stop between rounds and after a stop mid-turn, where the repeated round is answered once.

| Shape | Across a restart | Why |
|---|---|---|
| Codex app-server | parks; the server resumes it on the next round | unchanged |
| Claude, owned worktree | ledger, `--resume <session>`; probe passed (`docs/probes/2026-10-23-claude-reviewer-restart-resume/`) | session store is `~/.claude/projects/<cwd>`, and the worktree path is kept; a Claude reviewer is owned-only |
| Cursor, owned and borrowed snapshot | ends as today until its probe passes | keeps its own HOME, so its store survives; restart-resume probe passed for the interactive shape only |
| OpenCode, owned | ends as today until its probe passes | the reviewer's isolated config dir is keyed by daemon epoch; the probe decides whether the session store lives outside it, else not resumable |
| Copilot | ends as today | a borrowed reviewer's session store sits in the per-launch sandbox HOME, which the launch owns; keeping and re-binding it is a follow-up |
| Codex PTY | ends as today | `CodexHostedAgentRuntimeFactory` refuses a reviewer resume off the app-server route; reviewers route to app-server |
| Pi | ends as today | its session file lives in the per-launch reviewer dir, which disposal and the successor's early sweep delete |
| Muse | ends as today | `session/resume` cannot carry the reviewer's read-only profile, approval mode and MCP servers |
| Antigravity | ends as today | a reviewer runs under a per-launch HOME |
| Gemini, Grok, Kiro | ends as today | no verified native resume; kcap continue starts a new agent id the flow does not know |

A shape whose probe fails is "ends as today". What the flow sees then: `participant_died` on the
round in flight, and a fresh reviewer with no earlier context on the next submit.

### What the entry records

Beyond the interactive descriptor: launch kind; MCP allowlist; tools; the reviewer certification
requirement without its connection id; the borrowed shape (below); the activity seq and logical age
at stop; and the last round input delivered, with its time.

A reviewer always runs on the daemon's default account, which the entry records like any launch's.
The resume command names no account: the resume resolves the recorded one, and a review-flow launch
that names an account is refused.

### Borrowed snapshots

Needed by the Cursor follow-up, the first borrowed shape; no shape that resumes is borrowed until
then. Containment must exist again, by the same code, before the vendor runs. The entry records the
borrowed flag, the canonical borrowed cwd and git root, the snapshot root, the git-relative cwd and
the review-context root. The resume command carries `Borrowed = true` and the borrowed cwd, so
`snapshotBorrow`, the sandbox, result brokering and capability minting take their ordinary
branches. Before that, the resume:

1. re-runs `BorrowAuthorizer` on the recorded cwd and requires the canonical cwd and git root it
   returns to equal the recorded ones (a retargeted symlink fails);
2. re-runs the capture-scope refusal;
3. re-syncs the kept snapshot from the source, publishing a new review-context generation, and
   points `WorktreeInfo` at the recorded snapshot root, context root and relative cwd.

Missing metadata, a mismatch, a refusal or an unavailable sandbox or brokered credential fails the
resume before spawn.

### Certification

A resume re-evaluates the recorded requirement with `EvaluateReviewerCertification`'s terminal arms:
vendor, required launcher policy against the successor's own, and the vendor CLI's freshly probed
version against the allowed range. The connection-id and advertisement-swap arms are skipped: they
guard an advertisement, and the successor has not used one. A failure fails the resume before spawn.

### What the resumed reviewer is sent

The daemon cannot tell whether the last round settled: `AwaitingInput` is a silence heuristic for a
PTY vendor, an ACP turn can end without a submission, and a direct submission never passes through
the daemon. So every resumed participant gets the same first turn, and its own restored history
decides: a reviewer nudge (the daemon restarted while you were working on a round; if you
  already submitted its result, do nothing; if `submit_review_result` refuses the token, the round
  has ended, so stop) followed by the last round input, repeated verbatim. The vendor may not have
  persisted that input, and only it carries the round token. A reviewer that had already submitted
  before the stop is told not to submit again; a duplicate is refused by its stale token. Between
  rounds this costs one short turn.

### Input that arrives during recovery

The successor connects before its resume lane runs, so the server can dispatch a round to a
participant that is still a candidate, queued or handshaking. `HandleSendInput` must not answer
`unknown_agent` for it, which the server would take as absence proof. Input for a pending
participant candidate is held in order (bounded; overflow drops with `queue_full`) and delivered
after publication and after the nudge. If the attempt ends without publishing, the held input is
dropped with `unknown_agent`, which is then true. A server stop of a pending candidate withdraws it.

### Startup completeness

While any participant candidate is pending, `ComputeStartupReapComplete` is false, so the server
proves nothing absent mid-resume. This also delays proof for unrelated dead old-epoch participants,
so it is bounded:

- participant candidates run ahead of interactive ones on the resume lane;
- one recovery deadline covers them all from daemon start (my call: 90 s). It bounds every unsettled
  participant attempt, not only unpublished ones: queueing, preparation, handshake, registration
  (whose server call takes the attempt's token, not the connection's lifetime token) and readiness.
  At the deadline each unsettled attempt is cancelled, its process terminated, and a late completion
  is fenced so it can neither publish nor mark itself resumed; it is ended as a failed resume;
- `DaemonConnect` and every status report take one immutable recovery snapshot under the ledger
  lock — live ids, live agent metadata, resuming claims and the completeness flag together — so no
  message can omit a participant from both its live list and its claims while reporting complete.

The hold only adds a condition: completeness stays false, as now, for unresolved orphan records or
incomplete Linux discovery after the deadline. No new `UnresolvedStartupCandidate` reason is
introduced; the wire enum is closed.

### Mid-round, and when server timers expire

The restart still waits up to `RestartTurnWait` for participants to finish their round. Past it, a
resumable participant is stopped mid-turn and resumed as above. Continuity is guaranteed only while
the server's timers hold: the restart and resume complete within the claim lifetime, and within the
round's remaining silence budget, which a long round may have nearly spent before the stop. When
either expires the server stops the role, and a re-registration no longer carries the flow pin. The
daemon cannot see that today: the resumed reviewer is orphaned, its submissions are refused, and the
daemon's idle reaper ends it. A server change (below) refuses that registration so the resume fails
at once.

### The successor's orphan reap

The successor reaps the predecessor's leftover process before it loads the ledger, and reports that
process gone. A report naming the flow run and role makes the server record the participant stopped
and clear the role, so the next round launches a fresh reviewer and stops the resumed one. A reap of
a participant whose entry the boot will resume under the same id reports the process gone without
its flow run and role; any other reap reports them, so a reviewer that really died is replaced.

### Predecessor session ends

The predecessor's vendor process ending must not end the participant. `ResumeEndSuppression` holds
an end only while the entry is `Resuming`, and `TryPublishResumed` rewrites it `Running`, so a
delayed Claude end hook, a watcher's final end or a spooled `parent_exited` from the old process can
still land after publication and reach the server's `HostedAgentDeathBridge`.

- Before publication the existing rule stands: the entry is `Resuming`, and an end is held.
- After publication an end is also held when the ending process's daemon epoch differs from the
  epoch the entry records for its current run, which `TryPublishResumed` writes. The child env
  already carries the epoch it was launched under; `HostedAgent` reads it.
- The decision is made where the end is produced (hook, watcher, exited-session sweep). An end the
  producer let through can sit in the spool across the whole restart — enqueued while the entry was
  still `Running`, held by a backlog or a failed post, drained after the successor published. The
  commit stamp below is taken before the end is posted or spooled, whichever comes first, so such an
  end has already made its run ineligible for resume and the drain needs no re-check. The sweep,
  which runs in another process, reads the agent id and epoch from the session's recorded origin,
  which gains them.

Checking at production and delivery still leaves a request in flight: an end can pass its last
check and be posted, the planned stop can then mark the entry `Resuming`, and the successor can
publish before the server processes the old end. So committing an end and marking for resume are
made mutually exclusive, by a compare-and-set on the entry under a per-entry cross-process lock
(`flock` on a lock file beside the entry; an exclusive open on Windows) that the hook, the watcher,
the spool drain and the daemon all take:

- Before posting or spooling a session end, the producer stamps the entry with `EndCommittedAt` and
  the ending epoch, only if the entry is `Running` under that epoch; otherwise the end is held.
- `TryMarkResuming` marks `Resuming` only if the entry carries no `EndCommittedAt`; otherwise the
  agent takes the ordinary end path at shutdown and is not resumed.
- The stamp is durable and never cleared for that run. An end that posted, timed out with an unknown
  outcome, or is still queued therefore makes its run ineligible for resume, which is safe: the flow
  heals as today. A resumed run's publish writes a fresh entry without the stamp, under its own
  epoch.

A genuine exit of the resumed process carries the matching epoch and posts. This also closes the
same window for interactive resumes.

### When a resume fails

No kcap continue for a participant, and no entry kept for `kcap agent resume`. Boot-time
ineligibility (auto-resume off, stale, not planned, missing worktree or session, newer schema), a
failed native resume, the recovery deadline and an exception all take one path: the agent's end is
reported as `resume_failed:<reason>`, so the server records the death and the flow heals as today.
Its resources are removed only once every process that could use them is proven gone: the
predecessor and any replacement the failed attempt spawned. Until then the entry stays as a
non-resumable tombstone that keeps the worktree and snapshot; an unresolved predecessor keeps its pid
record for the orphan reaper, and an unresolved replacement goes to the kill quarantine with its
spawn identity and capacity, as a stuck child does today. The tombstone is cleaned when the last of
them is confirmed dead.

### Local control

Ledger rows report the entry's own kind, not `Default`, so `kcap agent ls` shows a resuming reviewer
as one, and local stop applies its review-flow protection before any discard: an unforced stop of a
pending participant is refused as it is for a live one; `--force`, a server stop and an end-agents
shutdown withdraw it so it can never publish later. `stop --all` covers pending participants under
the same rule.

### Clocks

The resumed agent's activity clock starts above the recorded seq, before any pending-launch or stage
report can expose it, and its age starts at the recorded age, so the reviewer lifetime backstop
counts the whole run rather than restarting at every resume.

### Ledger compatibility

Participant entries live in their own subdirectory of the ledger, which a daemon without this change
never reads: it claims nothing for them, and the server reaps them as it does today. Interactive
entries are unaffected in both directions. A newer daemon booting after such a daemon finds the
entries' predecessor is not its prior instance, and discards them however recent they are.

## Server changes (kcap-server issue, not required to ship)

- The absence proof honours `ResumingAgents`, so the daemon need not hold `StartupReapComplete`.
- A registration of a flow participant whose role is stopped or whose run is closed is refused with
  a new `AgentRegistrationRefusal`, so a resume past a server timer fails at once instead of leaving
  an orphan for the idle reaper. An older server accepts it, which is the orphan case above.

## Out of scope

- `LaunchKind.Review` (PR review agents): unchanged.
- Cursor and OpenCode reviewer resume: follow-ups, each switched on by its own probe.
- Copilot, Pi and Codex PTY reviewer resume: follow-ups, each needing its store or route kept.

## Acceptance

- A Claude reviewer in an owned worktree: a restart between rounds and one mid-round past
  `RestartTurnWait` both end with the next round answered by the same agent id with earlier-round
  context, and no fresh reviewer launched.
- Round dispatched while the candidate waits for the lane, during handshake, and right after
  publication: delivered once, after the nudge.
- A silent Claude round with `AwaitingInput` set by the quiet heuristic, an ACP turn that ended
  without submitting, and input queued just before shutdown: each is resumed with the nudge and the
  round input and its round is answered. A reviewer that had submitted does not submit again.
- A stalled participant resume, published or not, beside an unrelated dead old-epoch participant:
  the recovery hold lifts at the deadline (completeness then follows its other conditions), the
  stalled one is reported failed and cannot publish later, and the dead one is proven absent.
- A reconnect racing the last candidate's publication or failure: the connect lists it as live or
  claimed, or reports incomplete.
- Predecessor end produced while `Resuming` and after successor publication: held, role stays live;
  a genuine exit of the resumed process still ends it. One spooled by the old process while its
  entry was still `Running`: the planned stop finds its stamp and does not resume the run.
- An end paused after its stamp and released after successor registration, and one that times out:
  the planned stop finds the stamp, the agent is not resumed, and the flow heals as today.
- A replacement that starts, fails recovery and cannot yet be proven dead: its worktree and snapshot
  stay until it is.
- With the Cursor follow-up, a nested-cwd borrowed snapshot: the resumed process's boundary and result channel match a
  fresh launch's; a retargeted source, missing metadata or unavailable sandbox fails before spawn.
- Claude CLI swapped for an out-of-range build: resume refused before spawn.
- Last round input lost by the vendor: the repeat lets it finish; a result accepted just before the
  stop is not submitted twice.
- A participant entry with an unresolved predecessor process: reported failed, resources kept until
  resolved, never offered to `kcap agent resume` or kcap continue.
- New → old → new daemon within the claim lifetime, worktree still present: no reviewer is revived.
- Unforced local stop leaves a pending participant's recovery intact; forced stop and end-agents
  shutdown prevent a later publish.
- First post-restart activity report is above the last pre-restart one; a reviewer near its lifetime
  limit is not given a fresh lifetime.
