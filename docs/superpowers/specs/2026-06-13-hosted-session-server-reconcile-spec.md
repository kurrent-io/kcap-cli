# Server-side reconciliation of stuck hosted/recorded sessions

**Date:** 2026-06-13
**Status:** Spec (for the kcap-**server** repo — cannot be implemented from kcap-cli)
**Related:** client-side fixes in this repo (watcher watchdog, daemon `EndAgentSession`); design `2026-06-13-permission-bridge-reconnect-retry-design.md`

## Problem (observed incident)

Session `9687912eed69420d9a21ba1c66b7698d` (a daemon-hosted agent) showed as **active
forever** on the server after the hosted `claude` process had already died. Forensics:

- The hosted `claude` PID was dead.
- Its two `kcap watch` sidecar processes were **orphaned but still alive**, holding live
  SignalR connections to the server (`…:443 ESTABLISHED`), 1h+ after the agent died.
- The hosting daemon had been replaced by a new daemon instance that had no knowledge of
  the session, so it never sent `EndAgentSession`.

So "active" was being held open by (a) orphaned watcher connections that never sent
session-end, and (b) a missing `EndAgentSession` for the agent-run.

## Why the client side alone can't guarantee this

There are three independent producers of "this session is active" and three matching
failure modes. The client-side fixes (this repo) close two of them, but a daemon/agent
that dies **hard** (SIGKILL, crash, host reboot, daemon replacement) can leave residue
the client can never clean up — by definition the cleanup code is dead. The server is
the only component guaranteed to be alive to reconcile. Defense in depth requires a
server-side backstop.

Client-side fixes already in flight (for reference, not server work):

1. **Watcher watchdog** — `kcap watch` now reliably resolves the durable coding-agent PID
   and self-terminates + POSTs `session-end` when the agent dies (previously the watchdog
   silently never started for Claude, orphaning the watcher).
2. **Daemon `EndAgentSession`** — daemon attempts to end the hosted AgentSession on stop /
   agent-exit / shutdown.

Neither survives a hard daemon/host death. Hence this spec.

## Goal

The server must converge a session/agent-run to a terminal state when its producers are
demonstrably gone, without waiting on a client message that may never arrive.

## Proposed reconciliation (server)

Two triggers, each with a short grace period to tolerate transient reconnects (the
cloudflared-instability pattern — connections drop and re-establish within seconds):

### A. Daemon disconnect → end that daemon's in-flight agent-runs

When a daemon's hub connection drops and is **not** re-established (by the same daemon
identity / `(owner, name)` slot) within a grace window (suggest **60–90s**, comfortably
above the daemon's auto-reconnect cadence which retries at ≤30s):

- Mark every `AgentRun`/`AgentSession` that daemon owned and that is still `active` as
  ended, reason `daemon_disconnected` (distinct from `agent_stopped`/`agent_exited` so it's
  attributable in the read model).
- This is the backstop the daemon code already *assumes* exists — see the comments in
  `AgentOrchestrator.cs` (`ReadAgentOutputAsync` finally, `CleanupAgentAsync`,
  `DisposeAsync`) which skip `EndAgentSession` on shutdown "because the server detects the
  daemon disconnection and ends its sessions on its own." **This assumption must be
  verified; the incident suggests it is not happening (or not for replaced daemons).**

### B. Watcher disconnect → end sessions with no remaining live producer

When the last `kcap watch` connection for a session drops and is not re-established within
a grace window (suggest **60–90s**), and there is no other live producer (no connected
daemon hosting it, no active local-CLI watcher):

- Mark the session ended, reason `watcher_disconnected`.
- Optionally trigger the same post-session work the normal `session-end` path does
  (what's-done generation) if not already done — idempotently.

### Idempotency

Both triggers must be **idempotent** with the existing `SessionEnded` / `EndAgentSession`
paths: if the client later reconnects and sends session-end (or the daemon's
`EndAgentSession` lands), it must be a no-op, not a duplicate end or a resurrection.

## Open questions for the server team

1. **Does the server already react to daemon hub disconnect at all today?** The daemon code
   asserts it does. If yes: why did the replaced-daemon case (incident) leave the session
   active — is reconciliation keyed on connection-id rather than daemon identity, so a
   *replacement* daemon connecting masks the old one's disconnect without ending its runs?
2. **Is "active session" derived from a live watcher connection, from `SessionEnded`
   absence, or both?** Determines whether trigger B is needed or whether watcher-connection
   teardown already clears it.
3. **Grace-period source of truth** — align with the daemon heartbeat (7s tick / 5s ping
   deadline) and auto-reconnect (≤30s) so the window can't fire during a normal blip.

## Acceptance

- Kill a hosted agent's `claude` and its watchers without any client-side session-end →
  session converges to ended within the grace window, attributed `daemon_disconnected` /
  `watcher_disconnected`.
- A transient reconnect within the grace window does **not** end the session.
- Re-delivery of a client session-end afterwards is a no-op.
