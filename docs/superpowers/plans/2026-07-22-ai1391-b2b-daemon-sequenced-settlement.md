# AI-1391 B2-b — Daemon Sequenced-Settlement Implementation Plan (kcap-cli / daemon half)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the daemon (producer) half of AI-1391 B2-b: additive sequenced-command wire DTOs, two serialized sequenced lanes with a contiguous-prefix watermark, a durable resolved-candidates ledger fed by four positive-confirmation hooks, a durable coverage journal / boot-chain attestation driving `RecordlessSurvivorsImpossible`, per-platform `StartupReapComplete` + `StartupDiscovery`, and the daemon-side heal-barrier obligations — all capability-gated on `SupportsSequencedCommands` and advertised-but-inert until the paired server PR consumes it.

**Architecture:** This is PR 2 of the paired B2-b rollout — daemon ships first (capability advertised, unused = inert). The design layers on the shipped AI-1313 Phase B substrate: `AgentPidRecordStore` (atomic temp+rename), `AgentKillQuarantine`, `OrphanReaper`, `ProcessReaper`, `ProcessIdentity`, the per-boot `_daemonEpoch`, and the `DaemonLock` per-name flock. New durable state (`CoverageJournal`, `ResolvedCandidatesLedger`, the marker-candidate source) lives in the daemon's per-name state dir under the same crash-consistency discipline as `AgentPidRecordStore`. Sequenced-command handling is a self-contained `SequencedCommandProcessor` injected with execute/liveness/send delegates so it is unit-testable without a live orchestrator, mirroring how `OrphanReaper`/`AgentKillQuarantine` are tested against isolated `DummyProcess` instances.

**Tech Stack:** .NET 10, C#, System.Text.Json source-gen (`CapacitorJsonContext`, snake_case + string enums), SignalR (`HubConnection`), TUnit on Microsoft Testing Platform.

## Global Constraints

- **.NET 10, TUnit on Microsoft Testing Platform.** Test projects are `OutputType Exe`; NEVER add `Microsoft.NET.Test.Sdk`. Every assertion is `await Assert.That(...)`.
- **No Linear issue IDs (`AI-<digits>`) in any `.c`/`.h`/`.cs` file.** `scripts/check-linear-ids.sh` (CI job "No Linear issue IDs in C# source") rejects them unconditionally in `src/**/*.cs` and (unless `// linear-id-ok: <reason>`) in `test/**/*.cs`. In code + comments say "Phase B2-b" / "the parent design" / "the sequenced-settlement design", never the issue key. This `.md` plan may reference `AI-1391` freely.
- **All wire changes are additive** — trailing optional fields with defaults; SignalR's `JsonHubProtocol` binds a single-record argument by snake_case property name, so old servers ignore unknown fields and new daemons get `default` for missing ones.
- **Everything is capability-gated on `SupportsSequencedCommands`.** Old server + new daemon = legacy unsequenced lane, watermark never advances. New server + old daemon is the server's B2-a degradation (not this PR).
- **`Epoch` is the shipped Phase B `_daemonEpoch`** (a GUID `"N"` string, fresh per boot, already stamped into children). Reuse it — do NOT mint a second epoch concept. `Seq`/`Generation`/`HighestAcceptedSeq`/`LastProcessedSeq` are `long`; `CommandId` is a GUID string.
- **Crash-consistency everywhere durable state is written:** atomic temp-file + same-directory rename; for the ledger, ledger-append BEFORE source-deletion; fail-closed I/O (unreadable/corrupt/write-failed ⇒ the safe answer). Every ledger hook and the boot-chain get crash-injection tests; the boot-chain matrix runs through the REAL `DaemonLock` lifecycle, never a hand-edited marker.
- **No test touches a real daemon or live flows** (parent §2/§8). Tests use isolated `DummyProcess` children and the `CaptureServerConnection` / fake-delegate patterns from Phase B.
- **Register every new DTO** with `[JsonSerializable(typeof(...))]` on `CapacitorJsonContext` (Models.cs) — the source-gen resolver is inserted first in the SignalR protocol chain, so an unregistered type silently drops the invocation.

## File Structure

**Core (kcap-cli Core — the shared wire contract, producer side):**
- `src/Capacitor.Cli.Core/Models.cs` — MODIFY: new DTOs + enums (see appendix), additive fields on `DaemonConnect`/`DaemonStatusReport`/`LaunchAgentCommand`, `[JsonSerializable]` registrations.

**Daemon durable-state components (new, in `Daemon.Services`):**
- `src/Capacitor.Cli.Daemon/Services/CoverageJournal.cs` — CREATE: boot-chain fold → `RecordlessSurvivorsImpossible`.
- `src/Capacitor.Cli.Daemon/Services/ResolvedCandidatesLedger.cs` — CREATE: durable positive-death-evidence outbox.
- `src/Capacitor.Cli.Daemon/Services/MarkerCandidateStore.cs` — CREATE: dedicated durable marker-candidate source for recordless survivors.
- `src/Capacitor.Cli.Daemon/Services/SequencedCommandProcessor.cs` — CREATE: two serialized lanes, watermark, identity cache, acks/rejections.

**Daemon wiring (modify):**
- `src/Capacitor.Cli.Daemon/DaemonLock.cs` — expose `PriorInstanceId` (capture-before-overwrite under the flock).
- `src/Capacitor.Cli.Daemon/DaemonRunner.cs` — record the coverage boot before `ConnectAsync`; pass durable-state deps to the orchestrator.
- `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` — own the ledger/marker-store/processor; the four ledger hooks; `ReadLiveness`; `StartupReapComplete`/`StartupDiscovery`/`UnresolvedStartupCandidates` computation; enriched `BuildStatusReport` + `DaemonConnect` payload; route sequenced commands.
- `src/Capacitor.Cli.Daemon/Services/OrphanReaper.cs` — record-pass + marker-scan confirmation callbacks into the ledger; populate blocked candidates.
- `src/Capacitor.Cli.Daemon/Services/AgentKillQuarantine.cs` — quarantine-drain confirmation callback.
- `src/Capacitor.Cli.Daemon/Services/ServerConnection.cs` — `StopAgentV2` / `AckProcessedPrefix` / `AckResolvedCandidates` / `RequestStatusReport` receive handlers; `CommandAckAsync` / `CommandRejectedAsync` one-way sends; enriched `DaemonConnect`.

**Tests (new, `test/Capacitor.Cli.Tests.Unit/Daemon/`):**
- `SequencedSettlementWireTests.cs`, `CoverageJournalTests.cs`, `DaemonLockPriorInstanceIdTests.cs`, `ResolvedCandidatesLedgerTests.cs`, `LedgerHookTests.cs`, `MarkerCandidateResolutionTests.cs`, `SequencedCommandProcessorTests.cs`, `StartupCompletenessTests.cs`, `HealBarrierReportTests.cs`. Existing `AgentOrchestratorVendorTests` (partial) reused for orchestrator-level assertions.

---

## Appendix — Wire contract (shared; the server plan consumes this verbatim)

All records are `public readonly record struct` in `src/Capacitor.Cli.Core/Models.cs`, registered on `CapacitorJsonContext`, snake_case on the wire. Enums carry `[JsonStringEnumMemberName(...)]` so the wire token is pinned EXACTLY regardless of the global naming policy; the zero value is always the safe default. `using System.Text.Json.Serialization;` is already imported by Models.cs.

**New nested / report DTOs:**

```csharp
/// Positive per-id death evidence for a prior-incarnation startup candidate.
/// Generation = daemon-lifetime monotonic ack/ordering id; (AgentId, OldEpoch) = the
/// crash-reconciliation + server-upsert identity. Flow fields come ONLY from a trusted
/// record-tracked resolved entry — never from a recordless marker kill (mutable env).
public readonly record struct ResolvedStartupCandidate(
        long    Generation,
        string  AgentId,
        string  OldEpoch,
        string? FlowRunId = null,
        string? FlowRole  = null
    );

public enum StartupCandidateUnresolvedReason {
    [JsonStringEnumMemberName("pending_marker")]        PendingMarker        = 0,
    [JsonStringEnumMemberName("legacy_unresolvable")]   LegacyUnresolvable   = 1,
    [JsonStringEnumMemberName("identity_unresolvable")] IdentityUnresolvable = 2,
}

/// A known-id prior-incarnation candidate that is blocked (keeps StartupReapComplete false).
public readonly record struct UnresolvedStartupCandidate(
        string                           AgentId,
        StartupCandidateUnresolvedReason Reason,
        string?                          FlowRunId = null,
        string?                          FlowRole  = null
    );

public enum MarkerScanState {
    [JsonStringEnumMemberName("pending")]        Pending       = 0, // conservative default (missing field / intermediate daemon)
    [JsonStringEnumMemberName("complete")]       Complete      = 1,
    [JsonStringEnumMemberName("failed")]         Failed        = 2,
    [JsonStringEnumMemberName("not_applicable")] NotApplicable = 3, // Windows (no scan) / macOS (env redacted)
}

/// Recordless-survivor discovery status; lets the server render WHY StartupReapComplete is false.
public readonly record struct StartupDiscovery(
        MarkerScanState MarkerScanState,
        DateTimeOffset? LastSuccessfulScanAt = null
    );
```

**New command / ack DTOs:**

```csharp
/// Sequenced stop primitive (capability daemons receive this instead of StopAgent).
public readonly record struct StopAgentV2(
        string AgentId,
        string Epoch,
        long   Seq,
        string CommandId
    );

public enum CommandAckState {
    [JsonStringEnumMemberName("accepted")]  Accepted  = 0, // accepted, not yet terminally processed
    [JsonStringEnumMemberName("processed")] Processed = 1, // terminal; OutcomeKind + CurrentState set
}

public enum CommandOutcomeKind {
    [JsonStringEnumMemberName("launch_executed")]       LaunchExecuted      = 0,
    [JsonStringEnumMemberName("launch_rejected")]       LaunchRejected      = 1,
    [JsonStringEnumMemberName("launch_failed_cleaned")] LaunchFailedCleaned = 2,
    [JsonStringEnumMemberName("stop_executed")]         StopExecuted        = 3,
    [JsonStringEnumMemberName("internal_error")]        InternalError       = 4,
}

/// Current liveness read live at ack time (confirmed-death precedence Live>Quarantined>Dead).
public enum AgentLiveness {
    [JsonStringEnumMemberName("live")]        Live        = 0,
    [JsonStringEnumMemberName("quarantined")] Quarantined = 1,
    [JsonStringEnumMemberName("dead")]        Dead        = 2,
    [JsonStringEnumMemberName("not_found")]   NotFound    = 3,
}

/// Answer to an exact-duplicate sequenced command — NO re-execution.
public readonly record struct CommandAck(
        string              Epoch,
        long                Seq,
        string              CommandId,
        CommandAckState     State,
        CommandOutcomeKind? OutcomeKind     = null, // set iff State == Processed
        AgentLiveness?      CurrentState    = null, // set iff State == Processed
        string?             AgentId         = null,
        string?             SessionId       = null,
        string?             RejectionReason = null
    );

public enum CommandRejectedReason {
    [JsonStringEnumMemberName("wrong_next")]           WrongNext          = 0,
    [JsonStringEnumMemberName("duplicate_collision")]  DuplicateCollision = 1,
    [JsonStringEnumMemberName("stale_epoch")]          StaleEpoch         = 2,
    [JsonStringEnumMemberName("daemon_capacity")]      DaemonCapacity     = 3,
    [JsonStringEnumMemberName("backpressure")]         Backpressure       = 4,
    [JsonStringEnumMemberName("internal_error")]       InternalError      = 5,
    [JsonStringEnumMemberName("semantic")]             Semantic           = 6,
}

/// Terminal rejection of a sequenced command (never advances the old epoch's watermark).
public readonly record struct CommandRejected(
        string                Epoch,
        long                  Seq,
        string                CommandId,
        CommandRejectedReason Reason,
        string?               AgentId = null
    );

/// Server→daemon retirement proof: may retire identity-cache entries <= UpToSeq for Epoch.
public readonly record struct AckProcessedPrefix(
        string Epoch,
        long   UpToSeq
    );

/// One resolved-candidate ack entry (sparse, per-entry prune — no head-of-line retention).
public readonly record struct ResolvedCandidateAck(
        long   Generation,
        string AgentId,
        string OldEpoch
    );

/// Server→daemon: prune individual resolved-candidate ledger entries.
public readonly record struct AckResolvedCandidates(
        ResolvedCandidateAck[] Entries
    );
```

**`RequestStatusReport`** is a **zero-argument server→daemon hub invocation** (no DTO), registered like the existing no-arg `_hub.On("AgentInstancesChanged", …)` sinks: `_hub.On("RequestStatusReport", handler)`. It consumes no `Seq`, mutates no state, and is answered by an immediate out-of-band `DaemonStatusReport`.

**Additive fields on existing records (appended last — wire-compat):**

```csharp
// LaunchAgentCommand — after FlowRole:
        string? Epoch     = null,  // present ⇒ sequenced lane; absent ⇒ legacy unsequenced lane
        long?   Seq       = null,
        string? CommandId = null

// DaemonConnect — after UnattendedVendors:
        QuarantinedAgentInfo[]?       Quarantined                   = null,
        string?                       Epoch                         = null,
        long?                         HighestAcceptedSeq            = null,
        long?                         LastProcessedSeq              = null,
        bool?                         StartupReapComplete           = null,
        ResolvedStartupCandidate[]?   ResolvedStartupCandidates     = null,
        UnresolvedStartupCandidate[]? UnresolvedStartupCandidates   = null,
        StartupDiscovery?             StartupDiscovery              = null,
        bool?                         RecordlessSurvivorsImpossible = null, // absent/false ⇒ has a recordless class
        bool                          SupportsSequencedCommands     = false // THE capability gate

// DaemonStatusReport — after Quarantined:
        string?                       Epoch                         = null,
        long?                         LastProcessedSeq              = null,
        long?                         HighestAcceptedSeq            = null,
        bool?                         StartupReapComplete           = null,
        ResolvedStartupCandidate[]?   ResolvedStartupCandidates     = null,
        UnresolvedStartupCandidate[]? UnresolvedStartupCandidates   = null,
        StartupDiscovery?             StartupDiscovery              = null
```

**Compatibility rules the server plan must honor:** absent `StartupDiscovery` ⇒ treat as `MarkerScanState.Pending` (never `Complete`); absent/false `RecordlessSurvivorsImpossible` ⇒ "has a recordless class" (per-id proof required); `SupportsSequencedCommands == false` ⇒ no watermarks, no sequenced settlement; a new daemon that receives an un-`Seq`'d `LaunchAgentCommand` runs the legacy lane and never advances `LastProcessedSeq`.

**`AgentLiveness` note (intentional):** `AgentLiveness`/`CommandOutcomeKind` stay as defined — nullable on the wire and always explicitly set when `State == Processed`. The daemon's `ReadLiveness` (Task 16) never returns `NotFound`; an agent absent from `_agents ∪ _quarantine` reads `Dead`. `NotFound` collapsing to `Dead` for liveness purposes is deliberate — both satisfy the server's confirmed-absence barriers — while `NotFound` remains a defined wire value a future daemon/path (e.g. a `StopExecuted` outcome, spec §5.5) may still emit distinctly.

---

### Task 1: Wire DTOs — report / connect side

**Files:**
- Modify: `src/Capacitor.Cli.Core/Models.cs` (add `ResolvedStartupCandidate`, `UnresolvedStartupCandidate` + reason enum, `StartupDiscovery` + `MarkerScanState` enum near the D2 self-report DTOs ~line 1298; add additive fields to `DaemonConnect` ~line 1401 and `DaemonStatusReport` ~line 1294; add `[JsonSerializable]` lines ~line 961).
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/SequencedSettlementWireTests.cs` (CREATE)

**Interfaces:**
- Produces: the report/connect DTOs + enums in the appendix; enriched `DaemonConnect` and `DaemonStatusReport` constructors (all new args optional, defaulted).
- Consumes: nothing (pure Core additions).

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class SequencedSettlementWireTests {
    static readonly JsonSerializerOptions Opts = new() {
        TypeInfoResolver = CapacitorJsonContext.Default,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Test]
    public async Task StartupDiscovery_pins_exact_wire_tokens_and_defaults_to_pending() {
        var json = JsonSerializer.Serialize(new StartupDiscovery(MarkerScanState.Complete), Opts);
        await Assert.That(json).Contains("\"marker_scan_state\":\"complete\"");

        // The zero value (missing field) is the conservative default.
        await Assert.That(default(MarkerScanState)).IsEqualTo(MarkerScanState.Pending);
    }

    [Test]
    public async Task Resolved_and_unresolved_candidates_round_trip() {
        var resolved = new ResolvedStartupCandidate(7, "a1", "old-epoch", "flow-1", "reviewer");
        var rt = JsonSerializer.Deserialize<ResolvedStartupCandidate>(JsonSerializer.Serialize(resolved, Opts), Opts);
        await Assert.That(rt).IsEqualTo(resolved);

        var blockedJson = JsonSerializer.Serialize(
            new UnresolvedStartupCandidate("a2", StartupCandidateUnresolvedReason.PendingMarker), Opts);
        await Assert.That(blockedJson).Contains("\"reason\":\"pending_marker\"");
    }

    [Test]
    public async Task DaemonStatusReport_new_fields_round_trip_and_default_null() {
        // Old-shape construction still compiles (all new args optional).
        var old = new DaemonStatusReport(2, [], []);
        await Assert.That(old.Epoch).IsNull();
        await Assert.That(old.StartupDiscovery).IsNull();

        var full = new DaemonStatusReport(
            2, [], [], Epoch: "e1", LastProcessedSeq: 5, HighestAcceptedSeq: 6,
            StartupReapComplete: true, ResolvedStartupCandidates: [],
            UnresolvedStartupCandidates: [], StartupDiscovery: new StartupDiscovery(MarkerScanState.Complete));
        var rt = JsonSerializer.Deserialize<DaemonStatusReport>(JsonSerializer.Serialize(full, Opts), Opts);
        await Assert.That(rt.Epoch).IsEqualTo("e1");
        await Assert.That(rt.StartupReapComplete).IsTrue();
        await Assert.That(rt.StartupDiscovery!.Value.MarkerScanState).IsEqualTo(MarkerScanState.Complete);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "SequencedSettlementWireTests"`
Expected: FAIL to COMPILE — `ResolvedStartupCandidate`/`StartupDiscovery`/`MarkerScanState` and the new `DaemonStatusReport` args do not exist yet.

- [ ] **Step 3: Write minimal implementation**

In `Models.cs`, add the four report/connect DTOs + two enums from the appendix immediately after the `DaemonStatusReport` record (~line 1298). Append the additive fields to `DaemonStatusReport` (after `Quarantined`) and `DaemonConnect` (after `UnattendedVendors`) exactly as the appendix shows. Add these registrations next to the existing D2 lines (~line 961):

```csharp
[JsonSerializable(typeof(ResolvedStartupCandidate))]
[JsonSerializable(typeof(ResolvedStartupCandidate[]))]
[JsonSerializable(typeof(UnresolvedStartupCandidate))]
[JsonSerializable(typeof(UnresolvedStartupCandidate[]))]
[JsonSerializable(typeof(StartupCandidateUnresolvedReason))]
[JsonSerializable(typeof(StartupDiscovery))]
[JsonSerializable(typeof(MarkerScanState))]
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "SequencedSettlementWireTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Models.cs test/Capacitor.Cli.Tests.Unit/Daemon/SequencedSettlementWireTests.cs
git commit -m "Add B2-b report/connect wire DTOs (resolved/unresolved candidates, startup discovery)"
```

---

### Task 2: Wire DTOs — command / ack side

**Files:**
- Modify: `src/Capacitor.Cli.Core/Models.cs` (add `StopAgentV2`, `CommandAck` + `CommandAckState`/`CommandOutcomeKind`/`AgentLiveness`, `CommandRejected` + `CommandRejectedReason`, `AckProcessedPrefix`, `ResolvedCandidateAck`, `AckResolvedCandidates`; add `Epoch`/`Seq`/`CommandId` to `LaunchAgentCommand` ~line 1244; register all on `CapacitorJsonContext`).
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/SequencedSettlementWireTests.cs` (extend)

**Interfaces:**
- Produces: the command/ack DTOs + enums; `LaunchAgentCommand` with `Epoch`/`Seq`/`CommandId`.
- Consumes: Task 1's `AgentLiveness` reuse in `CommandAck`.

- [ ] **Step 1: Write the failing test** (append to `SequencedSettlementWireTests`)

```csharp
    [Test]
    public async Task Command_dtos_pin_wire_tokens_and_round_trip() {
        var stop = new StopAgentV2("a1", "e1", 9, "cmd-guid");
        var rt = JsonSerializer.Deserialize<StopAgentV2>(JsonSerializer.Serialize(stop, Opts), Opts);
        await Assert.That(rt).IsEqualTo(stop);

        var ackJson = JsonSerializer.Serialize(
            new CommandAck("e1", 9, "cmd", CommandAckState.Processed,
                CommandOutcomeKind.LaunchExecuted, AgentLiveness.Live, "a1", "sess"), Opts);
        await Assert.That(ackJson).Contains("\"state\":\"processed\"");
        await Assert.That(ackJson).Contains("\"outcome_kind\":\"launch_executed\"");
        await Assert.That(ackJson).Contains("\"current_state\":\"live\"");

        var rejJson = JsonSerializer.Serialize(
            new CommandRejected("e1", 9, "cmd", CommandRejectedReason.StaleEpoch, "a1"), Opts);
        await Assert.That(rejJson).Contains("\"reason\":\"stale_epoch\"");

        var pruneJson = JsonSerializer.Serialize(
            new AckResolvedCandidates([new ResolvedCandidateAck(3, "a1", "old")]), Opts);
        await Assert.That(pruneJson).Contains("\"generation\":3");
    }

    [Test]
    public async Task LaunchAgentCommand_gains_optional_sequencing_fields() {
        var legacy = new LaunchAgentCommand("a1", "hi", "opus", null, "/repo", null, null, "claude");
        await Assert.That(legacy.Seq).IsNull(); // absent ⇒ legacy lane

        var seqd = legacy with { Epoch = "e1", Seq = 4, CommandId = "cmd" };
        var rt = JsonSerializer.Deserialize<LaunchAgentCommand>(JsonSerializer.Serialize(seqd, Opts), Opts);
        await Assert.That(rt.Seq).IsEqualTo(4L);
        await Assert.That(rt.CommandId).IsEqualTo("cmd");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "SequencedSettlementWireTests"`
Expected: FAIL to COMPILE — `StopAgentV2`/`CommandAck`/`CommandRejected` and `LaunchAgentCommand.Seq` do not exist.

- [ ] **Step 3: Write minimal implementation**

Add the command/ack DTOs + enums from the appendix after the report DTOs in `Models.cs`. Append `Epoch`/`Seq`/`CommandId` to `LaunchAgentCommand` after `FlowRole`. Register each:

```csharp
[JsonSerializable(typeof(StopAgentV2))]
[JsonSerializable(typeof(CommandAck))]
[JsonSerializable(typeof(CommandAckState))]
[JsonSerializable(typeof(CommandOutcomeKind))]
[JsonSerializable(typeof(AgentLiveness))]
[JsonSerializable(typeof(CommandRejected))]
[JsonSerializable(typeof(CommandRejectedReason))]
[JsonSerializable(typeof(AckProcessedPrefix))]
[JsonSerializable(typeof(ResolvedCandidateAck))]
[JsonSerializable(typeof(AckResolvedCandidates))]
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "SequencedSettlementWireTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Models.cs test/Capacitor.Cli.Tests.Unit/Daemon/SequencedSettlementWireTests.cs
git commit -m "Add B2-b command/ack wire DTOs (StopAgentV2, CommandAck/Rejected, acks) + LaunchAgentCommand sequencing fields"
```

---

### Task 3: `DaemonLock` captures the prior boot's `InstanceId`

The lock file's `InstanceId` is the persistent per-boot nonce — rewritten by every shipped version at every boot and NOT deleted on clean shutdown (`Dispose` only removes the PID + version files; `Dispose_DoesNotDeleteLockFile` proves the `.lock` survives). The coverage boot-chain needs the PREVIOUS boot's id, captured under the held flock before we overwrite it.

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/DaemonLock.cs` (open `FileAccess.ReadWrite`; capture content before `SetLength(0)`; add `PriorInstanceId` property).
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/DaemonLockPriorInstanceIdTests.cs` (CREATE)

**Interfaces:**
- Produces: `DaemonLock.PriorInstanceId` (`string?` — the previous holder's InstanceId, or null on a genuinely fresh lock / unreadable content).
- Consumes: nothing.

- [ ] **Step 1: Write the failing test**

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon;

namespace Capacitor.Cli.Tests.Unit.Daemon;

[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class DaemonLockPriorInstanceIdTests {
    static string Scratch() {
        var dir = Path.Combine(Path.GetTempPath(), "kcap-lock-prior", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        return dir;
    }

    [Test]
    public async Task FreshSlot_has_null_prior_instance_id() {
        var dir = Scratch();
        try {
            using var l = DaemonLock.TryAcquire("alpha");
            await Assert.That(l!.PriorInstanceId).IsNull();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); Directory.Delete(dir, true); }
    }

    [Test]
    public async Task ReAcquire_sees_the_previous_boots_instance_id() {
        var dir = Scratch();
        try {
            var first = DaemonLock.TryAcquire("alpha")!;
            var firstId = first.InstanceId;
            first.Dispose(); // lock file (with firstId) survives Dispose

            using var second = DaemonLock.TryAcquire("alpha");
            await Assert.That(second!.PriorInstanceId).IsEqualTo(firstId);
            await Assert.That(second.InstanceId).IsNotEqualTo(firstId);
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); Directory.Delete(dir, true); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "DaemonLockPriorInstanceIdTests"`
Expected: FAIL to COMPILE — `PriorInstanceId` does not exist.

- [ ] **Step 3: Write minimal implementation**

In `DaemonLock.cs`: add the field + property, thread it through the private ctor, open `FileAccess.ReadWrite`, and capture before truncation.

```csharp
    // add near PriorHolderPid:
    public string? PriorInstanceId { get; }

    // private ctor: add a `string? priorInstanceId` parameter and `PriorInstanceId = priorInstanceId;`
```

In `TryAcquire`, change the open to `FileAccess.ReadWrite`:

```csharp
            stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
```

Immediately after `var (priorUnclean, priorPid) = InspectPriorHolder(pidPath);`, capture the prior lock content (we hold the flock, so the prior holder is provably gone) BEFORE the `stream.SetLength(0)` truncation:

```csharp
        // Capture the previous holder's InstanceId from the lock-file content BEFORE we overwrite it —
        // the persistent per-boot nonce the coverage boot-chain uses to detect an intervening
        // (possibly unaware) boot. Read failures degrade to null (fail-closed downstream).
        string? priorInstanceId = null;
        try {
            if (stream.Length > 0) {
                stream.Position = 0;
                var buf = new byte[stream.Length];
                var n = stream.Read(buf, 0, buf.Length);
                priorInstanceId = System.Text.Encoding.UTF8.GetString(buf, 0, n)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();
            }
        } catch { priorInstanceId = null; }
```

Pass `priorInstanceId` into the single `new DaemonLock(...)` call site (`DaemonLock.cs:125`, the final `return`).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "DaemonLockPriorInstanceIdTests"`
Expected: PASS (2 tests). Also re-run `DaemonLockTests` — unchanged behavior.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/DaemonLock.cs test/Capacitor.Cli.Tests.Unit/Daemon/DaemonLockPriorInstanceIdTests.cs
git commit -m "DaemonLock: capture the prior boot's InstanceId under the held flock"
```

---

### Task 4: `CoverageJournal` — durable boot-chain fold → `RecordlessSurvivorsImpossible`

A durable fold in the daemon's state dir. `cumulative_covered(this boot) = tail.cumulative_covered AND chain_check_passed AND this_epoch_contained`, with a sound genesis base case and fail-closed I/O. Sticky-false by construction (the detecting boot persists `false` in its own tail).

**Single-file atomicity (spec §4.2.3: "`state_dir_initialized` marker + journal in the SAME atomic operation").** The `initialized` flag is folded INTO the journal file itself — the journal is a single JSON document `{ initialized: true, instance_id, cumulative_covered }` written by ONE temp+rename. There is NO separate `state_dir_initialized` marker file (two non-atomic writes would leave a genesis-boot crash with journal-present/marker-absent, which the next boot would read as genesis=false and permanently poison `cumulative_covered` to false — operator-reset-only). **Genesis-eligibility is therefore "the journal file is absent"** (not "a separate marker is absent"): the single atomic rename is the only durable state transition, so a crash is provably either before the rename (no journal ⇒ still genesis-eligible, re-seeds correctly) or after it (a valid, fully-initialized journal). All other pinned semantics are unchanged: genesis needs no prior lock `InstanceId`; a new/deleted dir WITH a prior lock `InstanceId` ⇒ seed false; fail-closed; sticky-false; operator-reset-only `false→true`; downgrade-sandwich detection via the captured-before-overwrite lock `InstanceId`.

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/CoverageJournal.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/CoverageJournalTests.cs` (CREATE — the boot-chain matrix, driven through the REAL `DaemonLock` lifecycle)

**Interfaces:**
- Produces: `internal sealed class CoverageJournal(string stateDir, ILogger logger)` with `bool RecordBoot(string myInstanceId, string? priorLockInstanceId, bool thisEpochContained)` — writes the new tail atomically and returns `cumulative_covered`.
- Consumes: `DaemonLock.PriorInstanceId` (Task 3), `DaemonLock.InstanceId`.

- [ ] **Step 1: Write the failing test**

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class CoverageJournalTests {
    // Real DaemonLock lifecycle so the InstanceId chain is genuine, never a hand-edited marker.
    sealed class Harness : IDisposable {
        public readonly string LockDir  = Path.Combine(Path.GetTempPath(), "kcap-cov-lock", Guid.NewGuid().ToString("N"));
        public readonly string StateDir = Path.Combine(Path.GetTempPath(), "kcap-cov-state", Guid.NewGuid().ToString("N"));
        public Harness() { Directory.CreateDirectory(LockDir); Directory.CreateDirectory(StateDir);
            DaemonLockPaths.OverrideDirectoryForTesting(LockDir); }
        // One real boot: acquire the lock, record coverage (aware), dispose.
        public bool AwareBoot(bool contained = true) {
            using var l = DaemonLock.TryAcquire("alpha")!;
            return new CoverageJournal(StateDir, NullLogger.Instance)
                .RecordBoot(l.InstanceId, l.PriorInstanceId, contained);
        }
        // An unaware/old boot: acquires the real lock (mints a fresh InstanceId) but writes NO journal.
        public void UnawareBoot() { using var l = DaemonLock.TryAcquire("alpha")!; }
        public void Dispose() { DaemonLockPaths.OverrideDirectoryForTesting(null);
            try { Directory.Delete(LockDir, true); } catch { } try { Directory.Delete(StateDir, true); } catch { } }
    }

    [Test] public async Task Genesis_first_ever_contained_boot_seeds_true() {
        using var h = new Harness();
        await Assert.That(h.AwareBoot(contained: true)).IsTrue();
    }

    [Test] public async Task Genesis_uncontained_epoch_is_false() {
        using var h = new Harness();
        await Assert.That(h.AwareBoot(contained: false)).IsFalse();
    }

    [Test] public async Task Clean_and_crashed_W1_to_W1_keep_the_chain() {
        using var h = new Harness();
        await Assert.That(h.AwareBoot()).IsTrue();
        await Assert.That(h.AwareBoot()).IsTrue(); // prior tail id == prior lock id ⇒ chain intact
    }

    [Test] public async Task Downgrade_sandwich_breaks_the_chain_permanently() {
        using var h = new Harness();
        await Assert.That(h.AwareBoot()).IsTrue();
        h.UnawareBoot();                             // old boot mints a fresh lock InstanceId, no journal
        await Assert.That(h.AwareBoot()).IsFalse();  // prior lock id != journal tail id ⇒ broken
        await Assert.That(h.AwareBoot()).IsFalse();  // sticky: the detecting boot persisted false
    }

    [Test] public async Task Aware_but_uncontained_epoch_poisons_all_later_boots() {
        using var h = new Harness();
        await Assert.That(h.AwareBoot(contained: true)).IsTrue();
        await Assert.That(h.AwareBoot(contained: false)).IsFalse(); // this_epoch_contained=false
        await Assert.That(h.AwareBoot(contained: true)).IsFalse();  // folds from false ⇒ still false
    }

    [Test] public async Task Empty_looking_used_dir_with_prior_lock_is_false() {
        using var h = new Harness();
        // A pre-existing (previously-used) state dir with NO journal file + a prior lock InstanceId.
        // Genesis-eligibility is "journal absent", but a prior lock InstanceId ⇒ un-journaled history ⇒ false.
        h.UnawareBoot(); // prior lock id exists; state dir has no journal
        await Assert.That(h.AwareBoot()).IsFalse();
    }

    [Test] public async Task Corrupt_coverage_state_is_false() {
        using var h = new Harness();
        await Assert.That(h.AwareBoot()).IsTrue();
        File.WriteAllText(Path.Combine(h.StateDir, "coverage.json"), "{ not json");
        await Assert.That(h.AwareBoot()).IsFalse();
    }

    [Test] public async Task Genesis_crash_before_the_single_rename_leaves_no_journal_and_reseeds() {
        using var h = new Harness();
        // The genesis write is ONE atomic temp+rename of a single {initialized,instance_id,cumulative_covered}
        // document. A crash BEFORE the rename leaves at most a stray .tmp — never a partial journal and never
        // a separate marker — so File.Exists(coverage.json) stays false and the next boot is still
        // genesis-eligible (re-seeds correctly). A COMPLETED rename is a valid, fully-initialized journal.
        Directory.CreateDirectory(h.StateDir);
        var journal = Path.Combine(h.StateDir, "coverage.json");
        File.WriteAllText(journal + ".tmp-deadbeef", "{ partial"); // crash-before-rename residue
        await Assert.That(File.Exists(journal)).IsFalse();

        await Assert.That(h.AwareBoot(contained: true)).IsTrue();  // journal absent + no prior lock id ⇒ genesis re-seeds
        await Assert.That(File.Exists(journal)).IsTrue();          // the completed rename is durable
        await Assert.That(h.AwareBoot(contained: true)).IsTrue();  // a valid initialized journal ⇒ chain intact
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "CoverageJournalTests"`
Expected: FAIL to COMPILE — `CoverageJournal` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Phase B2-b (sequenced-settlement design §4.2.3): the durable boot-chain attestation that
/// drives <c>RecordlessSurvivorsImpossible</c>. A boolean alone cannot witness boots by UNAWARE
/// binaries, so this folds across the lock file's persistent per-boot <c>InstanceId</c> nonce
/// (the only marker every shipped version rewrites at boot and never deletes):
///
///   cumulative_covered(this boot) = tail.cumulative_covered AND chain_check AND this_epoch_contained
///
/// where chain_check is "the immediately-preceding boot was our own recorded aware epoch"
/// (prior lock InstanceId == journal tail's recorded id). Sticky-false by construction: the
/// detecting boot persists false in its OWN tail, so every later boot inherits it. Fail-closed:
/// any missing/corrupt/unwritable state evaluates to false. The only false->true path is the
/// documented operator reset (delete the state dir AND the per-name lock -> next boot is genesis).
///
/// The spec's "state_dir_initialized marker + journal in the SAME atomic operation" is satisfied by
/// folding the <c>Initialized</c> flag INTO the journal document — a single JSON
/// {initialized, instance_id, cumulative_covered} written by ONE temp+rename. There is deliberately
/// NO separate marker file (two non-atomic writes would let a genesis-boot crash leave
/// journal-present/marker-absent → next boot reads genesis=false → permanently poisoned). Genesis
/// eligibility is therefore "the journal file is absent": the single rename is the only durable state
/// transition, so a crash is provably either before it (no journal ⇒ re-seed) or after it (a valid
/// initialized journal).
/// </summary>
internal sealed class CoverageJournal(string stateDir, ILogger logger) {
    readonly string _journalPath = Path.Combine(stateDir, "coverage.json");

    readonly record struct Journal(bool Initialized, string InstanceId, bool CumulativeCovered);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(Journal))]
    partial class Ctx : JsonSerializerContext;

    /// <summary>Fold this boot and persist the new journal atomically BEFORE Connect/spawn. Returns
    /// cumulative_covered. Never throws — an I/O failure returns false (fail-closed).</summary>
    public bool RecordBoot(string myInstanceId, string? priorLockInstanceId, bool thisEpochContained) {
        try {
            var journalExists = File.Exists(_journalPath);
            var prior         = journalExists ? ReadJournal() : null; // null ⇒ present-but-corrupt

            bool covered;
            if (!journalExists) {
                // Genesis is the ONLY seed-true base case: the journal file is absent (we are about to
                // atomically initialize it) AND the captured prior lock shows no pre-existing InstanceId
                // (a genuine first-ever boot for this name). A previously-used dir whose journal is gone
                // but whose prior lock DOES carry an InstanceId is un-journaled history that re-pointing/
                // deleting the dir cannot launder ⇒ false.
                var genesis = priorLockInstanceId is null;
                covered = genesis && thisEpochContained;
            } else if (prior is not { Initialized: true } t) {
                covered = false; // journal present but corrupt / not fully initialized -> fail-closed
            } else {
                var chainOk = priorLockInstanceId is { } pid && string.Equals(pid, t.InstanceId, StringComparison.Ordinal);
                covered = t.CumulativeCovered && chainOk && thisEpochContained;
            }

            WriteJournalAtomic(new Journal(true, myInstanceId, covered)); // persists the break in THIS entry (sticky)
            return covered;
        } catch (Exception ex) {
            logger.LogWarning(ex, "CoverageJournal: fold/persist failed — advertising RecordlessSurvivorsImpossible=false (fail-closed)");
            return false;
        }
    }

    Journal? ReadJournal() {
        try {
            return JsonSerializer.Deserialize(File.ReadAllText(_journalPath), Ctx.Default.Journal);
        } catch { return null; } // corrupt -> journal present but unparseable => false (never genesis)
    }

    void WriteJournalAtomic(Journal journal) {
        Directory.CreateDirectory(stateDir);
        var tmp = _journalPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(tmp, JsonSerializer.Serialize(journal, Ctx.Default.Journal));
        File.Move(tmp, _journalPath, overwrite: true);   // THE single atomic same-directory rename (marker folded in)
    }
}
```

Note on the `Corrupt_coverage_state_is_false` case: after the first aware boot the journal exists with `initialized:true`; a corrupt journal then lands in the `prior is not { Initialized: true } t` branch (journal present, unparseable) ⇒ false — never mistaken for genesis (genesis requires the file to be ABSENT). Correct.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "CoverageJournalTests"`
Expected: PASS (8 tests — incl. the genesis crash-before-rename case).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/CoverageJournal.cs test/Capacitor.Cli.Tests.Unit/Daemon/CoverageJournalTests.cs
git commit -m "Add CoverageJournal boot-chain fold (RecordlessSurvivorsImpossible attestation)"
```

---

### Task 5: Record the coverage boot + advertise `RecordlessSurvivorsImpossible`

Wire `CoverageJournal.RecordBoot` into daemon boot (BEFORE `ConnectAsync`/spawn), pass the result to the orchestrator, and advertise it on `DaemonConnect` + `DaemonStatusReport`. `this_epoch_contained` is true only where OS containment leaves genuinely no recordless survivor class — the Windows Job Object (`OperatingSystem.IsWindows()`); the server consumes `RecordlessSurvivorsImpossible` only on Windows, so a Linux/macOS value is inert.

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs` (compute + record before `ConnectAsync`; stash on `DaemonConfig`).
- Modify: `src/Capacitor.Cli.Daemon/DaemonConfig.cs` (add `bool RecordlessSurvivorsImpossible { get; set; }`).
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (`BuildStatusReport` + the `DaemonConnect` payload advertise it).
- Modify: `src/Capacitor.Cli.Daemon/Services/ServerConnection.cs` (`DaemonConnectAsync` passes it).
- Test: extend `AgentOrchestratorVendorTests` (partial) — the report carries the config value.

**Interfaces:**
- Consumes: `CoverageJournal.RecordBoot` (Task 4), `DaemonLock.PriorInstanceId`/`InstanceId` (Task 3).
- Produces: `DaemonConfig.RecordlessSurvivorsImpossible`; the flag on `DaemonStatusReport`/`DaemonConnect`.

- [ ] **Step 1: Write the failing test** (new file `test/Capacitor.Cli.Tests.Unit/Daemon/StartupCompletenessTests.cs`, partial of the vendor-tests class for the harness)

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

public partial class AgentOrchestratorVendorTests {
    [Test]
    public async Task Status_report_advertises_recordless_survivors_impossible_from_config() {
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(),
            configure: c => c.RecordlessSurvivorsImpossible = true);

        await Assert.That(orch.BuildStatusReport().RecordlessSurvivorsImpossible).IsTrue();
    }
}
```

Wait — `DaemonStatusReport` has no `RecordlessSurvivorsImpossible` field (it is a connect-only capability). Assert via the connect payload instead: expose an internal `BuildConnectRecordlessFlag()` seam OR assert on `orch.RecordlessSurvivorsImpossibleForTest`. Use the latter:

```csharp
        await Assert.That(orch.RecordlessSurvivorsImpossibleForTest).IsTrue();
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "advertises_recordless"`
Expected: FAIL to COMPILE — `DaemonConfig.RecordlessSurvivorsImpossible` / `RecordlessSurvivorsImpossibleForTest` do not exist.

- [ ] **Step 3: Write minimal implementation**

`DaemonConfig.cs`: add `public bool RecordlessSurvivorsImpossible { get; set; }`.

`AgentOrchestrator.cs`: store it from config in the ctor and expose it (used by the connect payload in Task 17 and the test seam):

```csharp
    // field near _daemonEpoch:
    readonly bool _recordlessSurvivorsImpossible;
    // in ctor, after _daemonEpoch assignment:
    _recordlessSurvivorsImpossible = config.RecordlessSurvivorsImpossible;
    // test seam near DaemonEpochForTest:
    internal bool RecordlessSurvivorsImpossibleForTest => _recordlessSurvivorsImpossible;
```

`ServerConnection.DaemonConnectAsync`: pass `RecordlessSurvivorsImpossible: _config.RecordlessSurvivorsImpossible` in the `DaemonConnect` ctor (see Task 17 for the full enriched payload; for now add just this named arg).

`DaemonRunner.cs`: after `config.InstanceId = daemonLock.InstanceId;` (~line 198), record the coverage boot before the services are built:

```csharp
        // Phase B2-b (sequenced-settlement §4.2.3): fold the durable coverage boot-chain BEFORE any
        // Connect/spawn. this_epoch_contained is true only where OS containment leaves NO recordless
        // survivor class (the Windows Job Object). Fail-closed inside RecordBoot.
        var coverageStateDir = Path.Combine(
            config.StateDir ?? DaemonLockPaths.Directory, DaemonLockPaths.Sanitize(config.Name));
        config.RecordlessSurvivorsImpossible = new CoverageJournal(coverageStateDir, /*logger*/ NullLogger.Instance)
            .RecordBoot(daemonLock.InstanceId, daemonLock.PriorInstanceId, thisEpochContained: OperatingSystem.IsWindows());
```

(Use the DaemonRunner's available logger factory rather than `NullLogger` if one is in scope at that point; `NullLogger.Instance` is acceptable this early in boot — add `using Microsoft.Extensions.Logging.Abstractions;`.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "advertises_recordless"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/DaemonRunner.cs src/Capacitor.Cli.Daemon/DaemonConfig.cs src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs src/Capacitor.Cli.Daemon/Services/ServerConnection.cs test/Capacitor.Cli.Tests.Unit/Daemon/StartupCompletenessTests.cs
git commit -m "Record coverage boot at startup and advertise RecordlessSurvivorsImpossible"
```

---

### Task 6: `ResolvedCandidatesLedger` — durable positive-death-evidence outbox

Durable, state-dir, atomic temp+rename (same discipline as `AgentPidRecordStore`). Daemon-lifetime monotonic `Generation` persisted WITH the ledger. Idempotent upsert keyed on the source-stable `(AgentId, OldEpoch)` — NOT `Generation` (which the pre-append source lacks). Per-entry `Ack` prunes independently (no head-of-line retention). Re-reported until acked.

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/ResolvedCandidatesLedger.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/ResolvedCandidatesLedgerTests.cs` (CREATE)

**Interfaces:**
- Produces:
  - `internal sealed class ResolvedCandidatesLedger(string stateDir, ILogger logger)`
  - `ResolvedStartupCandidate Upsert(string agentId, string oldEpoch, string? flowRunId, string? flowRole)` — assigns/returns the entry (idempotent on `(agentId, oldEpoch)`; allocates a new `Generation` only for a genuinely new key), persisted atomically before returning.
  - `IReadOnlyList<ResolvedStartupCandidate> Snapshot()` — all un-acked entries, in `Generation` order.
  - `void Ack(IEnumerable<ResolvedCandidateAck> entries)` — prune matching `(Generation, AgentId, OldEpoch)`; unknown/already-pruned keys are idempotent no-ops.
- Consumes: `ResolvedStartupCandidate`/`ResolvedCandidateAck` (Task 1/2).

- [ ] **Step 1: Write the failing test**

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class ResolvedCandidatesLedgerTests {
    static ResolvedCandidatesLedger New(out string dir) {
        dir = Path.Combine(Path.GetTempPath(), "kcap-ledger-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return new ResolvedCandidatesLedger(dir, NullLogger.Instance);
    }

    [Test] public async Task Upsert_is_idempotent_on_agent_and_epoch_and_monotonic_generation() {
        var l = New(out _);
        var a = l.Upsert("a1", "e0", null, null);
        var b = l.Upsert("a2", "e0", null, null);
        var aAgain = l.Upsert("a1", "e0", null, null); // same key -> same generation, no new entry
        await Assert.That(a.Generation).IsEqualTo(1L);
        await Assert.That(b.Generation).IsEqualTo(2L);
        await Assert.That(aAgain.Generation).IsEqualTo(1L);
        await Assert.That(l.Snapshot().Count).IsEqualTo(2);
    }

    [Test] public async Task Snapshot_and_generation_survive_restart() {
        var l = New(out var dir);
        l.Upsert("a1", "e0", "flow", "reviewer");
        var reopened = new ResolvedCandidatesLedger(dir, NullLogger.Instance);
        await Assert.That(reopened.Snapshot().Single().AgentId).IsEqualTo("a1");
        // A new key after restart must not reuse generation 1.
        await Assert.That(reopened.Upsert("a2", "e0", null, null).Generation).IsEqualTo(2L);
    }

    [Test] public async Task Ack_prunes_sparsely_without_head_of_line_blocking() {
        var l = New(out _);
        var g1 = l.Upsert("a1", "e0", null, null);
        var g2 = l.Upsert("a2", "e0", null, null);
        var g3 = l.Upsert("a3", "e0", null, null);
        l.Ack([new ResolvedCandidateAck(g1.Generation, "a1", "e0"),
               new ResolvedCandidateAck(g3.Generation, "a3", "e0")]);
        await Assert.That(l.Snapshot().Select(x => x.AgentId)).IsEquivalentTo(new[] { "a2" });
        l.Ack([new ResolvedCandidateAck(g2.Generation, "a2", "e0")]); // idempotent for already-pruned too
        l.Ack([new ResolvedCandidateAck(99, "nope", "e0")]);          // unknown -> no-op
        await Assert.That(l.Snapshot()).IsEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "ResolvedCandidatesLedgerTests"`
Expected: FAIL to COMPILE — `ResolvedCandidatesLedger` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Phase B2-b (sequenced-settlement design §4.2.4): the durable outbox of positive per-id death
/// evidence. Persisted atomically (temp+rename) in the daemon state dir; the monotonic
/// <c>Generation</c> counter persists WITH it (spans epochs + restarts). Upsert is idempotent on the
/// source-stable <c>(AgentId, OldEpoch)</c> key — the same key never mints a second entry, so a
/// crash-reconciled re-append (source leftover matched by <c>(AgentId, OldEpoch)</c>) collapses onto
/// the committed entry. <c>Generation</c> is the server-facing ack/ordering id only. Entries are
/// re-reported every connect/report until <see cref="Ack"/> prunes them sparsely.
/// </summary>
internal sealed class ResolvedCandidatesLedger {
    readonly string _path;
    readonly ILogger _logger;
    readonly object _lock = new();
    readonly Dictionary<(string AgentId, string OldEpoch), ResolvedStartupCandidate> _entries = new();
    long _nextGeneration = 1;

    public ResolvedCandidatesLedger(string stateDir, ILogger logger) {
        Directory.CreateDirectory(stateDir);
        _path = Path.Combine(stateDir, "resolved-candidates.json");
        _logger = logger;
        Load();
    }

    public ResolvedStartupCandidate Upsert(string agentId, string oldEpoch, string? flowRunId, string? flowRole) {
        lock (_lock) {
            var key = (agentId, oldEpoch);
            if (_entries.TryGetValue(key, out var existing)) return existing; // idempotent — no new generation
            var entry = new ResolvedStartupCandidate(_nextGeneration++, agentId, oldEpoch, flowRunId, flowRole);
            _entries[key] = entry;
            Persist();
            return entry;
        }
    }

    public IReadOnlyList<ResolvedStartupCandidate> Snapshot() {
        lock (_lock) return [.. _entries.Values.OrderBy(e => e.Generation)];
    }

    public void Ack(IEnumerable<ResolvedCandidateAck> entries) {
        lock (_lock) {
            var changed = false;
            foreach (var a in entries) {
                var key = (a.AgentId, a.OldEpoch);
                if (_entries.TryGetValue(key, out var e) && e.Generation == a.Generation)
                    changed |= _entries.Remove(key);
            }
            if (changed) Persist();
        }
    }

    // ── durable state (persist the entries AND the generation high-water together) ────────────────
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(Persisted))]
    partial class Ctx : JsonSerializerContext;

    readonly record struct Persisted(long NextGeneration, ResolvedStartupCandidate[] Entries);

    void Load() {
        try {
            if (!File.Exists(_path)) return;
            var p = JsonSerializer.Deserialize(File.ReadAllText(_path), Ctx.Default.Persisted);
            _nextGeneration = Math.Max(1, p.NextGeneration);
            foreach (var e in p.Entries ?? []) _entries[(e.AgentId, e.OldEpoch)] = e;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "ResolvedCandidatesLedger: unreadable ledger — starting empty (re-derived from sources next boot)");
        }
    }

    void Persist() {
        var tmp = _path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(tmp, JsonSerializer.Serialize(
            new Persisted(_nextGeneration, [.. _entries.Values]), Ctx.Default.Persisted));
        File.Move(tmp, _path, overwrite: true); // atomic same-directory rename
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "ResolvedCandidatesLedgerTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/ResolvedCandidatesLedger.cs test/Capacitor.Cli.Tests.Unit/Daemon/ResolvedCandidatesLedgerTests.cs
git commit -m "Add ResolvedCandidatesLedger durable positive-death-evidence outbox"
```

---

### Task 7: Hook A — `OrphanReaper` record-pass confirmed-gone → ledger (append-before-delete)

The record pass confirms a prior-incarnation record's process gone (killed, already-exited, or proven identity mismatch — `ReapByRecordAsync == true`). Emit the resolved entry to the ledger BEFORE deleting the source record. On a crash between append and delete, the restart's record pass re-derives the leftover source, `Upsert` collapses onto the committed entry (idempotent on `(AgentId, OldEpoch)`), and the delete is retried. Flow fields come from the TRUSTED record.

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/OrphanReaper.cs` (add an optional `onRecordResolved` callback; invoke it before `store.Delete` in the record pass).
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/LedgerHookTests.cs` (CREATE)

**Interfaces:**
- Consumes: `ResolvedCandidatesLedger.Upsert` (Task 6), `AgentPidRecord.DaemonEpoch`/`FlowRunId`/`FlowRole`.
- Produces: `OrphanReaper` ctor gains a trailing `Action<string agentId, string oldEpoch, string? flowRunId, string? flowRole>? onRecordResolved = null` (existing 4-arg call sites/tests unaffected).

- [ ] **Step 1: Write the failing test**

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class LedgerHookTests {
    static (AgentPidRecordStore store, ResolvedCandidatesLedger ledger, string dir) NewPair() {
        var dir = Path.Combine(Path.GetTempPath(), "kcap-hook-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return (new AgentPidRecordStore(dir, NullLogger.Instance), new ResolvedCandidatesLedger(dir, NullLogger.Instance), dir);
    }

    [Test]
    public async Task Record_pass_appends_resolved_evidence_before_deleting_the_source() {
        var (store, ledger, _) = NewPair();
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; var id = ProcessIdentity.Capture(pid)!;
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5)); // process gone -> confirmed absent

        store.Write(new AgentPidRecord("gone", pid, id, PidIdentityKind.Present, "ReviewFlow", "codex",
            "flow-1", "reviewer", "did", "old-epoch", DateTimeOffset.UtcNow));

        var reaper = new OrphanReaper(store, "did", "new-epoch", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => ledger.Upsert(a, e, fr, role));
        await reaper.ReapOnceAsync();

        var entry = ledger.Snapshot().Single();
        await Assert.That(entry.AgentId).IsEqualTo("gone");
        await Assert.That(entry.OldEpoch).IsEqualTo("old-epoch");
        await Assert.That(entry.FlowRole).IsEqualTo("reviewer"); // from the trusted record
        await Assert.That(store.ReadAll()).IsEmpty();            // source deleted after the append
    }

    [Test]
    public async Task Crash_between_append_and_delete_reconciles_idempotently_on_restart() {
        var (store, ledger, dir) = NewPair();
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; var id = ProcessIdentity.Capture(pid)!;
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5));
        store.Write(new AgentPidRecord("gone", pid, id, PidIdentityKind.Present, "ReviewFlow", "codex",
            "flow-1", "reviewer", "did", "old-epoch", DateTimeOffset.UtcNow));

        // Simulate crash-after-append-before-delete: append, but skip the delete this pass.
        var crashing = new OrphanReaper(store, "did", "new-epoch", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => { ledger.Upsert(a, e, fr, role); throw new IOException("crash before delete"); });
        try { await crashing.ReapOnceAsync(); } catch { /* the reaper swallows per-record faults */ }
        await Assert.That(store.ReadAll()).IsNotEmpty();          // leftover source
        var gen = ledger.Snapshot().Single().Generation;

        // Restart: re-derive from the leftover source; Upsert collapses onto the committed entry.
        var restarted = new OrphanReaper(store, "did", "new-epoch", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => ledger.Upsert(a, e, fr, role));
        await restarted.ReapOnceAsync();
        await Assert.That(store.ReadAll()).IsEmpty();             // leftover source now deleted
        await Assert.That(ledger.Snapshot().Single().Generation).IsEqualTo(gen); // single emit, no new generation
    }

    [Test]
    public async Task Crash_before_append_re_derives_the_record_pass_evidence_next_boot() {
        // Parent §8: crash BEFORE the durable append leaves the source only → re-derived next boot
        // (at-least-once, deduped). The record pass confirms death but the daemon dies before Upsert.
        var (store, ledger, _) = NewPair();
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; var id = ProcessIdentity.Capture(pid)!;
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5));
        store.Write(new AgentPidRecord("gone", pid, id, PidIdentityKind.Present, "ReviewFlow", "codex",
            "flow-1", "reviewer", "did", "old-epoch", DateTimeOffset.UtcNow));

        // Crash before the append: the confirmed-gone branch throws before touching the ledger.
        var crashing = new OrphanReaper(store, "did", "new-epoch", NullLogger.Instance,
            onRecordResolved: (_, _, _, _) => throw new IOException("crash before append"));
        try { await crashing.ReapOnceAsync(); } catch { /* per-record faults swallowed */ }
        await Assert.That(ledger.Snapshot()).IsEmpty();          // nothing committed
        await Assert.That(store.ReadAll()).IsNotEmpty();         // source persists

        // Next boot re-derives from the on-disk pre-append source shape (keyed (AgentId, OldEpoch)).
        var restarted = new OrphanReaper(store, "did", "new-epoch", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => ledger.Upsert(a, e, fr, role));
        await restarted.ReapOnceAsync();
        var entry = ledger.Snapshot().Single();
        await Assert.That(entry.AgentId).IsEqualTo("gone");
        await Assert.That(entry.OldEpoch).IsEqualTo("old-epoch");
        await Assert.That(store.ReadAll()).IsEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "LedgerHookTests"`
Expected: FAIL to COMPILE — the `onRecordResolved` ctor parameter does not exist.

- [ ] **Step 3: Write minimal implementation**

In `OrphanReaper.cs`, add the trailing ctor parameter and invoke it before deletion in the record pass:

```csharp
internal sealed class OrphanReaper(
        AgentPidRecordStore store, string daemonId, string currentEpoch, ILogger logger,
        Action<string, string, string?, string?>? onRecordResolved = null) {
```

In `ReapOnceAsync`, the confirmed-gone branch of the record pass — invoke the callback BEFORE `store.Delete`:

```csharp
                if (confirmedGone) {
                    // Phase B2-b (§4.2.4): ledger-append BEFORE source-deletion. A crash between the two
                    // leaves a committed entry + leftover source; the next boot re-derives it and Upsert
                    // (idempotent on (AgentId, OldEpoch)) collapses onto the committed entry, then deletes.
                    onRecordResolved?.Invoke(record.AgentId, record.DaemonEpoch, record.FlowRunId, record.FlowRole);
                    store.Delete(record.AgentId);
                    logger.LogInformation(
                        "OrphanReaper: reaped leftover agent {AgentId} (pid {Pid}) from a prior daemon run",
                        record.AgentId, record.Pid);
                }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "LedgerHookTests"`
Expected: PASS (3 tests — happy path + crash between-append-delete + crash before-append). Re-run `OrphanReaperTests` — unaffected (callback defaults null).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/OrphanReaper.cs test/Capacitor.Cli.Tests.Unit/Daemon/LedgerHookTests.cs
git commit -m "Hook OrphanReaper record-pass into the resolved-candidates ledger (append-before-delete)"
```

---

### Task 8: Hooks B & C — quarantine drain + StopAgent-fallback → ledger

Both live in the orchestrator. The **quarantine drain** (`RetryQuarantineOnceAsync`) confirms death of a current-incarnation agent; emit `(agentId, _daemonEpoch, flow…)` before deleting its record (harmless per outbox idempotency for a same-epoch id; gives the server id-level absence proof). The **StopAgent-fallback** (`TryStopByPidRecordAsync`) confirms a record's process gone; emit `(agentId, record.DaemonEpoch, record.Flow…)` before deleting.

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentKillQuarantine.cs` (`RetryAllAsync` returns the drained *entries* — not just ids — so their flow identity is available; or expose a lookup). Simpler: change the drained return to `IReadOnlyList<Entry>`.
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (own the `ResolvedCandidatesLedger`; emit in `RetryQuarantineOnceAsync` and `TryStopByPidRecordAsync`).
- Test: extend `test/Capacitor.Cli.Tests.Unit/Daemon/LedgerHookTests.cs` (quarantine drain) + an orchestrator-level assertion in `AgentOrchestratorVendorTests`.

**Interfaces:**
- Consumes: `ResolvedCandidatesLedger.Upsert`; `AgentKillQuarantine.Entry` (now returned by `RetryAllAsync`).
- Produces: `AgentKillQuarantine.RetryAllAsync` returns `IReadOnlyList<Entry>`; `AgentOrchestrator._resolvedLedger` field + test seam `ResolvedLedgerSnapshotForTest`.

- [ ] **Step 1: Write the failing test** (append to `LedgerHookTests`)

```csharp
    [Test]
    public async Task Quarantine_drain_returns_entries_and_confirmed_ones_are_emittable() {
        var q = new AgentKillQuarantine(NullLogger.Instance);
        using var dummy = DummyProcess.StartSleep(30);
        var id = ProcessIdentity.Capture(dummy.Pid)!;
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5)); // confirmed dead -> will drain

        q.Add(new AgentKillQuarantine.Entry("q1", dummy.Pid, id, "ReviewFlow", DateTimeOffset.UtcNow, "flow-1", "reviewer"));
        var drained = await q.RetryAllAsync(CancellationToken.None);

        await Assert.That(drained.Select(e => e.AgentId)).IsEquivalentTo(new[] { "q1" });
        await Assert.That(drained.Single().FlowRole).IsEqualTo("reviewer");
    }

    // ── Quarantine-drain hook — crash both sides (parent §8). The drain's durable source is the RETAINED
    // AgentPidRecord (teardown quarantines AND keeps the record); the drain emits (AgentId, epoch-at-drain,
    // flow…) then deletes the record. After a crash the record's DaemonEpoch == that same epoch, so the
    // next boot's OrphanReaper record pass reconciles on the source-stable (AgentId, OldEpoch) key (NOT
    // Generation, which the pre-append source lacks).
    [Test]
    public async Task Quarantine_drain_crash_between_append_and_delete_reconciles_single_emit() {
        var (store, ledger, _) = NewPair();
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; var id = ProcessIdentity.Capture(pid)!;
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5));
        store.Write(new AgentPidRecord("qr", pid, id, PidIdentityKind.Present, "ReviewFlow", "codex",
            "flow-1", "reviewer", "did", "drain-epoch", DateTimeOffset.UtcNow));
        var committed = ledger.Upsert("qr", "drain-epoch", "flow-1", "reviewer"); // drain appended
        // crash before DeletePidRecord → committed entry + leftover record

        var reaper = new OrphanReaper(store, "did", "new-epoch", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => ledger.Upsert(a, e, fr, role));
        await reaper.ReapOnceAsync();
        await Assert.That(store.ReadAll()).IsEmpty();
        await Assert.That(ledger.Snapshot().Single().Generation).IsEqualTo(committed.Generation); // single emit
    }

    [Test]
    public async Task Quarantine_drain_crash_before_append_re_derives_next_boot() {
        var (store, ledger, _) = NewPair();
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; var id = ProcessIdentity.Capture(pid)!;
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5));
        store.Write(new AgentPidRecord("qr", pid, id, PidIdentityKind.Present, "ReviewFlow", "codex",
            "flow-1", "reviewer", "did", "drain-epoch", DateTimeOffset.UtcNow));
        await Assert.That(ledger.Snapshot()).IsEmpty(); // crash before append → record only

        var reaper = new OrphanReaper(store, "did", "new-epoch", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => ledger.Upsert(a, e, fr, role));
        await reaper.ReapOnceAsync();
        await Assert.That(ledger.Snapshot().Single().AgentId).IsEqualTo("qr");
        await Assert.That(store.ReadAll()).IsEmpty();
    }

    // ── StopAgent-fallback hook — crash both sides (parent §8). TryStopByPidRecordAsync emits
    // (agentId, record.DaemonEpoch, record.flow…) before deleting the record; same durable source +
    // (AgentId, OldEpoch) reconciliation as the drain hook.
    [Test]
    public async Task Stop_fallback_crash_between_append_and_delete_reconciles_single_emit() {
        var (store, ledger, _) = NewPair();
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; var id = ProcessIdentity.Capture(pid)!;
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5));
        store.Write(new AgentPidRecord("sf", pid, id, PidIdentityKind.Present, "ReviewFlow", "codex",
            "flow-2", "reviewer", "did", "stop-epoch", DateTimeOffset.UtcNow));
        var committed = ledger.Upsert("sf", "stop-epoch", "flow-2", "reviewer"); // stop-fallback appended
        // crash before _pidRecords.Delete → committed entry + leftover record

        var reaper = new OrphanReaper(store, "did", "new-epoch", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => ledger.Upsert(a, e, fr, role));
        await reaper.ReapOnceAsync();
        await Assert.That(store.ReadAll()).IsEmpty();
        await Assert.That(ledger.Snapshot().Single().Generation).IsEqualTo(committed.Generation);
    }

    [Test]
    public async Task Stop_fallback_crash_before_append_re_derives_next_boot() {
        var (store, ledger, _) = NewPair();
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; var id = ProcessIdentity.Capture(pid)!;
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5));
        store.Write(new AgentPidRecord("sf", pid, id, PidIdentityKind.Present, "ReviewFlow", "codex",
            "flow-2", "reviewer", "did", "stop-epoch", DateTimeOffset.UtcNow));
        await Assert.That(ledger.Snapshot()).IsEmpty(); // crash before append

        var reaper = new OrphanReaper(store, "did", "new-epoch", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => ledger.Upsert(a, e, fr, role));
        await reaper.ReapOnceAsync();
        await Assert.That(ledger.Snapshot().Single().AgentId).IsEqualTo("sf");
        await Assert.That(store.ReadAll()).IsEmpty();
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "Quarantine_drain OR Stop_fallback"`
Expected: FAIL to COMPILE — `RetryAllAsync` returns `IReadOnlyList<string>`, not `Entry`.

- [ ] **Step 3: Write minimal implementation**

In `AgentKillQuarantine.cs`, change `RetryAllAsync` to return the drained entries:

```csharp
    public async Task<IReadOnlyList<Entry>> RetryAllAsync(CancellationToken ct) {
        var drained = new List<Entry>();
        foreach (var entry in _entries.Values) {
            try {
                if (await ProcessReaper.ReapByIdentityAsync(entry.Pid, entry.Identity, entry.AgentId, logger, ct)
                    && _entries.TryRemove(new KeyValuePair<string, Entry>(entry.AgentId, entry)))
                    drained.Add(entry);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                logger.LogWarning(ex, "AgentKillQuarantine: retry failed for {AgentId} (pid {Pid})", entry.AgentId, entry.Pid);
            }
        }
        return drained;
    }
```

In `AgentOrchestrator.cs`: add a `ResolvedCandidatesLedger? _resolvedLedger;` field, construct it in the ctor next to `_pidRecords` (`_resolvedLedger = new ResolvedCandidatesLedger(recordRoot, logger);`). Then in `RetryQuarantineOnceAsync`, emit before deleting:

```csharp
            foreach (var e in await _quarantine.RetryAllAsync(ct)) {
                _resolvedLedger?.Upsert(e.AgentId, _daemonEpoch, e.FlowRunId, e.FlowRole); // append before delete
                DeletePidRecord(e.AgentId);
            }
```

And in `TryStopByPidRecordAsync`, emit before delete:

```csharp
        var confirmedGone = await ProcessReaper.ReapByRecordAsync(record, _logger, _shutdownCts.Token);
        if (confirmedGone) {
            _resolvedLedger?.Upsert(agentId, record.DaemonEpoch, record.FlowRunId, record.FlowRole); // append before delete
            _pidRecords.Delete(agentId);
        }
        return confirmedGone;
```

Wire the OrphanReaper's `onRecordResolved` (Task 7) to `_resolvedLedger` in the ctor:

```csharp
        _orphanReaper = new OrphanReaper(_pidRecords, _daemonId, _daemonEpoch, logger,
            onRecordResolved: (a, e, fr, role) => _resolvedLedger?.Upsert(a, e, fr, role));
```

Add the test seam: `internal IReadOnlyList<ResolvedStartupCandidate> ResolvedLedgerSnapshotForTest => _resolvedLedger?.Snapshot() ?? [];`

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "Quarantine_drain OR Stop_fallback"`
Expected: PASS (drain-returns-entries + the four crash-injection tests — quarantine-drain and StopAgent-fallback, both sides each). Re-run `AgentKillQuarantineTests` — update the one existing assertion that expected `IReadOnlyList<string>` (now `.Select(e => e.AgentId)`).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/AgentKillQuarantine.cs src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs test/Capacitor.Cli.Tests.Unit/Daemon/LedgerHookTests.cs
git commit -m "Hook quarantine drain + StopAgent-fallback into the resolved-candidates ledger"
```

---

### Task 9: Hook D — marker-candidate source + env-marker recordless resolution matrix

The env-marker scan can confirm-kill a fully **recordless** prior-epoch survivor. For crash-consistency it does NOT mint a spawn-bound `AgentPidRecord` (that would violate the capture-binding rule); it writes a dedicated durable **marker-candidate source** `{AgentId, DaemonId, OldEpoch, pid}` (marker-based kill authority; NO trusted flow identity from mutable env), then resolves: **(a)** pid dead per `ProcessIdentity.IsAlive` (ESRCH or Linux zombie) ⇒ resolved + emit; **(b)** alive + triple still matches ⇒ kill → on confirmed death, resolved; **(c)** alive + mismatch/unreadable ⇒ spare, source stays PENDING, NO ledger entry (surfaces as `pending_marker`). Ledger-append BEFORE source-deletion; crash re-reads the source and re-runs (a)/(b)/(c) — re-reading the live triple so a reused pid is spared.

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/MarkerCandidateStore.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/OrphanReaper.cs` (write the source before the marker kill; emit before `store.Delete` on confirmed death; leave the source pending on spare; add an optional `MarkerCandidateStore` + `onMarkerResolved` callback; a boot reconciliation pass over persisted sources).
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/MarkerCandidateResolutionTests.cs` (CREATE)

**Interfaces:**
- Produces: `MarkerCandidateStore` (`Write`/`Delete`/`ReadAll` over `internal readonly record struct MarkerCandidate(string AgentId, string DaemonId, string OldEpoch, int Pid)`); `OrphanReaper` gains trailing optional `MarkerCandidateStore? markerStore = null, Action<string,string>? onMarkerResolved = null` (`(agentId, oldEpoch)` — no flow identity, for a fully **recordless** survivor). A resolution that finds a co-existing durable record for the same id (the Linux `identity_unavailable` case) routes the trusted flow via the Task 7 `onRecordResolved` sink instead (record epoch + record flow), never null — see `EmitAndClear`.
- Consumes: `ProcessIdentity.IsAlive`/`MatchesTri`/`Capture`/`ReadAgentEnv`, `ProcessReaper.ReapByMarkerAsync`, `AgentPidRecordStore.ReadAll`, the Task 7 `onRecordResolved` sink.

- [ ] **Step 1: Write the failing test** (Linux-gated, mirroring `OrphanReaperTests`)

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class MarkerCandidateResolutionTests {
    static (AgentPidRecordStore store, MarkerCandidateStore markers, string dir) New() {
        var dir = Path.Combine(Path.GetTempPath(), "kcap-mk-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return (new AgentPidRecordStore(dir, NullLogger.Instance), new MarkerCandidateStore(dir, NullLogger.Instance), dir);
    }

    [Test]
    public async Task Recordless_marker_kill_emits_resolved_and_deletes_the_source() {
        if (!OperatingSystem.IsLinux()) return;
        var (store, markers, _) = New();
        using var dummy = DummyProcess.StartSleep(30, new Dictionary<string, string> {
            ["KCAP_AGENT_ID"] = "rec-less", ["KCAP_DAEMON_ID"] = "did", ["KCAP_DAEMON_EPOCH"] = "old" });

        var resolved = new List<(string, string)>();
        var reaper = new OrphanReaper(store, "did", "new", NullLogger.Instance,
            markerStore: markers, onMarkerResolved: (a, e) => resolved.Add((a, e)));
        await reaper.ReapOnceAsync();

        dummy.WaitForExit(TimeSpan.FromSeconds(8));
        await Assert.That(dummy.HasExited).IsTrue();
        await Assert.That(resolved).Contains(("rec-less", "old"));
        await Assert.That(markers.ReadAll()).IsEmpty();          // source deleted after the emit
    }

    [Test]
    public async Task Alive_mismatch_spares_and_leaves_the_marker_candidate_pending() {
        if (!OperatingSystem.IsLinux()) return;
        var (store, markers, _) = New();
        // A persisted marker-candidate whose pid is now occupied by an UNRELATED process (no triple).
        using var occupant = DummyProcess.StartSleep(30); // no KCAP_* env
        markers.Write(new MarkerCandidate("stale", "did", "old", occupant.Pid));

        var resolved = new List<(string, string)>();
        var reaper = new OrphanReaper(store, "did", "new", NullLogger.Instance,
            markerStore: markers, onMarkerResolved: (a, e) => resolved.Add((a, e)));
        await reaper.ReapOnceAsync(); // boot reconciliation re-reads the source, re-runs (a)/(b)/(c)

        await Assert.That(occupant.HasExited).IsFalse();          // reused pid spared
        await Assert.That(resolved).IsEmpty();                    // (c) never emits
        await Assert.That(markers.ReadAll().Single().AgentId).IsEqualTo("stale"); // stays pending
        occupant.Kill();
    }

    [Test]
    public async Task Confirmed_dead_persisted_candidate_resolves_incl_zombie_path() {
        if (!OperatingSystem.IsLinux()) return;
        var (store, markers, _) = New();
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5)); // dead per IsAlive
        markers.Write(new MarkerCandidate("dead1", "did", "old", pid));

        var resolved = new List<(string, string)>();
        var reaper = new OrphanReaper(store, "did", "new", NullLogger.Instance,
            markerStore: markers, onMarkerResolved: (a, e) => resolved.Add((a, e)));
        await reaper.ReapOnceAsync();

        await Assert.That(resolved).Contains(("dead1", "old")); // (a) dead -> resolved+emit
        await Assert.That(markers.ReadAll()).IsEmpty();
    }

    // ── Marker-scan kill hook — crash both sides (parent §8; recordless→marker-candidate case). ──
    [Test]
    public async Task Marker_kill_crash_before_append_re_derives_from_the_persisted_source() {
        if (!OperatingSystem.IsLinux()) return;
        var (store, markers, _) = New();
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5)); // dead per IsAlive (branch a)
        markers.Write(new MarkerCandidate("mk-gone", "did", "old", pid));

        // Crash BEFORE the emit: onMarkerResolved throws before the ledger append -> no entry, source persists.
        var crashing = new OrphanReaper(store, "did", "new", NullLogger.Instance,
            markerStore: markers, onMarkerResolved: (_, _) => throw new IOException("crash before append"));
        try { await crashing.ReapOnceAsync(); } catch { /* per-source faults swallowed */ }
        await Assert.That(markers.ReadAll()).IsNotEmpty(); // source persists (never a source-less window)

        // Next boot reconciles: re-read the on-disk source, (a) dead -> single emit + delete.
        var resolved = new List<(string, string)>();
        var restarted = new OrphanReaper(store, "did", "new", NullLogger.Instance,
            markerStore: markers, onMarkerResolved: (a, e) => resolved.Add((a, e)));
        await restarted.ReapOnceAsync();
        await Assert.That(resolved).IsEquivalentTo(new[] { ("mk-gone", "old") });
        await Assert.That(markers.ReadAll()).IsEmpty();
    }

    [Test]
    public async Task Marker_kill_crash_between_append_and_source_delete_reconciles_idempotently() {
        if (!OperatingSystem.IsLinux()) return;
        var (store, markers, dir) = New();
        var ledger = new ResolvedCandidatesLedger(dir, NullLogger.Instance);
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5));
        markers.Write(new MarkerCandidate("mk-gone", "did", "old", pid));
        var committed = ledger.Upsert("mk-gone", "old", null, null); // append happened
        // crash before markerStore.Delete -> committed entry + leftover marker source

        // Next boot: reconciliation re-reads the source, (a) dead -> idempotent Upsert (key (AgentId,OldEpoch)) + delete.
        var restarted = new OrphanReaper(store, "did", "new", NullLogger.Instance,
            markerStore: markers, onMarkerResolved: (a, e) => ledger.Upsert(a, e, null, null));
        await restarted.ReapOnceAsync();
        await Assert.That(ledger.Snapshot().Single().Generation).IsEqualTo(committed.Generation); // single emit
        await Assert.That(markers.ReadAll()).IsEmpty();
    }

    [Test]
    public async Task Identity_unavailable_record_resolved_via_marker_scan_emits_trusted_flow() {
        if (!OperatingSystem.IsLinux()) return;
        var (store, markers, _) = New();
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5)); // dead per IsAlive (branch a)
        // A Linux identity_unavailable RECORD (capture failed) co-existing with a marker-candidate source.
        store.Write(new AgentPidRecord("iu", pid, "", PidIdentityKind.IdentityUnavailable, "ReviewFlow",
            "codex", "flow-9", "reviewer", "did", "old", DateTimeOffset.UtcNow));
        markers.Write(new MarkerCandidate("iu", "did", "old", pid));

        var recordResolved = new List<(string a, string e, string? fr, string? role)>();
        var markerResolved = new List<(string, string)>();
        var reaper = new OrphanReaper(store, "did", "new", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => recordResolved.Add((a, e, fr, role)),
            markerStore: markers, onMarkerResolved: (a, e) => markerResolved.Add((a, e)));
        await reaper.ReapOnceAsync();

        // The trusted record's flow is emitted (via onRecordResolved), NOT null (via onMarkerResolved).
        await Assert.That(recordResolved.Single().role).IsEqualTo("reviewer");
        await Assert.That(recordResolved.Single().fr).IsEqualTo("flow-9");
        await Assert.That(markerResolved).IsEmpty();
        await Assert.That(store.ReadAll()).IsEmpty();       // identity_unavailable record cleared
        await Assert.That(markers.ReadAll()).IsEmpty();     // marker source cleared
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "MarkerCandidateResolutionTests"`
Expected: FAIL to COMPILE — `MarkerCandidateStore` + the new `OrphanReaper` params do not exist.

- [ ] **Step 3: Write minimal implementation**

Create `MarkerCandidateStore.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Phase B2-b (§4.2.4): durable marker-candidate sources for RECORDLESS prior-epoch
/// survivors. Kill authority is marker-based (the live env triple, re-read at kill time); there is NO
/// spawn-bound start-identity and NO trusted flow identity (the env is mutable). Same atomic
/// temp+rename + hashed-filename discipline as <see cref="AgentPidRecordStore"/>.</summary>
internal sealed class MarkerCandidateStore(string stateDir, ILogger logger) {
    readonly string _dir = Path.Combine(stateDir, "marker-candidates");

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(MarkerCandidate))]
    partial class Ctx : JsonSerializerContext;

    public void Write(MarkerCandidate c) {
        Directory.CreateDirectory(_dir);
        var final = PathFor(c.AgentId);
        var tmp   = final + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(tmp, JsonSerializer.Serialize(c, Ctx.Default.MarkerCandidate));
        File.Move(tmp, final, overwrite: true);
    }

    public bool Delete(string agentId) {
        try { var p = PathFor(agentId); if (!File.Exists(p)) return false; File.Delete(p); return true; }
        catch (Exception ex) { logger.LogWarning(ex, "MarkerCandidateStore: delete failed for {AgentId}", agentId); return false; }
    }

    public IReadOnlyList<MarkerCandidate> ReadAll() {
        if (!Directory.Exists(_dir)) return [];
        var r = new List<MarkerCandidate>();
        foreach (var p in Directory.EnumerateFiles(_dir, "*.json")) {
            try {
                var c = JsonSerializer.Deserialize(File.ReadAllText(p), Ctx.Default.MarkerCandidate);
                if (!string.IsNullOrEmpty(c.AgentId)) r.Add(c);
            } catch (Exception ex) { logger.LogWarning(ex, "MarkerCandidateStore: unparseable source {Path}", p); }
        }
        return r;
    }

    string PathFor(string agentId) => Path.Combine(_dir,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(agentId ?? ""))).ToLowerInvariant() + ".json");
}

internal readonly record struct MarkerCandidate(string AgentId, string DaemonId, string OldEpoch, int Pid);
```

In `OrphanReaper.cs`, add the trailing ctor params and restructure the env-marker scan. First the signature:

```csharp
internal sealed class OrphanReaper(
        AgentPidRecordStore store, string daemonId, string currentEpoch, ILogger logger,
        Action<string, string, string?, string?>? onRecordResolved = null,
        MarkerCandidateStore? markerStore = null,
        Action<string, string>? onMarkerResolved = null) {
```

At the start of `ScanEnvMarkersAsync`, first RECONCILE any persisted sources (crash recovery), applying (a)/(b)/(c) by re-reading the live pid — a source outlives one pass, so this covers the crash-before-kill window:

```csharp
        // Boot/heartbeat reconciliation of persisted marker-candidate sources (crash recovery). Re-read
        // the LIVE triple so a pid reused since the source was written is spared, never mis-killed.
        if (markerStore is not null) {
            foreach (var c in markerStore.ReadAll()) {
                if (ct.IsCancellationRequested) return;
                await ResolveMarkerCandidateAsync(c, ct);
            }
        }
```

In the live scan loop, when a recordless prior-incarnation survivor is positively identified (after the existing triple checks + `MatchesTri(pid, token) == true`), replace the direct `ReapByMarkerAsync`/`store.Delete` block with: write the source, then resolve it:

```csharp
            // Recordless survivor of a PRIOR incarnation of THIS daemon. Persist a durable
            // marker-candidate source BEFORE the kill (crash-consistency: a crash before the kill
            // re-runs the resolution next boot; never a source-less window, never a retroactive mint).
            var candidate = new MarkerCandidate(agentId, daemonId, epoch, pid);
            markerStore?.Write(candidate);
            await ResolveMarkerCandidateAsync(candidate, ct);
```

Add the resolution method (the matrix), keeping the record-store cleanup for the identity_unavailable case:

```csharp
    async Task ResolveMarkerCandidateAsync(MarkerCandidate c, CancellationToken ct) {
        // (a) dead per the shipped zombie-aware IsAlive (ESRCH or Linux 'Z') -> conclusively resolved.
        if (!ProcessIdentity.IsAlive(c.Pid)) { EmitAndClear(c); return; }

        // Re-read the live triple: (c) mismatch/unreadable -> SPARE, stay pending, NO emit (a triple
        // mismatch cannot distinguish PID-reuse from the process mutating its own env).
        var agentId = ProcessIdentity.ReadAgentEnv(c.Pid, "KCAP_AGENT_ID");
        var did     = ProcessIdentity.ReadAgentEnv(c.Pid, "KCAP_DAEMON_ID");
        var epoch   = ProcessIdentity.ReadAgentEnv(c.Pid, "KCAP_DAEMON_EPOCH");
        var token   = ProcessIdentity.Capture(c.Pid);
        var tripleMatches = agentId == c.AgentId && did == c.DaemonId && epoch == c.OldEpoch && token is not null;
        if (!tripleMatches) return; // (c) pending

        // (b) alive + triple still matches -> kill; on CONFIRMED death, resolve.
        if (await ProcessReaper.ReapByMarkerAsync(c.Pid, token!, c.AgentId, logger, ct)) EmitAndClear(c);
        else logger.LogWarning("OrphanReaper: marker kill of {AgentId} (pid {Pid}) not confirmed — retry next tick", c.AgentId, c.Pid);
    }

    void EmitAndClear(MarkerCandidate c) {
        // Ledger-append BEFORE source-deletion (crash reconciled next boot; idempotent). Trust boundary:
        // a fully RECORDLESS survivor's env is untrusted, so it maps to NO role (onMarkerResolved, null
        // flow). But a Linux identity_unavailable RECORD resolved via the marker scan carries TRUSTED flow
        // identity — written from the daemon's own AgentInstance into the durable record at spawn — so pull
        // FlowRunId/FlowRole (and the trusted DaemonEpoch) FROM THAT RECORD via the record-resolved sink,
        // so its role can be individually healed. Never trust flow from the mutable env.
        var record = store.ReadAll().FirstOrDefault(r => string.Equals(r.AgentId, c.AgentId, StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(record.AgentId))
            onRecordResolved?.Invoke(record.AgentId, record.DaemonEpoch, record.FlowRunId, record.FlowRole);
        else
            onMarkerResolved?.Invoke(c.AgentId, c.OldEpoch);
        store.Delete(c.AgentId);   // clears the identity_unavailable record (no-op for a pure recordless survivor)
        markerStore?.Delete(c.AgentId);
    }
```

Wire the orchestrator ctor (extend the Task 8 `_orphanReaper` construction): construct a `MarkerCandidateStore _markerCandidates = new(recordRoot, logger);` field and pass `markerStore: _markerCandidates, onMarkerResolved: (a, e) => _resolvedLedger?.Upsert(a, e, null, null)`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "MarkerCandidateResolutionTests"`
Expected: PASS (6 tests on Linux — the 3 resolution-matrix cases + marker crash-injection both sides + the identity_unavailable trusted-flow case; no-ops on macOS/Windows). Re-run `OrphanReaperTests` — unaffected (new params default null; the identity_unavailable record-clear path still works via `EmitAndClear`'s `store.Delete`).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/MarkerCandidateStore.cs src/Capacitor.Cli.Daemon/Services/OrphanReaper.cs src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs test/Capacitor.Cli.Tests.Unit/Daemon/MarkerCandidateResolutionTests.cs
git commit -m "Add marker-candidate source + env-marker recordless resolution matrix into the ledger"
```

---

### Task 10: Advertise `ResolvedStartupCandidates` + honor `AckResolvedCandidates`

The ledger's `Snapshot()` is re-reported on every `DaemonConnect` + `DaemonStatusReport` until acked; the server's `AckResolvedCandidates` prunes per-entry.

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (`BuildStatusReport` includes `ResolvedStartupCandidates`; `GetLiveAgents`-style seam feeding the connect payload; an `OnAckResolvedCandidates` handler routes to `_resolvedLedger.Ack`).
- Modify: `src/Capacitor.Cli.Daemon/Services/ServerConnection.cs` (register the `AckResolvedCandidates` receive handler; expose a `GetResolvedStartupCandidates` snapshot func for the connect payload).
- Test: extend `AgentOrchestratorVendorTests` (report carries ledger entries; ack prunes).

**Interfaces:**
- Consumes: `ResolvedCandidatesLedger.Snapshot`/`Ack` (Task 6).
- Produces: `AgentOrchestrator.HandleAckResolvedCandidates(AckResolvedCandidates)`; `ServerConnection.OnAckResolvedCandidates` event + `GetResolvedStartupCandidates` func.

- [ ] **Step 1: Write the failing test** (append to `StartupCompletenessTests` partial)

```csharp
    [Test]
    public async Task Status_report_carries_resolved_candidates_and_ack_prunes_them() {
        var server = new CaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());

        var g = orch.SeedResolvedCandidateForTest("a1", "old-epoch");   // test seam over the ledger
        await Assert.That(orch.BuildStatusReport().ResolvedStartupCandidates!.Single().AgentId).IsEqualTo("a1");

        // The seam + the underlying ledger Ack are SYNCHRONOUS (void) — no await (would be CS4008).
        orch.HandleAckResolvedCandidatesForTest(
            new AckResolvedCandidates([new ResolvedCandidateAck(g.Generation, "a1", "old-epoch")]));
        await Assert.That(orch.BuildStatusReport().ResolvedStartupCandidates).IsEmpty();
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "carries_resolved_candidates"`
Expected: FAIL to COMPILE — `SeedResolvedCandidateForTest`/`HandleAckResolvedCandidatesForTest` and the report field wiring do not exist.

- [ ] **Step 3: Write minimal implementation**

In `AgentOrchestrator.cs`, enrich `BuildStatusReport` (keep the existing positional args, add the new named ones):

```csharp
    internal DaemonStatusReport BuildStatusReport() =>
        new(ActiveCount, [.. BuildLiveAgents()], [.. QuarantineSnapshot()],
            Epoch: _daemonEpoch,                                   // (Epoch/counters finalized in Task 16)
            ResolvedStartupCandidates: [.. _resolvedLedger?.Snapshot() ?? []]);
```

Add the ack handler + test seams:

```csharp
    internal void HandleAckResolvedCandidates(AckResolvedCandidates ack) => _resolvedLedger?.Ack(ack.Entries ?? []);

    internal ResolvedStartupCandidate SeedResolvedCandidateForTest(string agentId, string oldEpoch)
        => _resolvedLedger!.Upsert(agentId, oldEpoch, null, null);
    internal void HandleAckResolvedCandidatesForTest(AckResolvedCandidates ack) => HandleAckResolvedCandidates(ack);
```

In the ctor, wire the receive handler + connect snapshot func:

```csharp
        _server.OnAckResolvedCandidates += HandleAckResolvedCandidates;
        _server.GetResolvedStartupCandidates = () => [.. _resolvedLedger?.Snapshot() ?? []];
```

In `ServerConnection.cs`, add the event + func + hub registration (one-way receive):

```csharp
    public event Action<AckResolvedCandidates>? OnAckResolvedCandidates;
    public Func<ResolvedStartupCandidate[]>? GetResolvedStartupCandidates { get; set; }
    // in the ctor, next to the other _hub.On registrations:
    _hub.On<AckResolvedCandidates>("AckResolvedCandidates", ack => { OnAckResolvedCandidates?.Invoke(ack); return Task.CompletedTask; });
```

Include the snapshot in the `DaemonConnect` payload (finalized in Task 16; add the named arg now):
`ResolvedStartupCandidates: GetResolvedStartupCandidates?.Invoke()`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "carries_resolved_candidates"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs src/Capacitor.Cli.Daemon/Services/ServerConnection.cs test/Capacitor.Cli.Tests.Unit/Daemon/StartupCompletenessTests.cs
git commit -m "Advertise ResolvedStartupCandidates and honor AckResolvedCandidates prune"
```

---

### Task 11: `StartupReapComplete` (per-platform) + `StartupDiscovery` + `UnresolvedStartupCandidates`

Compute the three completeness signals and advertise them on `DaemonConnect` + `DaemonStatusReport`.

- **`StartupDiscovery`**: Linux = `pending` until one clean env-marker-scan pass, then `complete` (`failed` on enumeration error); Windows + macOS = `not_applicable` (no scan).
- **`UnresolvedStartupCandidates`**: known-id blocked candidates = leftover records that can't resolve (identity_unavailable / cross-scheme legacy) + pending marker-candidate sources (`pending_marker`). Reason mapping: pending marker source ⇒ `pending_marker`; identity_unavailable record ⇒ `identity_unresolvable`; macOS cross-scheme (`Present`+alive+ambiguous) legacy record ⇒ `legacy_unresolvable`. Flow fields from the trusted record where available.
- **`StartupReapComplete`**: Linux = no blocked candidates AND one clean marker-scan pass succeeded; Windows-with-`RecordlessSurvivorsImpossible` = trivially true; pre-W1 Windows + macOS = record-pass-only (no blocked record-tracked candidates).

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/OrphanReaper.cs` (track `LastScanState`/`LastSuccessfulScanAt`; expose blocked candidates from the record pass + marker store).
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (compute the three signals from the reaper + marker store + ledger; add to `BuildStatusReport` + the connect payload).
- Test: extend `StartupCompletenessTests`.

**Interfaces:**
- Produces: `OrphanReaper.CurrentDiscovery` (`StartupDiscovery`), `OrphanReaper.BlockedCandidates()` (`IReadOnlyList<UnresolvedStartupCandidate>`); `AgentOrchestrator.ComputeStartupReapComplete()` (bool) feeding the report/connect.
- Consumes: `MarkerCandidateStore.ReadAll` (Task 9), `AgentPidRecordStore.ReadAll`.

- [ ] **Step 1: Write the failing test**

```csharp
    [Test]
    public async Task Startup_discovery_is_not_applicable_off_linux_and_pending_before_a_scan() {
        await using var orch = BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());
        var report = orch.BuildStatusReport();
        var expected = OperatingSystem.IsLinux() ? MarkerScanState.Complete : MarkerScanState.NotApplicable;
        // A clean boot with no candidates: after ReapOrphansOnceAsync the Linux scan is complete.
        await orch.ReapOrphansOnceAsync();
        await Assert.That(orch.BuildStatusReport().StartupDiscovery!.Value.MarkerScanState)
            .IsEqualTo(OperatingSystem.IsLinux() ? MarkerScanState.Complete : MarkerScanState.NotApplicable);
    }

    [Test]
    public async Task Pending_marker_candidate_blocks_completion_and_surfaces_reason() {
        if (!OperatingSystem.IsLinux()) return;
        await using var orch = BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedPendingMarkerCandidateForTest("blocked", "old"); // occupant with no matching triple
        var report = orch.BuildStatusReport();
        await Assert.That(report.StartupReapComplete).IsFalse();
        await Assert.That(report.UnresolvedStartupCandidates!.Single().Reason)
            .IsEqualTo(StartupCandidateUnresolvedReason.PendingMarker);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "Startup_discovery_is_not_applicable OR Pending_marker_candidate_blocks"`
Expected: FAIL to COMPILE / assert — the signals are not computed yet.

- [ ] **Step 3: Write minimal implementation**

In `OrphanReaper.cs`, track discovery state and expose blocked candidates:

```csharp
    public StartupDiscovery CurrentDiscovery { get; private set; } =
        new(OperatingSystem.IsLinux() ? MarkerScanState.Pending : MarkerScanState.NotApplicable);

    // Wrap the whole env-marker scan (the Task 9 persisted-source reconciliation loop + the live-scan loop)
    // in a try/catch and set the discovery state at BOTH exit points. Linux only — Windows/macOS have no
    // scan and CurrentDiscovery stays NotApplicable. A clean enumeration pass is Complete even if it left a
    // spared `pending_marker` candidate (that blocks completion via BlockedCandidates, not via the pass state).
    async Task ScanEnvMarkersAsync(CancellationToken ct) {
        if (!OperatingSystem.IsLinux()) return; // no scan off Linux; CurrentDiscovery stays NotApplicable
        try {
            if (markerStore is not null)
                foreach (var c in markerStore.ReadAll()) {
                    if (ct.IsCancellationRequested) return;
                    await ResolveMarkerCandidateAsync(c, ct);   // crash-recovery reconciliation (Task 9)
                }
            // ... existing live-enumeration + recordless-survivor resolution loop (Task 9) ...
            CurrentDiscovery = new StartupDiscovery(MarkerScanState.Complete, DateTimeOffset.UtcNow); // one clean pass
        } catch (Exception ex) {
            // Enumeration failure → Failed, retried on the next heartbeat; keep the last successful scan time.
            logger.LogWarning(ex, "OrphanReaper: env-marker scan failed — StartupDiscovery=failed, retry next heartbeat");
            CurrentDiscovery = new StartupDiscovery(MarkerScanState.Failed, CurrentDiscovery.LastSuccessfulScanAt);
        }
    }

    /// <summary>Known-id prior-incarnation candidates that block completion.</summary>
    public IReadOnlyList<UnresolvedStartupCandidate> BlockedCandidates(MarkerCandidateStore? markerStore) {
        var list = new List<UnresolvedStartupCandidate>();
        foreach (var r in store.ReadAll()) {
            if (string.Equals(r.DaemonEpoch, currentEpoch, StringComparison.Ordinal)) continue; // current-incarnation
            if (r.IdentityKind == PidIdentityKind.IdentityUnavailable)
                list.Add(new(r.AgentId, StartupCandidateUnresolvedReason.IdentityUnresolvable, r.FlowRunId, r.FlowRole));
            else if (OperatingSystem.IsMacOS() && ProcessIdentity.IsAlive(r.Pid) && ProcessIdentity.MatchesTri(r.Pid, r.StartIdentity) is null)
                list.Add(new(r.AgentId, StartupCandidateUnresolvedReason.LegacyUnresolvable, r.FlowRunId, r.FlowRole));
        }
        foreach (var m in markerStore?.ReadAll() ?? [])
            list.Add(new(m.AgentId, StartupCandidateUnresolvedReason.PendingMarker));
        return list;
    }
```

In `AgentOrchestrator.cs`, compute the signals and add to `BuildStatusReport`:

```csharp
    internal bool ComputeStartupReapComplete() {
        var blocked = _orphanReaper?.BlockedCandidates(_markerCandidates).Count ?? 0;
        if (blocked > 0) return false;
        if (OperatingSystem.IsLinux())
            return _orphanReaper?.CurrentDiscovery.MarkerScanState == MarkerScanState.Complete;
        if (OperatingSystem.IsWindows() && _recordlessSurvivorsImpossible) return true;
        return true; // pre-W1 Windows / macOS: record-pass-only completion (no blocked record-tracked candidates)
    }
```

Extend `BuildStatusReport`:

```csharp
    internal DaemonStatusReport BuildStatusReport() =>
        new(ActiveCount, [.. BuildLiveAgents()], [.. QuarantineSnapshot()],
            Epoch: _daemonEpoch,
            StartupReapComplete: ComputeStartupReapComplete(),
            ResolvedStartupCandidates: [.. _resolvedLedger?.Snapshot() ?? []],
            UnresolvedStartupCandidates: [.. _orphanReaper?.BlockedCandidates(_markerCandidates) ?? []],
            StartupDiscovery: _orphanReaper?.CurrentDiscovery);
```

Add the test seam:

```csharp
    internal void SeedPendingMarkerCandidateForTest(string agentId, string oldEpoch) =>
        _markerCandidates.Write(new MarkerCandidate(agentId, _daemonId, oldEpoch, 999_999)); // pid value is irrelevant to this seam
```

(The `999_999` pid is fine: `BlockedCandidates` lists every persisted marker-candidate source as `pending_marker` **without** a liveness check, and the test asserts the blocked/reason surface directly via `BuildStatusReport` — it never runs a scan, so a dead pid is never resolved away. No live-pid/`DummyProcess` plumbing is needed for this test.)

Mirror the three fields into the `DaemonConnect` payload via `ServerConnection` getters (`GetStartupReapComplete`, `GetUnresolvedStartupCandidates`, `GetStartupDiscovery`), wired in the ctor and read in `DaemonConnectAsync` (finalized alongside the counters in Task 16).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "Startup_discovery_is_not_applicable OR Pending_marker_candidate_blocks"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/OrphanReaper.cs src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs src/Capacitor.Cli.Daemon/Services/ServerConnection.cs test/Capacitor.Cli.Tests.Unit/Daemon/StartupCompletenessTests.cs
git commit -m "Compute per-platform StartupReapComplete + StartupDiscovery + UnresolvedStartupCandidates"
```

---

### Task 12: `SequencedCommandProcessor` core — exact-next accept, serial lane, contiguous watermark, synthesized-error item

The heart of the daemon side: a self-contained, delegate-injected processor (unit-tested without a live orchestrator). Under one lock it accepts only the exact-next `Seq`, records a cache entry, and enqueues to a single-reader ordered lane; a background consumer executes strictly serially and advances the contiguous `LastProcessedSeq` on each terminal outcome. If lane-item creation fails after the counter is reserved, a synthesized errored terminal item advances the watermark anyway (an advertised-accepted `Seq` with no processable item is impossible).

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/SequencedCommandProcessor.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/SequencedCommandProcessorTests.cs` (CREATE)

**Interfaces:**
- Produces:
  - `internal enum SequencedKind { Launch, Stop }`
  - `internal readonly record struct SequencedItem(SequencedKind Kind, string Epoch, long Seq, string CommandId, string AgentId)`
  - `internal readonly record struct CommandOutcome(CommandOutcomeKind Kind, string? AgentId = null, string? SessionId = null, CommandRejectedReason? RejectReason = null)`
  - `internal sealed class SequencedCommandProcessor : IAsyncDisposable` with `Task SubmitAsync(SequencedItem, Func<Task<CommandOutcome>> execute)`, `void AckPrefix(AckProcessedPrefix)` (Task 14), `string Epoch`/`long HighestAcceptedSeq`/`long LastProcessedSeq`, the private contiguity-safe `AdvanceWatermarkLocked()` (shared by the lane consumer and `SynthesizeErrorLocked`), and the `internal void CompleteLaneForTest()` seam.
- Consumes: `CommandAck`/`CommandRejected`/`AgentLiveness`/`CommandOutcomeKind`/`CommandRejectedReason` (Task 2).

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Concurrent;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class SequencedCommandProcessorTests {
    sealed class Harness {
        public readonly List<CommandAck> Acks = [];
        public readonly List<CommandRejected> Rejects = [];
        public readonly ConcurrentQueue<long> ExecOrder = new();
        public SequencedCommandProcessor P(string epoch = "e1", int bound = 256) => new(
            epoch, _ => AgentLiveness.Live,
            a => { lock (Acks) Acks.Add(a); return Task.CompletedTask; },
            r => { lock (Rejects) Rejects.Add(r); return Task.CompletedTask; },
            NullLogger.Instance, bound);
        public SequencedItem Launch(long seq, string epoch = "e1", string id = "cmd", string agent = "a")
            => new(SequencedKind.Launch, epoch, seq, id + seq, agent + seq);
    }

    [Test] public async Task Exact_next_commands_execute_serially_and_advance_the_watermark() {
        var h = new Harness(); await using var p = h.P();
        await p.SubmitAsync(h.Launch(1), () => { h.ExecOrder.Enqueue(1); return Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)); });
        await p.SubmitAsync(h.Launch(2), () => { h.ExecOrder.Enqueue(2); return Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)); });
        await Assert.That(p.LastProcessedSeq).IsEqualTo(2L);
        await Assert.That(h.ExecOrder.ToArray()).IsEquivalentTo(new[] { 1L, 2L });
    }

    [Test] public async Task Out_of_order_command_is_not_accepted() {
        var h = new Harness(); await using var p = h.P();
        var ran = false;
        await p.SubmitAsync(h.Launch(2), () => { ran = true; return Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)); });
        await Assert.That(p.HighestAcceptedSeq).IsEqualTo(0L); // Seq 2 while next is 1 -> not accepted
        await Assert.That(ran).IsFalse();
    }

    [Test] public async Task Execute_fault_becomes_internal_error_and_still_advances_the_watermark() {
        var h = new Harness(); await using var p = h.P();
        await p.SubmitAsync(h.Launch(1), () => throw new InvalidOperationException("boom"));
        await Assert.That(p.LastProcessedSeq).IsEqualTo(1L);
        await Assert.That(h.Rejects.Single().Reason).IsEqualTo(CommandRejectedReason.InternalError);
    }

    [Test] public async Task Forced_item_creation_failure_synthesizes_a_terminal_item_and_advances_monotonically() {
        // Parent §8: forced item-creation failure AFTER counter reservation -> synthesized errored terminal
        // item at N, watermark advances. AND the monotonicity hazard: when the lane is completing while an
        // earlier accepted item is still draining, the synthesized advance must NOT jump past it (which the
        // draining item's later advance would then regress below).
        var h = new Harness(); await using var p = h.P();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Item 1 accepted + enqueued; its execute BLOCKS mid-flight (still draining).
        var t1 = p.SubmitAsync(h.Launch(1),
            async () => { started.SetResult(); await gate.Task; return new CommandOutcome(CommandOutcomeKind.LaunchExecuted); });
        await started.Task;                 // item 1 is dequeued and executing

        // Complete the lane while item 1 drains, then submit item 2 -> TryWrite fails -> SynthesizeErrorLocked.
        p.CompleteLaneForTest();
        var t2 = p.SubmitAsync(h.Launch(2), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await t2;                           // the synthesized terminal completes immediately
        var afterSynth = p.LastProcessedSeq;
        await Assert.That(afterSynth).IsEqualTo(0L);   // synth at N=2 did NOT skip past the still-draining N=1

        gate.SetResult();                   // item 1 drains; contiguous prefix now reaches 1 then 2
        await t1;
        await Assert.That(afterSynth).IsLessThanOrEqualTo(p.LastProcessedSeq); // monotonic — never regressed
        await Assert.That(p.LastProcessedSeq).IsEqualTo(2L);                    // contiguous prefix reaches 2
        await Assert.That(h.Rejects.Single().Reason).IsEqualTo(CommandRejectedReason.InternalError); // synth emitted the reject
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "SequencedCommandProcessorTests"`
Expected: FAIL to COMPILE — the type does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

internal enum SequencedKind { Launch, Stop }

internal readonly record struct SequencedItem(
    SequencedKind Kind, string Epoch, long Seq, string CommandId, string AgentId);

internal readonly record struct CommandOutcome(
    CommandOutcomeKind Kind, string? AgentId = null, string? SessionId = null, CommandRejectedReason? RejectReason = null);

/// <summary>
/// Phase B2-b (sequenced-settlement design §4.2.2; parent §5.5): the daemon's two-lane sequenced
/// command handler. Exactly two command types are sequenced (Seq'd LaunchAgentCommand + StopAgentV2),
/// executed strictly serially per epoch. Acceptance (bump HighestAcceptedSeq + cache entry + enqueue)
/// is one atomic operation under <c>_lock</c>; LastProcessedSeq is the contiguous terminal prefix
/// (advances only on a terminal outcome). Self-contained + delegate-injected so it is unit-testable
/// with no live orchestrator (mirrors OrphanReaper/AgentKillQuarantine).
/// </summary>
internal sealed class SequencedCommandProcessor : IAsyncDisposable {
    sealed class CacheEntry { public required string CommandId; public bool Processed; public CommandOutcome Outcome; }
    readonly record struct LaneItem(SequencedItem Item, Func<Task<CommandOutcome>> Execute, TaskCompletionSource Done);

    readonly string _epoch;
    readonly Func<string, AgentLiveness> _readLiveness;
    readonly Func<CommandAck, Task> _sendAck;
    readonly Func<CommandRejected, Task> _sendRejected;
    readonly ILogger _logger;
    readonly int _cacheBound;

    readonly object _lock = new();
    long _highestAcceptedSeq;
    long _lastProcessedSeq;
    long _lastAckedPrefix;
    readonly Dictionary<long, CacheEntry> _cache = new();
    readonly Channel<LaneItem> _lane = Channel.CreateUnbounded<LaneItem>(new UnboundedChannelOptions { SingleReader = true });
    readonly Task _laneTask;

    public SequencedCommandProcessor(
            string epoch, Func<string, AgentLiveness> readLiveness,
            Func<CommandAck, Task> sendAck, Func<CommandRejected, Task> sendRejected,
            ILogger logger, int cacheBound = 256) {
        _epoch = epoch; _readLiveness = readLiveness; _sendAck = sendAck; _sendRejected = sendRejected;
        _logger = logger; _cacheBound = cacheBound;
        _laneTask = Task.Run(RunLaneAsync);
    }

    public string Epoch => _epoch;
    public long HighestAcceptedSeq { get { lock (_lock) return _highestAcceptedSeq; } }
    public long LastProcessedSeq   { get { lock (_lock) return _lastProcessedSeq; } }

    public Task SubmitAsync(SequencedItem item, Func<Task<CommandOutcome>> execute) {
        lock (_lock) {
            if (!string.Equals(item.Epoch, _epoch, StringComparison.Ordinal))
                return RejectLocked(item, CommandRejectedReason.StaleEpoch);   // never touches THIS epoch's lane

            if (_cache.TryGetValue(item.Seq, out var existing))
                return HandleDuplicateLocked(item, existing);                   // Task 13 answers with CommandAck

            if (item.Seq != _highestAcceptedSeq + 1)
                return HandleNonNextLocked(item);                              // Task 15 sends wrong_next

            // ACCEPT + lane-item, atomically under _lock.
            _highestAcceptedSeq = item.Seq;
            _cache[item.Seq] = new CacheEntry { CommandId = item.CommandId, Processed = false };
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_lane.Writer.TryWrite(new LaneItem(item, execute, done))) {
                SynthesizeErrorLocked(item); // shutdown/allocation race: watermark must still advance
                done.SetResult();
            }
            return done.Task;
        }
    }

    // Task 12 stubs (replaced in later tasks):
    Task HandleDuplicateLocked(SequencedItem item, CacheEntry existing) => Task.CompletedTask;
    Task HandleNonNextLocked(SequencedItem item) => Task.CompletedTask;

    Task RejectLocked(SequencedItem item, CommandRejectedReason reason) {
        _ = _sendRejected(new CommandRejected(item.Epoch, item.Seq, item.CommandId, reason, item.AgentId));
        return Task.CompletedTask;
    }

    void SynthesizeErrorLocked(SequencedItem item) {
        // Lane-item creation failed AFTER acceptance (shutdown/allocation race) — an advertised-accepted
        // Seq with no processable item is impossible, so mark this Seq terminally errored and advance the
        // watermark THROUGH THE CONTIGUOUS PREFIX only. NEVER set _lastProcessedSeq = item.Seq directly:
        // if the lane is completing while an earlier accepted item is still draining, a direct jump to N
        // would (a) advertise a non-contiguous prefix and (b) be regressed below when the earlier item's
        // consumer later advances to N-1. AdvanceWatermarkLocked is monotonic + contiguous by construction.
        _cache[item.Seq] = new CacheEntry {
            CommandId = item.CommandId, Processed = true,
            Outcome = new CommandOutcome(CommandOutcomeKind.InternalError, item.AgentId) };
        AdvanceWatermarkLocked();
        _ = _sendRejected(new CommandRejected(item.Epoch, item.Seq, item.CommandId, CommandRejectedReason.InternalError, item.AgentId));
    }

    /// <summary>The watermark is the contiguous terminal-processed prefix. Walk forward through Processed
    /// cache entries from _lastProcessedSeq+1 so a synthesized out-of-order terminal (a shutdown race)
    /// never advances past a still-draining earlier item, and no advance can ever regress the watermark
    /// (monotonic by construction). Retired seqs are always &lt;= _lastProcessedSeq, so the walk is safe.</summary>
    void AdvanceWatermarkLocked() {
        while (_cache.TryGetValue(_lastProcessedSeq + 1, out var next) && next.Processed)
            _lastProcessedSeq++;
    }

    /// <summary>Test seam: complete the lane writer so a subsequent accepted Submit's TryWrite fails,
    /// forcing the SynthesizeErrorLocked path deterministically (mirrors a shutdown race).</summary>
    internal void CompleteLaneForTest() => _lane.Writer.TryComplete();

    async Task RunLaneAsync() {
        await foreach (var li in _lane.Reader.ReadAllAsync()) {
            CommandOutcome outcome;
            try {
                outcome = await li.Execute();
            } catch (Exception ex) {
                _logger.LogWarning(ex, "SequencedCommandProcessor: execution faulted for seq {Seq} — internal_error", li.Item.Seq);
                outcome = new CommandOutcome(CommandOutcomeKind.InternalError, li.Item.AgentId);
                _ = _sendRejected(new CommandRejected(li.Item.Epoch, li.Item.Seq, li.Item.CommandId, CommandRejectedReason.InternalError, li.Item.AgentId));
            }

            // Task 15: an execution-time terminal rejection (daemon_capacity / semantic) emits CommandRejected.
            if (outcome.Kind == CommandOutcomeKind.LaunchRejected && outcome.RejectReason is { } r)
                _ = _sendRejected(new CommandRejected(li.Item.Epoch, li.Item.Seq, li.Item.CommandId, r, li.Item.AgentId));

            lock (_lock) {
                if (_cache.TryGetValue(li.Item.Seq, out var e)) { e.Processed = true; e.Outcome = outcome; }
                AdvanceWatermarkLocked(); // contiguous terminal prefix — serial lane => normally == prior + 1,
                                          // but shared with SynthesizeErrorLocked so a race can never regress it
            }
            li.Done.SetResult();
        }
    }

    public void AckPrefix(AckProcessedPrefix ack) { /* Task 14 */ }

    public async ValueTask DisposeAsync() {
        _lane.Writer.TryComplete();
        try { await _laneTask; } catch { /* best-effort */ }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "SequencedCommandProcessorTests"`
Expected: PASS (4 tests — incl. the forced-item-creation-failure synthesized-terminal case).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/SequencedCommandProcessor.cs test/Capacitor.Cli.Tests.Unit/Daemon/SequencedCommandProcessorTests.cs
git commit -m "Add SequencedCommandProcessor core (exact-next accept, serial lane, contiguous watermark, synthesized-error item)"
```

---

### Task 13: Duplicate → `CommandAck`; different-CommandId → `duplicate_collision`

An exact duplicate `(Epoch, Seq, CommandId)` is answered — never re-executed — with a `CommandAck`: `Accepted` while still processing, or `Processed` with the cached `OutcomeKind` + a LIVE `CurrentState` read (confirmed-death precedence). A different `CommandId` at an already-accepted `Seq` is a protocol violation → `CommandRejected(duplicate_collision)`.

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/SequencedCommandProcessor.cs` (replace the `HandleDuplicateLocked` stub).
- Test: extend `SequencedCommandProcessorTests`.

**Interfaces:**
- Consumes: the injected `readLiveness`/`sendAck`/`sendRejected`.
- Produces: no new public surface (behavior change).

- [ ] **Step 1: Write the failing test** (append)

```csharp
    [Test] public async Task Duplicate_of_a_processed_command_is_acked_with_outcome_and_live_state_not_reexecuted() {
        var h = new Harness(); await using var p = h.P();
        var runs = 0;
        var item = h.Launch(1);
        await p.SubmitAsync(item, () => { runs++; return Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted, "a", "sess")); });
        await p.SubmitAsync(item, () => { runs++; return Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)); });
        await Assert.That(runs).IsEqualTo(1);                                    // no re-execution
        var ack = h.Acks.Single();
        await Assert.That(ack.State).IsEqualTo(CommandAckState.Processed);
        await Assert.That(ack.OutcomeKind).IsEqualTo(CommandOutcomeKind.LaunchExecuted);
        await Assert.That(ack.CurrentState).IsEqualTo(AgentLiveness.Live);       // read live at ack time
    }

    [Test] public async Task Different_command_id_at_an_accepted_seq_is_a_duplicate_collision() {
        var h = new Harness(); await using var p = h.P();
        await p.SubmitAsync(h.Launch(1), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        // Same Seq, different CommandId:
        await p.SubmitAsync(new SequencedItem(SequencedKind.Launch, "e1", 1, "OTHER", "a1"),
            () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await Assert.That(h.Rejects.Single().Reason).IsEqualTo(CommandRejectedReason.DuplicateCollision);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "Duplicate_of_a_processed OR Different_command_id"`
Expected: FAIL — the stub sends nothing.

- [ ] **Step 3: Write minimal implementation** (replace the `HandleDuplicateLocked` stub)

```csharp
    Task HandleDuplicateLocked(SequencedItem item, CacheEntry existing) {
        if (!string.Equals(existing.CommandId, item.CommandId, StringComparison.Ordinal)) {
            // A DIFFERENT command claiming an accepted Seq — protocol invariant violation.
            _ = _sendRejected(new CommandRejected(item.Epoch, item.Seq, item.CommandId, CommandRejectedReason.DuplicateCollision, item.AgentId));
            return Task.CompletedTask;
        }

        if (!existing.Processed) {
            _ = _sendAck(new CommandAck(_epoch, item.Seq, item.CommandId, CommandAckState.Accepted));
        } else {
            // CurrentState is read LIVE at ack time (immutable execution fact vs current liveness);
            // the readLiveness delegate reads the daemon lifecycle collections with confirmed-death precedence.
            var live = _readLiveness(existing.Outcome.AgentId ?? item.AgentId);
            _ = _sendAck(new CommandAck(_epoch, item.Seq, item.CommandId, CommandAckState.Processed,
                existing.Outcome.Kind, live, existing.Outcome.AgentId ?? item.AgentId, existing.Outcome.SessionId));
        }
        return Task.CompletedTask;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "Duplicate_of_a_processed OR Different_command_id"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/SequencedCommandProcessor.cs test/Capacitor.Cli.Tests.Unit/Daemon/SequencedCommandProcessorTests.cs
git commit -m "SequencedCommandProcessor: answer duplicates with CommandAck; reject collisions"
```

---

### Task 14: `AckProcessedPrefix` retirement + backpressure (never evict unacked identity)

The identity cache is retired ONLY by a validated `AckProcessedPrefix` (current epoch, `UpToSeq <= LastProcessedSeq`, strictly monotonic). If the cache reaches its bound before an ack, further sequenced accepts are rejected with `backpressure` — an unacked entry is NEVER evicted.

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/SequencedCommandProcessor.cs` (implement `AckPrefix`; add the backpressure check to `SubmitAsync`'s accept path).
- Test: extend `SequencedCommandProcessorTests`.

**Interfaces:**
- Consumes: `AckProcessedPrefix` (Task 2).
- Produces: `AckPrefix` behavior; `CommandRejectedReason.Backpressure` emission.

- [ ] **Step 1: Write the failing test** (append)

```csharp
    [Test] public async Task Backpressure_rejects_when_the_cache_is_full_and_ack_prefix_reopens_capacity() {
        var h = new Harness(); await using var p = h.P(bound: 2);
        await p.SubmitAsync(h.Launch(1), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await p.SubmitAsync(h.Launch(2), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await p.SubmitAsync(h.Launch(3), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await Assert.That(h.Rejects.Single().Reason).IsEqualTo(CommandRejectedReason.Backpressure);
        await Assert.That(p.HighestAcceptedSeq).IsEqualTo(2L);       // 3 not accepted (unacked identity kept)

        p.AckPrefix(new AckProcessedPrefix("e1", 2));                // retire <= 2
        await p.SubmitAsync(h.Launch(3), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await Assert.That(p.LastProcessedSeq).IsEqualTo(3L);
    }

    [Test] public async Task AckPrefix_rejects_over_ahead_regressing_and_stale_epoch_without_eviction() {
        var h = new Harness(); await using var p = h.P();
        await p.SubmitAsync(h.Launch(1), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        p.AckPrefix(new AckProcessedPrefix("e1", 5));   // over-ahead (> LastProcessedSeq) -> ignored
        p.AckPrefix(new AckProcessedPrefix("WRONG", 1));// stale epoch -> ignored
        // A duplicate is still answerable (identity not evicted):
        await p.SubmitAsync(h.Launch(1), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await Assert.That(h.Acks.Count).IsEqualTo(1);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "Backpressure_rejects OR AckPrefix_rejects"`
Expected: FAIL — `AckPrefix` is a no-op and no backpressure check exists.

- [ ] **Step 3: Write minimal implementation**

Add the backpressure guard in `SubmitAsync`, immediately before the accept block (after the exact-next check passes):

```csharp
            if (item.Seq != _highestAcceptedSeq + 1)
                return HandleNonNextLocked(item);

            if (_cache.Count >= _cacheBound)                                   // never evict unacked identity
                return RejectLocked(item, CommandRejectedReason.Backpressure);

            // ACCEPT + lane-item, atomically under _lock.
```

Implement `AckPrefix`:

```csharp
    public void AckPrefix(AckProcessedPrefix ack) {
        lock (_lock) {
            if (!string.Equals(ack.Epoch, _epoch, StringComparison.Ordinal)) return; // stale epoch
            if (ack.UpToSeq > _lastProcessedSeq) return;                              // over-ahead of processed prefix
            if (ack.UpToSeq <= _lastAckedPrefix) return;                             // regressing / duplicate
            _lastAckedPrefix = ack.UpToSeq;
            foreach (var seq in _cache.Keys.Where(k => k <= ack.UpToSeq).ToList()) _cache.Remove(seq);
        }
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "Backpressure_rejects OR AckPrefix_rejects"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/SequencedCommandProcessor.cs test/Capacitor.Cli.Tests.Unit/Daemon/SequencedCommandProcessorTests.cs
git commit -m "SequencedCommandProcessor: AckProcessedPrefix retirement + backpressure (no unacked eviction)"
```

---

### Task 15: `CommandRejected` per-reason — `wrong_next`, `stale_epoch`, `daemon_capacity`, `semantic`

Replace the `HandleNonNextLocked` stub with a `wrong_next` emission (a too-far-ahead `Seq` — the transport must resync; the accept/watermark are untouched). `stale_epoch` already emits (Task 12). Execution-time terminal rejections (`daemon_capacity`, `semantic`) flow via the `execute` delegate's `CommandOutcome{ Kind = LaunchRejected, RejectReason = … }` and advance the watermark (rejected-as-item), which the lane already maps to a `CommandRejected` (Task 12 lane code).

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/SequencedCommandProcessor.cs` (replace `HandleNonNextLocked`).
- Test: extend `SequencedCommandProcessorTests`.

**Interfaces:**
- Consumes: `CommandRejectedReason` (Task 2).
- Produces: `wrong_next` on a non-next Seq; `daemon_capacity`/`semantic` via `CommandOutcome.RejectReason`.

- [ ] **Step 1: Write the failing test** (append)

```csharp
    [Test] public async Task Non_next_future_seq_is_rejected_wrong_next_without_accepting() {
        var h = new Harness(); await using var p = h.P();
        await p.SubmitAsync(h.Launch(1), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await p.SubmitAsync(h.Launch(3), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted))); // gap
        await Assert.That(h.Rejects.Single().Reason).IsEqualTo(CommandRejectedReason.WrongNext);
        await Assert.That(p.HighestAcceptedSeq).IsEqualTo(1L);
    }

    [Test] public async Task Execution_time_daemon_capacity_rejection_advances_watermark_and_emits_reject() {
        var h = new Harness(); await using var p = h.P();
        await p.SubmitAsync(h.Launch(1), () => Task.FromResult(
            new CommandOutcome(CommandOutcomeKind.LaunchRejected, "a", RejectReason: CommandRejectedReason.DaemonCapacity)));
        await Assert.That(p.LastProcessedSeq).IsEqualTo(1L);        // rejected-as-item is terminal
        await Assert.That(h.Rejects.Single().Reason).IsEqualTo(CommandRejectedReason.DaemonCapacity);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "Non_next_future_seq OR Execution_time_daemon_capacity"`
Expected: FAIL — `HandleNonNextLocked` is a silent stub.

- [ ] **Step 3: Write minimal implementation** (replace the `HandleNonNextLocked` stub)

```csharp
    Task HandleNonNextLocked(SequencedItem item) {
        // A future Seq with a gap (Seq > HighestAcceptedSeq + 1). Never accepted out of order; the
        // server's transport sequencer resyncs (nudge -> observe -> retransmit). A too-LOW uncached Seq
        // (already retired) is also wrong_next — the sender is behind the retirement frontier.
        return RejectLocked(item, CommandRejectedReason.WrongNext);
    }
```

The `daemon_capacity`/`semantic` path needs no processor change — the lane's Task 12 mapping already emits `CommandRejected` for a `LaunchRejected` outcome carrying a `RejectReason`. (The orchestrator's launch execute-closure returns that outcome; Task 16.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "Non_next_future_seq OR Execution_time_daemon_capacity"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/SequencedCommandProcessor.cs test/Capacitor.Cli.Tests.Unit/Daemon/SequencedCommandProcessorTests.cs
git commit -m "SequencedCommandProcessor: wrong_next + execution-time daemon_capacity/semantic rejections"
```

---

### Task 16: Wire the processor into `ServerConnection` + `AgentOrchestrator` (routing, legacy lane, counters, `SupportsSequencedCommands`, `RequestStatusReport`)

Route sequenced commands through the processor; keep the legacy unsequenced lane for un-`Seq`'d commands (old server). Advertise `Epoch`/`HighestAcceptedSeq`/`LastProcessedSeq` + `SupportsSequencedCommands = true` on the report/connect. Serve `AckProcessedPrefix` and `RequestStatusReport`, and send `CommandAck`/`CommandRejected` one-way.

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/ServerConnection.cs` (receive handlers `StopAgentV2`/`AckProcessedPrefix`/`RequestStatusReport`; one-way sends `CommandAckAsync`/`CommandRejectedAsync`; the full enriched `DaemonConnect` payload).
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (own the processor; refactor `HandleLaunchAgent` into a router + `HandleLaunchAgentCore`; add `HandleStopAgentV2`; add `ReadLiveness`; finalize `BuildStatusReport` counters + the connect getters).
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/HealBarrierReportTests.cs` (CREATE — a `partial class AgentOrchestratorVendorTests`, namespace `Capacitor.Cli.Tests.Unit`, reusing the existing `BuildOrchestrator`/`CaptureServerConnection`/`SeedAgentForTest` harness; Task 17 MODIFYs this same file).

**Interfaces:**
- Consumes: `SequencedCommandProcessor` (Tasks 12–15), `StopAgentV2`/`AckProcessedPrefix`/`CommandAck`/`CommandRejected` (Task 2).
- Produces:
  - `ServerConnection`: `event Func<StopAgentV2, Task>? OnStopAgentV2`, `event Action<AckProcessedPrefix>? OnAckProcessedPrefix`, `event Func<Task>? OnRequestStatusReport`, `Task CommandAckAsync(CommandAck)`, `Task CommandRejectedAsync(CommandRejected)`, connect-payload getters `Func<long?>? GetHighestAcceptedSeq`/`GetLastProcessedSeq`, `Func<bool>? GetStartupReapComplete`, `Func<UnresolvedStartupCandidate[]>? GetUnresolvedStartupCandidates`, `Func<StartupDiscovery?>? GetStartupDiscovery`.
  - `AgentOrchestrator`: `AgentLiveness ReadLiveness(string agentId)`; `HandleLaunchAgentCore`.

- [ ] **Step 1: Write the failing test**

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

public partial class AgentOrchestratorVendorTests {
    [Test]
    public async Task Report_advertises_sequencing_capability_and_counters() {
        await using var orch = BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());
        var report = orch.BuildStatusReport();
        await Assert.That(report.Epoch).IsNotNull();
        await Assert.That(report.HighestAcceptedSeq).IsEqualTo(0L); // fresh epoch, nothing accepted yet
        await Assert.That(report.LastProcessedSeq).IsEqualTo(0L);
    }

    [Test]
    public async Task ReadLiveness_follows_confirmed_death_precedence() {
        await using var orch = BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedAgentForTest("live", LaunchKind.ReviewFlow, status: "Running");
        await Assert.That(orch.ReadLiveness("live")).IsEqualTo(AgentLiveness.Live);
        await Assert.That(orch.ReadLiveness("never")).IsEqualTo(AgentLiveness.Dead);
    }

    [Test]
    public async Task ReadLiveness_racing_transitions_never_yields_a_transient_false_dead() {
        // Parent §8: CommandAck.CurrentState racing live->quarantine / quarantine->dead transitions must
        // never surface a transient false Dead. Hammer ReadLiveness while the agent moves live -> quarantine
        // (teardown of a surviving child) -> dead (drain after the child exits). The shipped ordering
        // invariant (CleanupAgentAsync adds to _quarantine BEFORE removing from _agents) keeps the id
        // continuously in _agents ∪ _quarantine until the drain, so deadness is monotonic.
        var server = new CaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());
        using var dummy = DummyProcess.StartSleep(30);
        orch.SeedAgentForTest("rev", LaunchKind.ReviewFlow, status: "Running", flowRunId: "f", flowRole: "reviewer",
            pty: new LivePtyDouble(dummy.Pid), startIdentity: ProcessIdentity.Capture(dummy.Pid));

        var observed = new System.Collections.Concurrent.ConcurrentQueue<AgentLiveness>();
        using var stop = new CancellationTokenSource();
        var hammer = Task.Run(() => { while (!stop.IsCancellationRequested) observed.Enqueue(orch.ReadLiveness("rev")); });

        await orch.CleanupAgentForTest("rev");                        // live -> quarantine (child still alive)
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5));
        await orch.RetryQuarantineForTest();                          // quarantine -> dead (drain confirms death)
        stop.Cancel(); await hammer;

        // No transient false Dead: once Dead is observed it is never followed by a non-Dead reading.
        var seq = observed.ToArray();
        var firstDead = Array.IndexOf(seq, AgentLiveness.Dead);
        if (firstDead >= 0)
            await Assert.That(seq.Skip(firstDead).All(s => s == AgentLiveness.Dead)).IsTrue();
        await Assert.That(orch.ReadLiveness("rev")).IsEqualTo(AgentLiveness.Dead); // genuinely dead at the end
    }

    /// <summary>A live-pid-backed pty double whose TerminateAsync deliberately does NOT kill (so teardown
    /// quarantines the "surviving" child). Mirrors LaunchCleanupTests' private LiveNoKillPtyProcess.</summary>
    sealed class LivePtyDouble(int pid) : IPtyProcess {
        public int  Pid       => pid;
        public bool HasExited => false;
        public int? ExitCode  => null;
        public ValueTask DisposeAsync() => default;
        public Task WaitForExitAsync(TimeSpan? _) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan?   _) => Task.CompletedTask; // deliberately does NOT kill
#pragma warning disable CS1998
        public async IAsyncEnumerable<byte[]> ReadOutputAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _ = default) { yield break; }
#pragma warning restore CS1998
        public Task WriteAsync(string _) => Task.CompletedTask;
        public Task WriteAsync(byte[] _) => Task.CompletedTask;
        public void Resize(ushort     _, ushort __) { }
        public void SendInterrupt() { }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "advertises_sequencing_capability OR ReadLiveness"`
Expected: FAIL to COMPILE — `report.HighestAcceptedSeq`/`ReadLiveness` do not exist.

- [ ] **Step 3: Write minimal implementation**

`ServerConnection.cs` — events, sends, hub registrations (add in the ctor next to the existing `_hub.On` calls):

```csharp
    public event Func<StopAgentV2, Task>? OnStopAgentV2;
    public event Action<AckProcessedPrefix>? OnAckProcessedPrefix;
    public event Func<Task>? OnRequestStatusReport;

    public virtual async Task CommandAckAsync(CommandAck ack) {
        if (!IsReady) return;
        try { await _hub.SendAsync("CommandAck", ack, cancellationToken: _ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "CommandAck send failed (old server or transient)"); }
    }
    public virtual async Task CommandRejectedAsync(CommandRejected rej) {
        if (!IsReady) return;
        try { await _hub.SendAsync("CommandRejected", rej, cancellationToken: _ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "CommandRejected send failed (old server or transient)"); }
    }

    // ctor registrations:
    _hub.On<StopAgentV2>("StopAgentV2", cmd => SafeInvoke("StopAgentV2", () => OnStopAgentV2?.Invoke(cmd)));
    _hub.On<AckProcessedPrefix>("AckProcessedPrefix", ack => { OnAckProcessedPrefix?.Invoke(ack); return Task.CompletedTask; });
    _hub.On("RequestStatusReport", () => OnRequestStatusReport?.Invoke() ?? Task.CompletedTask);
```

Add the connect getters (fields) and pass the FULL enriched payload in `DaemonConnectAsync`:

```csharp
    public Func<long?>? GetHighestAcceptedSeq { get; set; }
    public Func<long?>? GetLastProcessedSeq { get; set; }
    public Func<bool>? GetStartupReapComplete { get; set; }
    public Func<UnresolvedStartupCandidate[]>? GetUnresolvedStartupCandidates { get; set; }
    public Func<StartupDiscovery?>? GetStartupDiscovery { get; set; }
    public Func<QuarantinedAgentInfo[]>? GetQuarantined { get; set; }

    // in DaemonConnectAsync, replace the DaemonConnect ctor call with the enriched payload:
    new DaemonConnect(
        _config.Name, platform, repoPaths, _config.MaxConcurrentAgents, liveIds,
        _config.InstanceId, _config.Version, _config.SupportedVendors, MachineId.Get(), liveAgents,
        _config.UnattendedVendors,
        Quarantined:                   GetQuarantined?.Invoke(),
        Epoch:                         _config.DaemonEpoch,
        HighestAcceptedSeq:            GetHighestAcceptedSeq?.Invoke(),
        LastProcessedSeq:              GetLastProcessedSeq?.Invoke(),
        StartupReapComplete:           GetStartupReapComplete?.Invoke(),
        ResolvedStartupCandidates:     GetResolvedStartupCandidates?.Invoke(),
        UnresolvedStartupCandidates:   GetUnresolvedStartupCandidates?.Invoke(),
        StartupDiscovery:              GetStartupDiscovery?.Invoke(),
        RecordlessSurvivorsImpossible: _config.RecordlessSurvivorsImpossible,
        SupportsSequencedCommands:     true)
```

(`_config.DaemonEpoch` must be set — `DaemonRunner` currently leaves it null and the orchestrator mints it. Make `DaemonRunner` set `config.DaemonEpoch ??= Guid.NewGuid().ToString("N");` before building services, so the connect epoch and the orchestrator's `_daemonEpoch` agree.)

`AgentOrchestrator.cs`:

Add the processor field + `ReadLiveness` + construct the processor in the ctor:

```csharp
    SequencedCommandProcessor? _processor;

    // in ctor, after _resolvedLedger/_markerCandidates/_orphanReaper set up:
    _processor = new SequencedCommandProcessor(
        _daemonEpoch, ReadLiveness, _server.CommandAckAsync, _server.CommandRejectedAsync, logger);

    // wire receive handlers + connect getters:
    _server.OnStopAgentV2          += HandleStopAgentV2;
    _server.OnAckProcessedPrefix   += ack => _processor?.AckPrefix(ack);
    _server.OnRequestStatusReport  += SendDaemonStatusReportOnceAsync;
    _server.GetHighestAcceptedSeq          = () => _processor?.HighestAcceptedSeq;
    _server.GetLastProcessedSeq            = () => _processor?.LastProcessedSeq;
    _server.GetStartupReapComplete         = ComputeStartupReapComplete;
    _server.GetUnresolvedStartupCandidates = () => [.. _orphanReaper?.BlockedCandidates(_markerCandidates) ?? []];
    _server.GetStartupDiscovery            = () => _orphanReaper?.CurrentDiscovery;
    _server.GetQuarantined                 = () => [.. QuarantineSnapshot()];

    /// <summary>Phase B2-b (spec §5.5/§4.2.2): the single lifecycle-state read (confirmed-death precedence
    /// Live>Quarantined>Dead) over the same collections CleanupAgentAsync + AgentKillQuarantine mutate. The
    /// spec mandates that a duplicate CommandAck's CurrentState be read so a teardown racing the read can
    /// NEVER surface a transient false Dead. This read is lock-free (it does not take the per-agent lifecycle
    /// lock) and is SOUND ONLY BECAUSE OF THE SHIPPED CleanupAgentAsync ORDERING INVARIANT: the confirmed-
    /// death teardown adds the surviving child to `_quarantine` BEFORE removing it from `_agents`
    /// (AgentOrchestrator.cs — "Add to quarantine BEFORE removing from _agents so EffectiveCount never dips"),
    /// so an agent is CONTINUOUSLY present in `_agents ∪ _quarantine` from spawn until its quarantine entry
    /// is drained (RetryQuarantineOnceAsync) — there is no window where a live/tearing-down agent is absent
    /// from both, hence no transient false Dead. Dead is returned only after the genuine drain (confirmed
    /// death). If that ordering invariant is ever broken, this must instead take the per-agent lifecycle lock.
    /// NotFound collapses to Dead here (see the appendix note) — both satisfy confirmed-absence.</summary>
    internal AgentLiveness ReadLiveness(string agentId) {
        // Order matters: check _agents first (Live/Quarantined-by-status), then _quarantine, then Dead.
        // The add-to-quarantine-before-remove-from-_agents invariant makes this ordering false-Dead-free.
        if (_agents.TryGetValue(agentId, out var a)) return a.Status is "Starting" or "Running" ? AgentLiveness.Live : AgentLiveness.Quarantined;
        if (_quarantine?.Snapshot().Any(q => q.Id == agentId) == true) return AgentLiveness.Quarantined;
        return AgentLiveness.Dead;
    }
```

Refactor the launch handler into a router + core, and add the V2 stop router:

```csharp
    // Rename the existing HandleLaunchAgent body to HandleLaunchAgentCore, and make HandleLaunchAgent route:
    async Task HandleLaunchAgent(LaunchAgentCommand cmd) {
        if (_processor is { } proc && cmd.Seq is { } seq && cmd.Epoch is { } epoch && cmd.CommandId is { } cmdId) {
            await proc.SubmitAsync(
                new SequencedItem(SequencedKind.Launch, epoch, seq, cmdId, cmd.AgentId),
                async () => {
                    await HandleLaunchAgentCore(cmd);
                    return _agents.TryGetValue(cmd.AgentId, out var a)
                        ? new CommandOutcome(CommandOutcomeKind.LaunchExecuted, cmd.AgentId, a.SessionId)
                        : new CommandOutcome(CommandOutcomeKind.LaunchFailedCleaned, cmd.AgentId);
                });
            return;
        }
        await HandleLaunchAgentCore(cmd); // legacy unsequenced lane (old server) — never advances the watermark
    }

    async Task HandleStopAgentV2(StopAgentV2 cmd) {
        if (_processor is { } proc) {
            await proc.SubmitAsync(
                new SequencedItem(SequencedKind.Stop, cmd.Epoch, cmd.Seq, cmd.CommandId, cmd.AgentId),
                async () => { await HandleStopAgent(cmd.AgentId); return new CommandOutcome(CommandOutcomeKind.StopExecuted, cmd.AgentId); });
            return;
        }
        await HandleStopAgent(cmd.AgentId);
    }
```

Finalize `BuildStatusReport` counters:

```csharp
    internal DaemonStatusReport BuildStatusReport() =>
        new(ActiveCount, [.. BuildLiveAgents()], [.. QuarantineSnapshot()],
            Epoch: _daemonEpoch,
            LastProcessedSeq: _processor?.LastProcessedSeq,
            HighestAcceptedSeq: _processor?.HighestAcceptedSeq,
            StartupReapComplete: ComputeStartupReapComplete(),
            ResolvedStartupCandidates: [.. _resolvedLedger?.Snapshot() ?? []],
            UnresolvedStartupCandidates: [.. _orphanReaper?.BlockedCandidates(_markerCandidates) ?? []],
            StartupDiscovery: _orphanReaper?.CurrentDiscovery);
```

Dispose the processor in `DisposeAsync` (add `if (_processor is not null) await _processor.DisposeAsync();`).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "advertises_sequencing_capability OR ReadLiveness"`
Expected: PASS (counters + `ReadLiveness_follows_confirmed_death_precedence` + the racing-transition test). Run the full Unit suite once to catch the `SafeInvoke`/handler-wiring regressions.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/ServerConnection.cs src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs src/Capacitor.Cli.Daemon/DaemonRunner.cs test/Capacitor.Cli.Tests.Unit/Daemon/HealBarrierReportTests.cs
git commit -m "Wire SequencedCommandProcessor into daemon: routing, counters, capability, RequestStatusReport"
```

---

### Task 17: Heal-barrier obligations — post-stop report proves absence; `SupportsSequencedCommands` gating

Verify the daemon-side heal-barrier contract end to end through the orchestrator: after a `StopAgentV2` at `Seq = M` terminally processes, a subsequent `DaemonStatusReport` carries `LastProcessedSeq >= M` and omits the stopped id from `LiveAgents ∪ Quarantined` once confirmed dead; a still-unconfirmed-death stop keeps the id in `Quarantined` (physical-liveness set), so absence is NOT proven (the shipped CleanupAgentAsync unconfirmed-death move already guarantees this). Also confirm `RequestStatusReport` triggers an immediate report and the legacy lane never advances the watermark.

**Files:**
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/HealBarrierReportTests.cs` (MODIFY — Task 16 CREATEd it as a `partial class AgentOrchestratorVendorTests`; add these two heal-barrier tests to the same partial) — orchestrator-level, using `SeedAgentForTest` + the `SequencedCommandProcessor` via `HandleStopAgentV2ForTest`, and the **existing shipped** `HandleLaunchAgentForTest` seam (`AgentOrchestrator.cs:2103`, already routes through `HandleLaunchAgent`) for the legacy-lane launch.
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (add the one new test seam `HandleStopAgentV2ForTest`; `HandleLaunchAgentForTest` already exists and needs no change).

**Interfaces:**
- Consumes: everything above; the **existing** `AgentOrchestrator.HandleLaunchAgentForTest(LaunchAgentCommand)` seam (shipped at `AgentOrchestrator.cs:2103`) — the Task 16 refactor makes it route an un-`Seq`'d command down the legacy lane.
- Produces: `AgentOrchestrator.HandleStopAgentV2ForTest(StopAgentV2)` (the only new seam); the `CaptureServerConnection` capture lists as needed (extend the existing double).

- [ ] **Step 1: Write the failing test**

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

public partial class AgentOrchestratorVendorTests {
    [Test]
    public async Task Stop_via_v2_advances_watermark_and_a_later_report_omits_the_confirmed_dead_id() {
        var server = new CaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());
        var agent = orch.SeedAgentForTest("rev", LaunchKind.ReviewFlow, status: "Running", flowRunId: "f", flowRole: "reviewer");
        var epoch = orch.DaemonEpochForTest;

        await orch.HandleStopAgentV2ForTest(new StopAgentV2("rev", epoch, 1, "cmd-1"));

        var report = orch.BuildStatusReport();
        await Assert.That(report.LastProcessedSeq).IsEqualTo(1L);                          // stop at M=1 terminally processed
        await Assert.That(report.LiveAgents.Select(x => x.Id)).DoesNotContain("rev");      // confirmed dead (Noop pty) -> absent
        await Assert.That(report.Quarantined.Select(x => x.Id)).DoesNotContain("rev");
    }

    [Test]
    public async Task Legacy_unsequenced_launch_never_advances_the_watermark() {
        var server = new CaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());
        // No Epoch/Seq/CommandId -> legacy lane.
        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand("x", "hi", "opus", null, "/tmp", null, null, "bogus"));
        await Assert.That(orch.BuildStatusReport().HighestAcceptedSeq).IsEqualTo(0L);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "Stop_via_v2_advances OR Legacy_unsequenced_launch"`
Expected: FAIL to COMPILE — `HandleStopAgentV2ForTest` does not exist.

- [ ] **Step 3: Write minimal implementation**

Add the ONE new test seam in `AgentOrchestrator.cs` (the launch seam `HandleLaunchAgentForTest` already exists at `AgentOrchestrator.cs:2103` — the `Legacy_unsequenced_launch...` test reuses it, and the Task 16 refactor makes it route an un-`Seq`'d command down the legacy lane; no new launch seam):

```csharp
    internal Task HandleStopAgentV2ForTest(StopAgentV2 cmd) => HandleStopAgentV2(cmd);
```

(The seeded agent uses `NoopPtyProcess` — pid 0 / not-alive — so `HandleStopAgent` → `CleanupAgentAsync` confirms death immediately, removing it from `_agents` without quarantining. The stop terminally processes at `Seq = 1`, so `LastProcessedSeq == 1` and the id is absent from both collections. No production change is needed beyond the one seam; this task is verification that Tasks 12–16 compose correctly.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --filter "Stop_via_v2_advances OR Legacy_unsequenced_launch"`
Expected: PASS. Then run the FULL Unit suite (`dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`) and the Linear-ID guard (`bash scripts/check-linear-ids.sh`) — both green.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs test/Capacitor.Cli.Tests.Unit/Daemon/HealBarrierReportTests.cs
git commit -m "Verify heal-barrier post-stop report proves absence; legacy lane never advances watermark"
```

---

## Self-Review — spec-requirement → task coverage

**AI-1391 §4.2 (B2-b), daemon-side requirements:**

| Spec requirement | Task(s) |
| --- | --- |
| §4.2.1 wire: `DaemonConnect`/`DaemonStatusReport`/`LaunchAgentCommand` additive fields; `StopAgentV2`/`CommandAck`/`CommandRejected`/`AckProcessedPrefix`/`AckResolvedCandidates`/`RequestStatusReport`; `ResolvedStartupCandidate` shape; `UnresolvedStartupCandidate` reason enum `{pending_marker,legacy_unresolvable,identity_unresolvable}`; `StartupDiscovery` `{MarkerScanState∈{complete,pending,failed,not_applicable}, LastSuccessfulScanAt?}`; CapacitorJsonContext + snake_case + additive-compat | 1, 2 (appendix) |
| §4.2.1 `Epoch` = shipped `_daemonEpoch`; old server → legacy lane | 16 (routing + DaemonRunner epoch pinning) |
| §4.2.2 exactly two sequenced lanes, strictly serial per epoch | 12 |
| §4.2.2 `LastProcessedSeq` contiguous terminal prefix; exact-next acceptance | 12 |
| §4.2.2 accept + lane-item atomic under one lock; synthesized `internal_error` item + `CommandRejected(internal_error)` | 12 |
| §4.2.2 per-epoch identity cache retired only by validated `AckProcessedPrefix` (monotonic, ≤ LastProcessedSeq, current epoch); backpressure, never evict unacked | 14 |
| §4.2.2 duplicate → `CommandAck {Accepted\|Processed(+outcome)}` with `Outcome.CurrentState` (confirmed-death precedence, invariant-sound lock-free read documented in Task 16; racing-transition test proves no transient false `Dead`) | 13, 16 (`ReadLiveness` + racing test) |
| §4.2.2 `Epoch` reuse (no second epoch) | 5, 16 |
| §4.2.4 durable ledger, atomic temp+rename, daemon-lifetime monotonic `Generation` persisted with it | 6 |
| §4.2.4 four positive-confirmation hooks: record-pass / quarantine drain / StopAgent-fallback / env-marker kill (incl. recordless→marker-candidate) | 7, 8, 9 |
| §4.2.4 dedicated marker-candidate source `{AgentId,DaemonId,OldEpoch,pid}`, marker-based authority, no spawn-bound record, no trusted flow identity from env | 9 |
| §4.2.4 write ordering ledger-append-BEFORE-source-deletion; crash reconciliation on `(AgentId,OldEpoch)`; idempotent upsert; re-report until `AckResolvedCandidates` prunes per-entry | 6 (upsert/ack), 7 (record crash-inject both sides), 8 (quarantine-drain + StopAgent-fallback crash-inject both sides), 9 (marker crash-inject both sides), 10 (prune) |
| §4.2.4 marker resolution matrix (a) dead/zombie→resolved (b) alive+match→kill→resolved (c) alive+mismatch/unreadable→spare/`pending_marker` | 9 |
| §4.2.3 coverage journal, `cumulative_covered` fold, genesis rule (single-file `{initialized,…}` — marker folded into the journal, ONE atomic temp+rename; genesis-eligibility = journal absent), downgrade/unaware-boot detection via lock `InstanceId`, fail-closed, sticky-false, operator-reset-only | 3, 4 |
| §4.2.3 `RecordlessSurvivorsImpossible` = journal tail's `cumulative_covered`, re-derived at every DaemonConnect | 5 |
| §4.2.5 `StartupReapComplete` per-platform (Linux marker-scan-complete; Windows-with-RecordlessSurvivorsImpossible trivial; pre-W1 Windows + macOS record-pass-only) + `StartupDiscovery` + `UnresolvedStartupCandidates` population | 11 |
| §4.2.6 heal-barrier: serve `RequestStatusReport`; post-stop report carries `LastProcessedSeq ≥ M` omitting the id; `CommandRejected` per-reason (`wrong_next`/`duplicate_collision`/`stale_epoch`/`daemon_capacity`/semantic) | 12 (stale_epoch/internal_error), 13 (duplicate_collision), 15 (wrong_next/daemon_capacity/semantic), 16 (RequestStatusReport), 17 (post-stop report) |
| §5 compat: additive, `SupportsSequencedCommands`-gated, legacy lane never advances watermark | 16, 17 |

**Parent §8 "Settlement" acceptance list (daemon-observable items):**

| Parent §8 Settlement item | Task |
| --- | --- |
| contiguous-prefix watermark: slow launch at N + fast stop at N+1 → watermark stays < N until N processes | 12 (serial lane ⇒ N+1 can't process before N) |
| exact-next acceptance; send-fails-before-accept then N+1 → rejected, no permanent hole | 15 (`wrong_next`) |
| accept+enqueue atomicity — forced item-creation failure → synthesized errored terminal item at N, watermark advances (monotonically, via the contiguity-safe `AdvanceWatermarkLocked`; tested with an earlier item still draining) | 12 |
| duplicate before/during/after execution → `CommandAck` `Accepted` vs `Processed(+outcome)`; no re-execution | 13 |
| `CommandAck.CurrentState` racing live→quarantine/quarantine→dead → no transient false `Dead` (invariant-sound lock-free read: `CleanupAgentAsync` quarantines BEFORE removing from `_agents`; asserted by the racing-transition test) | 13, 16 (`ReadLiveness` precedence + racing test) |
| `AckProcessedPrefix` validation — over-ahead/regressing/stale-epoch rejected without eviction | 14 |
| backpressure with cache pressure → reject, never evict unacked identity | 14 |
| `CommandRejected` per-reason — wrong_next resync, duplicate_collision alarm, stale_epoch, daemon_capacity, semantic | 12, 13, 15 |
| generation-identity: `Generation` persistence across restarts | 6 |
| ledger entries at the four hooks, not for spared/ambiguous; crash-injection **both sides of each hook** (before-append re-derives next boot; between-append-delete reconciles single-emit); `(AgentId,OldEpoch)` reconciliation key (NOT `Generation`) | 7 (record-pass), 8 (quarantine-drain + StopAgent-fallback), 9 (marker-scan incl. recordless) — before-append + between-append-delete tests in each |
| marker-candidate exit+PID-reuse race → re-read triple spares the reused occupant | 9 |
| `StartupReapComplete` per-platform matrix; identity_unavailable/legacy/pending detail in `UnresolvedStartupCandidates` | 11 |
| `ResolvedStartupCandidates` (single name) + `UnresolvedStartupCandidates` round-trips + old-server-ignores-unknown | 1, 10 |
| explicit `StartupDiscovery` cases — each `MarkerScanState` + `LastSuccessfulScanAt` round-trip | 1, 11 |
| `RecordlessSurvivorsImpossible` round-trip + default; boot-chain matrix through the REAL `DaemonLock` (clean/crashed W1→W1, downgrade sandwich, aware-uncontained, durable stickiness, empty-dir-with-prior-lock, corrupt, **genesis crash-before-the-single-rename re-seeds**) | 3, 4, 5 |
| macOS/pre-W1-Windows per-id-only override (flag never sufficient alone) | 11 (`ComputeStartupReapComplete` platform arms) + **server-side** (below) |
| `AgentUnregistered` / retired-generation dedup; teardown-with-unconfirmed-death → id moves to Quarantined, watermark advances, absence NOT satisfied | 17 (post-stop report) + shipped Phase B `CleanupAgentAsync` |
| no test touches a real daemon or live flows | all (DummyProcess / delegate fakes / real DaemonLock) |

**Gaps / server-owned items intentionally out of this daemon PR (flagged):**
1. **Tombstone eviction / `EffectiveStartupComplete` predicate / heal-barrier clear / settlement paths (a)/(b)/(c) / the macOS per-id-only override CONSUMPTION** are **server-side** (parent §5.5, B2-b §4.2.3). This daemon PR only *produces* the signals (`StartupReapComplete`, `StartupDiscovery`, `RecordlessSurvivorsImpossible`, `ResolvedStartupCandidates`, counters). The server-plan author consumes them per the appendix. **Not a gap in this plan.**
2. **The server transport sequencer** (allocate Seq + transmit, pause-nudge-retransmit, `AckProcessedPrefix` emission, `AckResolvedCandidates` emission, `RequestStatusReport` nudge) is server-side. The daemon *receives* these; sending them is the server plan.
3. **`LaunchRejected` as a `CommandAck` outcome** (spec §5.5 lists it): resolved as a spec ambiguity — see below. The daemon emits capacity/semantic rejections via `CommandRejected` (Task 15), and `CommandAck` uses `LaunchExecuted`/`LaunchFailedCleaned`. `LaunchRejected` remains a defined wire value the server may still receive on the `CommandAck` path from a future daemon; no daemon-side gap.

**Spec ambiguities resolved (with resolution):**
- **`Epoch` wire type.** Parent §5.5 pins `HighestAcceptedSeq` as `Int64` but never types `Epoch`; the shipped `_daemonEpoch` is a GUID `"N"` **string**. Resolved: `Epoch` is `string` on every DTO (`LaunchAgentCommand`, `StopAgentV2`, `CommandAck`, `CommandRejected`, `AckProcessedPrefix`, reports) to match `_daemonEpoch` and `AgentPidRecord.DaemonEpoch`.
- **Enum wire tokens vs the global naming policy.** `CapacitorJsonContext` sets `UseStringEnumConverter=true` + `SnakeCaseLower`, but the existing DEV-1665 note describes camelCase enum output — ambiguous. Resolved: every new enum pins its exact wire token with `[JsonStringEnumMemberName("…")]` and the round-trip tests (Tasks 1/2) assert the raw JSON substring, so the cross-repo contract is exact regardless of the converter's default casing. Zero values are the safe defaults (`MarkerScanState.Pending = 0`).
- **`CommandAck` outcome richness.** §5.5 describes a discriminated `LaunchExecuted{metadata}` / `LaunchRejected{code}` / `LaunchFailedCleaned` / `StopExecuted` union. Resolved to a flat, AOT-friendly `CommandAck` (`OutcomeKind` + `CurrentState` + optional `AgentId`/`SessionId`/`RejectionReason`); the daemon determines a launch's `OutcomeKind` by a post-execution `_agents.ContainsKey` check (`LaunchExecuted` vs `LaunchFailedCleaned`), and emits capacity/semantic rejections via `CommandRejected` rather than a `CommandAck` `LaunchRejected` (kept as a defined-but-daemon-unused value).
- **Quarantine-drain hook `OldEpoch`.** The shipped `_quarantine` is in-memory kill-quarantine (current-incarnation only), so a drain's `OldEpoch` is the *current* epoch. Resolved: emit the resolved-candidate anyway (idempotent, harmless for a same-epoch id, and it gives the server id-level absence proof); the prior-epoch resolutions come from the record pass + marker scan.
- **`this_epoch_contained`.** Only the Windows Job Object leaves genuinely no recordless survivor class (Linux keeps records+scan as the descendant backstop per parent §6.4). Resolved: production `thisEpochContained = OperatingSystem.IsWindows()`; the server consumes `RecordlessSurvivorsImpossible` only on Windows, so the Linux/macOS value is inert. Tests inject both to exercise the aware-uncontained branch.
- **`RequestStatusReport` shape.** Listed as a "message" but carries no payload. Resolved: a zero-argument hub invocation (`_hub.On("RequestStatusReport", …)`), matching the shipped no-arg broadcast-sink pattern — no DTO.

## Locked wire-contract record shapes (hand to the server-plan author)

Exactly as in the Appendix. Summary of the daemon→server / server→daemon split:
- **server→daemon (received):** `LaunchAgentCommand` (+`Epoch`/`Seq`/`CommandId`), `StopAgentV2`, `AckProcessedPrefix`, `AckResolvedCandidates`, `RequestStatusReport` (0-arg).
- **daemon→server (produced):** `DaemonConnect` (+`Quarantined`/`Epoch`/`HighestAcceptedSeq`/`LastProcessedSeq`/`StartupReapComplete`/`ResolvedStartupCandidates`/`UnresolvedStartupCandidates`/`StartupDiscovery`/`RecordlessSurvivorsImpossible`/`SupportsSequencedCommands`), `DaemonStatusReport` (+`Epoch`/`LastProcessedSeq`/`HighestAcceptedSeq`/`StartupReapComplete`/`ResolvedStartupCandidates`/`UnresolvedStartupCandidates`/`StartupDiscovery`), `CommandAck`, `CommandRejected`.
- **enums:** `MarkerScanState`, `StartupCandidateUnresolvedReason`, `CommandAckState`, `CommandOutcomeKind`, `AgentLiveness`, `CommandRejectedReason` — each with `[JsonStringEnumMemberName]`-pinned snake_case tokens.

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-22-ai1391-b2b-daemon-sequenced-settlement.md`. Two execution options:

1. **Subagent-Driven (recommended)** — dispatch a fresh subagent per task, review between tasks, fast iteration.
2. **Inline Execution** — execute tasks in this session using executing-plans, batch execution with checkpoints.

Which approach?

---

## Revision log (adversarial review round 1)

Each finding → the change applied. Tasks touched are noted.

**CRITICAL**
1. **CoverageJournal genesis atomicity (Task 4; and every reader of the journal).** The plan wrote the journal via temp+rename and then wrote `state_dir_initialized` as a *separate* `File.WriteAllText` — two non-atomic ops, violating the spec's pinned "marker + journal in the SAME atomic operation" (a genesis-boot crash between them left journal-present/marker-absent → next boot computed genesis=false → permanently poisoned `cumulative_covered`). **Fix:** folded the `initialized` flag INTO the journal document (`{ initialized, instance_id, cumulative_covered }`) written by ONE temp+rename; there is no separate marker file. Genesis-eligibility is now "journal file absent". Rewrote the class doc, `RecordBoot` (journal-absent vs present-but-uninitialized/corrupt vs present-initialized branches), `ReadJournal`/`WriteJournalAtomic`, and the Task 4 preamble. Renamed `Empty_looking_used_dir_without_marker_is_false` → `..._with_prior_lock_is_false` (updated comment), and **added** `Genesis_crash_before_the_single_rename_leaves_no_journal_and_reseeds` (crash before the single rename ⇒ no journal ⇒ still genesis-eligible; a completed rename ⇒ valid initialized journal). Test count 7→8. All pinned semantics preserved.

**IMPORTANT**
2. **Task 17 undefined test seam (Tasks 16, 17).** The Task 17 test calls `orch.HandleLaunchAgentForTest(...)`, which **already exists** as a shipped seam (`AgentOrchestrator.cs:2103`, routes through `HandleLaunchAgent`); the Interfaces named a phantom `HandleLaunchAgentSequencedForTest`. **Fix:** reconciled to the existing `HandleLaunchAgentForTest` (only the new `HandleStopAgentV2ForTest` is added); removed the phantom from the Files/Interfaces/Step-3 text. Also reconciled `HealBarrierReportTests.cs` — Task 16 CREATEs it (a `partial class AgentOrchestratorVendorTests`), Task 17 MODIFYs it (fixed the commit `git add` paths on both tasks; dropped the stale `AgentOrchestratorVendorTests.cs`).
3. **Task 10 `await` on a `void` seam (CS4008).** The test did `await orch.HandleAckResolvedCandidatesForTest(...)`, but the seam and the underlying ledger `Ack` are synchronous (`void`). **Fix:** dropped the `await` (matches the synchronous shipped ledger `Ack`), with a clarifying comment.
4. **Missing crash-injection matrix (Tasks 7, 8, 9).** Parent §8 + B2-b §6 pin crash-injection on BOTH sides for ALL FOUR ledger hooks. **Fix:** added before-append + between-append-delete tests for each: Task 7 (record-pass — added the before-append case), Task 8 (quarantine-drain + StopAgent-fallback — 4 new tests), Task 9 (marker-scan kill incl. recordless→marker-candidate — 2 new tests). Each asserts single-emit reconciliation from the actual on-disk pre-append source via the source-stable `(AgentId, OldEpoch)` key (NOT `Generation`) using DummyProcess/real-store. Updated per-task pass counts.
5. **Synthesized-item path + monotonicity hazard (Task 12).** Added the forced-item-creation-failure test (completes the lane via a new `CompleteLaneForTest()` seam so an accepted Submit's `TryWrite` fails → `SynthesizeErrorLocked`). Fixed the hazard: `SynthesizeErrorLocked` no longer sets `_lastProcessedSeq = item.Seq` unconditionally — both it and the lane consumer now call a shared contiguity-safe `AdvanceWatermarkLocked()` that walks the contiguous terminal prefix, so a synthesize during lane-completion can neither jump past a still-draining earlier item nor be regressed below by it. The test asserts monotonicity + the final contiguous watermark.
6. **Task 16 lock-free `ReadLiveness` + missing racing test.** The read probes `_agents`/quarantine lock-free. **Fix (option b):** documented — in both the task text and a code comment — that the lock-free read is SOUND only because of the shipped `CleanupAgentAsync` ordering invariant (quarantine `Add` BEFORE `_agents.TryRemove`), so an agent is continuously in `_agents ∪ _quarantine` until the drain ⇒ no transient false `Dead` (and a note that it must otherwise take the per-agent lifecycle lock). Added `ReadLiveness_racing_transitions_never_yields_a_transient_false_dead` (hammers `ReadLiveness` across live→quarantine→dead using the shipped `DummyProcess` + a pid-backed pty double; asserts deadness is monotonic).

**MINORs**
- Task 3: "both `new DaemonLock(...)` call sites" → corrected to the single call site (`DaemonLock.cs:125`, the final `return`).
- Task 9: an `identity_unavailable` record carries TRUSTED flow — fixed `EmitAndClear` to look up a co-existing durable record and route its trusted `FlowRunId`/`FlowRole` (and record `DaemonEpoch`) via the `onRecordResolved` sink; a fully recordless survivor still maps to null flow via `onMarkerResolved`. Added `Identity_unavailable_record_resolved_via_marker_scan_emits_trusted_flow`. Updated the Interfaces note.
- Task 11: replaced the prose/inline-comment `CurrentDiscovery` Complete/Failed transitions with complete `ScanEnvMarkersAsync` try/catch code; dropped the confusing "use a live pid" parenthetical in `SeedPendingMarkerCandidateForTest` (the `999_999` pid is fine — `BlockedCandidates` lists marker sources as `pending_marker` without a liveness check).
- Tasks 16/17: `HealBarrierReportTests.cs` — Task 17 marked MODIFY (Task 16 CREATEs); names kept consistent.
- Appendix: added the one-line note that the daemon's `ReadLiveness` collapses `NotFound` to `Dead` intentionally (both satisfy absence), while `NotFound` stays a defined wire value a future `StopExecuted`-path daemon may emit distinctly.

**Structure preserved.** No tasks added or removed (still 17); every task keeps the 5-step TDD shape (failing test → run-red → minimal impl → run-green → commit); all additions are new test methods within existing Step 1 blocks and impl/seam changes within Step 3, with exact paths and complete code retained.
