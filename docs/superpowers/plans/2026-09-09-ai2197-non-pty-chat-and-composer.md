# Non-PTY Chat and Composer (Slice A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every non-PTY hosted session (Cursor, Copilot, Gemini, Kiro, OpenCode, Antigravity, Pi, Codex app-server) gets a Chat tab that renders its transcript and a composer that sends follow-ups, in the desktop app, with parity to the PTY path wherever the transport allows.

**Architecture:** The daemon appends every accepted `AcpEventEnvelope` to a per-agent JSONL journal under its state directory and reports the path as `transcript_path` plus a new `transcript_format`; the app tails it with the existing `JsonlTail` through a new passthrough projection. Input rides one new one-shot local frame (`SendText`/`SendTextAck`) routed into the same delivery core the server-origin `SendInput` uses. The app's composer gains an abstraction (`ChatInput`) with a terminal implementation for PTY and a frame implementation for everything else.

**Tech Stack:** .NET 10 (NativeAOT), System.Threading.Channels, System.Text.Json source generation, Avalonia + ReactiveUI + DynamicData, TUnit, `Microsoft.Extensions.Time.Testing.FakeTimeProvider`.

**Spec:** `docs/superpowers/specs/2026-09-09-ai2197-non-pty-chat-and-composer-design.md` (read it first; every task below cites the section it implements).

## Global Constraints

- Build, test and publish with `~/.dotnet/dotnet` (.NET 10); the `dotnet` on PATH is 8.0. Run one suite with `~/.dotnet/dotnet run --project test/<Suite>/<Suite>.csproj -- --treenode-filter '/*/*/<Class>/*'`.
- `FrameType` is append-only: `SendText = 22`, `SendTextAck = 80`. Never renumber anything.
- `LocalControlCapabilities.Current` gains `"input/1"` in the SAME commit as the `LocalControlServer` `case FrameType.SendText`.
- `AgentStatusDto` gains exactly one trailing member `string? TranscriptFormat = null` (`transcript_format`), always emitted; values `"vendor"` / `"envelopes"`.
- Journal path: `<DaemonStore.StateDirectory(config.Name)>/transcripts/<lowercase sha256 hex of agent id>.jsonl`. Journal channel: `BoundedChannelOptions(4096) { FullMode = Wait, SingleReader = true, SingleWriter = false }`. `Complete` grace 2 s. Path-lock bound 2 s. Retention 30 days, sweep every 24 h.
- Frame limits: `text` over 256 KiB of UTF-8 → `too_large`. Reason tokens exactly as the spec table: `malformed`, `text_empty`, `too_large`, `no_such_agent`, `protected_kind`, `not_running`, `reaper_claimed`, `reaper_claimed_late`, `queue_full`, `stop_failed`, `delivery_failed`; client-side `transport`.
- App wording (verbatim): "Update the daemon to view this session", "Update the app to view this session", "Update the daemon to send messages from the app", "delivery unconfirmed — check the chat before sending again", "agent is no longer running", "read-only participant", "the agent's input queue is full, try again shortly".
- No Linear ids in C# or comments (`bash scripts/check-linear-ids.sh` before every commit). Commit subjects: `one clause (#839)`, ≤80 chars including the reference; `#839` is the GitHub mirror of AI-2625 and is the only reference to use.
- Comments: scarce, no history, no spec coordinates (CLAUDE.md "Comments"). One type per file, named after the type.
- Never read an agent-owned file with a write-denying open: journal writes use `FileShare.ReadWrite | FileShare.Delete`; tests that read a journal use `FileStream` with `FileShare.ReadWrite | FileShare.Delete`.
- Worktree git: `/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 <args>`, one command per invocation, no heredocs. Every commit ends with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- After daemon or Core changes: `~/.dotnet/dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` must print nothing.

## File structure

Core (`src/Capacitor.Cli.Core/`), shared by daemon and app:

| File | Responsibility |
|---|---|
| `PlatformPaths.cs` (moved from `App/ViewModels/TrayModels.cs`) | one platform path comparison rule |
| `SessionIds.cs` | `Canonical(string?)`: GUID → `N` form, else trimmed unchanged |
| `LocalIpc/FrameType.cs`, `LocalFrame.cs`, `FrameCodec.cs` | `SendText`/`SendTextAck` frames |
| `LocalIpc/InputIpc.cs` | `SendTextDto`, `SendTextAckDto`, `SendTextReasons`, `InputIpcJsonContext`, `InputWire` |
| `LocalIpc/StatusIpc.cs` | `AgentStatusDto.TranscriptFormat`, `TranscriptFormats` |
| `LocalIpc/LocalControlOps.cs` | `ILocalControlOps.SendTextAsync`, `SendTextResult` |
| `EnvelopeJournalFormat.cs` | the JSONL line encoding both sides use |
| `IChatTranscriptProjection.cs` | the reader interface the chat tab consumes |
| `EnvelopeJournalProjection.cs` | passthrough projection over journal lines |
| `TranscriptChat.cs` | `TranscriptChatProjection : IChatTranscriptProjection`, `TranscriptChat.Journal` |
| `WorkItems/WorkContextIds.cs` | delegates to `SessionIds.Canonical` |

Daemon (`src/Capacitor.Cli.Daemon/Services/` unless noted):

| File | Responsibility |
|---|---|
| `AgentFileNames.cs` | `For(agentId)`: the shared hashed file name |
| `JournalPathLocks.cs` | ref-counted per-path `SemaphoreSlim` leases |
| `JournalItem.cs` | `(AcpEventEnvelope Envelope, int GapBefore)` |
| `TranscriptJournal.cs` | bounded queue + writer task + `Open`/`Record`/`CompleteAsync` |
| `TranscriptJournalSweep.cs` | `BackgroundService`: 30-day reap, startup + every 24 h |
| `InputNotAdmittedException.cs` | typed refusal from a runtime that will not queue or write |
| `InputDelivery.cs` | the delivery core's outcome (`InputDeliveryOutcome`) |
| `AgentOrchestrator.cs` | journal construction, snapshot fields, PTY gating, `DeliverInputAsync`, `SendInputDropReason.QueueFull` |
| `AgentOrchestrator.LocalIpc.cs` | `HandleLocalSendTextAsync`, `transcript_format` in the snapshot |
| `LocalControlServer.cs`, `LocalControlCapabilities.cs` | `SendText` routing + `input/1` |
| `IHostedAgentRuntimeFactory.cs` | `RuntimeStartContext.Journal` |
| `AcpHostedAgentRuntime.cs`, `AcpHostedAgentRuntimeFactory.cs` | journal at `EmitEnvelope`; `InputNotAdmittedException` |
| `Harness/Pi/PiRpcHostedAgentRuntime.cs`, `…Factory.cs` | journal at `Write` under a write lock |
| `Harness/Antigravity/AntigravityHostedAgentRuntime.cs`, `…Factory.cs` | `TimeProvider`, worker `user_message`, journal at `Write`, `InputNotAdmittedException` |
| `Harness/Codex/CodexForwardBuffer.cs`, `CodexAppServerHostedAgentRuntime.cs`, `CodexHostedAgentRuntimeFactory.cs`, `CodexTurnInputDispatcher.cs` | journal at `Emit`; `WriteCanonicalBlocking → bool`; refuse after `FaultAll` |
| `DaemonRunner.cs` | registers the sweep, runs it once after the orphan reap |

App (`src/Capacitor.App/`):

| File | Responsibility |
|---|---|
| `ViewModels/AgentPresence.cs` | `internal sealed record AgentPresence(AgentStatusDto? Dto, bool SessionEnded)` |
| `ViewModels/ChatInput.cs` | abstract composer channel |
| `ViewModels/TerminalChatInput.cs` | PTY channel over `TerminalTabViewModel` |
| `ViewModels/LocalFrameChatInput.cs` | `SendText` channel over `ILocalControlOps` |
| `ViewModels/ChatTranscriptSource.cs` | resolves `(projection, unavailable note)` from a dto |
| `ViewModels/ChatTabViewModel.cs` | takes `IChatTranscriptProjection?` + `ChatInput`; clear-on-ack |
| `ViewModels/WorkspaceViewModel.cs` | Chat for every session; channel by `has_terminal` |
| `Views/WorkspaceView.axaml` | Chat gates removed; tab-strip note removed |
| `Services/TerminalAttach.cs` | `SendAvailability.Unsupported` |
| `App.axaml.cs` | passes `ops` into `BuildWorkspace` |

Tests mirror these paths under `test/Capacitor.Cli.Core.Tests.Unit/`, `test/Capacitor.Cli.Daemon.Tests.Unit/`, `test/Capacitor.App.Tests.Unit/` (the App test project is flat: no `ViewModels/` subfolder).

---

## Part A — Core

### Task 1: Move `PlatformPaths` to Core

**Files:**
- Create: `src/Capacitor.Cli.Core/PlatformPaths.cs`
- Modify: `src/Capacitor.App/ViewModels/TrayModels.cs` (delete the `PlatformPaths` class, lines 27–62)
- Modify (usings only, if the namespace is not already imported): `src/Capacitor.App/ViewModels/SessionRailViewModel.cs`, `RailWorktreeViewModel.cs`, `HomeViewModel.cs`, `src/Capacitor.App/Services/AgentDirectory.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/PlatformPathsTests.cs`

**Interfaces:**
- Produces: `Capacitor.Cli.Core.PlatformPaths` with `static readonly StringComparer Comparer`, `static string Normalize(string)`, `static string Leaf(string)` — identical bodies to today's app class.

- [ ] **Step 1: Write the failing test**

```csharp
namespace Capacitor.Cli.Core.Tests.Unit;

public class PlatformPathsTests {
    [Test]
    public async Task Trailing_separator_is_ignored_by_the_comparer() {
        await Assert.That(PlatformPaths.Comparer.Equals("/a/b", "/a/b/")).IsTrue();
        await Assert.That(PlatformPaths.Comparer.GetHashCode("/a/b")).IsEqualTo(PlatformPaths.Comparer.GetHashCode("/a/b/"));
    }

    [Test]
    public async Task Leaf_is_the_last_segment_after_normalization() {
        await Assert.That(PlatformPaths.Leaf("/a/b/")).IsEqualTo("b");
        await Assert.That(PlatformPaths.Normalize("/a/b/")).IsEqualTo("/a/b");
    }

    [Test]
    public async Task Case_rule_follows_the_platform() {
        var equal = PlatformPaths.Comparer.Equals("/A", "/a");
        await Assert.That(equal).IsEqualTo(!OperatingSystem.IsLinux());
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/PlatformPathsTests/*'`
Expected: build error `The name 'PlatformPaths' does not exist`.

- [ ] **Step 3: Create the Core file and delete the app copy**

`src/Capacitor.Cli.Core/PlatformPaths.cs`:

```csharp
namespace Capacitor.Cli.Core;

/// One platform path rule for every surface: case-insensitive on Windows and macOS, case-sensitive
/// on Linux, and a trailing directory separator never distinguishes two paths.
public static class PlatformPaths {
    public static readonly StringComparer Comparer = new TrailingSeparatorComparer(
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string path) =>
        string.IsNullOrEmpty(path) ? path : Path.TrimEndingDirectorySeparator(path);

    public static string Leaf(string path) => Path.GetFileName(Normalize(path));

    sealed class TrailingSeparatorComparer(StringComparer inner) : StringComparer {
        public override int Compare(string? x, string? y) {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            return inner.Compare(Normalize(x), Normalize(y));
        }

        public override bool Equals(string? x, string? y) {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return inner.Equals(Normalize(x), Normalize(y));
        }

        public override int GetHashCode(string obj) => inner.GetHashCode(Normalize(obj));
    }
}
```

Delete the `PlatformPaths` class from `TrayModels.cs` (keep `RepoLabel` and `CheckoutLabel`). The five app files already compile against the name; add `using Capacitor.Cli.Core;` to any that lack it (`TrayModels.cs`, `SessionRailViewModel.cs`, `RailWorktreeViewModel.cs`, `HomeViewModel.cs`, `AgentDirectory.cs`).

- [ ] **Step 4: Build both projects and run the tests**

Run: `~/.dotnet/dotnet build src/Capacitor.App/Capacitor.App.csproj` then the Step 2 command.
Expected: build clean; 3 tests PASS. Also run `--treenode-filter '/*/*/RepoLabelTests/*'` and `'/*/*/SessionRailViewModelTests/*'` in the App suite: PASS.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add -A src/Capacitor.Cli.Core/PlatformPaths.cs src/Capacitor.App test/Capacitor.Cli.Core.Tests.Unit/PlatformPathsTests.cs
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Move PlatformPaths into Core (#839)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 2: `SessionIds.Canonical` and `WorkContextIds` delegation

**Files:**
- Create: `src/Capacitor.Cli.Core/SessionIds.cs`
- Modify: `src/Capacitor.Cli.Core/WorkItems/WorkContextIds.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/SessionIdsTests.cs`, `test/Capacitor.Cli.Core.Tests.Unit/WorkItems/WorkContextIdsTests.cs` (create if absent)

**Interfaces:**
- Produces: `public static class SessionIds { public static string? Canonical(string? id); }` — null/whitespace → null; parses as GUID → `g.ToString("N")`; otherwise `id.Trim()`.

- [ ] **Step 1: Write the failing tests**

`SessionIdsTests.cs`:

```csharp
namespace Capacitor.Cli.Core.Tests.Unit;

public class SessionIdsTests {
    [Test]
    public async Task Dashed_guid_becomes_its_n_form() =>
        await Assert.That(SessionIds.Canonical("8BC7255F-2453-4EFD-A733-0AF4B6AE9F20")).IsEqualTo("8bc7255f24534efda7330af4b6ae9f20");

    [Test]
    public async Task Opaque_id_is_returned_trimmed_and_unchanged() =>
        await Assert.That(SessionIds.Canonical("  sess-1 ")).IsEqualTo("sess-1");

    [Test]
    public async Task Null_and_blank_are_null() {
        await Assert.That(SessionIds.Canonical(null)).IsNull();
        await Assert.That(SessionIds.Canonical("   ")).IsNull();
    }
}
```

`WorkItems/WorkContextIdsTests.cs`:

```csharp
namespace Capacitor.Cli.Core.Tests.Unit.WorkItems;

public class WorkContextIdsTests {
    [Test]
    public async Task Guid_session_id_reaches_the_route_in_n_form() =>
        await Assert.That(WorkContextIds.CanonicalSessionId("8bc7255f-2453-4efd-a733-0af4b6ae9f20")).IsEqualTo("8bc7255f24534efda7330af4b6ae9f20");

    [Test]
    public async Task Opaque_dashed_id_reaches_the_route_unchanged() =>
        await Assert.That(WorkContextIds.CanonicalSessionId("sess-1")).IsEqualTo("sess-1");

    [Test]
    public async Task Dot_segments_and_blanks_are_still_rejected() {
        await Assert.That(WorkContextIds.CanonicalSessionId(".")).IsNull();
        await Assert.That(WorkContextIds.CanonicalSessionId("..")).IsNull();
        await Assert.That(WorkContextIds.CanonicalSessionId(" ")).IsNull();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/SessionIdsTests/*'`
Expected: build error on `SessionIds`.

- [ ] **Step 3: Implement**

`src/Capacitor.Cli.Core/SessionIds.cs`:

```csharp
namespace Capacitor.Cli.Core;

/// The session id the server files a session under: a GUID in any spelling collapses to its 32-hex
/// form, and an opaque vendor id (an ACP `sess-1`) is kept as written — stripping its dashes would
/// name a session the server has never seen.
public static class SessionIds {
    public static string? Canonical(string? id) {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var trimmed = id.Trim();
        return Guid.TryParse(trimmed, out var g) ? g.ToString("N") : trimmed;
    }
}
```

`WorkContextIds.cs`: replace the `CanonicalSessionId` body:

```csharp
    public static string? CanonicalSessionId(string? raw) => Validate(SessionIds.Canonical(raw));
```

- [ ] **Step 4: Run tests**

Run the Step 2 command plus `--treenode-filter '/*/*/WorkContextIdsTests/*'` and `'/*/*/WorkContextClientTests/*'`.
Expected: all PASS (the client test `Assignments_route_strips_dashes_and_parses_the_rows` uses a GUID, which still lands dashless).

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.Cli.Core/SessionIds.cs src/Capacitor.Cli.Core/WorkItems/WorkContextIds.cs test/Capacitor.Cli.Core.Tests.Unit/SessionIdsTests.cs test/Capacitor.Cli.Core.Tests.Unit/WorkItems/WorkContextIdsTests.cs
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Canonicalize session ids as the server does (#839)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 3: `transcript_format` on `AgentStatusDto`

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/StatusIpcJsonTests.cs`

**Interfaces:**
- Produces: `AgentStatusDto(..., bool? AwaitingInput = null, string? TranscriptFormat = null)`; `public static class TranscriptFormats { public const string Vendor = "vendor"; public const string Envelopes = "envelopes"; }`.

- [ ] **Step 1: Update the pinned JSON and add tests**

In `StatusIpcJsonTests.cs`, every pinned string that ends `"awaiting_input":null}` gains `,"transcript_format":null` before the closing brace (lines 32 ×2 agents, 110, 111, 142, 143). Add:

```csharp
    [Test]
    public async Task Transcript_format_is_the_trailing_member_and_always_emitted() {
        var vendor    = new AgentStatusDto("a", "agent", "claude", null, "Running", null, null, null, DateTime.UnixEpoch, null, null, TranscriptFormat: TranscriptFormats.Vendor);
        var envelopes = vendor with { TranscriptFormat = TranscriptFormats.Envelopes };
        var unset     = vendor with { TranscriptFormat = null };

        await Assert.That(JsonSerializer.Serialize(vendor, StatusIpcJsonContext.Default.AgentStatusDto)).EndsWith(""","awaiting_input":null,"transcript_format":"vendor"}""");
        await Assert.That(JsonSerializer.Serialize(envelopes, StatusIpcJsonContext.Default.AgentStatusDto)).EndsWith(""","transcript_format":"envelopes"}""");
        await Assert.That(JsonSerializer.Serialize(unset, StatusIpcJsonContext.Default.AgentStatusDto)).EndsWith(""","transcript_format":null}""");
    }

    [Test]
    public async Task Old_agent_json_without_transcript_format_deserializes_to_null() {
        var json = """{"id":"a","kind":"agent","vendor":"codex","repo_path":null,"status":"Live","flow_run_id":null,"flow_role":null,"requester":null,"created_at":"2026-08-01T00:00:00Z","model":null,"requester_display":null,"awaiting_input":true}""";
        var dto = JsonSerializer.Deserialize(json, StatusIpcJsonContext.Default.AgentStatusDto)!;
        await Assert.That(dto.TranscriptFormat).IsNull();
        await Assert.That(dto.AwaitingInput).IsEqualTo(true);
    }
```

If `StatusIpcJsonContext` has no `AgentStatusDto` type-info accessor, add `[JsonSerializable(typeof(AgentStatusDto))]` to the context (the nested type is already generated; the attribute only exposes the accessor).

- [ ] **Step 2: Run to verify failure**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/StatusIpcJsonTests/*'`
Expected: build error (`TranscriptFormat`).

- [ ] **Step 3: Implement**

In `StatusIpc.cs`, after `bool? AwaitingInput = null` add:

```csharp
    // Which reader a client uses for TranscriptPath: TranscriptFormats.Vendor for a PTY runtime's own
    // file, TranscriptFormats.Envelopes for the daemon-written envelope journal. Always emitted by a
    // current daemon, so null means an older daemon and nothing else.
    string? TranscriptFormat = null);
```

and beside `WorkLocationText`:

```csharp
/// Wire tokens for <see cref="AgentStatusDto.TranscriptFormat"/>, compared literally by every client.
public static class TranscriptFormats {
    public const string Vendor    = "vendor";
    public const string Envelopes = "envelopes";
}
```

- [ ] **Step 4: Run tests**

Run the Step 2 command. Expected: PASS. Also build the daemon and app: `~/.dotnet/dotnet build src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj && ~/.dotnet/dotnet build src/Capacitor.App/Capacitor.App.csproj` — PASS (trailing optional member).

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/StatusIpcJsonTests.cs
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Report transcript_format on the agent status row (#839)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 4: `EnvelopeJournalFormat`

**Files:**
- Create: `src/Capacitor.Cli.Core/EnvelopeJournalFormat.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/EnvelopeJournalFormatTests.cs`

**Interfaces:**
- Produces: `public static class EnvelopeJournalFormat { public static string Write(AcpEventEnvelope e); public static bool TryRead(string line, out AcpEventEnvelope envelope); public const int SupportedContractVersion = 1; }` — `Write` uses `CapacitorJsonContext.Default.AcpEventEnvelope` (snake_case, the server's bytes). `TryRead` validates structurally: JSON object, non-null non-empty `kind`, `contract_version` equal to 1 (absent counts as 1, the record's default).

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Core.Tests.Unit;

public class EnvelopeJournalFormatTests {
    [Test]
    public async Task Round_trip_pins_the_bytes() {
        var e = new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: "hi", TimestampIso: "2026-09-09T10:00:00.000Z");
        var line = EnvelopeJournalFormat.Write(e);
        await Assert.That(line).StartsWith("""{"contract_version":1,"seq":0,"kind":"user_message","text":"hi",""");
        await Assert.That(line).Contains("\"timestamp_iso\":\"2026-09-09T10:00:00.000Z\"");
        await Assert.That(line).DoesNotContain("\n");
        await Assert.That(EnvelopeJournalFormat.TryRead(line, out var back)).IsTrue();
        await Assert.That(back).IsEqualTo(e);
    }

    [Test]
    [Arguments("not json")]
    [Arguments("{}")]
    [Arguments("42")]
    [Arguments("[]")]
    [Arguments("""{"contract_version":1,"kind":null}""")]
    [Arguments("""{"contract_version":1,"kind":""}""")]
    [Arguments("""{"contract_version":2,"kind":"user_message","text":"x"}""")]
    public async Task Structurally_invalid_lines_are_rejected(string line) =>
        await Assert.That(EnvelopeJournalFormat.TryRead(line, out _)).IsFalse();

    [Test]
    public async Task Unknown_kind_on_a_v1_line_is_accepted() {
        await Assert.That(EnvelopeJournalFormat.TryRead("""{"contract_version":1,"kind":"future_kind"}""", out var e)).IsTrue();
        await Assert.That(e.Kind).IsEqualTo("future_kind");
    }

    [Test]
    public async Task Missing_contract_version_reads_as_v1() =>
        await Assert.That(EnvelopeJournalFormat.TryRead("""{"kind":"assistant_text","text":"a"}""", out _)).IsTrue();
}
```

- [ ] **Step 2: Run to verify failure**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/EnvelopeJournalFormatTests/*'`
Expected: build error.

- [ ] **Step 3: Implement**

```csharp
using System.Text.Json;

namespace Capacitor.Cli.Core;

/// One envelope per JSONL line, the same bytes the server receives. Validity is decided here, not by
/// the deserializer: a record struct decodes from `{}` or a null `kind` without complaint, and a
/// later contract version may reuse a known kind with different fields.
public static class EnvelopeJournalFormat {
    public const int SupportedContractVersion = 1;

    public static string Write(AcpEventEnvelope envelope) =>
        JsonSerializer.Serialize(envelope, CapacitorJsonContext.Default.AcpEventEnvelope);

    public static bool TryRead(string line, out AcpEventEnvelope envelope) {
        envelope = default;
        try {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(kind.GetString())) return false;
            if (root.TryGetProperty("contract_version", out var version)
             && (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != SupportedContractVersion)) return false;
            envelope = JsonSerializer.Deserialize(line, CapacitorJsonContext.Default.AcpEventEnvelope);
            return true;
        } catch (JsonException) {
            return false;
        }
    }
}
```

- [ ] **Step 4: Run tests** — Step 2 command. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.Cli.Core/EnvelopeJournalFormat.cs test/Capacitor.Cli.Core.Tests.Unit/EnvelopeJournalFormatTests.cs
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Add the envelope journal line format (#839)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 5: `IChatTranscriptProjection`, `EnvelopeJournalProjection`, `TranscriptChat.Journal`

**Files:**
- Create: `src/Capacitor.Cli.Core/IChatTranscriptProjection.cs`, `src/Capacitor.Cli.Core/EnvelopeJournalProjection.cs`
- Modify: `src/Capacitor.Cli.Core/TranscriptChat.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/EnvelopeJournalProjectionTests.cs`

**Interfaces:**
- Produces:

```csharp
public interface IChatTranscriptProjection {
    TranscriptContext CreateContext(string sessionId, string? agentId);
    IReadOnlyList<AcpEventEnvelope> Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context);
}
public sealed class EnvelopeJournalProjection : IChatTranscriptProjection { … }   // Project throws FormatException on an invalid line
public static class TranscriptChat { public static readonly IChatTranscriptProjection Journal; public static TranscriptChatProjection? For(string vendor); }
```

`TranscriptChatProjection` implements the interface with its existing two members unchanged.

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Core;
using Capacitor.Models.Transcripts;

namespace Capacitor.Cli.Core.Tests.Unit;

public class EnvelopeJournalProjectionTests {
    static readonly IChatTranscriptProjection Sut = TranscriptChat.Journal;

    [Test]
    [Arguments(AcpEventKind.UserMessage)]
    [Arguments(AcpEventKind.AssistantText)]
    [Arguments(AcpEventKind.SystemNote)]
    [Arguments(AcpEventKind.ToolCall)]
    [Arguments(AcpEventKind.Usage)]
    [Arguments("future_kind")]
    public async Task Every_kind_passes_through_as_one_envelope(string kind) {
        var line = EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: kind, Text: "t", ToolCallId: "c1"));
        var context = Sut.CreateContext("s", "a1");
        var result = Sut.Project(line, 1, DateTimeOffset.UnixEpoch, context);
        await Assert.That(result).Count().IsEqualTo(1);
        await Assert.That(result[0].Kind).IsEqualTo(kind);
    }

    [Test]
    public async Task Malformed_line_throws_so_the_tab_logs_it_once() {
        var context = Sut.CreateContext("s", null);
        await Assert.That(() => Sut.Project("{}", 1, DateTimeOffset.UnixEpoch, context)).Throws<FormatException>();
    }

    [Test]
    public async Task Context_is_stateless_and_reusable() {
        var context = Sut.CreateContext("s", null);
        context.BeginBatch();
        var line = EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: "x"));
        await Assert.That(Sut.Project(line, 1, DateTimeOffset.UnixEpoch, context)).Count().IsEqualTo(1);
        await Assert.That(Sut.Project(line, 2, DateTimeOffset.UnixEpoch, context)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Vendor_registry_is_unchanged() {
        await Assert.That(TranscriptChat.For("claude")).IsNotNull();
        await Assert.That(TranscriptChat.For("gemini")).IsNull();
        IChatTranscriptProjection asInterface = TranscriptChat.For("codex")!;
        await Assert.That(asInterface).IsNotNull();
    }
}
```

- [ ] **Step 2: Run to verify failure** — filter `EnvelopeJournalProjectionTests`; expected: build error.

- [ ] **Step 3: Implement**

`IChatTranscriptProjection.cs`:

```csharp
using Capacitor.Models.Transcripts;

namespace Capacitor.Cli.Core;

/// What the chat tab needs from any transcript reader: a per-file context and a line-to-envelopes step.
public interface IChatTranscriptProjection {
    TranscriptContext CreateContext(string sessionId, string? agentId);
    IReadOnlyList<AcpEventEnvelope> Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context);
}
```

`EnvelopeJournalProjection.cs`:

```csharp
using Capacitor.Models.Transcripts;

namespace Capacitor.Cli.Core;

/// Journal lines are already envelopes: no vendor rules, no dedupe, one envelope per valid line.
public sealed class EnvelopeJournalProjection : IChatTranscriptProjection {
    sealed class StatelessContext : TranscriptContext;

    public TranscriptContext CreateContext(string sessionId, string? agentId) => new StatelessContext();

    public IReadOnlyList<AcpEventEnvelope> Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) =>
        EnvelopeJournalFormat.TryRead(line, out var envelope)
            ? [envelope]
            : throw new FormatException($"line {lineNumber} is not a v{EnvelopeJournalFormat.SupportedContractVersion} envelope");
}
```

`TranscriptChat.cs`: `public sealed class TranscriptChatProjection(...) : IChatTranscriptProjection` (bodies unchanged), and in `TranscriptChat` add `public static readonly IChatTranscriptProjection Journal = new EnvelopeJournalProjection();`. If `TranscriptContext` is not already reachable from Core, add `using Capacitor.Models.Transcripts;` (Core already references the transcripts project through `TranscriptProjection`).

- [ ] **Step 4: Run tests** — filter `EnvelopeJournalProjectionTests`; expected PASS. Build the app project to confirm `ChatTabViewModel` still compiles against `TranscriptChatProjection`.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.Cli.Core/IChatTranscriptProjection.cs src/Capacitor.Cli.Core/EnvelopeJournalProjection.cs src/Capacitor.Cli.Core/TranscriptChat.cs test/Capacitor.Cli.Core.Tests.Unit/EnvelopeJournalProjectionTests.cs
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Read envelope journals through a chat projection (#839)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 6: `SendText` / `SendTextAck` frames and DTOs

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs`, `LocalFrame.cs`, `FrameCodec.cs`
- Create: `src/Capacitor.Cli.Core/LocalIpc/InputIpc.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/FrameCodecInputTests.cs`, `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/InputWireContractsTests.cs`

**Interfaces:**
- Produces:

```csharp
public enum FrameType : byte { …, SendText = 22, …, SendTextAck = 80 }
public static LocalFrame LocalFrame.InputJson(FrameType type, string json);
public sealed record SendTextDto(string AgentId, string Text);
public sealed record SendTextAckDto(bool Ok, string? Reason, string? Error, string? Outcome = null);
public static class SendTextReasons { Malformed="malformed", TextEmpty="text_empty", TooLarge="too_large", NoSuchAgent="no_such_agent", ProtectedKind="protected_kind", NotRunning="not_running", ReaperClaimed="reaper_claimed", ReaperClaimedLate="reaper_claimed_late", QueueFull="queue_full", StopFailed="stop_failed", DeliveryFailed="delivery_failed", Transport="transport" }
public static class SendTextOutcomes { Delivered="delivered", Stopped="stopped" }
public static class InputWire { public const int MaxTextBytes = 256 * 1024; public static bool IsStructurallyValid(SendTextDto? dto); }
public partial class InputIpcJsonContext : JsonSerializerContext   // snake_case; SendTextDto + SendTextAckDto
```

- [ ] **Step 1: Write the failing tests**

`FrameCodecInputTests.cs`:

```csharp
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class FrameCodecInputTests {
    static async Task<LocalFrame> RoundTrip(LocalFrame f) {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, f, CancellationToken.None);
        ms.Position = 0;
        return (await FrameCodec.ReadAsync(ms, CancellationToken.None))!;
    }

    [Test]
    [Arguments(FrameType.SendText)]
    [Arguments(FrameType.SendTextAck)]
    public async Task Input_frames_roundtrip_with_text_payload(FrameType type) {
        var f = await RoundTrip(LocalFrame.InputJson(type, """{"k":"v"}"""));
        await Assert.That(f.Type).IsEqualTo(type);
        await Assert.That(f.Text).IsEqualTo("""{"k":"v"}""");
    }

    [Test]
    public async Task Input_frame_values_are_stable_wire_bytes() {
#pragma warning disable TUnitAssertions0005
        await Assert.That((byte)FrameType.SendText).IsEqualTo((byte)22);
        await Assert.That((byte)FrameType.SendTextAck).IsEqualTo((byte)80);
#pragma warning restore TUnitAssertions0005
    }
}
```

`InputWireContractsTests.cs`:

```csharp
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class InputWireContractsTests {
    [Test]
    public async Task Send_text_serializes_snake_case() =>
        await Assert.That(JsonSerializer.Serialize(new SendTextDto("a1", "hello"), InputIpcJsonContext.Default.SendTextDto))
            .IsEqualTo("""{"agent_id":"a1","text":"hello"}""");

    [Test]
    public async Task Ack_serializes_every_member_with_outcome_last() {
        await Assert.That(JsonSerializer.Serialize(new SendTextAckDto(true, null, null, SendTextOutcomes.Delivered), InputIpcJsonContext.Default.SendTextAckDto))
            .IsEqualTo("""{"ok":true,"reason":null,"error":null,"outcome":"delivered"}""");
        await Assert.That(JsonSerializer.Serialize(new SendTextAckDto(false, SendTextReasons.QueueFull, "full", null), InputIpcJsonContext.Default.SendTextAckDto))
            .IsEqualTo("""{"ok":false,"reason":"queue_full","error":"full","outcome":null}""");
    }

    [Test]
    public async Task Ack_without_outcome_deserializes_to_null_outcome() {
        var ack = JsonSerializer.Deserialize("""{"ok":true,"reason":null,"error":null}""", InputIpcJsonContext.Default.SendTextAckDto)!;
        await Assert.That(ack.Ok).IsTrue();
        await Assert.That(ack.Outcome).IsNull();
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{"agent_id":"a1"}""")]
    [Arguments("""{"agent_id":null,"text":"x"}""")]
    [Arguments("""{"agent_id":"a1","text":null}""")]
    public async Task Missing_or_null_members_are_structurally_invalid(string json) {
        var dto = JsonSerializer.Deserialize(json, InputIpcJsonContext.Default.SendTextDto);
        await Assert.That(InputWire.IsStructurallyValid(dto)).IsFalse();
    }

    [Test]
    public async Task Empty_text_is_structurally_valid_so_the_handler_can_name_it() =>
        await Assert.That(InputWire.IsStructurallyValid(new SendTextDto("a1", ""))).IsTrue();
}
```

- [ ] **Step 2: Run to verify failure** — filters `FrameCodecInputTests` and `InputWireContractsTests`; expected: build errors.

- [ ] **Step 3: Implement**

`FrameType.cs`: after `PermissionResolve = 21,` add

```csharp
    // Composer input for a hosted agent — one-shot; the ack lands when the daemon's delivery settles.
    SendText = 22, // Text = SendTextDto JSON
```

after `PermissionAck = 79,` add

```csharp
    SendTextAck = 80, // Text = SendTextAckDto JSON, reply to SendText
```

`LocalFrame.cs`: add

```csharp
    /// Constructs a SendText or SendTextAck frame, whose payload is UTF-8 JSON (snake_case via
    /// InputIpcJsonContext) carried in Text — see InputIpc.cs.
    public static LocalFrame InputJson(FrameType type, string json) => new(type) { Text = json };
```

`FrameCodec.cs`: in both `Encode` and `Decode`, extend the text-payload arm: after `or FrameType.PermissionPending or FrameType.PermissionResolved or FrameType.PermissionAck` insert `or FrameType.SendText or FrameType.SendTextAck` (before the `=>`).

`InputIpc.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// JSON payloads for the composer input frames. snake_case on the wire; every member always emitted.
public sealed record SendTextDto(string AgentId, string Text);

/// Reason is one of SendTextReasons when Ok is false; Outcome is one of SendTextOutcomes when Ok is true.
public sealed record SendTextAckDto(bool Ok, string? Reason, string? Error, string? Outcome = null);

public static class SendTextReasons {
    public const string Malformed         = "malformed";
    public const string TextEmpty         = "text_empty";
    public const string TooLarge          = "too_large";
    public const string NoSuchAgent       = "no_such_agent";
    public const string ProtectedKind     = "protected_kind";
    public const string NotRunning        = "not_running";
    public const string ReaperClaimed     = "reaper_claimed";
    public const string ReaperClaimedLate = "reaper_claimed_late";
    public const string QueueFull         = "queue_full";
    public const string StopFailed        = "stop_failed";
    public const string DeliveryFailed    = "delivery_failed";
    /// Client-side only: the request or the reply never crossed the socket.
    public const string Transport         = "transport";
}

public static class SendTextOutcomes {
    public const string Delivered = "delivered";
    public const string Stopped   = "stopped";
}

public static class InputWire {
    /// One frame is one buffer, where PTY input streams under the PTY's own flow control.
    public const int MaxTextBytes = 256 * 1024;

    /// STJ source-gen leaves a missing member null and `{}` decodes fine; emptiness is the handler's call.
    public static bool IsStructurallyValid(SendTextDto? dto) =>
        dto is not null && dto.AgentId is not null && dto.Text is not null;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SendTextDto))]
[JsonSerializable(typeof(SendTextAckDto))]
public partial class InputIpcJsonContext : JsonSerializerContext;
```

- [ ] **Step 4: Run tests** — the two filters plus `FrameCodecTests` and `FrameCodecPermissionTests`; expected PASS.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.Cli.Core/LocalIpc/FrameType.cs src/Capacitor.Cli.Core/LocalIpc/LocalFrame.cs src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs src/Capacitor.Cli.Core/LocalIpc/InputIpc.cs test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/FrameCodecInputTests.cs test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/InputWireContractsTests.cs
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Add the SendText local frames and their payloads (#839)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 7: `ILocalControlOps.SendTextAsync`

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/LocalControlOps.cs`
- Modify (add the member so they compile): `src/Capacitor.App/Services/Onboarding/WizardLateBinding.cs` (`LateBoundLocalControlOps` delegates: `bind().SendTextAsync(agentId, text, ct)`), `test/Capacitor.App.Tests.Unit/ScriptedLocalControlOps.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/LocalControlOpsTests.cs`

**Interfaces:**
- Produces: `public sealed record SendTextResult(bool Ok, string? Reason, string? Error, string? Outcome);` and `Task<SendTextResult> SendTextAsync(string agentId, string text, CancellationToken ct)` on `ILocalControlOps`. No reply timeout: EOF, `InvalidDataException`, `IOException`, `SocketException` → `Ok=false, Reason="transport", Error=message, Outcome=null`; the caller's cancellation propagates as `OperationCanceledException`; an `Error` frame → `Ok=false, Reason="transport", Error=frame text`.
- `ScriptedLocalControlOps` gains `ArmSendText()` / `QueueSendText(SendTextResult)` / `SendTextCalls` / `SendTextPayloads` in the existing arm-then-trigger style.

- [ ] **Step 1: Write the failing tests** (append to `LocalControlOpsTests`, reusing `ScriptedOpsServer`, `Sock()` and the Windows guard the file already uses)

```csharp
    static ConnScript SendTextAckThen(string json, Action<string>? capture = null) => async (_, s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);
        if (f?.Type == FrameType.SendText) {
            capture?.Invoke(f.Text);
            await FrameCodec.WriteAsync(s, LocalFrame.InputJson(FrameType.SendTextAck, json), ct);
        }
    };

    [Test]
    public async Task SendText_passes_all_four_ack_members_through() {
        if (OperatingSystem.IsWindows()) return;
        string? sent = null;
        await WithOpsAsync([SendTextAckThen("""{"ok":true,"reason":null,"error":null,"outcome":"stopped"}""", j => sent = j)], async ops => {
            var result = await ops.SendTextAsync("a1", "/quit", CancellationToken.None);
            await Assert.That(result).IsEqualTo(new SendTextResult(true, null, null, "stopped"));
            await Assert.That(sent).IsEqualTo("""{"agent_id":"a1","text":"/quit"}""");
        });
    }

    [Test]
    public async Task SendText_maps_eof_to_transport() {
        if (OperatingSystem.IsWindows()) return;
        await WithOpsAsync([Eof()], async ops => {
            var result = await ops.SendTextAsync("a1", "hi", CancellationToken.None);
            await Assert.That(result.Ok).IsFalse();
            await Assert.That(result.Reason).IsEqualTo(SendTextReasons.Transport);
            await Assert.That(result.Outcome).IsNull();
        });
    }

    [Test]
    public async Task SendText_waits_past_the_reply_timeout_for_a_late_ack() {
        if (OperatingSystem.IsWindows()) return;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConnScript late = async (_, s, ct) => {
            await FrameCodec.ReadAsync(s, ct);
            await release.Task;
            await FrameCodec.WriteAsync(s, LocalFrame.InputJson(FrameType.SendTextAck, """{"ok":true,"reason":null,"error":null,"outcome":"delivered"}"""), ct);
        };
        await WithOpsAsync([late], async ops => {
            var pending = ops.SendTextAsync("a1", "hi", CancellationToken.None);
            await Task.Delay(300);
            await Assert.That(pending.IsCompleted).IsFalse();
            release.SetResult();
            await Assert.That((await pending).Outcome).IsEqualTo("delivered");
        }, configure: ops => { ops.ReplyTimeout = TimeSpan.FromMilliseconds(50); ops.StopReplyTimeout = TimeSpan.FromMilliseconds(50); });
    }

    [Test]
    public async Task SendText_cancellation_closes_the_connection_and_propagates() {
        if (OperatingSystem.IsWindows()) return;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConnScript holdUntilEof = async (_, s, ct) => {
            await FrameCodec.ReadAsync(s, ct);
            var eof = await FrameCodec.ReadAsync(s, ct); // null once the client closes
            if (eof is null) closed.SetResult();
        };
        using var cts = new CancellationTokenSource();
        await WithOpsAsync([holdUntilEof], async ops => {
            var pending = ops.SendTextAsync("a1", "hi", cts.Token);
            await Task.Delay(100);
            cts.Cancel();
            await Assert.That(async () => await pending).Throws<OperationCanceledException>();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        });
    }
```

(`WithOpsAsync`, `Eof()` and `ConnScript` already exist in the file; `SendTextAckThen` is the new building block above.)

- [ ] **Step 2: Run to verify failure** — filter `LocalControlOpsTests`; expected: build error (`SendTextAsync`).

- [ ] **Step 3: Implement**

In `LocalControlOps.cs`, after `StopAgentResult`:

```csharp
/// The SendTextAck's four members verbatim; Reason is "transport" when the exchange itself failed.
public sealed record SendTextResult(bool Ok, string? Reason, string? Error, string? Outcome);
```

Add to `ILocalControlOps`: `Task<SendTextResult> SendTextAsync(string agentId, string text, CancellationToken ct);`

Implementation (the reply timeout is `Timeout.InfiniteTimeSpan`: the ack arrives when the delivery settles):

```csharp
    public async Task<SendTextResult> SendTextAsync(string agentId, string text, CancellationToken ct) {
        var json = JsonSerializer.Serialize(new SendTextDto(agentId, text), InputIpcJsonContext.Default.SendTextDto);
        LocalFrame reply;
        try {
            reply = await ExchangeAsync(LocalFrame.InputJson(FrameType.SendText, json), Timeout.InfiniteTimeSpan, ct);
        } catch (LocalControlOpsException ex) {
            return new SendTextResult(false, SendTextReasons.Transport, ex.Message, null);
        }
        switch (reply.Type) {
            case FrameType.SendTextAck:
                var ack = DeserializeOrThrow(reply.Text, InputIpcJsonContext.Default.SendTextAckDto, "malformed send text ack reply");
                if (ack is null) return new SendTextResult(false, SendTextReasons.Transport, "malformed send text ack reply", null);
                return new SendTextResult(ack.Ok, ack.Reason, ack.Error, ack.Outcome);
            case FrameType.Error:
                return new SendTextResult(false, SendTextReasons.Transport, reply.Text, null);
            default:
                return new SendTextResult(false, SendTextReasons.Transport, $"unexpected daemon response to send text ({reply.Type})", null);
        }
    }
```

`ExchangeAsync` must accept an infinite reply timeout: `new CancellationTokenSource(replyTimeout, _time)` already accepts `Timeout.InfiniteTimeSpan`; verify and leave it. `DeserializeOrThrow` throws `LocalControlOpsException` on bad JSON — wrap the `SendTextAck` case in `try { … } catch (LocalControlOpsException ex) { return new SendTextResult(false, SendTextReasons.Transport, ex.Message, null); }`.

`LateBoundLocalControlOps`: `public Task<SendTextResult> SendTextAsync(string agentId, string text, CancellationToken ct) => bind().SendTextAsync(agentId, text, ct);`

`ScriptedLocalControlOps` (App tests): add a `Queue<TaskCompletionSource<SendTextResult>> _sendTexts`, `public int SendTextCalls;`, `public readonly List<(string AgentId, string Text)> SendTextPayloads = [];`, `ArmSendText()`, `QueueSendText(SendTextResult r)`, and

```csharp
    public Task<SendTextResult> SendTextAsync(string agentId, string text, CancellationToken ct) {
        SendTextCalls++;
        SendTextPayloads.Add((agentId, text));
        if (ct.IsCancellationRequested) return Task.FromCanceled<SendTextResult>(ct);
        var tcs = _sendTexts.Count > 0 ? _sendTexts.Dequeue() : throw new InvalidOperationException("arm SendText first");
        return tcs.Task.WaitAsync(ct);
    }
```

- [ ] **Step 4: Run tests** — `LocalControlOpsTests` filter; build the App project and App tests (`~/.dotnet/dotnet build test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`). Expected: PASS / clean.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.Cli.Core/LocalIpc/LocalControlOps.cs src/Capacitor.App/Services/Onboarding/WizardLateBinding.cs test/Capacitor.App.Tests.Unit/ScriptedLocalControlOps.cs test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/LocalControlOpsTests.cs
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Send composer text over the local control socket (#839)" -m "The ack lands only when the daemon's delivery settles, so this exchange has no reply timeout; the caller's token is the only bound." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Part B — Daemon: the envelope journal

### Task 8: `AgentFileNames.For`

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/AgentFileNames.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentPidRecordStore.cs` (delete its private `SafeName`, call `AgentFileNames.For`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentFileNamesTests.cs`

**Interfaces:**
- Produces: `internal static class AgentFileNames { public static string For(string agentId); }` — lowercase SHA-256 hex of the UTF-8 agent id (`""` for null), 64 chars, no extension.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class AgentFileNamesTests {
    [Test]
    [Arguments("../x")]
    [Arguments("a/b")]
    [Arguments("a\\b")]
    [Arguments("/etc/passwd")]
    [Arguments("agent-1")]
    public async Task Every_id_yields_a_fixed_length_lowercase_hex_name(string id) {
        var name = AgentFileNames.For(id);
        await Assert.That(name).HasLength().EqualTo(64);
        await Assert.That(name).Matches("^[0-9a-f]{64}$");
        await Assert.That(AgentFileNames.For(id)).IsEqualTo(name);
    }

    [Test]
    public async Task Name_is_the_sha256_the_pid_record_store_always_used() {
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("agent-1"))).ToLowerInvariant();
        await Assert.That(AgentFileNames.For("agent-1")).IsEqualTo(expected);
        using var tmp = new TempDir();
        var store = new AgentPidRecordStore(tmp.Path, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        store.Write(new AgentPidRecord("agent-1", 1, "", PidIdentityKind.IdentityUnavailable, "agent", "pi", null, null, "d", "e", DateTimeOffset.UnixEpoch));
        await Assert.That(File.Exists(Path.Combine(tmp.Path, "agents", expected + ".json"))).IsTrue();
    }
}
```



- [ ] **Step 2: Run to verify failure** — `~/.dotnet/dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter '/*/*/AgentFileNamesTests/*'`; expected: build error.

- [ ] **Step 3: Implement**

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Daemon.Services;

/// The file name every per-agent store uses. The agent id crosses the wire unconstrained, so it is
/// hashed rather than interpolated: no `..` or separator can leave the directory, and the PID record
/// and the transcript journal of one agent always share a name.
internal static class AgentFileNames {
    public static string For(string agentId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(agentId ?? ""))).ToLowerInvariant();
}
```

`AgentPidRecordStore.PathFor`: `Path.Combine(_agentsDir, AgentFileNames.For(agentId) + ".json")`; delete `SafeName` and the now-unused `using System.Security.Cryptography; using System.Text;` if nothing else uses them.

- [ ] **Step 4: Run tests** — filter `AgentFileNamesTests` and `AgentPidRecordStoreTests`; PASS.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.Cli.Daemon/Services/AgentFileNames.cs src/Capacitor.Cli.Daemon/Services/AgentPidRecordStore.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentFileNamesTests.cs
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Share the hashed per-agent file name across stores (#839)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 9: `JournalPathLocks`

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/JournalPathLocks.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/JournalPathLocksTests.cs`

**Interfaces:**
- Produces:

```csharp
internal sealed class JournalPathLocks {
    public static readonly JournalPathLocks Shared = new();
    /// Null when the wait timed out; throws OperationCanceledException on ct.
    public Task<IDisposable?> AcquireAsync(string path, TimeSpan timeout, CancellationToken ct);
    /// Entries with a live owner or waiter (tests).
    public int LiveEntries { get; }
}
```

Keyed by `Path.GetFullPath(path)` under `PlatformPaths.Comparer`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class JournalPathLocksTests {
    [Test]
    public async Task Acquire_release_churn_leaves_the_map_empty() {
        var locks = new JournalPathLocks();
        for (var i = 0; i < 200; i++) {
            using var lease = await locks.AcquireAsync($"/j/{i}.jsonl", TimeSpan.FromSeconds(1), CancellationToken.None);
            await Assert.That(lease).IsNotNull();
        }
        await Assert.That(locks.LiveEntries).IsEqualTo(0);
    }

    [Test]
    public async Task A_waiter_keeps_the_entry_alive_until_it_too_releases() {
        var locks = new JournalPathLocks();
        var owner = await locks.AcquireAsync("/j/a.jsonl", TimeSpan.FromSeconds(1), CancellationToken.None);
        var waiter = locks.AcquireAsync("/j/a.jsonl", TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Delay(50);
        await Assert.That(locks.LiveEntries).IsEqualTo(1);
        owner!.Dispose();
        await Assert.That(locks.LiveEntries).IsEqualTo(1);
        (await waiter)!.Dispose();
        await Assert.That(locks.LiveEntries).IsEqualTo(0);
    }

    [Test]
    public async Task Timed_out_and_cancelled_waits_release_nothing_and_leave_no_entry() {
        var locks = new JournalPathLocks();
        var owner = await locks.AcquireAsync("/j/a.jsonl", TimeSpan.FromSeconds(1), CancellationToken.None);

        var timedOut = await locks.AcquireAsync("/j/a.jsonl", TimeSpan.FromMilliseconds(20), CancellationToken.None);
        await Assert.That(timedOut).IsNull();

        using var cts = new CancellationTokenSource(20);
        await Assert.That(async () => await locks.AcquireAsync("/j/a.jsonl", TimeSpan.FromSeconds(5), cts.Token)).Throws<OperationCanceledException>();

        // Still held: a third waiter must not get in.
        await Assert.That(await locks.AcquireAsync("/j/a.jsonl", TimeSpan.FromMilliseconds(20), CancellationToken.None)).IsNull();
        owner!.Dispose();
        await Assert.That(locks.LiveEntries).IsEqualTo(0);
    }

    [Test]
    public async Task Two_spellings_of_one_path_share_one_entry() {
        var locks = new JournalPathLocks();
        using var tmp = new TempDir();
        var a = tmp.PathTo("j.jsonl");
        var b = Path.Combine(tmp.Path, ".", "j.jsonl");
        using var owner = await locks.AcquireAsync(a, TimeSpan.FromSeconds(1), CancellationToken.None);
        await Assert.That(await locks.AcquireAsync(b, TimeSpan.FromMilliseconds(20), CancellationToken.None)).IsNull();
        await Assert.That(locks.LiveEntries).IsEqualTo(1);
    }
}
```

- [ ] **Step 2: Run to verify failure** — filter `JournalPathLocksTests`; build error.

- [ ] **Step 3: Implement**

```csharp
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// One writer per journal path at a time, process-wide. FileStream has no cross-handle append, so two
/// handles on one file overwrite each other; the lock is what makes "one handle" true. Entries are
/// reference-counted so a waiter can never be left holding an entry the map has forgotten.
internal sealed class JournalPathLocks {
    public static readonly JournalPathLocks Shared = new();

    sealed class Entry { public readonly SemaphoreSlim Gate = new(1, 1); public int Count; }

    readonly Dictionary<string, Entry> _entries = new(PlatformPaths.Comparer);
    readonly Lock _sync = new();

    public int LiveEntries { get { lock (_sync) return _entries.Count; } }

    public async Task<IDisposable?> AcquireAsync(string path, TimeSpan timeout, CancellationToken ct) {
        var key = Path.GetFullPath(path);
        Entry entry;
        lock (_sync) {
            if (!_entries.TryGetValue(key, out entry!)) _entries[key] = entry = new Entry();
            entry.Count++;
        }
        var acquired = false;
        try {
            acquired = await entry.Gate.WaitAsync(timeout, ct).ConfigureAwait(false);
        } finally {
            if (!acquired) Decrement(key, entry);
        }
        return acquired ? new Lease(this, key, entry) : null;
    }

    void Decrement(string key, Entry entry) {
        lock (_sync) {
            if (--entry.Count == 0) _entries.Remove(key);
        }
    }

    sealed class Lease(JournalPathLocks owner, string key, Entry entry) : IDisposable {
        int _disposed;
        public void Dispose() {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            entry.Gate.Release();
            owner.Decrement(key, entry);
        }
    }
}
```

- [ ] **Step 4: Run tests** — filter `JournalPathLocksTests`; PASS.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.Cli.Daemon/Services/JournalPathLocks.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/JournalPathLocksTests.cs
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Add reference-counted per-path journal locks (#839)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 10: `TranscriptJournal` — open, record, drain

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/JournalItem.cs`, `src/Capacitor.Cli.Daemon/Services/TranscriptJournal.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/TranscriptJournalTests.cs`

**Interfaces:**
- Produces:

```csharp
internal readonly record struct JournalItem(AcpEventEnvelope Envelope, int GapBefore);

internal sealed class TranscriptJournal {
    public const int Capacity = 4096;
    public static readonly TimeSpan CompleteGrace = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan LockBound     = TimeSpan.FromSeconds(2);

    /// append(path, bytes) is the disk seam; the default appends with FileMode.Open/FileAccess.Write/FileShare.ReadWrite|Delete, seek-to-end, write, flush, close.
    public TranscriptJournal(string path, ILogger logger, TimeProvider? time = null, Action<string, byte[]>? append = null, JournalPathLocks? locks = null, int capacity = Capacity, TimeSpan? completeGrace = null, TimeSpan? lockBound = null);
    public static TranscriptJournal ForAgent(string stateDir, string agentId, ILogger logger);   // <stateDir>/transcripts/<AgentFileNames.For(agentId)>.jsonl

    public string Path { get; }
    public bool IsOpen { get; }        // true only after Open wrote and flushed the header
    public bool CreatedFile { get; }   // the file did not exist before Open
    public bool Drained { get; }       // set by CompleteAsync: the writer exited within the grace
    public int  PendingGap { get; }    // tests

    public bool Open(string? cwd, string? model);   // synchronous IO on the caller's thread
    public void Record(AcpEventEnvelope envelope);  // no IO, never throws, no-op unless IsOpen and not latched
    public Task<bool> CompleteAsync();              // closes the queue, awaits the writer ≤ grace, returns Drained
}
```

The spec calls the lifecycle call `Complete()`; here it is `CompleteAsync()` because it awaits the writer.

Design notes the implementation must honour:
- Channel: `Channel.CreateBounded<JournalItem>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false })`.
- `Record`: return if `!IsOpen || _latched || envelope.Ephemeral`. Take `gap = Interlocked.Exchange(ref _pendingGap, 0)`; `if (!writer.TryWrite(new JournalItem(envelope, gap))) Interlocked.Add(ref _pendingGap, gap + 1);` (put the gap back and count this drop).
- Writer task (`Task.Run`): `await foreach (var item in reader.ReadAllAsync(ct))` → build one buffer: if `item.GapBefore > 0` the `system_note` line first (`new AcpEventEnvelope(Kind: AcpEventKind.SystemNote, Text: $"{item.GapBefore} envelopes were not recorded to this journal", TimestampIso: NowIso())`) then the envelope line, each `EnvelopeJournalFormat.Write(e) + "\n"`; then `using var lease = await locks.AcquireAsync(Path, lockBound, CancellationToken.None)` — a null lease is a write failure; `append(Path, bytes)`. On any exception: log one Warning, set `_latched`, `writer.TryComplete()`, drain and count the rest into the abandonment count, exit. Cancellation is observed only between items (the token is checked by `ReadAllAsync`, never inside an append).
- After the loop ends normally: if `_pendingGap > 0` write one final note line (same path lock).
- `Open`: `Directory.CreateDirectory(dir)`; `CreatedFile = !File.Exists(Path)`; under the path lock (`AcquireAsync(...).GetAwaiter().GetResult()` is acceptable here — the launch path is synchronous by design; a null lease → `IsOpen` stays false, Warning); `using var fs = new FileStream(Path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); fs.Seek(0, SeekOrigin.End); fs.Write(header); fs.Flush(true);` where header is `EnvelopeJournalFormat.Write(AcpEventTranslator.BuildSessionStarted(0, NowIso(), cwd, model)) + "\n"`. On success set `IsOpen = true` and start the writer. Any exception: Warning, `IsOpen` false, return false.
- `CompleteAsync`: idempotent; `writer.TryComplete()`; `await Task.WhenAny(writerTask, Task.Delay(grace, time))`; if the writer finished → `Drained = true`; else `_writerCts.Cancel()`, log Warning "abandoning journal writer: item {Kind} in flight, {Queued} queued, {Gap} unrecorded", `_latched = true`, `Drained = false`. Return `Drained`.
- `NowIso()` = `time.GetUtcNow().ToString("o")`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class TranscriptJournalTests {
    static readonly FakeTimeProvider Time = new(new DateTimeOffset(2026, 9, 9, 10, 0, 0, TimeSpan.Zero));

    static string[] Lines(string path) {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    static AcpEventEnvelope Text(string t) => new(Kind: AcpEventKind.AssistantText, Text: t);

    [Test]
    public async Task Open_writes_the_header_synchronously_and_reports_created() {
        using var tmp = new TempDir();
        var journal = TranscriptJournal.ForAgent(tmp.Path, "agent-1", NullLogger.Instance);

        await Assert.That(journal.Open("/w", "m1")).IsTrue();

        await Assert.That(journal.IsOpen).IsTrue();
        await Assert.That(journal.CreatedFile).IsTrue();
        await Assert.That(journal.Path).IsEqualTo(Path.Combine(tmp.Path, "transcripts", AgentFileNames.For("agent-1") + ".jsonl"));
        var lines = Lines(journal.Path);
        await Assert.That(lines).Count().IsEqualTo(1);
        await Assert.That(EnvelopeJournalFormat.TryRead(lines[0], out var header)).IsTrue();
        await Assert.That(header.Kind).IsEqualTo(AcpEventKind.SessionStarted);
        await Assert.That(header.Cwd).IsEqualTo("/w");
        await Assert.That(header.Model).IsEqualTo("m1");
        await Assert.That(header.RawSessionId).IsNull();
        await journal.CompleteAsync();
    }

    [Test]
    public async Task Second_open_of_the_same_agent_appends_a_header_and_keeps_every_byte() {
        using var tmp = new TempDir();
        var first = TranscriptJournal.ForAgent(tmp.Path, "agent-1", NullLogger.Instance);
        first.Open("/w", null);
        first.Record(Text("a"));
        await Assert.That(await first.CompleteAsync()).IsTrue();
        var before = Lines(first.Path);

        var second = TranscriptJournal.ForAgent(tmp.Path, "agent-1", NullLogger.Instance);
        second.Open("/w", null);
        await Assert.That(second.CreatedFile).IsFalse();
        await second.CompleteAsync();

        var after = Lines(second.Path);
        await Assert.That(after.Take(before.Length)).IsEquivalentTo(before);
        await Assert.That(after).Count().IsEqualTo(before.Length + 1);
    }

    [Test]
    public async Task Record_appends_in_order_skips_ephemerals_and_drains_on_complete() {
        using var tmp = new TempDir();
        var journal = TranscriptJournal.ForAgent(tmp.Path, "agent-1", NullLogger.Instance);
        journal.Open(null, null);
        journal.Record(Text("a"));
        journal.Record(new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: "live", Ephemeral: true));
        journal.Record(Text("b"));

        await Assert.That(await journal.CompleteAsync()).IsTrue();

        var texts = Lines(journal.Path).Skip(1).Select(l => { EnvelopeJournalFormat.TryRead(l, out var e); return e.Text; });
        await Assert.That(texts).IsEquivalentTo(new[] { "a", "b" });
    }

    [Test]
    public async Task No_handle_is_held_between_writes() {
        using var tmp = new TempDir();
        var journal = TranscriptJournal.ForAgent(tmp.Path, "agent-1", NullLogger.Instance);
        journal.Open(null, null);
        journal.Record(Text("a"));
        await journal.CompleteAsync();

        // Exclusive open and delete both succeed: nothing else holds the file.
        using (new FileStream(journal.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        File.Delete(journal.Path);
        await Assert.That(File.Exists(journal.Path)).IsFalse();
    }

    [Test]
    public async Task Header_failure_leaves_the_journal_closed_and_record_a_noop() {
        using var tmp = new TempDir();
        var blockedDir = tmp.CreateFile("transcripts"); // a FILE where the directory should be
        var journal = new TranscriptJournal(Path.Combine(blockedDir, "x.jsonl"), NullLogger.Instance);

        await Assert.That(journal.Open(null, null)).IsFalse();

        await Assert.That(journal.IsOpen).IsFalse();
        journal.Record(Text("a")); // must not throw
        await Assert.That(await journal.CompleteAsync()).IsTrue();
    }

    [Test]
    public async Task Record_never_blocks_while_the_sink_hangs() {
        using var tmp = new TempDir();
        var hang = new ManualResetEventSlim(false);
        var journal = new TranscriptJournal(tmp.PathTo("j.jsonl"), NullLogger.Instance, Time,
            append: (path, bytes) => { if (Lines(path).Length >= 1) hang.Wait(); File.AppendAllText(path, System.Text.Encoding.UTF8.GetString(bytes)); },
            capacity: 4);
        journal.Open(null, null);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100; i++) journal.Record(Text(i.ToString()));
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromMilliseconds(500));

        await Assert.That(journal.PendingGap).IsGreaterThan(0);
        hang.Set();
        await journal.CompleteAsync();
    }
}
```

- [ ] **Step 2: Run to verify failure** — filter `TranscriptJournalTests`; build error.

- [ ] **Step 3: Implement** `JournalItem.cs` and `TranscriptJournal.cs` per the design notes above. Skeleton:

```csharp
using System.Text;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// The daemon-written envelope record of one hosted session: a bounded queue fed from the runtime's
/// emit site and one writer task that appends to disk. Record never does IO; a hung disk costs journal
/// lines, never protocol liveness.
internal sealed class TranscriptJournal {
    public const int Capacity = 4096;
    public static readonly TimeSpan CompleteGrace = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan LockBound     = TimeSpan.FromSeconds(2);

    readonly ILogger _logger;
    readonly TimeProvider _time;
    readonly Action<string, byte[]> _append;
    readonly JournalPathLocks _locks;
    readonly TimeSpan _grace, _lockBound;
    readonly Channel<JournalItem> _queue;
    readonly CancellationTokenSource _writerCts = new();
    Task? _writer;
    int _pendingGap;
    volatile bool _latched;
    volatile JournalItem? _inFlight;
    int _completed;

    public TranscriptJournal(string path, ILogger logger, TimeProvider? time = null, Action<string, byte[]>? append = null,
            JournalPathLocks? locks = null, int capacity = Capacity, TimeSpan? completeGrace = null, TimeSpan? lockBound = null) {
        Path = path; _logger = logger; _time = time ?? TimeProvider.System; _append = append ?? AppendToFile;
        _locks = locks ?? JournalPathLocks.Shared; _grace = completeGrace ?? CompleteGrace; _lockBound = lockBound ?? LockBound;
        _queue = Channel.CreateBounded<JournalItem>(new BoundedChannelOptions(capacity) {
            FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    }

    public static TranscriptJournal ForAgent(string stateDir, string agentId, ILogger logger) =>
        new(System.IO.Path.Combine(stateDir, "transcripts", AgentFileNames.For(agentId) + ".jsonl"), logger);

    public string Path { get; }
    public bool IsOpen { get; private set; }
    public bool CreatedFile { get; private set; }
    public bool Drained { get; private set; }
    public int PendingGap => Volatile.Read(ref _pendingGap);

    public bool Open(string? cwd, string? model) {
        try {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            CreatedFile = !File.Exists(Path);
            using var lease = _locks.AcquireAsync(Path, _lockBound, CancellationToken.None).GetAwaiter().GetResult();
            if (lease is null) { _logger.LogWarning("Transcript journal {Path}: could not take the path lock to open", Path); return false; }
            var header = Encode(AcpEventTranslator.BuildSessionStarted(0, NowIso(), cwd, model));
            using (var fs = new FileStream(Path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) {
                fs.Seek(0, SeekOrigin.End); fs.Write(header); fs.Flush(true);
            }
            IsOpen = true;
            _writer = Task.Run(() => RunWriterAsync(_writerCts.Token));
            return true;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Transcript journal {Path}: open failed; the session will not be journaled", Path);
            return false;
        }
    }

    public void Record(AcpEventEnvelope envelope) {
        if (!IsOpen || _latched || envelope.Ephemeral) return;
        var gap = Interlocked.Exchange(ref _pendingGap, 0);
        if (!_queue.Writer.TryWrite(new JournalItem(envelope, gap))) Interlocked.Add(ref _pendingGap, gap + 1);
    }

    public async Task<bool> CompleteAsync() {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return Drained;
        _queue.Writer.TryComplete();
        if (_writer is null) { Drained = true; return true; }
        var finished = await Task.WhenAny(_writer, Task.Delay(_grace, _time)).ConfigureAwait(false) == _writer;
        if (finished) { Drained = true; return true; }
        _writerCts.Cancel();
        _latched = true;
        _logger.LogWarning("Transcript journal {Path}: abandoning the writer — item {Kind} in flight, {Queued} queued, {Gap} unrecorded",
            Path, _inFlight?.Envelope.Kind ?? "(none)", _queue.Reader.Count, PendingGap);
        return false;
    }

    async Task RunWriterAsync(CancellationToken ct) {
        try {
            await foreach (var item in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false)) {
                _inFlight = item;
                await WriteItemAsync(item).ConfigureAwait(false);
                _inFlight = null;
            }
            var gap = Interlocked.Exchange(ref _pendingGap, 0);
            if (gap > 0) await WriteBytesAsync(Encode(GapNote(gap))).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
        } catch (Exception ex) {
            _latched = true;
            _queue.Writer.TryComplete();
            var abandoned = 0;
            while (_queue.Reader.TryRead(out _)) abandoned++;
            _logger.LogWarning(ex, "Transcript journal {Path}: write failed; {Abandoned} queued and {Gap} unrecorded envelopes are lost", Path, abandoned, PendingGap);
        }
    }

    Task WriteItemAsync(JournalItem item) {
        var bytes = item.GapBefore > 0
            ? [.. Encode(GapNote(item.GapBefore)), .. Encode(item.Envelope)]
            : Encode(item.Envelope);
        return WriteBytesAsync(bytes);
    }

    async Task WriteBytesAsync(byte[] bytes) {
        using var lease = await _locks.AcquireAsync(Path, _lockBound, CancellationToken.None).ConfigureAwait(false)
                       ?? throw new IOException("could not take the journal path lock");
        _append(Path, bytes);
    }

    AcpEventEnvelope GapNote(int count) =>
        new(Kind: AcpEventKind.SystemNote, Text: $"{count} envelopes were not recorded to this journal", TimestampIso: NowIso());

    static byte[] Encode(AcpEventEnvelope e) => Encoding.UTF8.GetBytes(EnvelopeJournalFormat.Write(e) + "\n");
    string NowIso() => _time.GetUtcNow().ToString("o");

    static void AppendToFile(string path, byte[] bytes) {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(0, SeekOrigin.End); fs.Write(bytes); fs.Flush(true);
    }
}
```

`JournalItem.cs`: `internal readonly record struct JournalItem(AcpEventEnvelope Envelope, int GapBefore);`

- [ ] **Step 4: Run tests** — filter `TranscriptJournalTests`; PASS.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.Cli.Daemon/Services/JournalItem.cs src/Capacitor.Cli.Daemon/Services/TranscriptJournal.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/TranscriptJournalTests.cs
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Journal envelope transcripts to the daemon state dir (#839)" -m "Record is one TryWrite onto a bounded queue and never touches the disk, so a hung filesystem costs journal lines rather than an ACP read loop." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 11: `TranscriptJournal` — gaps, abandonment, latching, path locks

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/TranscriptJournal.cs` (only if a test below exposes a defect)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/TranscriptJournalTests.cs` (append)

- [ ] **Step 1: Write the tests** (append to the class; `Lines`, `Text`, `Time` from Task 10)

```csharp
    /// A sink that blocks every append until released, then writes for real.
    sealed class GatedSink {
        public readonly SemaphoreSlim Release = new(0);
        public int Appends;
        public void Append(string path, byte[] bytes) {
            Release.Wait();
            Interlocked.Increment(ref Appends);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(0, SeekOrigin.End); fs.Write(bytes); fs.Flush(true);
        }
    }

    [Test]
    public async Task Gap_note_lands_between_the_last_kept_and_the_first_after_the_loss() {
        using var tmp = new TempDir();
        var sink = new GatedSink();
        var journal = new TranscriptJournal(tmp.PathTo("j.jsonl"), NullLogger.Instance, Time, sink.Append, capacity: 2);
        journal.Open(null, null);
        journal.Record(Text("a")); journal.Record(Text("b")); // fill the queue (writer is blocked)
        journal.Record(Text("lost-1")); journal.Record(Text("lost-2"));
        await Assert.That(journal.PendingGap).IsEqualTo(2);
        sink.Release.Release(); // the writer takes "a": one slot frees
        await Task.Delay(100);
        journal.Record(Text("c")); // exactly one slot free: this item carries the gap
        await Assert.That(journal.PendingGap).IsEqualTo(0);
        sink.Release.Release(10);
        await Assert.That(await journal.CompleteAsync()).IsTrue();

        var texts = Lines(journal.Path).Skip(1).Select(l => { EnvelopeJournalFormat.TryRead(l, out var e); return (e.Kind, e.Text); }).ToList();
        await Assert.That(texts).IsEquivalentTo(new[] {
            (AcpEventKind.AssistantText, "a"), (AcpEventKind.AssistantText, "b"),
            (AcpEventKind.SystemNote, "2 envelopes were not recorded to this journal"), (AcpEventKind.AssistantText, "c") });
    }

    [Test]
    public async Task Gap_pending_at_completion_is_written_last() {
        using var tmp = new TempDir();
        var sink = new GatedSink();
        var journal = new TranscriptJournal(tmp.PathTo("j.jsonl"), NullLogger.Instance, Time, sink.Append, capacity: 1);
        journal.Open(null, null);
        journal.Record(Text("a")); journal.Record(Text("lost"));
        sink.Release.Release(10);
        await Assert.That(await journal.CompleteAsync()).IsTrue();
        var last = Lines(journal.Path).Last();
        EnvelopeJournalFormat.TryRead(last, out var e);
        await Assert.That(e.Text).IsEqualTo("1 envelopes were not recorded to this journal");
    }

    [Test]
    public async Task Complete_against_a_hung_sink_returns_within_the_grace_and_reports_not_drained() {
        using var tmp = new TempDir();
        var sink = new GatedSink();
        var log = new CapturingLogger();
        var journal = new TranscriptJournal(tmp.PathTo("j.jsonl"), log, Time, sink.Append, capacity: 2, completeGrace: TimeSpan.FromMilliseconds(200));
        journal.Open(null, null);
        journal.Record(Text("a")); journal.Record(Text("b")); journal.Record(Text("lost"));
        await Task.Delay(50);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var drained = await journal.CompleteAsync();

        await Assert.That(drained).IsFalse();
        await Assert.That(journal.Drained).IsFalse();
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));
        await Assert.That(log.Warnings.Single()).Contains("assistant_text").And.Contains("1 queued").And.Contains("1 unrecorded");
        journal.Record(Text("after")); // latched: silently ignored
        sink.Release.Release(10);
    }

    [Test]
    public async Task Cancellation_never_splits_a_gap_bearing_item() {
        using var tmp = new TempDir();
        var sink = new GatedSink();
        var journal = new TranscriptJournal(tmp.PathTo("j.jsonl"), NullLogger.Instance, Time, sink.Append, capacity: 1, completeGrace: TimeSpan.FromMilliseconds(100));
        journal.Open(null, null);
        journal.Record(Text("a")); journal.Record(Text("lost"));
        await Task.Delay(50);
        journal.Record(Text("c")); // still full: counted
        var complete = journal.CompleteAsync(); // expires while the sink still blocks on "a"
        await Assert.That(await complete).IsFalse();
        sink.Release.Release(10);
        await Task.Delay(200);

        // The abandoned writer finished "a" (one item, whole) and then observed cancellation: no torn note.
        var lines = Lines(journal.Path);
        foreach (var line in lines) await Assert.That(EnvelopeJournalFormat.TryRead(line, out _)).IsTrue();
        await Assert.That(sink.Appends).IsEqualTo(1);
    }

    [Test]
    public async Task First_append_failure_latches_after_one_warning_and_counts_the_queue() {
        using var tmp = new TempDir();
        var log = new CapturingLogger();
        var journal = new TranscriptJournal(tmp.PathTo("j.jsonl"), log, Time, append: (_, _) => throw new IOException("disk gone"));
        journal.Open(null, null);
        journal.Record(Text("a")); journal.Record(Text("b"));
        await Assert.That(await journal.CompleteAsync()).IsTrue(); // the writer exited (by faulting), so it drained
        await Assert.That(log.Warnings).Count().IsEqualTo(1);
        await Assert.That(log.Warnings[0]).Contains("write failed");
        await Assert.That(Lines(journal.Path)).Count().IsEqualTo(1); // header only
    }

    [Test]
    public async Task Torn_gap_item_renders_the_note_and_drops_the_torn_envelope() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("j.jsonl");
        // The crash model: a buffer cut after its note line.
        var journal = new TranscriptJournal(path, NullLogger.Instance, Time, append: (p, bytes) => {
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            var cut = text.IndexOf('\n') + 1 + 5;
            File.AppendAllText(p, text[..Math.Min(cut, text.Length)]);
        }, capacity: 1);
        journal.Open(null, null);
        journal.Record(Text("a")); journal.Record(Text("lost")); // "a" carries no gap
        await Task.Delay(50);
        journal.Record(Text("c")); // carries gap 1: note + torn "c"
        await journal.CompleteAsync();

        var tail = new JsonlTail(path).ReadAppended();
        await Assert.That(tail.Lines.Count(l => l.Contains("not recorded"))).IsEqualTo(1);
        await Assert.That(tail.Lines.Any(l => l.Contains("\"c\""))).IsFalse();
    }

    [Test]
    public async Task Same_id_open_waits_out_an_abandoned_writer_and_appends_after_its_line() {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var locks = new JournalPathLocks();
        var sink = new GatedSink();
        var first = new TranscriptJournal(tmp.PathTo("j.jsonl"), NullLogger.Instance, Time, sink.Append, locks, capacity: 1, completeGrace: TimeSpan.FromMilliseconds(100), lockBound: TimeSpan.FromMilliseconds(100));
        first.Open(null, null);
        first.Record(Text("late"));
        await Task.Delay(50);
        await Assert.That(await first.CompleteAsync()).IsFalse(); // writer abandoned inside the (gated) append, holding the path lock

        var second = new TranscriptJournal(first.Path, NullLogger.Instance, Time, locks: locks, lockBound: TimeSpan.FromMilliseconds(100));
        await Assert.That(second.Open(null, null)).IsFalse(); // the lock is held by the abandoned call
        await Assert.That(second.IsOpen).IsFalse();

        sink.Release.Release(10);
        await Task.Delay(200);
        var third = new TranscriptJournal(first.Path, NullLogger.Instance, Time, locks: locks, lockBound: TimeSpan.FromMilliseconds(100));
        await Assert.That(third.Open(null, null)).IsTrue();
        await third.CompleteAsync();

        var texts = Lines(first.Path).Select(l => { EnvelopeJournalFormat.TryRead(l, out var e); return e.Kind == AcpEventKind.SessionStarted ? "header" : e.Text; });
        await Assert.That(texts).IsEquivalentTo(new[] { "header", "late", "header" });
    }

    [Test]
    public async Task Same_agent_id_under_two_state_dirs_takes_two_locks() {
        using var a = new TempDir(); using var b = new TempDir();
        var locks = new JournalPathLocks();
        var sink = new GatedSink();
        var hung = new TranscriptJournal(TranscriptJournal.ForAgent(a.Path, "agent-1", NullLogger.Instance).Path, NullLogger.Instance, Time, sink.Append, locks, capacity: 1, completeGrace: TimeSpan.FromMilliseconds(100));
        hung.Open(null, null); hung.Record(Text("x"));
        await Task.Delay(50);
        await hung.CompleteAsync();

        var other = new TranscriptJournal(TranscriptJournal.ForAgent(b.Path, "agent-1", NullLogger.Instance).Path, NullLogger.Instance, Time, locks: locks, lockBound: TimeSpan.FromMilliseconds(100));
        await Assert.That(other.Open(null, null)).IsTrue();
        await other.CompleteAsync();
        sink.Release.Release(10);
    }

    [Test]
    public async Task Abrupt_disposal_leaves_exactly_the_lines_the_writer_reached() {
        using var tmp = new TempDir();
        var journal = TranscriptJournal.ForAgent(tmp.Path, "agent-1", NullLogger.Instance);
        journal.Open(null, null);
        for (var i = 0; i < 50; i++) journal.Record(Text(i.ToString()));
        await Task.Delay(300); // no CompleteAsync: the process "dies"
        foreach (var line in Lines(journal.Path)) await Assert.That(EnvelopeJournalFormat.TryRead(line, out _)).IsTrue();
    }
```

`CapturingLogger` is an `ILogger` that records `LogLevel.Warning` messages into `List<string> Warnings`; if the daemon test project already has one (grep `class CapturingLogger` / `ListLogger` under `test/`), use it, otherwise add `test/Capacitor.Tests.Helpers/CapturingLogger.cs` (public, `IsEnabled` true, `BeginScope` returns a no-op disposable, `Log` formats with the supplied formatter and appends when `logLevel == LogLevel.Warning`).

- [ ] **Step 2: Run the tests** — filter `TranscriptJournalTests`. Fix any defect they expose in `TranscriptJournal` (the most likely: `CompleteAsync` must not await the writer after cancelling; `Record` must not lose the `gap` when `TryWrite` fails).

- [ ] **Step 3: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.Cli.Daemon/Services/TranscriptJournal.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/TranscriptJournalTests.cs test/Capacitor.Tests.Helpers
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Pin journal gap notes, abandonment and path locking (#839)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 12: `TranscriptJournalSweep`

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/TranscriptJournalSweep.cs`
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs` (register at line ~587 beside `LocalControlServer`; run once after `ReapOrphansOnceAsync()` at line ~742)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/TranscriptJournalSweepTests.cs`

**Interfaces:**
- Produces:

```csharp
internal sealed class TranscriptJournalSweep(string stateDir, TimeProvider time, ILogger<TranscriptJournalSweep> logger, JournalPathLocks? locks = null) : BackgroundService {
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    public static readonly TimeSpan Interval  = TimeSpan.FromHours(24);
    public Task RunOnceAsync(CancellationToken ct);   // single-flight, never throws
    public int SweepsStarted { get; }                 // tests
}
```

Rules: delete `<stateDir>/transcripts/*.jsonl` whose `File.GetLastWriteTimeUtc` is older than `time.GetUtcNow() - Retention` AND for which `<stateDir>/agents/<same stem>.json` does not exist; take the path lock (`LockBound`) first, skip when it cannot be had. Per-file try/catch at Warning; outer try/catch around enumeration. `ExecuteAsync`: `using var timer = new PeriodicTimer(Interval, time); while (await timer.WaitForNextTickAsync(ct)) await RunOnceAsync(ct);`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class TranscriptJournalSweepTests {
    static readonly DateTimeOffset Now = new(2026, 9, 9, 10, 0, 0, TimeSpan.Zero);

    static string Journal(TempDir tmp, string agentId, TimeSpan age) {
        var path = Path.Combine(tmp.CreateDir("transcripts"), AgentFileNames.For(agentId) + ".jsonl");
        File.WriteAllText(path, "{}\n");
        File.SetLastWriteTimeUtc(path, (Now - age).UtcDateTime);
        return path;
    }

    static void PidRecord(TempDir tmp, string agentId) =>
        File.WriteAllText(Path.Combine(tmp.CreateDir("agents"), AgentFileNames.For(agentId) + ".json"), "{}");

    static TranscriptJournalSweep Sweep(TempDir tmp, FakeTimeProvider time, JournalPathLocks? locks = null) =>
        new(tmp.Path, time, NullLogger<TranscriptJournalSweep>.Instance, locks);

    [Test]
    public async Task Deletes_only_old_journals_without_a_pid_record() {
        using var tmp = new TempDir();
        var time = new FakeTimeProvider(Now);
        var old        = Journal(tmp, "old", TimeSpan.FromDays(31));
        var fresh      = Journal(tmp, "fresh", TimeSpan.FromDays(1));
        var oldButLive = Journal(tmp, "live", TimeSpan.FromDays(31)); PidRecord(tmp, "live");
        var other      = Path.Combine(tmp.Path, "transcripts", "notes.txt"); File.WriteAllText(other, "x"); File.SetLastWriteTimeUtc(other, (Now - TimeSpan.FromDays(40)).UtcDateTime);

        await Sweep(tmp, time).RunOnceAsync(CancellationToken.None);

        await Assert.That(File.Exists(old)).IsFalse();
        await Assert.That(File.Exists(fresh)).IsTrue();
        await Assert.That(File.Exists(oldButLive)).IsTrue();
        await Assert.That(File.Exists(other)).IsTrue();
    }

    [Test]
    public async Task Skips_a_journal_whose_lock_is_held() {
        using var tmp = new TempDir();
        var time = new FakeTimeProvider(Now);
        var locks = new JournalPathLocks();
        var old = Journal(tmp, "old", TimeSpan.FromDays(31));
        using var held = await locks.AcquireAsync(old, TimeSpan.FromSeconds(1), CancellationToken.None);

        await Sweep(tmp, time, locks).RunOnceAsync(CancellationToken.None);

        await Assert.That(File.Exists(old)).IsTrue();
    }

    [Test]
    public async Task Missing_directory_and_enumeration_faults_never_escape() {
        using var tmp = new TempDir();
        var time = new FakeTimeProvider(Now);
        await Sweep(tmp, time).RunOnceAsync(CancellationToken.None); // no transcripts dir
        tmp.CreateFile("transcripts"); // a file where the directory belongs: enumeration throws
        await Sweep(tmp, time).RunOnceAsync(CancellationToken.None);
    }

    [Test]
    public async Task Runs_again_after_24_hours_without_a_restart() {
        using var tmp = new TempDir();
        var time = new FakeTimeProvider(Now);
        var sweep = Sweep(tmp, time);
        await sweep.StartAsync(CancellationToken.None);
        await Assert.That(sweep.SweepsStarted).IsEqualTo(0);

        var journal = Journal(tmp, "old", TimeSpan.FromDays(31));
        time.Advance(TimeSpan.FromHours(24));
        await WaitUntil(() => sweep.SweepsStarted == 1);
        await Assert.That(File.Exists(journal)).IsFalse();

        time.Advance(TimeSpan.FromHours(24));
        await WaitUntil(() => sweep.SweepsStarted == 2);
        await sweep.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task A_tick_during_a_sweep_is_skipped() {
        using var tmp = new TempDir();
        var time = new FakeTimeProvider(Now);
        var locks = new JournalPathLocks();
        var old = Journal(tmp, "old", TimeSpan.FromDays(31));
        using var held = await locks.AcquireAsync(old, TimeSpan.FromSeconds(1), CancellationToken.None); // the sweep waits ≤ LockBound on this
        var sweep = Sweep(tmp, time, locks);

        var first = sweep.RunOnceAsync(CancellationToken.None);
        var second = sweep.RunOnceAsync(CancellationToken.None);
        await Task.WhenAll(first, second);

        await Assert.That(sweep.SweepsStarted).IsEqualTo(1);
    }

    static async Task WaitUntil(Func<bool> condition) {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        await Assert.That(condition()).IsTrue();
    }
}
```

- [ ] **Step 2: Run to verify failure** — filter `TranscriptJournalSweepTests`; build error.

- [ ] **Step 3: Implement**

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// Reaps envelope journals kcap owns: older than the retention and with no PID record under the
/// same name. Runs once after the startup orphan reap (which removes a prior epoch's records) and
/// then every 24 hours. Never throws — a sweep fault must not block the daemon's connect.
internal sealed class TranscriptJournalSweep(string stateDir, TimeProvider time, ILogger<TranscriptJournalSweep> logger, JournalPathLocks? locks = null) : BackgroundService {
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    public static readonly TimeSpan Interval  = TimeSpan.FromHours(24);

    readonly JournalPathLocks _locks = locks ?? JournalPathLocks.Shared;
    int _running;
    int _started;

    public int SweepsStarted => Volatile.Read(ref _started);

    protected override async Task ExecuteAsync(CancellationToken ct) {
        using var timer = new PeriodicTimer(Interval, time);
        try {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) await RunOnceAsync(ct).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public async Task RunOnceAsync(CancellationToken ct) {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        Interlocked.Increment(ref _started);
        try {
            var dir = Path.Combine(stateDir, "transcripts");
            if (!Directory.Exists(dir)) return;
            var cutoff = time.GetUtcNow() - Retention;
            foreach (var path in Directory.EnumerateFiles(dir, "*.jsonl")) {
                ct.ThrowIfCancellationRequested();
                await TrySweepAsync(path, cutoff).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
        } catch (Exception ex) {
            logger.LogWarning(ex, "Transcript journal sweep: enumeration failed");
        } finally {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    async Task TrySweepAsync(string path, DateTimeOffset cutoff) {
        try {
            if (File.GetLastWriteTimeUtc(path) >= cutoff.UtcDateTime) return;
            var record = Path.Combine(stateDir, "agents", Path.GetFileNameWithoutExtension(path) + ".json");
            if (File.Exists(record)) return;
            using var lease = await _locks.AcquireAsync(path, TranscriptJournal.LockBound, CancellationToken.None).ConfigureAwait(false);
            if (lease is null) return;
            File.Delete(path);
        } catch (Exception ex) {
            logger.LogWarning(ex, "Transcript journal sweep: skipped {Path}", path);
        }
    }
}
```

`DaemonRunner.cs` — beside the `LocalControlServer` registration:

```csharp
        builder.Services.AddSingleton(sp => new TranscriptJournalSweep(
            config.Store.StateDirectory(config.Name), TimeProvider.System, sp.GetRequiredService<ILogger<TranscriptJournalSweep>>()));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TranscriptJournalSweep>());
```

and after `await orchestrator.ReapOrphansOnceAsync();`:

```csharp
                await host.Services.GetRequiredService<TranscriptJournalSweep>().RunOnceAsync(lifetime.ApplicationStopping);
```

- [ ] **Step 4: Run tests** — filter `TranscriptJournalSweepTests`; PASS. Build the daemon.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.Cli.Daemon/Services/TranscriptJournalSweep.cs src/Capacitor.Cli.Daemon/DaemonRunner.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/TranscriptJournalSweepTests.cs
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Sweep envelope journals after 30 days (#839)" -m "The startup run follows the orphan reap: the reap is what removes a prior epoch's PID records, and a sweep before it would keep every journal whose agent died with the old daemon." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 13: Orchestrator — journal on the launch path, snapshot fields, PTY-only gating

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntimeFactory.cs` (`RuntimeStartContext` gains a trailing `TranscriptJournal? Journal = null`)
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` — `AgentInstance` gains `public TranscriptJournal? Journal { get; init; }`; `HandleLaunchAgentCore` constructs the journal before `runtimeCtx`, sets `SessionId`/`TranscriptPath`/`Journal` in the `AgentInstance` initializer (line ~2360), gates `DetectSessionIdAsync` (line ~2481) on `start.Transcript is null`, gates the Codex probe (`isCodex` at line ~3705) on `agent.Runtime.EmitsTerminalOutput`
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs` — `SnapshotAgentsForStatus` emits `TranscriptFormat`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentStatusSnapshotTests.cs` (append), `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorJournalTests.cs` (new)

**Interfaces:**
- Consumes: `TranscriptJournal.ForAgent(stateDir, agentId, logger)`, `.IsOpen`, `.Path`, `SessionIds.Canonical`, `TranscriptFormats`.
- Produces: `RuntimeStartContext.Journal`; `AgentInstance.Journal`; every status row carries `transcript_format`.

- [ ] **Step 1: Write the failing tests**

Append to `AgentStatusSnapshotTests` (uses its `Build()` fixture and `RegisterAgentForTest`):

```csharp
    [Test]
    public async Task Envelope_sourced_agent_reports_envelopes_format_canonical_session_id_and_journal_path() {
        var f = Build();
        try {
            var journal = TranscriptJournal.ForAgent(f.Daemons.Store.StateDirectory("status-snapshot-test"), "acp-1", NullLogger.Instance);
            journal.Open("/w", "m");
            var runtime = new FakeAcpRuntime { AcpSessionId = "8BC7255F-2453-4EFD-A733-0AF4B6AE9F20" };
            f.Orchestrator.RegisterAgentForTest(new AgentInstance("acp-1", "p", "m", null, "/repo", "cursor", runtime, new WorktreeInfo("/repo", "b", "/w"), new CancellationTokenSource()) {
                SessionId = SessionIds.Canonical(runtime.AcpSessionId), TranscriptPath = journal.Path, Journal = journal });

            var row = f.Orchestrator.SnapshotAgentsForStatus().Single();

            await Assert.That(row.TranscriptFormat).IsEqualTo(TranscriptFormats.Envelopes);
            await Assert.That(row.SessionId).IsEqualTo("8bc7255f24534efda7330af4b6ae9f20");
            await Assert.That(row.TranscriptPath).IsEqualTo(journal.Path);
            await journal.CompleteAsync();
        } finally { await f.CleanupAsync(); }
    }

    [Test]
    public async Task Pty_agent_reports_vendor_format_and_null_path_until_discovery() {
        var f = Build();
        try {
            f.Orchestrator.RegisterAgentForTest(new AgentInstance("pty-1", "p", "m", null, "/repo", "claude",
                new PtyHostedAgentRuntime("claude", NoopPtyProcess.Instance), new WorktreeInfo("/repo", "b", "/w"), new CancellationTokenSource()));

            var row = f.Orchestrator.SnapshotAgentsForStatus().Single();

            await Assert.That(row.TranscriptFormat).IsEqualTo(TranscriptFormats.Vendor);
            await Assert.That(row.TranscriptPath).IsNull();
            await Assert.That(row.SessionId).IsNull();
        } finally { await f.CleanupAsync(); }
    }

    [Test]
    public async Task Opaque_acp_session_id_is_reported_unchanged() {
        var f = Build();
        try {
            var runtime = new FakeAcpRuntime { AcpSessionId = "sess-1" };
            f.Orchestrator.RegisterAgentForTest(new AgentInstance("acp-2", "p", "m", null, "/repo", "cursor", runtime, new WorktreeInfo("/repo", "b", "/w"), new CancellationTokenSource()) {
                SessionId = SessionIds.Canonical(runtime.AcpSessionId) });
            await Assert.That(f.Orchestrator.SnapshotAgentsForStatus().Single().SessionId).IsEqualTo("sess-1");
        } finally { await f.CleanupAsync(); }
    }
```

New `AgentOrchestratorJournalTests.cs` (launch through the real orchestrator with `SpyAcpHostedAgentRuntimeFactory`):

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class AgentOrchestratorJournalTests {
    [Test]
    public async Task Launch_hands_the_factory_an_unopened_journal_and_publishes_the_opened_path_before_registration() {
        using var repoPath = GitRepo.CreateWithCommit();
        var server = new CaptureServerConnection();
        var factory = new OpeningAcpFactory();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath, extraRuntimeFactories: [factory]);

        await orch.HandleLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch("agent-journal", repoPath));

        var ctx = factory.LastContext!;
        await Assert.That(ctx.Journal).IsNotNull();
        await Assert.That(ctx.Journal!.Path).IsEqualTo(Path.Combine(orch.PidRecordRootForTest, "transcripts", AgentFileNames.For("agent-journal") + ".jsonl"));
        var row = orch.SnapshotAgentsForStatus().Single();
        await Assert.That(row.TranscriptPath).IsEqualTo(ctx.Journal.Path);
        await Assert.That(row.TranscriptFormat).IsEqualTo(TranscriptFormats.Envelopes);
        await Assert.That(row.SessionId).IsEqualTo("acp-sess-1");
        await Assert.That(File.Exists(ctx.Journal.Path)).IsTrue();
        // The first status the server saw for this agent already carried the canonical id.
        await Assert.That(server.StatusChangedWithSession.First(c => c.AgentId == "agent-journal").SessionId).IsEqualTo("acp-sess-1");
        // Discovery never started for an envelope-sourced agent.
        await Assert.That(orch.DiscoveryStartsForTest).IsEqualTo(0);
    }

    [Test]
    public async Task Envelope_sourced_codex_keeps_the_journal_path_and_arms_no_probe() {
        using var repoPath = GitRepo.CreateWithCommit();
        var server = new CaptureServerConnection();
        var factory = new OpeningAcpFactory("codex");
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath, extraRuntimeFactories: [factory]);

        await orch.HandleLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch("agent-codex-env", repoPath) with { Vendor = "codex" });
        await orch.HandleSendInputForTest(new SendInputCommand("agent-codex-env", "hello", null));

        await Assert.That(orch.SnapshotAgentsForStatus().Single().TranscriptPath).IsEqualTo(factory.LastContext!.Journal!.Path);
        await Assert.That(orch.DiscoveryStartsForTest).IsEqualTo(0);
        await Assert.That(orch.CodexProbesArmedForTest).IsEqualTo(0);
    }

    /// The real factories call Open before constructing the runtime; this double does the same.
    sealed class OpeningAcpFactory(string vendor = "cursor") : IHostedAgentRuntimeFactory {
        readonly SpyAcpHostedAgentRuntimeFactory _inner = new(vendor);
        public string CliPath => _inner.CliPath;
        public string Vendor => _inner.Vendor;
        public bool SupportsUnattended => false;
        public RuntimeStartContext? LastContext => _inner.LastContext;
        public FakeAcpRuntime? LastRuntime => _inner.LastRuntime;
        public bool IsAvailable() => true;
        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            ctx.Journal?.Open(ctx.Worktree.Path, ctx.Model);
            return _inner.StartAsync(ctx, ct);
        }
    }
}
```

`CaptureServerConnection` records `(AgentId, Status)` in `StatusChangedCalls`; add a sibling `public List<(string AgentId, string Status, string? SessionId)> StatusChangedWithSession { get; } = [];` appended in the same `AgentStatusChangedAsync` override (leave the existing list untouched). Add `internal int CodexProbesArmedForTest => Volatile.Read(ref _codexProbesArmed);` to the orchestrator, incremented at the top of `ArmCodexTurnProbe`.

- [ ] **Step 2: Run to verify failure** — filters `AgentStatusSnapshotTests`, `AgentOrchestratorJournalTests`; build errors.

- [ ] **Step 3: Implement**

`IHostedAgentRuntimeFactory.cs` — last parameter of `RuntimeStartContext`:

```csharp
        // The launch's transcript journal, unopened. An envelope-emitting factory opens it before
        // constructing its runtime and hands it to the constructor; a PTY factory ignores it.
        TranscriptJournal? Journal = null
```

`AgentOrchestrator.cs`:

1. `AgentInstance`: add `public TranscriptJournal? Journal { get; init; }` beside `TranscriptPath`.
2. In `HandleLaunchAgentCore`, immediately before `var runtimeCtx = new RuntimeStartContext(`:

```csharp
            var journal = TranscriptJournal.ForAgent(_pidRecordRoot, agentId, _logger);
```

and add `Journal: journal` to the `RuntimeStartContext` construction. Declare `TranscriptJournal? journal = null;` beside `string? reviewerToken = null;` (line ~2008) and assign it here instead of `var`, so the catch block in Task 14 can reach it.

3. `AgentInstance` initializer (line ~2360): add

```csharp
                SessionId      = start.Transcript is { } t ? SessionIds.Canonical(t.AcpSessionId) : null,
                TranscriptPath = start.Transcript is null ? null : journal.IsOpen ? journal.Path : null,
                Journal        = start.Transcript is null ? null : journal,
```

4. `_ = DetectSessionIdAsync(agent, cmd.Vendor, spawnedAtUtc);` → `if (start.Transcript is null) _ = DetectSessionIdAsync(agent, cmd.Vendor, spawnedAtUtc);`
5. In `HandleSendInput`: `var isCodex = agent.Runtime.EmitsTerminalOutput && string.Equals(agent.Runtime.Vendor, "codex", StringComparison.OrdinalIgnoreCase);`
6. `ArmCodexTurnProbe`: first statement `Interlocked.Increment(ref _codexProbesArmed);` with `int _codexProbesArmed;` and the `ForTest` accessor.

`AgentOrchestrator.LocalIpc.cs` — in `SnapshotAgentsForStatus`, after `AwaitingInput: …`:

```csharp
                AwaitingInput: a.Status == "Running" && a.ActivityClock.AwaitingInput,
                TranscriptFormat: a.Runtime is IAcpTranscriptSource ? TranscriptFormats.Envelopes : TranscriptFormats.Vendor))];
```

Remove the `?? (a.Runtime as IAcpTranscriptSource)?.AcpSessionId` fallback from `SessionId:` only if every test still passes with the initializer supplying it; otherwise keep it (harmless, same value for a GUID-less id) — note: keep it wrapped as `a.SessionId ?? SessionIds.Canonical((a.Runtime as IAcpTranscriptSource)?.AcpSessionId)` so a seeded test agent without the initializer still reports the canonical form.

- [ ] **Step 4: Run tests** — the two filters plus `AgentOrchestratorAcpForwardingTests`, `SendInputQuitCommandTests`, `AgentOrchestratorBorrowLaunchTests`; PASS.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntimeFactory.cs src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentStatusSnapshotTests.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorJournalTests.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/CaptureServerConnection.cs
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Publish journal path, format and canonical id at construction (#839)" -m "PublishAgent precedes RegisterAgentAsync, whose server call can stall for an outage, so these fields must be in the initializer to reach the first snapshot." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 14: Orchestrator — journal lifecycle (cleanup, launch failure)

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` — `CleanupAgentAsync` (line ~4909) and the `HandleLaunchAgentCore` catch (line ~2504)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorJournalTests.cs` (append)

- [ ] **Step 1: Write the failing tests**

```csharp
    [Test]
    public async Task Cleanup_completes_the_journal_after_disposing_the_runtime() {
        using var repoPath = GitRepo.CreateWithCommit();
        var server = new CaptureServerConnection();
        var factory = new OpeningAcpFactory();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath, extraRuntimeFactories: [factory]);
        orch.GracefulExitWait = TimeSpan.FromMilliseconds(50);
        await orch.HandleLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch("agent-cleanup", repoPath));
        var journal = factory.LastContext!.Journal!;
        journal.Record(new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: "bye"));

        await orch.HandleLocalStopV2Async(force: true, "agent-cleanup", Stream.Null, CancellationToken.None);
        await WaitUntil(() => journal.Drained);

        await Assert.That(File.Exists(journal.Path)).IsTrue(); // agent exit never deletes a journal
        await Assert.That(File.ReadAllText(journal.Path)).Contains("\"bye\"");
    }

    [Test]
    public async Task Factory_failure_after_open_deletes_only_a_journal_this_launch_created() {
        using var repoPath = GitRepo.CreateWithCommit();
        var server = new CaptureServerConnection();
        var factory = new FailingAfterOpenFactory();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath, extraRuntimeFactories: [factory]);

        await orch.HandleLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch("agent-fail-fresh", repoPath));
        await Assert.That(File.Exists(factory.LastJournal!.Path)).IsFalse();

        // A pre-existing file (a rebind whose factory fails) keeps the prior incarnation's bytes.
        var existing = TranscriptJournal.ForAgent(orch.PidRecordRootForTest, "agent-fail-rebind", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        existing.Open("/w", null); await existing.CompleteAsync();
        await orch.HandleLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch("agent-fail-rebind", repoPath));
        await Assert.That(File.Exists(existing.Path)).IsTrue();
        await Assert.That(File.ReadLines(existing.Path).Count()).IsEqualTo(2); // its header plus the failed launch's header
    }

    sealed class FailingAfterOpenFactory : IHostedAgentRuntimeFactory {
        public string CliPath => "x"; public string Vendor => "cursor"; public bool SupportsUnattended => false;
        public TranscriptJournal? LastJournal { get; private set; }
        public bool IsAvailable() => true;
        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            LastJournal = ctx.Journal;
            ctx.Journal?.Open(ctx.Worktree.Path, ctx.Model);
            throw new InvalidOperationException("boom");
        }
    }

    static async Task WaitUntil(Func<bool> condition) {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        await Assert.That(condition()).IsTrue();
    }
```

- [ ] **Step 2: Run to verify failure** — filter `AgentOrchestratorJournalTests`; the two new tests FAIL (file still exists / never drained).

- [ ] **Step 3: Implement**

`CleanupAgentAsync`, directly after the `Runtime.DisposeAsync()` line:

```csharp
        if (agent.Journal is { } journal) {
            try { await journal.CompleteAsync(); } catch (Exception ex) { LogCleanupStepFailed(ex, "completing transcript journal", agentId); }
        }
```

In the `catch (Exception ex)` of `HandleLaunchAgentCore`, inside the pre-insert branch (after `if (_agents.ContainsKey(agentId)) { … return; }` and before the reviewer-token revoke):

```csharp
            if (journal is { IsOpen: true }) {
                var drained = await journal.CompleteAsync();
                if (journal.CreatedFile && drained) {
                    using var lease = await JournalPathLocks.Shared.AcquireAsync(journal.Path, TranscriptJournal.LockBound, CancellationToken.None);
                    if (lease is not null) { try { File.Delete(journal.Path); } catch (Exception deleteEx) { LogCleanupStepFailed(deleteEx, "deleting transcript journal (failed-launch)", agentId); } }
                }
            }
```

- [ ] **Step 4: Run tests** — filter `AgentOrchestratorJournalTests`; PASS.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorJournalTests.cs
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Complete the journal on cleanup and delete only a failed launch's own file (#839)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 15: ACP runtime records to the journal

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntime.cs` (ctor line ~608; `EmitEnvelope` line ~1799), `src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntimeFactory.cs` (line ~201)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AcpHostedAgentRuntimeTests.cs` (append)

**Interfaces:**
- Produces: `AcpHostedAgentRuntime(..., TranscriptJournal? journal = null)` (last optional parameter). `EmitEnvelope` calls `_journal?.Record(envelope)` inside `lock (_aggregationLock)` right after a successful `TryWrite`.

- [ ] **Step 1: Write the failing tests** (in the existing `Harness`, add a `TranscriptJournal? journal` ctor argument threaded into `new AcpHostedAgentRuntime(Conn, Process, NullLogger.Instance, journal: journal)`, and a `TempDir Tmp` field it owns; then)

```csharp
    static async Task<List<AcpEventEnvelope>> JournalEnvelopes(string path) {
        var list = new List<AcpEventEnvelope>();
        foreach (var line in await File.ReadAllLinesAsync(path)) if (EnvelopeJournalFormat.TryRead(line, out var e)) list.Add(e);
        return list;
    }

    [Test]
    public async Task Journal_receives_every_accepted_envelope_in_channel_order_including_the_initial_user_message() {
        using var tmp = new TempDir();
        var journal = TranscriptJournal.ForAgent(tmp.Path, "agent-1", NullLogger.Instance);
        journal.Open("/abs/worktree", null);
        await using var h = new Harness(journal);
        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "do the thing", h.Cts.Token).WaitAsync(HangGuard);
        h.Fake.EmitAgentText("hello");   // whichever FakeAcpAgent helper pushes a session/update agent_message_chunk
        var fromChannel = new List<AcpEventEnvelope>();
        while (fromChannel.Count < 2) fromChannel.Add(await h.Runtime.Envelopes.ReadAsync(h.Cts.Token).AsTask().WaitAsync(HangGuard));

        await journal.CompleteAsync();
        var fromJournal = (await JournalEnvelopes(journal.Path)).Skip(1).ToList(); // header first

        await Assert.That(fromJournal.Select(e => (e.Kind, e.Text))).IsEquivalentTo(fromChannel.Select(e => (e.Kind, e.Text)));
        await Assert.That(fromJournal[0].Kind).IsEqualTo(AcpEventKind.UserMessage);
    }

    [Test]
    public async Task Envelope_evicted_by_drop_oldest_is_still_in_the_journal_and_one_after_completion_is_not() {
        using var tmp = new TempDir();
        var journal = TranscriptJournal.ForAgent(tmp.Path, "agent-1", NullLogger.Instance);
        journal.Open("/abs/worktree", null);
        await using var h = new Harness(journal, transcriptCapacity: 1);
        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "p", h.Cts.Token).WaitAsync(HangGuard);
        h.Fake.EmitAgentText("a"); h.Fake.EmitAgentText("b"); // capacity 1: "a" (or the user turn) is evicted
        await Task.Delay(100);
        await h.Runtime.DisposeAsync();  // completes the channel
        h.Fake.EmitAgentText("late");
        await Task.Delay(100);
        await journal.CompleteAsync();

        var texts = (await JournalEnvelopes(journal.Path)).Select(e => e.Text).ToList();
        await Assert.That(texts).Contains("a").And.Contains("b");
        await Assert.That(texts).DoesNotContain("late");
    }
```

If `FakeAcpAgent` has no helper that emits an `agent_message_chunk` update on demand, add `EmitAgentText(string text)` to it that writes the corresponding `session/update` notification to the client read stream (copy the JSON shape from an existing test that does this). `Harness` gains optional `transcriptCapacity` forwarded to the runtime's `transcriptCapacity:` parameter.

- [ ] **Step 2: Run to verify failure** — filter `AcpHostedAgentRuntimeTests`; build error.

- [ ] **Step 3: Implement**

Constructor: add `TranscriptJournal? journal = null` as the final parameter and `_journal = journal;` (field `readonly TranscriptJournal? _journal;`).

`EmitEnvelope`:

```csharp
            if (!_transcript.Writer.TryWrite(envelope))
                _logger.LogDebug("ACP: dropped an ACP transcript envelope (Kind={Kind}) — transcript channel already completed.", envelope.Kind);
            else
                _journal?.Record(envelope);
```

Factory (`AcpHostedAgentRuntimeFactory.StartAsync`, before `runtime = new AcpHostedAgentRuntime(`): `ctx.Journal?.Open(ctx.Worktree.Path, ctx.Model);` and pass `journal: ctx.Journal` in the construction.

- [ ] **Step 4: Run tests** — filter `AcpHostedAgentRuntimeTests`, `AcpHostedAgentRuntimeFactoryTests`; PASS.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntime.cs src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntimeFactory.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/AcpHostedAgentRuntimeTests.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/FakeAcpAgent.cs
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Journal ACP envelopes at the emit site (#839)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 16: Pi runtime records to the journal

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Harness/Pi/PiRpcHostedAgentRuntime.cs` (ctor line ~168; `Write` line ~658), `src/Capacitor.Cli.Daemon/Harness/Pi/PiRpcHostedAgentRuntimeFactory.cs` (line ~121)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Pi/PiRpcRuntimeFakes.cs` (`NewRuntime` gains `TranscriptJournal? journal = null`), `test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Pi/PiRpcHostedAgentRuntimeTests.cs` (append)

**Interfaces:**
- Produces: `PiRpcHostedAgentRuntime(..., Action? onDisposed = null, TranscriptJournal? journal = null)`. `Write` takes a `readonly Lock _writeLock` around `TryWrite` + `Record`; the activity advance stays before the lock.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Test]
    public async Task Journal_matches_channel_order_under_concurrent_pump_and_send_time_writers() {
        using var tmp = new TempDir();
        var journal = TranscriptJournal.ForAgent(tmp.Path, "agent-1", NullLogger.Instance);
        journal.Open("/w", null);
        var (runtime, process) = PiRpcRuntimeFakes.NewRuntime(journal: journal);
        await using var _ = runtime;
        await runtime.WaitForSessionReadyForTesting();   // the existing ready seam, or the handshake-observing pattern the file already uses

        var drained = new List<AcpEventEnvelope>();
        var drain = Task.Run(async () => { await foreach (var e in runtime.Envelopes.ReadAllAsync()) drained.Add(e); });
        var pump  = Task.Run(() => { for (var i = 0; i < 200; i++) process.Push(PiRpcRuntimeFakes.AssistantText($"a{i}")); });
        var sends = Task.Run(async () => { for (var i = 0; i < 200; i++) await runtime.SendUserInputAsync($"u{i}"); });
        await Task.WhenAll(pump, sends);
        await Task.Delay(200);
        process.EndOfStream();
        await drain.WaitAsync(TimeSpan.FromSeconds(10));
        await journal.CompleteAsync();

        var journaled = File.ReadAllLines(journal.Path).Skip(1).Select(l => { EnvelopeJournalFormat.TryRead(l, out var e); return (e.Kind, e.Text); });
        await Assert.That(journaled).IsEquivalentTo(drained.Select(e => (e.Kind, e.Text)));
    }

    [Test]
    public async Task Envelope_written_after_channel_completion_is_not_journaled() {
        using var tmp = new TempDir();
        var journal = TranscriptJournal.ForAgent(tmp.Path, "agent-1", NullLogger.Instance);
        journal.Open("/w", null);
        var (runtime, process) = PiRpcRuntimeFakes.NewRuntime(journal: journal);
        process.EndOfStream();
        await runtime.WaitForExitAsync(TimeSpan.FromSeconds(5));
        await runtime.SendUserInputAsync("late"); // the channel is complete: dropped at Debug
        await runtime.DisposeAsync();
        await journal.CompleteAsync();
        await Assert.That(File.ReadAllText(journal.Path)).DoesNotContain("\"late\"");
    }
```

(Use the file's existing way of awaiting the handshake instead of `WaitForSessionReadyForTesting` if that seam has a different name; `SendUserInputAsync` may throw once the process has exited — wrap the late send in a try/catch that swallows `InvalidOperationException`/`IOException`.)

- [ ] **Step 2: Run to verify failure** — filter `PiRpcHostedAgentRuntimeTests`; build error.

- [ ] **Step 3: Implement**

Constructor: `TranscriptJournal? journal = null` after `onDisposed`; `_journal = journal;`. Fields: `readonly TranscriptJournal? _journal; readonly Lock _writeLock = new();`

```csharp
    void Write(AcpEventEnvelope env, bool agentActivity) {
        if (agentActivity) ActivityClock?.Advance();

        lock (_writeLock) {
            if (_transcript.Writer.TryWrite(env)) { _journal?.Record(env); return; }
        }

        _logger.LogDebug("Pi: dropped a transcript envelope — the channel is already completed.");
    }
```

Factory: before `runtime = new PiRpcHostedAgentRuntime(`: `ctx.Journal?.Open(ctx.Worktree.Path, ResolveModel(config, ctx));` and pass `journal: ctx.Journal`.

`PiRpcRuntimeFakes.NewRuntime`: add `TranscriptJournal? journal = null` and pass `journal: journal`.

- [ ] **Step 4: Run tests** — filter `PiRpcHostedAgentRuntimeTests`, `PiHostedLaunchTests`; PASS.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.Cli.Daemon/Harness/Pi test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Pi
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Journal Pi envelopes under a write lock (#839)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 17: Antigravity — clock, synthesized user turn, journal

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Harness/Antigravity/AntigravityHostedAgentRuntime.cs` (ctor line ~323; `RunTurnWorkerAsync` line ~534; `Write` line ~919), `src/Capacitor.Cli.Daemon/Harness/Antigravity/AntigravityHostedAgentRuntimeFactory.cs` (line ~316)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Antigravity/AntigravityRuntimeFakes.cs` (`FakeRuntime` gains `TimeProvider? time = null, TranscriptJournal? journal = null`), `test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Antigravity/AntigravityUserTurnTests.cs` (new)

**Interfaces:**
- Produces: `AntigravityHostedAgentRuntime(..., Action? onDisposed = null, TimeProvider? timeProvider = null, TranscriptJournal? journal = null)`; `string NowIso()`; the worker emits `AcpEventTranslator.BuildUserMessage(0, NowIso(), turn.Text)` via `Write(env, agentActivity: false)` after `_turnGate.WaitAsync` and before `ProcessTurnAsync`. `Write` takes `_writeLock` around `TryWrite` + `Record`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Antigravity;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Antigravity;

public class AntigravityUserTurnTests {
    static readonly FakeTimeProvider Time = new(new DateTimeOffset(2026, 9, 9, 10, 0, 0, TimeSpan.Zero));

    static async Task<List<AcpEventEnvelope>> Drain(AntigravityHostedAgentRuntime rt, int count) {
        var list = new List<AcpEventEnvelope>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (list.Count < count) list.Add(await rt.Envelopes.ReadAsync(cts.Token));
        return list;
    }

    [Test]
    public async Task One_user_message_per_admitted_turn_ordered_before_that_turns_output_with_the_injected_clock() {
        await using var rt = AntigravityRuntimeFakes.FakeRuntime(time: Time);
        await rt.StartAsync("/w", "first", CancellationToken.None);
        await rt.SendUserInputAsync("second");

        var envelopes = await Drain(rt, 4); // user, output…, user, output…
        var users = envelopes.Where(e => e.Kind == AcpEventKind.UserMessage).ToList();
        await Assert.That(users.Select(u => u.Text)).IsEquivalentTo(new[] { "first", "second" });
        await Assert.That(users.All(u => u.TimestampIso == "2026-09-09T10:00:00.0000000+00:00")).IsTrue();
        await Assert.That(envelopes[0].Kind).IsEqualTo(AcpEventKind.UserMessage);
        var secondIndex = envelopes.FindIndex(e => e.Kind == AcpEventKind.UserMessage && e.Text == "second");
        await Assert.That(envelopes.Take(secondIndex).Count(e => e.Kind != AcpEventKind.UserMessage)).IsGreaterThan(0);
    }

    [Test]
    public async Task A_refused_turn_emits_only_the_not_delivered_note() {
        await using var rt = AntigravityRuntimeFakes.FakeRuntime(FakeTurn.NeverEnds, queueCap: 1, time: Time);
        await rt.StartAsync("/w", "first", CancellationToken.None);
        await rt.SendUserInputAsync("queued");
        await Assert.That(async () => await rt.SendUserInputAsync("refused")).Throws<Exception>();
        await Task.Delay(100);
        var seen = new List<AcpEventEnvelope>();
        while (rt.Envelopes.TryRead(out var e)) seen.Add(e);
        await Assert.That(seen.Count(e => e.Kind == AcpEventKind.UserMessage && e.Text == "refused")).IsEqualTo(0);
        await Assert.That(seen.Any(e => e.Kind == AcpEventKind.SystemNote && e.Text!.Contains("not delivered"))).IsTrue();
    }

    [Test]
    public async Task Journal_matches_channel_order_under_worker_and_queue_full_notice_writers() {
        using var tmp = new TempDir();
        var journal = TranscriptJournal.ForAgent(tmp.Path, "agent-1", NullLogger.Instance);
        journal.Open("/w", null);
        await using var rt = AntigravityRuntimeFakes.FakeRuntime(FakeTurn.NeverEnds, queueCap: 1, time: Time, journal: journal);
        await rt.StartAsync("/w", "first", CancellationToken.None);
        await rt.SendUserInputAsync("queued");
        var refusals = Task.Run(async () => { for (var i = 0; i < 50; i++) { try { await rt.SendUserInputAsync($"r{i}"); } catch { } } });
        await refusals;
        await Task.Delay(200);
        var drained = new List<AcpEventEnvelope>();
        while (rt.Envelopes.TryRead(out var e)) drained.Add(e);
        await journal.CompleteAsync();

        var journaled = File.ReadAllLines(journal.Path).Skip(1).Select(l => { EnvelopeJournalFormat.TryRead(l, out var e); return (e.Kind, e.Text); });
        await Assert.That(journaled).IsEquivalentTo(drained.Select(e => (e.Kind, e.Text)));
    }
}
```

The `NeverEnds` fake blocks the worker inside the first turn so later turns queue; the fake `Normal` turn emits `init` and a `result`, which `AntigravityNdjson.ToEnvelopes` maps to at least one envelope — if the first test's `Drain(rt, 4)` needs a different count, read the fake's mapping and adjust (the assertion is on order and the user rows, not the count).

- [ ] **Step 2: Run to verify failure** — filter `AntigravityUserTurnTests`; build error.

- [ ] **Step 3: Implement**

Constructor: add `TimeProvider? timeProvider = null, TranscriptJournal? journal = null` after `onDisposed`; fields `readonly TimeProvider _time; readonly TranscriptJournal? _journal; readonly Lock _writeLock = new();`; `_time = timeProvider ?? TimeProvider.System;`. Add `string NowIso() => _time.GetUtcNow().ToString("o");`.

Worker (`RunTurnWorkerAsync`), after `await _turnGate.WaitAsync(ownerCt)…` and before `ActivityClock?.SetTurnInFlight(true);`:

```csharp
                    Write(AcpEventTranslator.BuildUserMessage(seq: 0, NowIso(), turn.Text), agentActivity: false);
```

(`using Capacitor.Cli.Daemon.Acp;` for the translator.) `Write`:

```csharp
    bool Write(AcpEventEnvelope env, bool agentActivity) {
        if (agentActivity) ActivityClock?.Advance();

        lock (_writeLock) {
            if (_transcript.Writer.TryWrite(env)) { _journal?.Record(env); return true; }
        }

        _logger.LogDebug("Antigravity: dropped a transcript envelope — the transcript channel is already completed.");
        return false;
    }
```

Factory: before `var runtime = new AntigravityHostedAgentRuntime(`: `ctx.Journal?.Open(ctx.Worktree.Path, model);` and pass `journal: ctx.Journal`.

`AntigravityRuntimeFakes.FakeRuntime`: add `TimeProvider? time = null, TranscriptJournal? journal = null` and pass `timeProvider: time, journal: journal`.

- [ ] **Step 4: Run tests** — filters `AntigravityUserTurnTests`, `AntigravityRuntimeLifecycleTests`, `AntigravityActivityClockTests`, `AntigravityReviewerLaunchTests`; PASS. Any lifecycle test that counted envelopes now sees one extra `user_message` per turn: update its expectation and say so in the commit body.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.Cli.Daemon/Harness/Antigravity test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Antigravity
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Emit Antigravity user turns from the worker and journal them (#839)" -m "The single worker is what orders the user turn ahead of the turn's own output; emitting from EnqueueTurn would race the child's first line." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 18: Codex forward buffer — journal and explicit acceptance

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Harness/Codex/CodexForwardBuffer.cs`, `src/Capacitor.Cli.Daemon/Harness/Codex/CodexAppServerHostedAgentRuntime.cs` (ctor line ~151; buffer construction line ~180), `src/Capacitor.Cli.Daemon/Harness/Codex/CodexHostedAgentRuntimeFactory.cs` (line ~126)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Codex/CodexForwardBufferTests.cs` (append; `New` gains `TranscriptJournal? journal = null, CancellationToken shutdown = default`)

**Interfaces:**
- Produces: `CodexForwardBuffer(int capacity, TimeSpan stallTimeout, CancellationToken shutdown, Action<TimeSpan> onStall, TranscriptJournal? journal = null)`; `bool WriteCanonicalBlocking(AcpEventEnvelope env)` (private; true only when the envelope entered the channel; `ChannelClosedException` → Debug log, false). `CodexAppServerHostedAgentRuntime(..., TimeSpan? approvalTimeout = null, TranscriptJournal? journal = null)`.

- [ ] **Step 1: Write the failing tests**

```csharp
    static (TranscriptJournal Journal, TempDir Tmp) OpenJournal() {
        var tmp = new TempDir();
        var journal = TranscriptJournal.ForAgent(tmp.Path, "agent-1", NullLogger.Instance);
        journal.Open("/w", null);
        return (journal, tmp);
    }

    static async Task<List<string?>> JournaledTexts(TranscriptJournal journal) {
        await journal.CompleteAsync();
        return File.ReadAllLines(journal.Path).Skip(1).Select(l => { EnvelopeJournalFormat.TryRead(l, out var e); return e.Text; }).ToList();
    }

    [Test]
    public async Task Canonical_envelopes_are_journaled_and_ephemerals_dropped_from_a_full_buffer_are_not() {
        var (journal, tmp) = OpenJournal(); using var _ = tmp;
        using var buf = New(1, TimeSpan.FromSeconds(5), journal: journal);
        buf.Emit(Canonical("a"));
        buf.Emit(Ephemeral("live"));  // full: dropped
        await Assert.That(buf.DroppedEphemeralCount).IsEqualTo(1);
        await Assert.That(await JournaledTexts(journal)).IsEquivalentTo(new[] { "a" });
    }

    [Test]
    public async Task Blocking_canonical_write_is_journaled_only_after_it_completes() {
        var (journal, tmp) = OpenJournal(); using var _ = tmp;
        using var buf = New(1, TimeSpan.FromSeconds(5), journal: journal);
        buf.Emit(Canonical("a"));
        var blocked = Task.Run(() => buf.Emit(Canonical("b")));
        await Task.Delay(100);
        await Assert.That(blocked.IsCompleted).IsFalse();
        await Assert.That(File.ReadAllText(journal.Path)).DoesNotContain("\"b\"");
        await buf.Reader.ReadAsync(); // frees the slot
        await blocked.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(await JournaledTexts(journal)).IsEquivalentTo(new[] { "a", "b" });
    }

    [Test]
    public async Task Stalled_buffer_journals_nothing_further_and_the_watchdog_fires_once() {
        var (journal, tmp) = OpenJournal(); using var _ = tmp;
        var stalls = 0;
        using var buf = New(1, TimeSpan.FromMilliseconds(100), onStall: _ => stalls++, journal: journal);
        buf.Emit(Canonical("a"));
        buf.Emit(Canonical("stalled")); // blocks 100 ms, then faults
        buf.Emit(Canonical("after"));
        await Assert.That(stalls).IsEqualTo(1);
        await Assert.That(buf.Stalled).IsTrue();
        await Assert.That(await JournaledTexts(journal)).IsEquivalentTo(new[] { "a" });
    }

    [Test]
    public async Task Shutdown_cancelled_wait_and_emit_after_complete_are_not_journaled_and_do_not_throw() {
        var (journal, tmp) = OpenJournal(); using var _ = tmp;
        using var shutdown = new CancellationTokenSource();
        using var buf = New(1, TimeSpan.FromSeconds(30), journal: journal, shutdown: shutdown.Token);
        buf.Emit(Canonical("a"));
        var blocked = Task.Run(() => buf.Emit(Canonical("cancelled")));
        await Task.Delay(50);
        shutdown.Cancel();
        await blocked.WaitAsync(TimeSpan.FromSeconds(5)); // returned, no throw
        buf.Complete();
        buf.Emit(Canonical("after-complete")); // no throw
        await Assert.That(await JournaledTexts(journal)).IsEquivalentTo(new[] { "a" });
    }
```

`Canonical(text)` / `Ephemeral(text)` are the file's existing envelope builders (add `Ephemeral` if absent: `new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: text, Ephemeral: true, ItemId: "i1")`).

- [ ] **Step 2: Run to verify failure** — filter `CodexForwardBufferTests`; build error.

- [ ] **Step 3: Implement**

```csharp
    public CodexForwardBuffer(int capacity, TimeSpan stallTimeout, CancellationToken shutdown, Action<TimeSpan> onStall, TranscriptJournal? journal = null) {
        …existing…
        _journal = journal;
    }

    public void Emit(AcpEventEnvelope env) {
        if (Stalled) return;

        if (env.Ephemeral) {
            if (!_channel.Writer.TryWrite(env)) Interlocked.Increment(ref _droppedEphemeral);
            return;
        }

        if (_channel.Writer.TryWrite(env) || WriteCanonicalBlocking(env)) _journal?.Record(env);
    }

    bool WriteCanonicalBlocking(AcpEventEnvelope env) {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
        stall.CancelAfter(_stallTimeout);
        try {
            _channel.Writer.WriteAsync(env, stall.Token).AsTask().GetAwaiter().GetResult();
            return true;
        } catch (ChannelClosedException) {
            _logger?.LogDebug("Codex: dropped a transcript envelope (Kind={Kind}) — forward buffer already completed.", env.Kind);
            return false;
        } catch (OperationCanceledException) {
            if (_shutdown.IsCancellationRequested) return false;
            if (Interlocked.Exchange(ref _stalled, 1) == 0) _onStall(_stallTimeout);
            return false;
        }
    }
```

The buffer has no logger today: add an optional `ILogger? logger = null` parameter after `journal` (the runtime passes its own) or drop the Debug line and keep the silent `return false`. Keep the constructor's existing four parameters in place.

`CodexAppServerHostedAgentRuntime`: add `TranscriptJournal? journal = null` as the last constructor parameter and pass it to `new CodexForwardBuffer(ForwardBufferCapacity, …, _cts.Token, OnForwardStall, journal)`.

Factory (`CodexHostedAgentRuntimeFactory`, before `var runtime = new CodexAppServerHostedAgentRuntime(`): `ctx.Journal?.Open(ctx.Worktree.Path, ctx.Model);` and pass `journal: ctx.Journal`.

`CodexForwardBufferTests.New`: `static CodexForwardBuffer New(int capacity, TimeSpan stall, Action<TimeSpan>? onStall = null, TranscriptJournal? journal = null, CancellationToken shutdown = default) => new(capacity, stall, shutdown, onStall ?? (_ => { }), journal);`

- [ ] **Step 4: Run tests** — filters `CodexForwardBufferTests`, `CodexAppServerHostedAgentRuntimeTests`, `CodexHostedAgentRuntimeFactoryTests`; PASS.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.Cli.Daemon/Harness/Codex test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Codex
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Journal Codex envelopes only once the forward buffer accepts them (#839)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Part C — Daemon: local input

### Task 19: `InputNotAdmittedException` — typed refusal from the runtimes

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/InputNotAdmittedException.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntime.cs` (doc contract on `SendUserInputAsync` / `SendUserInputAndWaitForWriteAsync`), `src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntime.cs` (`EnqueueTurn` line ~1167), `src/Capacitor.Cli.Daemon/Harness/Antigravity/AntigravityHostedAgentRuntime.cs` (`EnqueueTurn` line ~464), `src/Capacitor.Cli.Daemon/Harness/Codex/CodexTurnInputDispatcher.cs` (`EnqueueAsync` line ~80), `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (`SendInputDropReason.QueueFull`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/InputAdmissionTests.cs` (new), `test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Codex/CodexTurnInputDispatcherTests.cs` (append)

**Interfaces:**
- Produces: `internal sealed class InputNotAdmittedException(string message) : InvalidOperationException(message);` `SendInputDropReason.QueueFull = "queue_full"`. Contract: a runtime that will not queue or write the text fails with `InputNotAdmittedException` — thrown synchronously from `SendUserInputAsync`, carried by the task from `SendUserInputAndWaitForWriteAsync`.

- [ ] **Step 1: Write the failing tests**

`InputAdmissionTests.cs`:

```csharp
using Capacitor.Cli.Daemon.Harness.Antigravity;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Harness.Antigravity;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class InputAdmissionTests {
    [Test]
    public async Task Antigravity_full_queue_refuses_with_input_not_admitted_on_both_send_paths() {
        await using var rt = AntigravityRuntimeFakes.FakeRuntime(FakeTurn.NeverEnds, queueCap: 1);
        await rt.StartAsync("/w", "first", CancellationToken.None);
        await rt.SendUserInputAsync("queued");

        await Assert.That(() => rt.SendUserInputAsync("refused")).Throws<InputNotAdmittedException>();
        await Assert.That(async () => await rt.SendUserInputAndWaitForWriteAsync("refused-ack")).Throws<InputNotAdmittedException>();
    }

    [Test]
    public async Task Antigravity_terminal_runtime_refuses_the_acknowledging_path_with_input_not_admitted() {
        await using var rt = AntigravityRuntimeFakes.FakeRuntime();
        await rt.StartAsync("/w", "first", CancellationToken.None);
        await rt.TerminateAsync(TimeSpan.FromSeconds(1));
        await Assert.That(async () => await rt.SendUserInputAndWaitForWriteAsync("late")).Throws<InputNotAdmittedException>();
    }
}
```

Append to `AcpHostedAgentRuntimeTests` (its `Harness` gains an optional `pendingTurnsCapacity` forwarded to the runtime):

```csharp
    [Test]
    public async Task Full_pending_turns_queue_refuses_with_input_not_admitted_on_both_send_paths() {
        await using var h = new Harness(pendingTurnsCapacity: 1);
        h.StartFakeAgentLoop();
        h.Fake.HoldPromptResponses = true;   // the worker stays inside the first turn
        await h.Runtime.StartAsync("/abs/worktree", "first", h.Cts.Token).WaitAsync(HangGuard);
        await h.Runtime.SendUserInputAsync("queued");

        await Assert.That(() => h.Runtime.SendUserInputAsync("refused")).Throws<InputNotAdmittedException>();
        await Assert.That(async () => await h.Runtime.SendUserInputAndWaitForWriteAsync("refused-ack")).Throws<InputNotAdmittedException>();
    }
```

(If `FakeAcpAgent` has no way to hold a `session/prompt` response open, add a `bool HoldPromptResponses` that makes it skip replying to `session/prompt`; look at how the fake answers that method today.)

Append to `CodexTurnInputDispatcherTests`:

```csharp
    [Test]
    public async Task Enqueue_after_fault_all_is_refused_never_left_pending() {
        var sink = new FakeTurnSink();
        var d = new CodexTurnInputDispatcher(sink.Start, sink.Steer, NullLogger.Instance, CancellationToken.None);
        d.FaultAll(new ObjectDisposedException("runtime"));

        await Assert.That(() => d.EnqueueAsync("late")).Throws<InputNotAdmittedException>();
    }

    [Test]
    public async Task Enqueue_racing_fault_all_is_either_faulted_or_refused() {
        for (var round = 0; round < 50; round++) {
            var sink = new FakeTurnSink();
            var d = new CodexTurnInputDispatcher(sink.Start, sink.Steer, NullLogger.Instance, CancellationToken.None, sealedAtStart: true);
            var fault = Task.Run(() => d.FaultAll(new ObjectDisposedException("runtime")));
            Task ack;
            try { ack = d.EnqueueAsync("racing"); } catch (InputNotAdmittedException) { await fault; continue; }
            await fault;
            await Assert.That(async () => await ack.WaitAsync(TimeSpan.FromSeconds(2))).Throws<Exception>(); // faulted, not pending
        }
    }
```

- [ ] **Step 2: Run to verify failure** — filters `InputAdmissionTests`, `AcpHostedAgentRuntimeTests`, `CodexTurnInputDispatcherTests`; build errors.

- [ ] **Step 3: Implement**

`InputNotAdmittedException.cs`:

```csharp
namespace Capacitor.Cli.Daemon.Services;

/// A runtime declined to queue or write the text: its pending-turn queue is full or it is terminal.
/// Both callers of the delivery core map this to the `queue_full` reason.
internal sealed class InputNotAdmittedException(string message) : InvalidOperationException(message);
```

`IHostedAgentRuntime.cs` — extend the two doc comments: "Fails with <see cref="InputNotAdmittedException"/> when the runtime will not queue or write the text (a full pending-turn queue, a terminal runtime): thrown synchronously by <see cref="SendUserInputAsync"/>, carried by the task from <see cref="SendUserInputAndWaitForWriteAsync"/>."

`AcpHostedAgentRuntime.EnqueueTurn`:

```csharp
        if (_pendingTurns.Reader.Count >= _pendingTurnsCapacity) {
            var dropped = Interlocked.Increment(ref _droppedPendingTurns);
            _logger.LogWarning(…unchanged…);
            var full = new InputNotAdmittedException("ACP pending-turns queue is full.");
            if (written is null) throw full;
            written.TrySetException(full);
            return written.Task;
        }

        if (!_pendingTurns.Writer.TryWrite(new PendingTurn(text, written))) {
            _logger.LogDebug("ACP: dropped a prompt turn — pending-turns channel already completed.");
            var closed = new InputNotAdmittedException("ACP runtime is terminal; this input was not queued.");
            if (written is null) throw closed;
            written.TrySetException(closed);
        }
        return written?.Task ?? Task.CompletedTask;
```

`AntigravityHostedAgentRuntime.EnqueueTurn`: the terminal branch's `InvalidOperationException` and the queue-full `failure` both become `InputNotAdmittedException`; the non-acknowledging return `Task.FromException(failure)` becomes `throw failure` (synchronous, both branches), the acknowledging path keeps `written.TrySetException(...)`. The "message not delivered" note emission stays before the throw.

`CodexTurnInputDispatcher.EnqueueAsync`:

```csharp
    public Task EnqueueAsync(string text, CancellationToken ct = default) {
        var item = new InputItem(text, ct);
        lock (_gate) {
            if (_faulted) throw new InputNotAdmittedException("Codex dispatcher is torn down; this input was not queued.");
            _queue.Enqueue(item);
        }
        PumpDispatch();
        return item.Ack.Task;
    }
```

`AgentOrchestrator.SendInputDropReason`: add `public const string QueueFull = "queue_full";`.

- [ ] **Step 4: Run tests** — the three filters plus `AntigravityRuntimeLifecycleTests`; PASS (an existing lifecycle test asserting `InvalidOperationException` still passes: the new type derives from it).

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.Cli.Daemon/Services/InputNotAdmittedException.cs src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntime.cs src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntime.cs src/Capacitor.Cli.Daemon/Harness/Antigravity/AntigravityHostedAgentRuntime.cs src/Capacitor.Cli.Daemon/Harness/Codex/CodexTurnInputDispatcher.cs src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs test/Capacitor.Cli.Daemon.Tests.Unit
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Refuse unadmitted input with a typed exception (#839)" -m "The Codex dispatcher refuses under the lock FaultAll takes, so an enqueue can no longer slip in after the fault and stay pending forever." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 20: `DeliverInputAsync` — one delivery core for both callers

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/InputDelivery.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (`HandleSendInput` line ~3674 → thin wrapper; new `DeliverInputAsync`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/SendInputQuitCommandTests.cs` (must keep passing), `test/Capacitor.Cli.Daemon.Tests.Unit/Services/InputDeliveryCoreTests.cs` (new)

**Interfaces:**
- Produces:

```csharp
internal enum InputDeliveryKind { Delivered, QuitRequested, Dropped }
/// Reason is a SendInputDropReason token when Kind is Dropped; Error is the runtime's message for a delivery fault.
internal readonly record struct InputDeliveryOutcome(InputDeliveryKind Kind, string? Reason = null, string? Error = null) {
    public static readonly InputDeliveryOutcome Delivered = new(InputDeliveryKind.Delivered);
    public static readonly InputDeliveryOutcome QuitRequested = new(InputDeliveryKind.QuitRequested);
    public static InputDeliveryOutcome Drop(string reason, string? error = null) => new(InputDeliveryKind.Dropped, reason, error);
}
internal Task<InputDeliveryOutcome> AgentOrchestrator.DeliverInputAsync(AgentInstance agent, string text, string[]? attachmentIds);
```

`DeliverInputAsync` is `HandleSendInput`'s body from the quit check onward, with these changes only: a quit on a non-PTY runtime returns `QuitRequested` instead of stopping; a failed borrowed refresh returns `Drop(SendInputDropReason.ReaperClaimed)`-style? No — it returns `Drop("delivery_failed", "borrowed snapshot refresh failed")` (today that path silently returns; keep the log line); `InputNotAdmittedException` → `Drop(SendInputDropReason.QueueFull, ex.Message)`; any other exception from the runtime → `Drop(SendTextReasons.DeliveryFailed, ex.Message)` (the constant is in Core's `SendTextReasons`; add `SendInputDropReason.DeliveryFailed = "delivery_failed"` beside `QueueFull` and use that). Reaper tokens stay. Gate, `SendUserInputAndWaitForWriteAsync` for a borrowed round, activity clock and awaiting-input clearing on completion only, the status report, the Codex probe (already PTY-gated) — all unchanged. **No `ReportInputDroppedAsync` inside the core**: the caller reports.

`HandleSendInput` becomes: unknown agent / private agent checks (unchanged, they report) → `LogSendInputReceived` → `var outcome = await DeliverInputAsync(agent, text, attachmentIds);` → `QuitRequested` → `LogSendInputQuitCommand` + `await HandleUnsequencedStopAgent(agentId)`; `Dropped` → `await ReportInputDroppedAsync(cmd, outcome.Reason!)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Harness.Antigravity;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class InputDeliveryCoreTests {
    static AgentOrchestrator Build(CaptureServerConnection server) =>
        AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

    [Test]
    public async Task Full_queue_on_the_real_antigravity_runtime_maps_to_queue_full_and_the_server_caller_reports_it() {
        var server = new CaptureServerConnection();
        await using var orch = Build(server);
        await using var rt = AntigravityRuntimeFakes.FakeRuntime(FakeTurn.NeverEnds, queueCap: 1);
        await rt.StartAsync("/w", "first", CancellationToken.None);
        await rt.SendUserInputAsync("queued");
        var clock = new AgentActivityClock(new FakeTimeProvider());
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "agy-full", rt, activityClock: clock);
        clock.MarkAwaitingInput();   // whichever member sets AwaitingInput true today
        var before = clock.ActivitySeq;

        var outcome = await orch.DeliverInputAsync(agent, "refused", null);
        await Assert.That(outcome.Kind).IsEqualTo(InputDeliveryKind.Dropped);
        await Assert.That(outcome.Reason).IsEqualTo(SendInputDropReason.QueueFull);
        await Assert.That(clock.ActivitySeq).IsEqualTo(before);       // no advance on a refusal
        await Assert.That(clock.AwaitingInput).IsTrue();                // the needs-you pip survives

        var dispatch = Guid.NewGuid();
        await orch.HandleSendInputForTest(new SendInputCommand(agent.Id, "refused-again", null, dispatch));
        await Assert.That(server.InputRejections).Contains((dispatch, agent.Id, SendInputDropReason.QueueFull));
    }

    [Test]
    public async Task Borrowed_round_on_a_full_queue_uses_the_wait_for_write_path_and_still_maps_to_queue_full() {
        var server = new CaptureServerConnection();
        await using var orch = Build(server);
        await using var rt = AntigravityRuntimeFakes.FakeRuntime(FakeTurn.NeverEnds, queueCap: 1);
        await rt.StartAsync("/w", "first", CancellationToken.None);
        await rt.SendUserInputAsync("queued");
        var agent = AgentOrchestratorHarness.SeedBorrowedAcpAgent(orch, "agy-borrowed", rt);   // a seeded agent with BorrowedSnapshotSource set and Work = BorrowedCwd (no refresh)

        var outcome = await orch.DeliverInputAsync(agent, "refused", null);
        await Assert.That(outcome.Reason).IsEqualTo(SendInputDropReason.QueueFull);
    }

    [Test]
    public async Task Quit_on_a_non_pty_runtime_is_reported_not_delivered_and_the_server_caller_stops_the_agent() {
        var server = new CaptureServerConnection();
        await using var orch = Build(server);
        orch.GracefulExitWait = TimeSpan.FromMilliseconds(50);
        var rt = new FakeAcpRuntime();
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "acp-quit", rt);

        var outcome = await orch.DeliverInputAsync(agent, "/quit", null);
        await Assert.That(outcome.Kind).IsEqualTo(InputDeliveryKind.QuitRequested);
        await Assert.That(rt.HasExited).IsFalse(); // the core never stops

        await orch.HandleSendInputForTest(new SendInputCommand(agent.Id, "/exit", null));
        for (var i = 0; i < 300 && !rt.HasExited; i++) await Task.Delay(10);
        await Assert.That(rt.HasExited).IsTrue();
    }

    [Test]
    public async Task Runtime_fault_maps_to_delivery_failed_with_the_message() {
        var server = new CaptureServerConnection();
        await using var orch = Build(server);
        var rt = new FakeAcpRuntime { SendUserInputThrow = new IOException("pipe closed") };
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "acp-fault", rt);

        var outcome = await orch.DeliverInputAsync(agent, "hi", null);
        await Assert.That(outcome.Kind).IsEqualTo(InputDeliveryKind.Dropped);
        await Assert.That(outcome.Reason).IsEqualTo(SendInputDropReason.DeliveryFailed);
        await Assert.That(outcome.Error).IsEqualTo("pipe closed");
    }

    [Test]
    public async Task Delivery_completes_only_when_the_runtime_task_settles() {
        var server = new CaptureServerConnection();
        await using var orch = Build(server);
        var rt = new FakeAcpRuntime { SendUserInputGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "acp-slow", rt);

        var pending = orch.DeliverInputAsync(agent, "hi", null);
        await Task.Delay(200);
        await Assert.That(pending.IsCompleted).IsFalse();
        rt.SendUserInputGate.SetResult();
        await Assert.That((await pending).Kind).IsEqualTo(InputDeliveryKind.Delivered);
    }
}
```

`FakeAcpRuntime` gains `public Exception? SendUserInputThrow { get; init; }` and `public TaskCompletionSource? SendUserInputGate { get; init; }`, honoured by `SendUserInputAsync`/`SendUserInputAndWaitForWriteAsync` (throw / await the gate before recording the input). `SeedBorrowedAcpAgent` is a new harness helper identical to `SeedAcpAgent` but with `BorrowedSnapshotSource = <a non-null source the harness can construct>` and `Work = WorkLocation.BorrowedCwd` (check `AgentInstance.BorrowedSnapshotSource`'s type and use its simplest constructor or an existing fake). If `AgentActivityClock` has no direct setter for the awaiting flag, drive it through the member the turn-end path uses (grep `AwaitingInput` in `AgentActivityClock.cs`).

- [ ] **Step 2: Run to verify failure** — filter `InputDeliveryCoreTests`; build error.

- [ ] **Step 3: Implement** `InputDelivery.cs` (the enum and the record struct, one file named for the record: `InputDeliveryOutcome.cs` with the enum beside it is acceptable under the "enum plus its owner" exception; otherwise two files) and the `AgentOrchestrator` refactor described above. Add `SendInputDropReason.DeliveryFailed = "delivery_failed"`.

- [ ] **Step 4: Run tests** — filters `InputDeliveryCoreTests`, `SendInputQuitCommandTests`, `AgentOrchestratorBorrowLaunchTests`, and any test class whose name contains `SendInput`; PASS.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.Cli.Daemon/Services test/Capacitor.Cli.Daemon.Tests.Unit/Services
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Extract the input delivery core from the server-origin handler (#839)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 21: `HandleLocalSendTextAsync`, routing and `input/1`

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs` (new handler), `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs` (`case FrameType.SendText`; the `default:` error text lists `SendText`), `src/Capacitor.Cli.Daemon/Services/LocalControlCapabilities.cs` (`"input/1"`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalControlHelloTests.cs` (three pins gain `"input/1"`), `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalControlCapabilitiesTests.cs` (create if absent: pins the list equals `["consent/1","consent/2","consent/3","status/1","permission/1","input/1"]`), `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalSendTextTests.cs` (new)

**Interfaces:**
- Produces: `public async Task HandleLocalSendTextAsync(string payload, Stream stream, CancellationToken ct)` on `AgentOrchestrator` (partial, LocalIpc file). Every outcome is one `SendTextAck` frame; never an `Error` frame, never an escaped exception. Check order per the spec table: malformed → text_empty → too_large → no_such_agent → protected_kind → not_running → (core) reaper_claimed / reaper_claimed_late / queue_full / delivery_failed → quit: `StopAgentCoreAsync(agent)` → `Ok, Outcome="stopped"` or `stop_failed`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Harness.Antigravity;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class LocalSendTextTests {
    static AgentOrchestrator Build() =>
        AgentOrchestratorHarness.BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

    static async Task<SendTextAckDto> Send(AgentOrchestrator orch, string payload) {
        using var ms = new MemoryStream();
        await orch.HandleLocalSendTextAsync(payload, ms, CancellationToken.None);
        ms.Position = 0;
        var frame = await FrameCodec.ReadAsync(ms, CancellationToken.None);
        await Assert.That(frame!.Type).IsEqualTo(FrameType.SendTextAck);
        return JsonSerializer.Deserialize(frame.Text, InputIpcJsonContext.Default.SendTextAckDto)!;
    }

    static string Payload(string agentId, string text) =>
        JsonSerializer.Serialize(new SendTextDto(agentId, text), InputIpcJsonContext.Default.SendTextDto);

    [Test]
    [Arguments("not json")]
    [Arguments("{}")]
    [Arguments("""{"agent_id":"a1"}""")]
    [Arguments("""{"agent_id":null,"text":"x"}""")]
    [Arguments("""{"agent_id":"a1","text":null}""")]
    [Arguments("[]")]
    public async Task Malformed_payloads_ack_malformed(string payload) {
        await using var orch = Build();
        var ack = await Send(orch, payload);
        await Assert.That(ack.Ok).IsFalse();
        await Assert.That(ack.Reason).IsEqualTo(SendTextReasons.Malformed);
    }

    [Test]
    public async Task Empty_and_oversized_text_and_unknown_agent_are_named() {
        await using var orch = Build();
        await Assert.That((await Send(orch, Payload("a1", "   "))).Reason).IsEqualTo(SendTextReasons.TextEmpty);
        await Assert.That((await Send(orch, Payload("a1", new string('x', InputWire.MaxTextBytes + 1)))).Reason).IsEqualTo(SendTextReasons.TooLarge);
        await Assert.That((await Send(orch, Payload("nope", "hi"))).Reason).IsEqualTo(SendTextReasons.NoSuchAgent);
    }

    [Test]
    public async Task Protected_kind_and_not_running_are_refused_before_the_core() {
        await using var orch = Build();
        AgentOrchestratorHarness.SeedAcpAgent(orch, "rev", new FakeAcpRuntime(), kind: LaunchKind.Review);
        var ack = await Send(orch, Payload("rev", "hi"));
        await Assert.That(ack.Reason).IsEqualTo(SendTextReasons.ProtectedKind);
        await Assert.That(ack.Error).Contains("review");

        var starting = AgentOrchestratorHarness.SeedAcpAgent(orch, "starting", new FakeAcpRuntime(), status: "Starting");
        await Assert.That((await Send(orch, Payload("starting", "hi"))).Reason).IsEqualTo(SendTextReasons.NotRunning);
        var done = AgentOrchestratorHarness.SeedAcpAgent(orch, "done", new FakeAcpRuntime(), status: "Completed");
        await Assert.That((await Send(orch, Payload("done", "hi"))).Reason).IsEqualTo(SendTextReasons.NotRunning);
    }

    [Test]
    public async Task Delivery_acks_ok_delivered_only_when_the_core_settles() {
        await using var orch = Build();
        var rt = new FakeAcpRuntime { SendUserInputGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        AgentOrchestratorHarness.SeedAcpAgent(orch, "a1", rt);

        var pending = Send(orch, Payload("a1", "hello"));
        await Task.Delay(200);
        await Assert.That(pending.IsCompleted).IsFalse();
        rt.SendUserInputGate.SetResult();
        var ack = await pending;
        await Assert.That(ack).IsEqualTo(new SendTextAckDto(true, null, null, SendTextOutcomes.Delivered));
        await Assert.That(rt.SentInputs).IsEquivalentTo(new[] { "hello" });
    }

    [Test]
    public async Task Queue_full_reaper_claim_and_delivery_failure_are_coded() {
        await using var orch = Build();
        await using var agy = AntigravityRuntimeFakes.FakeRuntime(FakeTurn.NeverEnds, queueCap: 1);
        await agy.StartAsync("/w", "first", CancellationToken.None);
        await agy.SendUserInputAsync("queued");
        AgentOrchestratorHarness.SeedAcpAgent(orch, "full", agy);
        await Assert.That((await Send(orch, Payload("full", "x"))).Reason).IsEqualTo(SendTextReasons.QueueFull);

        var claimed = AgentOrchestratorHarness.SeedAcpAgent(orch, "claimed", new FakeAcpRuntime());
        orch.ClaimReapForTest(claimed);
        await Assert.That((await Send(orch, Payload("claimed", "x"))).Reason).IsEqualTo(SendTextReasons.ReaperClaimed);

        var late = AgentOrchestratorHarness.SeedAcpAgent(orch, "late", new FakeAcpRuntime());
        orch.SendInputBeforeWriteHookForTest = () => { orch.ClaimReapForTest(late); return Task.CompletedTask; };
        await Assert.That((await Send(orch, Payload("late", "x"))).Reason).IsEqualTo(SendTextReasons.ReaperClaimedLate);
        orch.SendInputBeforeWriteHookForTest = null;

        AgentOrchestratorHarness.SeedAcpAgent(orch, "faulty", new FakeAcpRuntime { SendUserInputThrow = new IOException("pipe closed") });
        var ack = await Send(orch, Payload("faulty", "x"));
        await Assert.That(ack.Reason).IsEqualTo(SendTextReasons.DeliveryFailed);
        await Assert.That(ack.Error).IsEqualTo("pipe closed");
    }

    [Test]
    public async Task Quit_stops_a_private_and_a_public_agent_through_the_owner_stop_and_acks_stopped() {
        await using var orch = Build();
        orch.GracefulExitWait = TimeSpan.FromMilliseconds(50);
        var pub = new FakeAcpRuntime();
        AgentOrchestratorHarness.SeedAcpAgent(orch, "pub", pub);
        var prv = new FakeAcpRuntime();
        var privateAgent = AgentOrchestratorHarness.SeedAcpAgent(orch, "prv", prv);
        orch.MarkPrivateForTest(privateAgent);

        await Assert.That((await Send(orch, Payload("pub", "/quit"))).Outcome).IsEqualTo(SendTextOutcomes.Stopped);
        await Assert.That(pub.HasExited).IsTrue();
        await Assert.That((await Send(orch, Payload("prv", "/exit"))).Outcome).IsEqualTo(SendTextOutcomes.Stopped);
        await Assert.That(prv.HasExited).IsTrue();

        var stuck = new FakeAcpRuntime { NeverExits = true };
        AgentOrchestratorHarness.SeedAcpAgent(orch, "stuck", stuck);
        await Assert.That((await Send(orch, Payload("stuck", "/quit"))).Reason).IsEqualTo(SendTextReasons.StopFailed);
    }

    [Test]
    public async Task Two_frames_against_a_pending_write_deliver_two_turns_in_order() {
        await using var orch = Build();
        var rt = new FakeAcpRuntime { SendUserInputGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        AgentOrchestratorHarness.SeedAcpAgent(orch, "a1", rt);
        var first = Send(orch, Payload("a1", "one"));
        var second = Send(orch, Payload("a1", "two"));
        await Task.Delay(100);
        rt.SendUserInputGate.SetResult();
        await Task.WhenAll(first, second);
        await Assert.That(rt.SentInputs).IsEquivalentTo(new[] { "one", "two" });
    }

    [Test]
    public async Task A_cancelled_wait_and_a_failing_ack_write_each_still_deliver_exactly_once() {
        await using var orch = Build();
        var rt = new FakeAcpRuntime { SendUserInputGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        AgentOrchestratorHarness.SeedAcpAgent(orch, "a1", rt);

        using var cts = new CancellationTokenSource();
        var cancelled = orch.HandleLocalSendTextAsync(Payload("a1", "one"), new MemoryStream(), cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        rt.SendUserInputGate.SetResult();
        try { await cancelled; } catch (OperationCanceledException) { }

        var rt2 = new FakeAcpRuntime();
        AgentOrchestratorHarness.SeedAcpAgent(orch, "a2", rt2);
        await orch.HandleLocalSendTextAsync(Payload("a2", "two"), new ThrowingStream(), CancellationToken.None); // ack write faults, no throw out

        await Assert.That(rt.SentInputs).IsEquivalentTo(new[] { "one" });
        await Assert.That(rt2.SentInputs).IsEquivalentTo(new[] { "two" });
    }

    sealed class ThrowingStream : Stream {
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => 0; public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("socket closed");
    }
}
```

Test seams to add: `SeedAcpAgent` gains an optional `LaunchKind kind = LaunchKind.Default` passed into the `AgentInstance` initializer (`Kind` is `init`-only); on the orchestrator (all `internal … ForTest`): `ClaimReapForTest(agent)` sets the agent's reap-claim latch the way `TryClaimReapAsync` does (grep `IsReapClaimed` for the field); `MarkPrivateForTest(agent)` sets whatever backs `IsPrivate`. `FakeAcpRuntime` gains `List<string> SentInputs` (recorded on both send paths after the gate) and `bool NeverExits` (TerminateAsync does not flip `HasExited`).

Socket-level test — append to `LocalControlHelloTests`:

```csharp
    [Test]
    public async Task SendText_frame_is_routed_and_answered_with_an_ack() {
        await RunAsync("hello-sendtext", async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            var json = JsonSerializer.Serialize(new SendTextDto("nope", "hi"), InputIpcJsonContext.Default.SendTextDto);
            await FrameCodec.WriteAsync(s, LocalFrame.InputJson(FrameType.SendText, json), ct);
            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp!.Type).IsEqualTo(FrameType.SendTextAck);
            var ack = JsonSerializer.Deserialize(resp.Text, InputIpcJsonContext.Default.SendTextAckDto)!;
            await Assert.That(ack.Reason).IsEqualTo(SendTextReasons.NoSuchAgent);
        });
    }
```

and change the three capability pins to `new[] { "consent/1", "consent/2", "consent/3", "status/1", "permission/1", "input/1" }`.

- [ ] **Step 2: Run to verify failure** — filters `LocalSendTextTests`, `LocalControlHelloTests`; build errors.

- [ ] **Step 3: Implement**

`AgentOrchestrator.LocalIpc.cs`:

```csharp
    /// Composer input from the owner's socket. Every outcome is an ack the composer can word, never an
    /// Error frame; the ack is written only once the delivery core has settled.
    public async Task HandleLocalSendTextAsync(string payload, Stream stream, CancellationToken ct) {
        var ack = await AnswerSendTextAsync(payload);
        try {
            var json = JsonSerializer.Serialize(ack, InputIpcJsonContext.Default.SendTextAckDto);
            await FrameCodec.WriteAsync(stream, LocalFrame.InputJson(FrameType.SendTextAck, json), ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            _logger.LogDebug(ex, "SendText: the ack could not be written; the delivery already settled ({Reason})", ack.Reason ?? ack.Outcome);
        }
    }

    async Task<SendTextAckDto> AnswerSendTextAsync(string payload) {
        SendTextDto? dto;
        try { dto = JsonSerializer.Deserialize(payload, InputIpcJsonContext.Default.SendTextDto); }
        catch (JsonException) { dto = null; }
        if (!InputWire.IsStructurallyValid(dto)) return Refuse(SendTextReasons.Malformed, "expected {\"agent_id\",\"text\"}");
        if (string.IsNullOrWhiteSpace(dto!.Text)) return Refuse(SendTextReasons.TextEmpty, "text is empty");
        if (Encoding.UTF8.GetByteCount(dto.Text) > InputWire.MaxTextBytes) return Refuse(SendTextReasons.TooLarge, $"text exceeds {InputWire.MaxTextBytes} bytes");
        if (!_agents.TryGetValue(dto.AgentId, out var agent)) return Refuse(SendTextReasons.NoSuchAgent, $"no agent {dto.AgentId}");
        if (agent.Kind != LaunchKind.Default) return Refuse(SendTextReasons.ProtectedKind, ProtectionReason(agent));
        if (agent.Status is "Starting" or "Completed" or "Failed") return Refuse(SendTextReasons.NotRunning, $"agent is {agent.Status}");

        InputDeliveryOutcome outcome;
        try { outcome = await DeliverInputAsync(agent, dto.Text, null); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Refuse(SendTextReasons.DeliveryFailed, ex.Message); }

        switch (outcome.Kind) {
            case InputDeliveryKind.Delivered:
                return new SendTextAckDto(true, null, null, SendTextOutcomes.Delivered);
            case InputDeliveryKind.QuitRequested:
                LogSendInputQuitCommand(agent.Id, agent.Runtime.Vendor);
                return await StopAgentCoreAsync(agent)
                    ? new SendTextAckDto(true, null, null, SendTextOutcomes.Stopped)
                    : Refuse(SendTextReasons.StopFailed, "the agent did not stop");
            default:
                return Refuse(outcome.Reason!, outcome.Error);
        }
    }

    static SendTextAckDto Refuse(string reason, string? error) => new(false, reason, error, null);
```

The drop reasons the core returns are the `SendInputDropReason` tokens; they are spelled identically to the `SendTextReasons` tokens (`reaper_claimed`, `reaper_claimed_late`, `queue_full`, `delivery_failed`), so they pass through unchanged. Add `using System.Text;` and `using System.Text.Json;` if absent.

`LocalControlServer.cs`: `case FrameType.SendText: await orchestrator.HandleLocalSendTextAsync(first.Text, stream, ct); break;` and append `/SendText` to the `default:` message. `LocalControlCapabilities.Current`: append `"input/1"`; add one sentence to its doc: `"input/1"` routes `SendText` to `AgentOrchestrator.HandleLocalSendTextAsync`.

- [ ] **Step 4: Run tests** — filters `LocalSendTextTests`, `LocalControlHelloTests`, `LocalControlCapabilitiesTests`; PASS. Then the whole daemon suite: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj`; green. AOT publish check prints nothing.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.Cli.Daemon test/Capacitor.Cli.Daemon.Tests.Unit
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Route SendText into the delivery core and advertise input/1 (#839)" -m "The capability is added in the same change as the routing case: nothing is advertised without a live handler." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Part D — App: workspace and composer

### Task 22: `AgentPresence` in its own file; `SendAvailability.Unsupported`

**Files:**
- Create: `src/Capacitor.App/ViewModels/AgentPresence.cs`
- Modify: `src/Capacitor.App/ViewModels/WorkspaceViewModel.cs` (delete the nested record), `src/Capacitor.App/Services/TerminalAttach.cs` (enum)
- Test: none new (a pure move plus one enum member); build both projects.

- [ ] **Step 1: Move the record**

`AgentPresence.cs`:

```csharp
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.ViewModels;

/// The workspace's accumulated view of one agent: its latest dto and whether the session has ended,
/// which stays true once set even if a later dto arrives.
internal sealed record AgentPresence(AgentStatusDto? Dto, bool SessionEnded);
```

Delete `sealed record AgentPresence(...)` from `WorkspaceViewModel`.

- [ ] **Step 2: Add the enum member**

`TerminalAttach.cs`: `public enum SendAvailability { Ready, Sending, Transitioning, ReadOnly, Connecting, Reattach, Ended, NoTerminal, Unsupported }`.

- [ ] **Step 3: Build and run the App suites that touch these** — `~/.dotnet/dotnet build src/Capacitor.App/Capacitor.App.csproj`; run filters `WorkspaceViewModelTests`, `TerminalTabViewModelTests`; PASS.

- [ ] **Step 4: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.App/ViewModels/AgentPresence.cs src/Capacitor.App/ViewModels/WorkspaceViewModel.cs src/Capacitor.App/Services/TerminalAttach.cs
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Lift AgentPresence out of the workspace and add Unsupported availability (#839)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 23: `ChatInput` and `TerminalChatInput`

**Files:**
- Create: `src/Capacitor.App/ViewModels/ChatInput.cs`, `src/Capacitor.App/ViewModels/TerminalChatInput.cs`
- Test: `test/Capacitor.App.Tests.Unit/TerminalChatInputTests.cs`

**Interfaces:**
- Produces:

```csharp
public abstract class ChatInput : ReactiveObject, IDisposable {
    public abstract SendAvailability Availability { get; }
    public abstract bool CanAcceptText { get; }
    public abstract string Hint { get; }
    /// Completes when the channel considers the text committed: true to clear the composer.
    public abstract Task<bool> SendAsync(string text, CancellationToken ct);
    public abstract void Dispose();
}
public sealed class TerminalChatInput(TerminalTabViewModel terminal) : ChatInput { … }
```

`TerminalChatInput.HintFor(SendAvailability, TerminalSessionState)` is today's `ChatTabViewModel.HintFor` moved verbatim (internal static).

- [ ] **Step 1: Write the failing tests**

```csharp
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class TerminalChatInputTests {
    static async Task<(TerminalTabViewModel Terminal, TerminalChatInput Input, FakeTerminalAttachClient Client, FakeTimeProvider Time)> BuildAttachedAsync() {
        var daemon = new FakeDaemonClientService();
        var factory = new FakeTerminalAttachClientFactory();
        var time = new FakeTimeProvider();
        var terminal = new TerminalTabViewModel("a1", daemon, factory.Factory, () => new FakeTerminalSurface(), time);
        var input = new TerminalChatInput(terminal);
        daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Running" });
        Dispatcher.UIThread.RunJobs();
        await (terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
        var client = factory.Created.Single();
        await client.TriggerAttached([]);
        return (terminal, input, client, time);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Mirrors_the_terminal_availability_and_wording() {
        await RunOnUiAsync(async () => {
            var (terminal, input, client, time) = await BuildAttachedAsync();
            await Assert.That(input.Availability).IsEqualTo(SendAvailability.Ready);
            await Assert.That(input.Hint).IsEqualTo("Enter sends · Shift+Enter for a new line");
            await Assert.That(input.CanAcceptText).IsTrue();

            var raised = new List<string>();
            input.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);
            await Assert.That(await input.SendAsync("hello", CancellationToken.None)).IsTrue();
            await Assert.That(input.Availability).IsEqualTo(SendAvailability.Sending);
            await Assert.That(input.Hint).IsEqualTo("Sending…");
            await Assert.That(raised).Contains(nameof(ChatInput.Availability));

            time.Advance(TimeSpan.FromMilliseconds(150));
            await terminal.PendingDeliveryForTesting!;
            await Assert.That(input.Availability).IsEqualTo(SendAvailability.Ready);
            await Assert.That(client.SentInput).Count().IsEqualTo(1);
            input.Dispose();
            await terminal.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Dispose_detaches_from_the_terminal() {
        await RunOnUiAsync(async () => {
            var (terminal, input, client, _) = await BuildAttachedAsync();
            input.Dispose();
            var raised = 0;
            input.PropertyChanged += (_, _) => raised++;
            await client.TriggerExited(0);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(raised).IsEqualTo(0);
            await Assert.That(await input.SendAsync("x", CancellationToken.None)).IsFalse();
            await terminal.TeardownAsync();
        });
    }
}
```

(`TriggerExited` — use whatever `FakeTerminalAttachClient` exposes to end the session; if it is named differently, use that member.)

- [ ] **Step 2: Run to verify failure** — `~/.dotnet/dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter '/*/*/TerminalChatInputTests/*'`; build error.

- [ ] **Step 3: Implement**

`ChatInput.cs`:

```csharp
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The composer's channel to the agent. The terminal channel commits when the paste is accepted; the
/// frame channel when the daemon's ack arrives — that difference lives in SendAsync alone.
public abstract class ChatInput : ReactiveObject, IDisposable {
    public abstract SendAvailability Availability { get; }
    public abstract bool CanAcceptText { get; }
    public abstract string Hint { get; }
    /// Completes when the channel considers the text committed: true to clear the composer.
    public abstract Task<bool> SendAsync(string text, CancellationToken ct);
    public abstract void Dispose();
}
```

`TerminalChatInput.cs`:

```csharp
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class TerminalChatInput : ChatInput {
    readonly TerminalTabViewModel _terminal;
    readonly IDisposable _subscription;
    bool _disposed;

    public TerminalChatInput(TerminalTabViewModel terminal) {
        _terminal = terminal;
        _subscription = terminal.WhenAnyValue(t => t.SendAvailability, t => t.State, t => t.CanAcceptText)
            .Skip(1)
            .Subscribe(_ => {
                this.RaisePropertyChanged(nameof(Availability));
                this.RaisePropertyChanged(nameof(CanAcceptText));
                this.RaisePropertyChanged(nameof(Hint));
            });
    }

    public override SendAvailability Availability => _disposed ? SendAvailability.Ended : _terminal.SendAvailability;
    public override bool CanAcceptText => !_disposed && _terminal.CanAcceptText;
    public override string Hint => HintFor(_terminal.SendAvailability, _terminal.State);

    /// The terminal path has nothing to cancel: acceptance is synchronous.
    public override Task<bool> SendAsync(string text, CancellationToken ct) =>
        Task.FromResult(!_disposed && _terminal.TrySendText(text));

    public override void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _subscription.Dispose();
    }

    /// The hint is built from the terminal's own availability, so it is true in the windows where
    /// State alone would lie (a reattach or detach under way while State reads Attached).
    internal static string HintFor(SendAvailability availability, TerminalSessionState state) => availability switch {
        SendAvailability.Ready         => "Enter sends · Shift+Enter for a new line",
        SendAvailability.Sending       => "Sending…",
        SendAvailability.Transitioning => "Updating the terminal connection…",
        SendAvailability.ReadOnly      => $"Read-only: {state.Detail}",
        SendAvailability.Connecting    => "Connecting to the terminal…",
        SendAvailability.Reattach      => "Reattach the terminal to send",
        SendAvailability.Ended         => "This session has ended",
        _                              => "No terminal to send to",
    };
}
```

Leave `ChatTabViewModel.HintFor` in place until Task 25 removes it (or make it delegate now: `internal static string HintFor(...) => TerminalChatInput.HintFor(...)`).

- [ ] **Step 4: Run tests** — filter `TerminalChatInputTests`; PASS.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.App/ViewModels/ChatInput.cs src/Capacitor.App/ViewModels/TerminalChatInput.cs src/Capacitor.App/ViewModels/ChatTabViewModel.cs test/Capacitor.App.Tests.Unit/TerminalChatInputTests.cs
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Abstract the composer channel and wrap the terminal in it (#839)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 24: `LocalFrameChatInput`

**Files:**
- Create: `src/Capacitor.App/ViewModels/LocalFrameChatInput.cs`
- Test: `test/Capacitor.App.Tests.Unit/LocalFrameChatInputTests.cs`

**Interfaces:**
- Consumes: `ILocalControlOps.SendTextAsync`, `SendTextResult`, `SendTextReasons`, `SendTextOutcomes`, `IDaemonClientService.Status` (`AttachStatus.State`, `.Capabilities`), `IObservable<AgentPresence>`.
- Produces: `internal sealed class LocalFrameChatInput(string agentId, IDaemonClientService daemon, ILocalControlOps ops, IObservable<AgentPresence> presence) : ChatInput`. State rules, in priority order: `Ended` when `SessionEnded`; `Connecting` while `Status.State != AttachState.Connected`; `Unsupported` when `Capabilities` lacks `"input/1"`; `Sending` while a send is in flight; `Ready` when `Dto.Status == "Running"`; otherwise `Connecting`. `Hint` shows the last refusal notice until the next send starts or the availability changes.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class LocalFrameChatInputTests {
    sealed class Rig {
        public FakeDaemonClientService Daemon { get; } = new();
        public ScriptedLocalControlOps Ops { get; } = new();
        public BehaviorSubject<AgentPresence> Presence { get; } = new(new AgentPresence(null, false));
        public LocalFrameChatInput Input { get; }
        public Rig() => Input = new LocalFrameChatInput("a1", Daemon, Ops, Presence);
        public void Connected(params string[] caps) => Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, caps));
        public void Running() => Presence.OnNext(new AgentPresence(Agent("a1", "pi", hasTerminal: false) with { Status = "Running" }, false));
    }

    [Test]
    public async Task Availability_matrix() {
        var rig = new Rig();
        await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Connecting);
        rig.Connected("status/1");
        rig.Running();
        await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Unsupported);
        await Assert.That(rig.Input.Hint).IsEqualTo("Update the daemon to send messages from the app");
        await Assert.That(await rig.Input.SendAsync("x", CancellationToken.None)).IsFalse();
        await Assert.That(rig.Ops.SendTextCalls).IsEqualTo(0);

        rig.Connected("status/1", "input/1");
        await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Ready);
        await Assert.That(rig.Input.CanAcceptText).IsTrue();
        await Assert.That(rig.Input.Hint).IsEqualTo("Enter sends · Shift+Enter for a new line");

        rig.Presence.OnNext(new AgentPresence(rig.Presence.Value.Dto, true));
        await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Ended);
        await Assert.That(rig.Input.Hint).IsEqualTo("This session has ended");
    }

    [Test]
    public async Task One_send_in_flight_until_the_ack_and_a_delivered_ack_clears() {
        var rig = new Rig(); rig.Connected("input/1"); rig.Running();
        var gate = rig.Ops.ArmSendText();
        var pending = rig.Input.SendAsync("hello", CancellationToken.None);
        await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Sending);
        await Assert.That(rig.Input.CanAcceptText).IsFalse();
        await Assert.That(await rig.Input.SendAsync("second", CancellationToken.None)).IsFalse();
        gate.SetResult(new SendTextResult(true, null, null, SendTextOutcomes.Delivered));
        await Assert.That(await pending).IsTrue();
        await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Ready);
        await Assert.That(rig.Ops.SendTextPayloads).IsEquivalentTo(new[] { ("a1", "hello") });
    }

    [Test]
    [Arguments("not_running", null, "agent is no longer running")]
    [Arguments("protected_kind", null, "read-only participant")]
    [Arguments("queue_full", null, "the agent's input queue is full, try again shortly")]
    [Arguments("delivery_failed", "pipe closed", "pipe closed")]
    [Arguments("transport", "eof", "delivery unconfirmed — check the chat before sending again")]
    public async Task Refusal_keeps_the_text_and_words_the_hint(string reason, string? error, string hint) {
        var rig = new Rig(); rig.Connected("input/1"); rig.Running();
        rig.Ops.QueueSendText(new SendTextResult(false, reason, error, null));
        await Assert.That(await rig.Input.SendAsync("hello", CancellationToken.None)).IsFalse();
        await Assert.That(rig.Input.Hint).IsEqualTo(hint);
        await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Ready);
        rig.Ops.ArmSendText();
        _ = rig.Input.SendAsync("again", CancellationToken.None);
        await Assert.That(rig.Input.Hint).IsEqualTo("Sending…"); // the notice clears when the next send starts
    }

    [Test]
    public async Task Stopped_outcome_clears_and_unknown_ok_outcome_counts_as_delivered() {
        var rig = new Rig(); rig.Connected("input/1"); rig.Running();
        rig.Ops.QueueSendText(new SendTextResult(true, null, null, SendTextOutcomes.Stopped));
        await Assert.That(await rig.Input.SendAsync("/quit", CancellationToken.None)).IsTrue();
        rig.Ops.QueueSendText(new SendTextResult(true, null, null, "future_outcome"));
        await Assert.That(await rig.Input.SendAsync("x", CancellationToken.None)).IsTrue();
    }

    [Test]
    public async Task Cancelled_wait_keeps_the_text_with_the_unconfirmed_hint() {
        var rig = new Rig(); rig.Connected("input/1"); rig.Running();
        rig.Ops.ArmSendText();
        using var cts = new CancellationTokenSource();
        var pending = rig.Input.SendAsync("hello", cts.Token);
        cts.Cancel();
        await Assert.That(await pending).IsFalse();
        await Assert.That(rig.Input.Hint).IsEqualTo("delivery unconfirmed — check the chat before sending again");
        await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Ready);
    }

    [Test]
    public async Task Completion_after_dispose_mutates_nothing_and_dispose_detaches_subscriptions() {
        var rig = new Rig(); rig.Connected("input/1"); rig.Running();
        var gate = rig.Ops.ArmSendText();
        var pending = rig.Input.SendAsync("hello", CancellationToken.None);
        rig.Input.Dispose();
        var raised = 0;
        rig.Input.PropertyChanged += (_, _) => raised++;
        gate.SetResult(new SendTextResult(false, "queue_full", null, null));
        await Assert.That(await pending).IsFalse();
        rig.Connected("input/1"); rig.Running();
        rig.Presence.OnNext(new AgentPresence(null, true));
        await Assert.That(raised).IsEqualTo(0);
    }
}
```

- [ ] **Step 2: Run to verify failure** — filter `LocalFrameChatInputTests`; build error.

- [ ] **Step 3: Implement**

```csharp
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The composer channel for a session with no PTY: one SendText exchange per send, acked when the
/// daemon's delivery settles. A lost ack is an unknown outcome and the hint says so.
internal sealed class LocalFrameChatInput : ChatInput {
    const string InputCapability = "input/1";
    const string Unconfirmed = "delivery unconfirmed — check the chat before sending again";

    readonly string _agentId;
    readonly ILocalControlOps _ops;
    readonly CompositeDisposable _subscriptions = new();
    AttachStatus _status = new(AttachState.Connecting, null, null);
    AgentPresence _presence = new(null, false);
    bool _sending;
    bool _disposed;
    string? _notice;

    public LocalFrameChatInput(string agentId, IDaemonClientService daemon, ILocalControlOps ops, IObservable<AgentPresence> presence) {
        _agentId = agentId;
        _ops = ops;
        daemon.Status.Subscribe(s => { _status = s; _notice = null; Raise(); }).DisposeWith(_subscriptions);
        presence.Subscribe(p => { _presence = p; Raise(); }).DisposeWith(_subscriptions);
    }

    public override SendAvailability Availability =>
        _presence.SessionEnded ? SendAvailability.Ended
        : _status.State != AttachState.Connected ? SendAvailability.Connecting
        : _status.Capabilities is not { } caps || !caps.Contains(InputCapability) ? SendAvailability.Unsupported
        : _sending ? SendAvailability.Sending
        : _presence.Dto?.Status == "Running" ? SendAvailability.Ready
        : SendAvailability.Connecting;

    public override bool CanAcceptText => Availability == SendAvailability.Ready;

    public override string Hint => _notice ?? Availability switch {
        SendAvailability.Ready       => "Enter sends · Shift+Enter for a new line",
        SendAvailability.Sending     => "Sending…",
        SendAvailability.Unsupported => "Update the daemon to send messages from the app",
        SendAvailability.Ended       => "This session has ended",
        _                            => "Connecting to the agent…",
    };

    public override async Task<bool> SendAsync(string text, CancellationToken ct) {
        if (!CanAcceptText) return false;
        _sending = true; _notice = null; Raise();
        SendTextResult result;
        try {
            result = await _ops.SendTextAsync(_agentId, text, ct);
        } catch (OperationCanceledException) {
            return Settle(false, Unconfirmed);
        } catch (Exception ex) {
            return Settle(false, ex.Message);
        }
        if (result.Ok) return Settle(true, null);
        return Settle(false, result.Reason switch {
            SendTextReasons.Transport     => Unconfirmed,
            SendTextReasons.NotRunning    => "agent is no longer running",
            SendTextReasons.NoSuchAgent   => "agent is no longer running",
            SendTextReasons.ProtectedKind => "read-only participant",
            SendTextReasons.QueueFull     => "the agent's input queue is full, try again shortly",
            SendTextReasons.StopFailed    => "the agent did not stop",
            SendTextReasons.DeliveryFailed => result.Error ?? "delivery failed",
            _                             => result.Error ?? result.Reason ?? "delivery failed",
        });
    }

    bool Settle(bool committed, string? notice) {
        if (_disposed) return committed;
        _sending = false; _notice = notice; Raise();
        return committed;
    }

    void Raise() {
        if (_disposed) return;
        this.RaisePropertyChanged(nameof(Availability));
        this.RaisePropertyChanged(nameof(CanAcceptText));
        this.RaisePropertyChanged(nameof(Hint));
    }

    public override void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _subscriptions.Dispose();
    }
}
```

- [ ] **Step 4: Run tests** — filter `LocalFrameChatInputTests`; PASS.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.App/ViewModels/LocalFrameChatInput.cs test/Capacitor.App.Tests.Unit/LocalFrameChatInputTests.cs
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Send composer text over SendText for sessions without a PTY (#839)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 25: `ChatTabViewModel` over `ChatInput` and `IChatTranscriptProjection`

**Files:**
- Create: `src/Capacitor.App/ViewModels/ChatTranscriptSource.cs`
- Modify: `src/Capacitor.App/ViewModels/ChatTabViewModel.cs`
- Modify (constructions): `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs:49`, `ChatComposerTests.cs:22,151`, `ChatTabViewSmokeTests.cs:106` — `new ChatTabViewModel("a1", Daemon, new TerminalChatInput(Terminal), projection, Opener, Time, Permissions)`
- Test: `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs` (append), `test/Capacitor.App.Tests.Unit/ChatComposerTests.cs` (existing tests keep passing), `test/Capacitor.App.Tests.Unit/ChatTranscriptSourceTests.cs` (new)

**Interfaces:**
- Produces:

```csharp
public ChatTabViewModel(string agentId, IDaemonClientService daemon, ChatInput input, IChatTranscriptProjection? projection,
                        IUrlOpener opener, TimeProvider time, IPermissionService permissions, string? unavailableNote = null);
internal static class ChatTranscriptSource {
    public const string OlderDaemonNote = "Update the daemon to view this session";
    public const string NewerDaemonNote = "Update the app to view this session";
    public static (IChatTranscriptProjection? Projection, string? UnavailableNote) Resolve(AgentStatusDto dto);
}
```

`PhaseNote` for `Unavailable` is `unavailableNote ?? "No chat view for this harness"`. `SendCommand` is `ReactiveCommand.CreateFromTask` over `input.SendAsync(text, _lifetimeToken)`; clears `ComposerText` only when `true` came back and the text still equals the sent snapshot. `TeardownAsync` cancels `_lifetime` **before** `_disposables.Dispose()` (the input is in `_disposables`). `ChatTabViewModel.HintFor` is deleted (moved to `TerminalChatInput` in Task 23).

- [ ] **Step 1: Write the failing tests**

`ChatTranscriptSourceTests.cs`:

```csharp
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class ChatTranscriptSourceTests {
    [Test]
    public async Task Formats_pick_the_reader() {
        var (vendorProjection, vendorNote) = ChatTranscriptSource.Resolve(Agent("a", "claude", hasTerminal: true) with { TranscriptFormat = TranscriptFormats.Vendor });
        await Assert.That(vendorProjection).IsNotNull(); await Assert.That(vendorNote).IsNull();

        var (leafless, leaflessNote) = ChatTranscriptSource.Resolve(Agent("a", "gemini", hasTerminal: true) with { TranscriptFormat = TranscriptFormats.Vendor });
        await Assert.That(leafless).IsNull(); await Assert.That(leaflessNote).IsNull();

        var (journal, journalNote) = ChatTranscriptSource.Resolve(Agent("a", "pi", hasTerminal: false) with { TranscriptFormat = TranscriptFormats.Envelopes });
        await Assert.That(journal).IsSameReferenceAs(TranscriptChat.Journal); await Assert.That(journalNote).IsNull();
    }

    [Test]
    public async Task Null_format_is_the_older_daemon_for_a_non_pty_dto_and_the_vendor_path_for_a_pty_dto() {
        var (older, olderNote) = ChatTranscriptSource.Resolve(Agent("a", "pi", hasTerminal: false));
        await Assert.That(older).IsNull(); await Assert.That(olderNote).IsEqualTo("Update the daemon to view this session");

        var (pty, ptyNote) = ChatTranscriptSource.Resolve(Agent("a", "claude", hasTerminal: true));
        await Assert.That(pty).IsNotNull(); await Assert.That(ptyNote).IsNull();
    }

    [Test]
    public async Task Unknown_format_asks_for_an_app_update() {
        var (projection, note) = ChatTranscriptSource.Resolve(Agent("a", "pi", hasTerminal: false) with { TranscriptFormat = "v9" });
        await Assert.That(projection).IsNull(); await Assert.That(note).IsEqualTo("Update the app to view this session");
    }
}
```

Append to `ChatTabViewModelTests` (its `Harness` ctor gains `ChatInput? input = null` → `input ?? new TerminalChatInput(Terminal)`, and `string? unavailableNote = null`):

```csharp
    /// A scripted ChatInput for composer tests: SendAsync completes when the test says so.
    sealed class ScriptedInput : ChatInput {
        public TaskCompletionSource<bool>? Pending;
        public int Disposals;
        public List<(string Text, CancellationToken Ct)> Sends { get; } = [];
        public override SendAvailability Availability => Pending is null ? SendAvailability.Ready : SendAvailability.Sending;
        public override bool CanAcceptText => Pending is null;
        public override string Hint => "scripted";
        public override Task<bool> SendAsync(string text, CancellationToken ct) {
            Sends.Add((text, ct));
            Pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.RaisePropertyChanged(nameof(CanAcceptText));
            return Pending.Task.ContinueWith(t => { Pending = null; this.RaisePropertyChanged(nameof(CanAcceptText)); return t.Result; }, ct, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        public override void Dispose() => Disposals++;
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Journal_file_renders_user_assistant_note_and_tool_rows() {
        await RunOnUiAsync(async () => {
            using var tmp = new TempDir();
            var path = tmp.PathTo("j.jsonl");
            File.WriteAllLines(path, [
                EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.SessionStarted, Cwd: "/w")),
                EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: "hi")),
                EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: "hello")),
                EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.SystemNote, Text: "note")),
                EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.ToolCall, ToolCallId: "c1", ToolName: "Read", ToolInputJson: """{"file_path":"/w/a.cs"}""")),
                EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1", ToolResult: "ok")),
            ]);
            var h = new Harness(TranscriptChat.Journal);
            await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { TranscriptPath = path, TranscriptFormat = TranscriptFormats.Envelopes });
            await h.TickAsync();

            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(new[] { "UserTurnItem", "AssistantTextItem", "SystemNoteItem", "ToolGroupItem" });
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Unavailable_note_words_the_older_and_newer_daemon_cases() {
        await RunOnUiAsync(async () => {
            var older = new Harness(null, unavailableNote: "Update the daemon to view this session");
            await Assert.That(older.Chat.Phase).IsEqualTo(ChatTabPhase.Unavailable);
            await Assert.That(older.Chat.PhaseNote).IsEqualTo("Update the daemon to view this session");
            await older.TeardownAsync();
            var plain = new Harness(null);
            await Assert.That(plain.Chat.PhaseNote).IsEqualTo("No chat view for this harness");
            await plain.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Send_clears_only_when_committed_and_the_text_is_unchanged() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.Journal, input: input);
            await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running" });

            h.Chat.ComposerText = "hello";
            var send = h.Chat.SendCommand.Execute().ToTask();
            await Assert.That(input.Sends.Single().Text).IsEqualTo("hello");
            h.Chat.ComposerText = "hello edited";
            input.Pending!.SetResult(true);
            await send;
            await Assert.That(h.Chat.ComposerText).IsEqualTo("hello edited"); // typed during the round trip: kept

            h.Chat.ComposerText = "two";
            send = h.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(false);
            await send;
            await Assert.That(h.Chat.ComposerText).IsEqualTo("two"); // refused: kept

            h.Chat.ComposerText = "three";
            send = h.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(true);
            await send;
            await Assert.That(h.Chat.ComposerText).IsEqualTo(""); // committed and unchanged: cleared
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Teardown_cancels_an_in_flight_send_before_disposing_the_input_once() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.Journal, input: input);
            await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running" });
            h.Chat.ComposerText = "hello";
            var send = h.Chat.SendCommand.Execute().ToTask();
            var ct = input.Sends.Single().Ct;
            await Assert.That(ct.IsCancellationRequested).IsFalse();

            await h.TeardownAsync();

            await Assert.That(ct.IsCancellationRequested).IsTrue();
            await Assert.That(input.Disposals).IsEqualTo(1);
            input.Pending?.TrySetCanceled(ct);
            try { await send; } catch (OperationCanceledException) { }
        });
    }
```

(`ScriptedInput` needs `using ReactiveUI.Reactive;` for `RaisePropertyChanged`; `Execute().ToTask()` needs `System.Reactive.Threading.Tasks`.)

- [ ] **Step 2: Run to verify failure** — filters `ChatTranscriptSourceTests`, `ChatTabViewModelTests`; build errors.

- [ ] **Step 3: Implement**

`ChatTranscriptSource.cs`:

```csharp
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.ViewModels;

/// Which reader a session's transcript_path gets, stated on the wire by transcript_format. Null is an
/// older daemon: its PTY agents take today's vendor path, anything else cannot be read.
internal static class ChatTranscriptSource {
    public const string OlderDaemonNote = "Update the daemon to view this session";
    public const string NewerDaemonNote = "Update the app to view this session";

    public static (IChatTranscriptProjection? Projection, string? UnavailableNote) Resolve(AgentStatusDto dto) => dto.TranscriptFormat switch {
        TranscriptFormats.Vendor    => (TranscriptChat.For(dto.Vendor), null),
        TranscriptFormats.Envelopes => (TranscriptChat.Journal, null),
        null => HostedHarnessCatalog.ShowsTerminal(dto.HasTerminal, dto.Vendor) ? (TranscriptChat.For(dto.Vendor), null) : (null, OlderDaemonNote),
        _    => (null, NewerDaemonNote),
    };
}
```

`ChatTabViewModel.cs` changes:

- Fields: replace `readonly TerminalTabViewModel _terminal;` with `readonly ChatInput _input;`; `readonly TranscriptChatProjection? _projection;` → `readonly IChatTranscriptProjection? _projection;`; add `readonly string? _unavailableNote;`.
- `TailLease.ContextFor(IChatTranscriptProjection projection, string agentId)`; `ReadAndApplyAsync(TailLease lease, IChatTranscriptProjection projection)`.
- Constructor signature as in Interfaces; `_input = input; _disposables.Add(input); _unavailableNote = unavailableNote;`.
- `PhaseNote`: `ChatTabPhase.Unavailable => _unavailableNote ?? "No chat view for this harness"`.
- Delete `HintFor` and the `using` it needed if unused.
- Composer observables:

```csharp
        _composerHint = Observable.CombineLatest(
                _input.WhenAnyValue(i => i.Hint),
                this.WhenAnyValue(x => x.IsReadOnlyParticipant),
                (hint, readOnly) => readOnly ? "" : hint)
            .ToProperty(this, x => x.ComposerHint, initialValue: IsReadOnlyParticipant ? "" : _input.Hint)
            .DisposeWith(_disposables);

        _showsComposer = Observable.CombineLatest(
                _input.WhenAnyValue(i => i.Availability),
                this.WhenAnyValue(x => x.IsReadOnlyParticipant),
                (availability, readOnly) => !readOnly && availability != SendAvailability.Ended)
            .ToProperty(this, x => x.ShowsComposer, initialValue: !IsReadOnlyParticipant && _input.Availability != SendAvailability.Ended)
            .DisposeWith(_disposables);

        var canSend = Observable.CombineLatest(
            this.WhenAnyValue(x => x.ComposerText),
            _input.WhenAnyValue(i => i.CanAcceptText),
            this.WhenAnyValue(x => x.IsReadOnlyParticipant),
            (text, can, readOnly) => can && !readOnly && !string.IsNullOrWhiteSpace(text));
        SendCommand = ReactiveCommand.CreateFromTask(async () => {
            var snapshot = ComposerText;
            bool committed;
            try { committed = await _input.SendAsync(snapshot, _lifetimeToken); }
            catch (OperationCanceledException) { return; }
            if (committed && ComposerText == snapshot) ComposerText = "";
        }, canSend);
        _disposables.Add(SendCommand);
```

- `TeardownAsync`: move `try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }` to before `_disposables.Dispose();`, keep `_lifetime.Dispose()` last.

Update the four test constructions listed under Files.

- [ ] **Step 4: Run tests** — filters `ChatTranscriptSourceTests`, `ChatTabViewModelTests`, `ChatComposerTests`, `ChatTabViewSmokeTests`; PASS. `Send_clears_the_text_on_acceptance_and_keeps_it_on_refusal` in `ChatComposerTests` still passes: the terminal input completes synchronously and the text is unchanged at that instant.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.App/ViewModels test/Capacitor.App.Tests.Unit
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Drive the chat tab from a ChatInput and a chat projection (#839)" -m "The composer clears only when the channel commits and the text is still what was sent, so typing during a round trip is never erased." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 26: Chat for every session — workspace, XAML, app wiring

**Files:**
- Modify: `src/Capacitor.App/ViewModels/WorkspaceViewModel.cs` (constructor gains `ILocalControlOps ops` right after `IWorkContextSource workContext`; Chat built on the first dto of any vendor; `NoTerminalNote` property removed)
- Modify: `src/Capacitor.App/Views/WorkspaceView.axaml` (lines 65–66 `ChatTabButton` loses `IsVisible`; line 69 `NoTerminalNote` TextBlock deleted; lines 161–167 `ChatHost.IsVisible` becomes `IsVisible="{Binding $parent[views:WorkspaceView].((vm:WorkspaceViewModel)DataContext).IsChatActive}"`; the tab-strip comment rewritten: "Chat for every session; Terminal only when the daemon reports a PTY.")
- Modify: `src/Capacitor.App/App.axaml.cs:528` (`BuildWorkspace` passes `workContext, ops, requestSignIn: …`)
- Modify (constructions gain `new ScriptedLocalControlOps()` or the ops already in scope): `test/Capacitor.App.Tests.Unit/WorkspaceViewModelTests.cs:25,260`, `WorkspaceViewSmokeTests.cs:43`, `MainWindowViewModelTests.cs:42`, `PullRequestViewSmokeTests.cs:25`, `WorkspaceNavigationTests.cs:88`, `MainWindowSmokeTests.cs:301,388`
- Test: `test/Capacitor.App.Tests.Unit/WorkspaceViewModelTests.cs` (invert the pin), `test/Capacitor.App.Tests.Unit/WorkspaceViewSmokeTests.cs` (names list, visibility test)

- [ ] **Step 1: Rewrite the tests**

`WorkspaceViewModelTests`: `Build` passes `new ScriptedLocalControlOps()` (or take an `ops` parameter defaulting to a fresh one). Replace `Chat_is_built_for_a_pty_dto_only_and_torn_down_with_the_workspace` with:

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Chat_is_built_on_the_first_dto_of_any_vendor_with_the_reader_the_format_names() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var vm = Build(daemon, NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener()), new FakeTerminalAttachClientFactory(), new FakeTimeProvider());
            await Assert.That(vm.Chat).IsNull();

            daemon.Agents.AddOrUpdate(Agent("a1", "pi", hasTerminal: false) with { TranscriptFormat = TranscriptFormats.Envelopes });
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await Assert.That(vm.Chat).IsNotNull();
            await Assert.That(vm.Chat!.Phase).IsEqualTo(ChatTabPhase.Waiting); // journal reader, no path yet
            await Assert.That(vm.ShowsTerminalTab).IsFalse();

            var chat = vm.Chat;
            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true));
            await Assert.That(vm.Chat).IsSameReferenceAs(chat); // built once

            await vm.TeardownAsync();
            await Assert.That(chat.PendingReadForTesting!).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Null_format_non_pty_dto_is_the_older_daemon_and_a_vendor_pty_dto_takes_the_vendor_reader() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var older = Build(daemon, NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener()), new FakeTerminalAttachClientFactory(), new FakeTimeProvider());
            daemon.Agents.AddOrUpdate(Agent("a1", "gemini", hasTerminal: false));
            await (older.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await Assert.That(older.Chat!.Phase).IsEqualTo(ChatTabPhase.Unavailable);
            await Assert.That(older.Chat.PhaseNote).IsEqualTo("Update the daemon to view this session");
            await older.TeardownAsync();

            var daemon2 = new FakeDaemonClientService();
            var pty = Build(daemon2, NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener()), new FakeTerminalAttachClientFactory(), new FakeTimeProvider(), agentId: "a2");
            daemon2.Agents.AddOrUpdate(Agent("a2", "claude", hasTerminal: true) with { TranscriptFormat = TranscriptFormats.Vendor });
            await (pty.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await Assert.That(pty.Chat!.Phase).IsEqualTo(ChatTabPhase.Waiting);
            await Assert.That(pty.ShowsTerminalTab).IsTrue();
            await pty.TeardownAsync();
        });
    }
```

`WorkspaceViewSmokeTests`: remove `"NoTerminalNote"` from the names list; rewrite `Tab_and_note_visibility_flip_with_ShowsTerminalTab` as `Chat_is_always_offered_and_the_terminal_pair_follows_ShowsTerminalTab`: after the `hasTerminal: false` dto assert `ChatTabButton.IsEffectivelyVisible` true, `ChatHost.IsVisible` true (Chat is the default tab), `TerminalTabButton.IsEffectivelyVisible` false, `TerminalHost.IsVisible` false, and that `Find<Control>(window, "NoTerminalNote")` is null; after the `hasTerminal: true` dto assert the terminal pair is visible and the Chat button still is.

- [ ] **Step 2: Run to verify failure** — filters `WorkspaceViewModelTests`, `WorkspaceViewSmokeTests`; build errors / failures.

- [ ] **Step 3: Implement**

`WorkspaceViewModel.cs`:

- Constructor: `…, IPermissionService permissions, IWorkContextSource workContext, ILocalControlOps ops, Action? requestSignIn = null, …`.
- Delete `_noTerminalNote` / `NoTerminalNote` and their construction.
- Replace the Chat construction:

```csharp
        presence
            .Where(p => p.Dto is not null)
            .Take(1)
            .Subscribe(p => {
                var dto = p.Dto!;
                var (projection, note) = ChatTranscriptSource.Resolve(dto);
                ChatInput input = HostedHarnessCatalog.ShowsTerminal(dto.HasTerminal, dto.Vendor)
                    ? new TerminalChatInput(Terminal)
                    : new LocalFrameChatInput(agentId, daemon, ops, presence);
                Chat = new ChatTabViewModel(agentId, daemon, input, projection, opener, time, permissions, note);
            })
            .DisposeWith(_disposables);
```

- Update the `Chat` property doc: "Built once, on the first dto; the reader comes from transcript_format and the input channel from has_terminal."

`WorkspaceView.axaml`: edits as listed under Files. `App.axaml.cs`: `workContext, ops, requestSignIn: requestSignIn, …` in `BuildWorkspace`. Update every test construction listed.

- [ ] **Step 4: Run tests** — the whole App suite: `~/.dotnet/dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`; green.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 add src/Capacitor.App test/Capacitor.App.Tests.Unit
/usr/bin/git -C /Users/tony/dev/kcap-cli/.claude/worktrees/pi-agents-desktop-display-9cfa56 commit -q -m "Show the Chat tab for every hosted session (#839)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 27: Full verification and the live reproduction

**Files:** none new (fixes only, if anything surfaces).

- [ ] **Step 1: Full suites**

```bash
~/.dotnet/dotnet test --solution Capacitor.slnx
```

Expected: green apart from the known environmental install/config failures that also fail on pristine `main` (compare before trusting a red).

- [ ] **Step 2: AOT and hygiene**

```bash
~/.dotnet/dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

Expected: no output.

```bash
bash scripts/check-linear-ids.sh
```

Expected: clean.

- [ ] **Step 3: Live reproduction** (the investigation's repro): build and run the daemon and the app from this worktree with `DOTNET_ROOT=~/.dotnet`; launch a Pi agent and a Cursor agent from the app. Verify in the app: both workspaces show a Chat tab with the initial prompt as the first user row and the agent's output following; no Terminal tab; the composer's hint reads "Enter sends · Shift+Enter for a new line"; a follow-up sent from the composer appears as a user row and gets a reply; typing `/quit` stops the agent and the composer reads "This session has ended". Verify on disk: `~/.config/kcap/daemons/<state>/<name>/transcripts/*.jsonl` exists per agent with a `session_started` first line. Verify in the web UI: the Pi session's status changes carry a session id (no "Agent failed to start" on a clean stop is out of scope — AI-2644).

- [ ] **Step 4: Spec ride-along**

The spec (`docs/superpowers/specs/2026-09-09-ai2197-non-pty-chat-and-composer-design.md`) and this plan are already committed on the branch; they ride the implementation PR. Re-read the spec's "Decisions" and §6 once more against the code and fix any drift found in a final commit `Align the non-PTY chat implementation with its spec (#839)`; if nothing drifted, no commit.

- [ ] **Step 5: Open the PR** per `.github/PULL_REQUEST_TEMPLATE.md` (title without a reference: `Show chat and composer for hosted sessions without a PTY`; description reference line `Closes #839` and `AI-2197`, `AI-2625`). Report the PR to Tony by its Linear ids, not the GitHub number.

---

## Self-review notes

- Spec coverage: §1 → Tasks 8–18 (journal, names, locks, sweep, orchestrator fields and gating, four runtimes, Antigravity user turn); §2 → Tasks 6, 7, 19, 20, 21; §3 → Tasks 3, 4, 5; §4 → Tasks 22–26; §5 compatibility matrix → `ChatTranscriptSource` (Task 25) and `LocalFrameChatInput.Unsupported` (Task 24); §6 parity items are asserted by the tests named in Tasks 13, 20, 21, 25; §7 test list mapped task by task; Task 1–2 are the two Core prerequisites the spec calls out (`PlatformPaths`, `SessionIds`).
- Naming: the spec's `Complete()` is `CompleteAsync()` here (it awaits the writer); the spec's delivery outcome is `InputDeliveryOutcome`; the spec's `AgentPresence` observable is `IObservable<AgentPresence>` fed by the workspace's existing replayed `presence`.
- Every `SendTextReasons` token equals the `SendInputDropReason` token of the same name, so the core's drop reasons pass through the local ack unchanged.
