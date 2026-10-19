# ACP native restart resume — implementation plan

Implements GitHub issue #1617 against the design in
`docs/superpowers/specs/2026-10-17-daemon-restart-agent-resume-design.md` §3.6 and §4.4, on top of the
restart-and-resume core (`docs/superpowers/plans/2026-10-17-daemon-restart-agent-resume-core.md`, whose
Global Constraints apply unchanged).

## Shape

- `AcpVendorDescriptor.SupportsRestartResume`, default false, separate from `SupportsReconnectResume`
  and from `KCAP_ACP_RECONNECT`. `AcpHostedAgentRuntimeFactory.SupportsRestartResume(transport)`
  answers it.
- `AcpHostedAgentRuntime.StartAsync` takes the session to reopen. Given one, the handshake sends
  `session/load { sessionId, cwd, mcpServers }` instead of `session/new`, drops every notification the
  load replays (the load response is the barrier), refuses when `initialize` does not advertise
  `loadSession`, and reports a refused load as a failed handshake. The nudge is the first turn.
- The factory passes `RuntimeStartContext.ResumeSessionId` only for a descriptor that declares support
  and an interactive launch; a resume it cannot honour fails the start instead of opening a fresh
  session, which would fork the conversation under the old agent id.
- Forwarding. `AcpSessionStarted` reports no cursor, and a fresh forwarder starting at seq 0 against a
  binding that already holds N events has its first N events deduplicated away. A resumed ACP agent
  reads the cursor with an empty `AcpSessionEvents` batch (the server answers its cursor and appends
  nothing) and starts the forwarder from it, without a second `SessionStarted`.
- A binding the server already holds as terminal drops the resumed agent's first event and the
  forwarder stops; a rejected bind starts no forwarder. Either, inside the readiness window, fails the
  native resume (`acp_binding_unavailable`): the runtime is terminated with its resume still pending,
  so the worktree and entry survive, and the lane falls through to kcap continue.
- Flags flip only for vendors whose graceful-stop probe passes:
  `docs/probes/2026-10-19-acp-restart-resume/`.

## Tasks

1. Descriptor flag and factory answer (test: the factory reports the descriptor's flag; every
   shipped descriptor's value is pinned).
2. Runtime `session/load` start path (tests: replay suppressed, nudge sent as the first prompt, no
   `session/new`; refusal and missing `loadSession` fail the start).
3. Factory threading (tests: the resume id reaches `session/load` for a supporting descriptor; an
   unsupported or review-flow resume fails before spawn).
4. Forwarder cursor for a resumed ACP agent (tests: numbering continues from the server's cursor and
   no `SessionStarted` is resent; a fresh launch is unchanged).
5. Terminal or rejected binding fails the native resume and falls through to continue (test).
6. Graceful-stop probe against every installed ACP vendor; findings; flip the flags that passed.
7. Daemon and Core unit suites, AOT publish check, PR.
