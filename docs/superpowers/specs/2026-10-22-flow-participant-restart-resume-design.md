# Flow participants across a daemon restart

A daemon restart (self-update, `kcap daemon restart`, `stop --keep-agents`) resumes interactive
hosted agents under their own ids. A review-flow reviewer, or any other `LaunchKind.ReviewFlow`
participant, does not: unless it is a Codex app-server reviewer, it is killed and its flow loses the
context of every round it reviewed. This design resumes it in place through the same resume ledger,
for every vendor that can reopen its own session, and leaves the rest as they are.

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

A flow participant whose vendor can reopen its session gets a ledger entry like an interactive
agent. On a keep-agents shutdown it is stopped for resume (worktree, MCP config, pid record and
entry kept); the successor relaunches it under the same agent id with the vendor's native resume.
No server change is required:

- A `ResumingAgentClaim` carries no kind restriction; the claimed participant survives the reconnect
  reap.
- Re-registering the same id is accepted as a duplicate registration and keeps the flow pin; the
  role is still assigned to that agent id and not stopped, so `submit_review_result` and round
  delivery work as before.
- The old-epoch absence proof ignores claims. The successor therefore reports `StartupReapComplete =
  false` while any participant resume is pending (seconds, bounded by the readiness window). Once the
  resumed agent is live it is in `LiveAgents`; once a resume fails it is gone and the proof records
  the death, which is the fallback below.
- The server's activity monitor latches an agent `Regressed` when its `ActivitySeq` goes backwards.
  The entry stores the last seq at stop, and the resumed agent's clock starts above it.

Codex app-server reviewers keep the park path: it is proven end to end and the server already
resumes them. A participant that is not resumable keeps today's end.

### Per vendor

A new factory predicate, `SupportsReviewerRestartResume(transport)`, default false, says a vendor's
reviewer launch shape reopens its session. It is separate from `SupportsRestartResume` because a
reviewer runs under a different launch shape: strict MCP config, an isolated HOME, a borrowed
snapshot.

| Vendor | Across a restart | Why |
|---|---|---|
| Codex app-server | parks; server resumes on the next round | unchanged |
| Codex PTY | ledger, `codex resume` | rollouts are global, path-independent |
| Claude | ledger, `--resume <session>` | same worktree path kept; reviewer is owned-worktree only |
| Cursor, Copilot, OpenCode | ledger, `session/load` | passed the graceful-stop probe (2026-10-19); the ACP factory's review-flow refusal is lifted for them |
| Pi, Muse | ledger if the reviewer's session store survives the stop; verified during implementation, else not resumable | |
| Antigravity | ends as today | a reviewer runs under a per-launch HOME, so its conversation store does not survive |
| Gemini, Grok, Kiro | ends as today | no verified native resume; kcap continue starts a new agent id the flow does not know |

What the flow sees for a vendor that ends: `participant_died` on the round in flight, and a fresh
reviewer with no earlier context on the next submit.

### Borrowed snapshots (Cursor, Copilot)

Containment must exist again before the vendor runs. The entry records the snapshot source; the
resume re-runs `BorrowAuthorizer` on it (the ledger is daemon-owned, but the borrow allowlist may
have changed), re-syncs the kept snapshot from the source, and mints a fresh reviewer token,
review-context capability and brokered result channel through the ordinary launch path. Copilot's OS
sandbox profile and brokered credential are built as for a fresh launch. Any refusal fails the
resume.

### What the resumed reviewer is told

The first turn is a reviewer nudge, not the interactive one: the daemon restarted; if a review round
was in progress and its result was not yet submitted with `submit_review_result`, finish it and
submit; otherwise wait for the next round and do nothing. It is sent whether or not a turn was in
flight, because a PTY vendor never reports `TurnInFlight`. Between rounds this costs one short turn.

### Mid-round, and past the wait bound

The restart still waits up to `RestartTurnWait` for participants to finish their round. Past the
bound, a resumable participant is stopped mid-turn and resumed. The server's round stays in flight:
the gap is seconds, well under its silence bound (at least 3 minutes), and the nudge has the reviewer
finish and submit. If the vendor's session lost the unfinished turn, the reviewer redoes the round
from its context.

### When a resume fails

No kcap continue for a participant, and no `interrupted` entry kept for `kcap agent resume`: the
entry, its worktree and its snapshot are removed, `resume_failed:<reason>` is reported as the agent's
end, and the server records the death. The flow then behaves as it does today.

### Ledger compatibility

Participant entries carry their launch kind, MCP allowlist, snapshot source and last activity seq,
and are written at a bumped schema version. A daemon too old to know them marks them
`ledger_newer_than_daemon` instead of resuming a reviewer as an interactive agent.

## Out of scope

- `LaunchKind.Review` (PR review agents): unchanged.
- Server hardening: the absence proof could honour `ResumingAgents` so the daemon need not hold
  `StartupReapComplete`. Filed as a kcap-server issue, not required here.
- Reviewer certification: a resume runs under the successor's launcher policy without re-checking
  the server's certification block, whose connection id belongs to the predecessor. A vendor the
  successor no longer certifies refuses in its factory and the resume fails.
