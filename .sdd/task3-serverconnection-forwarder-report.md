# AI-688 Option B — Task 3 report: ServerConnection ACP hub methods + AcpTranscriptForwarder + reconnect re-bind

## STATUS: DONE

## Commit
See `git log -1` on `ai-688-cursor-hosted-agent-prototype` for the exact sha (commit message:
"AI-688 Option B task 3: ServerConnection ACP hub methods + AcpTranscriptForwarder + reconnect re-bind").
Not pushed.

## Test summary
`dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj` (full suite):
**2703 total, 2702 passed, 1 skipped (gated live E2E, `KCAP_ACP_LIVE=1`), 0 failed.** The known
pre-existing flaky `HttpClientExtensionsRetryTests` did not appear/flake in this run.

New tests added (17 total, all genuinely red→green — see "TDD verification" below):
- `test/Capacitor.Cli.Tests.Unit/Acp/AcpTranscriptForwarderTests.cs` (7): monotonic seq assignment,
  normal-ack buffer drop, gap-resend, terminal-drop (stop + clear buffer), send-throw-then-recover
  (same seq retried, never skipped/duplicated), channel-complete → `RunAsync` returns, cancellation
  returns promptly without throwing.
- `test/Capacitor.Cli.Tests.Unit/Acp/AcpServerConnectionTests.cs` (10): `AcpSessionStartedAsync`/
  `SendAcpEventsAsync` block on `IsReady` and never invoke the raw hub call until ready, then invoke
  with the exact payload; `RegisterAcpBinding`/`UnregisterAcpBinding`/`ReBindAcpSessionsAsync`
  correctness; and the critical anti-deadlock/ordering test — the ACP rebind step runs (and
  completes) **while the real `RegistrationGate` still reports not-ready**, and only after it
  returns does the gate flip ready.

## AOT gate
Both empty (no `IL2xxx`/`IL3xxx` diagnostics):
```
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release        | grep -E 'IL[23][01][0-9]{2}'   # empty
dotnet publish src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj -c Release | grep -E 'IL[23][01][0-9]{2}'   # empty
```
(Daemon's own AOT publish checked too, since the new code lives there, not in `Capacitor.Cli`.)

`dotnet build` clean for `Capacitor.Cli.Core` and `Capacitor.Cli.Daemon` (only pre-existing, unrelated
warnings: `CA1822` on `AgentOrchestrator.LocalIpc.cs`, doc-file IDE0005 notices).

## The exact server `AcpBatchAck` semantics encoded

Read (read-only) `CapacitorHub.AcpSessionStarted`/`AcpSessionEvents` and `AcpSessionRegistry`
(`AcpSessionBinding`, `Snapshot`, `RecordPersisted`, `Advance`) in
`/Users/tony/Documents/kcap-server-wt/ai-686-acp-permission-bridge`. The hub processes a batch's
envelopes strictly in `Seq` order, per envelope:

1. `env.Seq <= AcceptedSeq` → duplicate/resent, silently skipped (no state change), loop continues.
2. `env.Seq > AcceptedSeq + 1` → **gap**: returns **immediately** (skipping the rest of the batch)
   with `AcpBatchAck(AcceptedSeq, PersistedSeq, ExpectedNextSeq: AcceptedSeq + 1)`.
3. Otherwise (`env.Seq == AcceptedSeq + 1`):
   - if the binding is already `Terminal` → dropped without advancing `AcceptedSeq`, loop continues
     (no early return);
   - else → mapped + appended, `RecordPersisted` advances `AcceptedSeq`/`PersistedSeq` atomically
     (and marks `Terminal` iff the mapped event is `SessionEnded`); an envelope whose `ContractVersion`
     isn't understood or whose kind maps to `null` instead calls `Advance` (bumps `AcceptedSeq` only,
     not `PersistedSeq`) so the daemon isn't wedged retrying something the server will never accept.
4. If the loop completes without an early gap-return, the final ack is
   `AcpBatchAck(AcceptedSeq, PersistedSeq)` — `ExpectedNextSeq` implicitly `null`.

**Forwarder rules encoded exactly as the brief specified** (`AcpTranscriptForwarder.RunAsync`/
`ProcessAck`-equivalent logic):
- `ExpectedNextSeq != null` → **gap**: resend from `ExpectedNextSeq`, sourced from the unacked buffer.
- `ExpectedNextSeq == null` **and** `AcceptedSeq < highest-seq-ever-sent` → **terminal-drop**: stop the
  loop (`IsTerminal = true`), clear the buffer, no further sends/retries.
- Otherwise (`ExpectedNextSeq == null` and `AcceptedSeq` caught up to the highest seq sent) → **normal
  ack**: drop every buffered envelope with `Seq <= AcceptedSeq`.

This is provably correct for (a) any single-envelope batch, and (b) any batch where the binding turns
terminal *during* that very batch (its own `SessionEnded` mapping) — both produce the clean
terminal-drop signature per the trace above.

## Known mismatch / residual edge case (as requested — flagging, not silently patching)

**If a binding was ALREADY terminal before a MULTI-envelope batch starts**, the server's per-envelope
loop silently drops the first envelope (it matches `AcceptedSeq + 1` exactly, so it doesn't trip the
gap check), then the **second** envelope in that same batch **does** trip the gap check — because
`AcceptedSeq` never advanced — and the server returns `ExpectedNextSeq = AcceptedSeq + 1` (the very
seq it just silently dropped). That ack is **indistinguishable on the wire from a genuine gap**: my
forwarder (correctly, per the given rules) treats it as "gap → resend from `ExpectedNextSeq`", and a
resend of the same multi-envelope batch reproduces the identical ack forever — an infinite retry loop
against a binding that will never accept anything again.

This can only arise if something ends the binding out-of-band *before* this forwarder's first
interaction with it and the forwarder then sends >1 envelope in one shot (e.g. a large opportunistic
drain after a stall) — under the intended task 4 wiring (bind → forward → drain-then-`EndAgentSessionAsync`
in that order) it shouldn't occur in the ordinary lifecycle, but it's a real gap in the protocol as
specified, not a hypothetical one. I did **not** invent extra heuristics (e.g. "no-progress-after-resend"
detection) to close it, since that's exactly the kind of thing the design spec explicitly defers:
*"Deeper resilience (backoff tuning, long-outage replay depth) is AI-689."* Recommend AI-689 track this
specific case (e.g. detect a gap whose `ExpectedNextSeq` repeats with zero net progress across N resends
and treat it as terminal). Documented in `AcpTranscriptForwarder`'s class-level doc comment too.

## What was built

- **`ServerConnection` (task 3, §2.3):**
  - `AcpSessionStartedAsync(agentId, vendor, acpSessionId, cwd, model, metadata, ct)` and
    `SendAcpEventsAsync(agentId, acpSessionId, envelopes, ct)` — both `ConnectionRetry`/`IsReady`-gated
    exactly like `EndAgentSessionAsync`/`RequestPermissionAsync`; same hub method names/argument
    order as the server (`AcpSessionStarted`, `AcpSessionEvents`).
  - `RegisterAcpBinding`/`UnregisterAcpBinding` — an in-memory `ConcurrentDictionary<string, AcpBindInfo>`
    of active bindings (task 4 populates/drains it).
  - `ReBindAcpSessionsAsync` — replays `AcpSessionStarted` for every registered binding via a
    **raw, ungated** hub invoke (`InvokeAcpSessionStartedRawAsync`), deliberately bypassing
    `AcpSessionStartedAsync`'s `IsReady` gate. This is the load-bearing anti-deadlock decision: it runs
    from inside `RegistrationGate.RunRegistrationAsync`'s bracket, i.e. *while* `IsReady` is still
    false — gating it on `IsReady` there would deadlock (readiness can only flip once this method
    returns). Mirrors why `AgentRegisteredAsync`/`AgentStatusChangedAsync` are already called ungated
    from `AgentOrchestrator.ReRegisterAgentsAsync`.
  - `ReRegisterAgentsAndAcpBindingsAsync` composes the existing `ReRegisterAgentsHook` + the ACP
    rebind (hook first, rebind second) and is what `RegisterDaemon` now passes as `reRegisterAgents` —
    so both run, in order, before `IsReady` can report true, on every (re)connect.
  - `IsReady` and the two raw invoke methods (`InvokeAcpSessionStartedRawAsync`/
    `InvokeAcpSessionEventsRawAsync`) were made `internal virtual` purely as a test seam (no behavior
    change) so unit tests can drive gating/ordering without a live SignalR transport — the same
    pattern the existing suite uses for `RegistrationGate`/`ConnectionRetry`.
  - `ConnectionRetry` gained a non-generic `Task`-returning overload (delegates to the existing
    generic one) since `AcpSessionStartedAsync` has no return value.
  - `Capacitor.Cli.Core.Models` (`CapacitorJsonContext`) gained `[JsonSerializable(typeof(AcpEventEnvelope[]))]`
    and `[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]` — both are now real
    top-level SignalR hub-argument types and need their own source-gen `JsonTypeInfo`.

- **`AcpTranscriptForwarder` (task 3, §2.3, new file `src/Capacitor.Cli.Daemon/Services/AcpTranscriptForwarder.cs`):**
  stateful pump: assigns `Seq = 0` to the pre-built initial envelope and monotonic `1, 2, …` to
  everything dequeued from `IAcpTranscriptSource.Envelopes`; keeps a `SortedDictionary<long, AcpEventEnvelope>`
  unacked buffer; batches opportunistically (drains whatever's immediately available); single-in-flight
  (one send outstanding at a time — no seq/ack races to reason about); implements the three ack rules
  above; retries a failed send indefinitely with bounded exponential backoff (capped at 30s, injectable
  for tests) from the **same** unacked batch (never skips/duplicates seq); swallows its own
  cancellation and returns promptly (mirrors `AcpHostedAgentRuntime.RunTurnWorkerAsync`'s convention);
  exposes `IsTerminal` for task 4. Per the brief's exclusions: it does **not** build/emit the
  `SessionStarted` envelope, does **not** call `AcpSessionStarted`, does **not** do the final-drain, and
  does **not** touch `AgentOrchestrator`.

## Concerns for task 4

1. The residual "already-terminal + multi-envelope-batch" ambiguity above — worth a defensive look
   when task 4 wires the drain-then-`EndAgentSessionAsync` ordering, since that's exactly what's
   supposed to prevent this forwarder from ever seeing a terminal ack for a binding it didn't already
   know about.
2. `ReBindAcpSessionsAsync` is best-effort per-binding (a failed re-bind is logged and does not stop
   other bindings or withhold daemon readiness) — matches `ReRegisterAgentsAsync`'s per-agent
   isolation, but means a binding whose re-bind keeps failing silently stops forwarding until task 4
   notices (no automatic re-registration retry beyond the next reconnect).
3. `AcpBindInfo.Metadata` exists on the wire/type (`IReadOnlyDictionary<string, string>?`) but nothing
   populates it yet — task 4 decides whether/what to pass.
