# Prompt Attachments Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the desktop app's Home goal box and session Chat composer take pasted, dropped or picked files, and deliver them to the agent as readable files, failing closed at every step.

**Architecture:** The app stages files in memory, uploads them to the server's existing temp attachment store at Send, and sends ids: on the existing `attachment_ids` launch field, or on a new local frame `SendTextWithAttachments = 24`. The daemon validates, fetches each batch into a staging directory, publishes it by one rename into a placement chosen per vendor (`Worktree` for everyone, `DaemonStore` for default-kind Codex), composes the `[Attached files: …]` trailer, and rolls the batch back on any refusal before the runtime write. Every PTY write goes through one lane so a composer send can never be interleaved by keystrokes.

**Tech Stack:** .NET 10 / C# 13, Avalonia 12.1.2, ReactiveUI, TUnit + WireMock.Net, NativeAOT (`dotnet publish -c Release` must stay free of IL2026/IL3050).

**Spec:** `docs/superpowers/specs/2026-09-12-ai2318-prompt-attachments-design.md` (the Linear document on AI-2318 is the durable copy; where they differ, Linear wins).

## Global Constraints

- Build/test with `~/.dotnet/dotnet` (the PATH `dotnet` is 8.0). Run a single suite as `~/.dotnet/dotnet run --project test/<Suite>/<Suite>.csproj -- --treenode-filter '/*/*/<Class>/*'`.
- `FrameType` values are append-only: the new frame is `SendTextWithAttachments = 24`; nothing else moves. `LocalControlCapabilities.Current` gains `"input/2"` only in the same commit that routes frame 24.
- Limits, verbatim from the spec: `MaxAttachmentsPerPrompt = 10`, `MaxAttachmentBytes = 10L * 1024 * 1024`, ids are Guid "N" (32 hex, case-insensitive on input, canonical lowercase).
- User-facing wording is fixed by the spec §1 and §2 and repeated in each task; do not paraphrase it.
- The daemon never looks for a credential; `DownloadAttachmentsAsync` keeps its existing `_tokens.GetValidTokensForServerAsync` call and nothing else.
- Comments: scarce; no ticket ids, no change narration, no spec section references. Test doc comments say what the test pins.
- One type per file, named after the type; `FrozenSet<T>.Empty`/`FrozenDictionary<K,V>.Empty` for empty read-only collections.
- Never `Process.Kill(bool)` in the daemon; never `Environment.GetFolderPath`.
- Every task ends with the touched suites green and one commit. Commit subjects: imperative, ≤ 80 chars, no ticket ids; end the body with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- Branch: `tonyyoung/ai-2318-desktop-prompt-attachments` in the current worktree. Git commands: `/usr/bin/git -C <worktree> …`, one per Bash call.

---

## File Structure

**Capacitor.Cli.Core** (`src/Capacitor.Cli.Core/LocalIpc/`)
- Modify `FrameType.cs` — add `SendTextWithAttachments = 24`.
- Modify `FrameCodec.cs` — add the frame to both text-payload lists.
- Modify `InputIpc.cs` — `SendTextWithAttachmentsDto`, `InputWire` additions, `SendTextReasons.AttachmentsRefused`, JSON context registration.
- Create `AttachmentIds.cs` — `Canonical`, `Validate`.
- Create `AttachmentTrailer.cs` — `Prefix`, `For`.
- Modify `LocalControlOps.cs` — `SendTextWithAttachmentsAsync` on the interface and class.

**Capacitor.Cli.Daemon**
- Create `Services/AttachmentPlacement.cs` — the enum.
- Modify `Services/IHostedAgentRuntimeFactory.cs` — default member `AttachmentPlacementFor(LaunchKind)`.
- Modify `Harness/Codex/CodexHostedAgentRuntimeFactory.cs` — override.
- Modify `Services/AgentOrchestrator.cs` — `AgentInstance.Placement`; validation, placement rule, protected-kind and quit checks in `DeliverInputAsync`; batch rollback; launch fail-closed + lease; cleanup removes the store dir; `IsLiveAttachmentStem`.
- Modify `Services/AgentOrchestrator.LocalIpc.cs` — frame-24 handler; local spawn records placement.
- Create `Services/AttachmentStore.cs` — root, `DirectoryFor`, `Remove`, `Lease`, `SweepOrphans`.
- Create `Services/AttachmentBatch.cs` — published batch handle with `Rollback`.
- Create `Services/AttachmentFetch.cs` — fetch result record.
- Create `Services/AttachmentFetcher.cs` — the fetch (staging, caps, rename); replaces `AgentOrchestrator.DownloadAttachmentsAsync`.
- Modify `Services/LocalControlServer.cs` — route frame 24.
- Modify `Services/LocalControlCapabilities.cs` — `"input/2"`.
- Modify `Services/PtyHostedAgentRuntime.cs` — input lane, 150 ms, `TimeProvider`.
- Modify `DaemonRunner.cs` — register `AttachmentStore`, run the orphan sweep at startup.

**Capacitor.App**
- Create `ViewModels/StagedAttachment.cs`, `ViewModels/IntakeRefusal.cs`, `ViewModels/AttachmentTray.cs`, `ViewModels/StagedAttachmentViewModel.cs`.
- Create `Services/AttachmentIntake.cs`, `Services/IntakeKind.cs`, `Services/IntakeResult.cs`.
- Create `Services/IAttachmentUploader.cs`, `Services/UploadOutcome.cs`, `Services/ServerAttachmentUploader.cs`, `Services/NoAttachmentUploader.cs`.
- Create `Services/IAttachmentSink.cs`, `Views/AttachmentDropPaste.cs`, `Views/AttachmentChipStrip.axaml(.cs)`.
- Create `Services/LaunchAttachments.cs` — `MinDaemonVersion`, `IsCapable`.
- Modify `ViewModels/ChatInput.cs`, `LocalFrameChatInput.cs`, `TerminalChatInput.cs`, `ChatTabViewModel.cs`, `QueuedChatMessage.cs`, `WorkspaceViewModel.cs`, `HomeViewModel.cs`; create `ViewModels/LaunchDraft.cs`.
- Modify `Services/ILaunchClient.cs` (`LaunchRequest.AttachmentIds`, `LaunchPayload.For`).
- Modify `Views/ChatTabView.axaml(.cs)`, `Views/LauncherPaneView.axaml(.cs)`, `App.axaml.cs`.

**Tests** mirror those paths under `test/Capacitor.Cli.Core.Tests.Unit`, `test/Capacitor.Cli.Daemon.Tests.Unit` (add `WireMock.Net` package reference), `test/Capacitor.App.Tests.Unit`.

**Docs**: `docs/CHANGES.md` entry (Task 18).

---

### Task 1: Core wire — frame 24, DTO, ids, trailer, limits

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs`
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs` (the two `switch` lists that name `FrameType.SendText or FrameType.SendTextAck`)
- Modify: `src/Capacitor.Cli.Core/LocalIpc/InputIpc.cs`
- Create: `src/Capacitor.Cli.Core/LocalIpc/AttachmentIds.cs`
- Create: `src/Capacitor.Cli.Core/LocalIpc/AttachmentTrailer.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/InputWireContractsTests.cs` (extend), `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/AttachmentIdsTests.cs` (new), `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/AttachmentTrailerTests.cs` (new), `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/FrameCodecTests.cs` (extend if it exists; otherwise create)

**Interfaces:**
- Produces: `FrameType.SendTextWithAttachments = 24`; `SendTextWithAttachmentsDto(string AgentId, string Text, string[] AttachmentIds)`; `InputWire.MaxAttachmentsPerPrompt`, `InputWire.MaxAttachmentBytes`, `InputWire.IsValidAttachmentId(string?)`, `InputWire.IsStructurallyValid(SendTextWithAttachmentsDto?)`; `AttachmentIds.Canonical(string)`, `AttachmentIds.Validate(IReadOnlyList<string?>?) : string?`; `AttachmentTrailer.Prefix`, `AttachmentTrailer.For(IEnumerable<string>)`; `SendTextReasons.AttachmentsRefused = "attachments_refused"`.

- [ ] **Step 1: Write the failing tests**

Append to `InputWireContractsTests.cs`:

```csharp
    [Test]
    public async Task Send_text_with_attachments_serializes_snake_case_with_every_member() =>
        await Assert.That(JsonSerializer.Serialize(
                new SendTextWithAttachmentsDto("a1", "hello", ["0123456789abcdef0123456789abcdef"]),
                InputIpcJsonContext.Default.SendTextWithAttachmentsDto))
            .IsEqualTo("""{"agent_id":"a1","text":"hello","attachment_ids":["0123456789abcdef0123456789abcdef"]}""");

    [Test]
    [Arguments("{}")]
    [Arguments("""{"agent_id":"a1","text":"x"}""")]
    [Arguments("""{"agent_id":"a1","text":"x","attachment_ids":null}""")]
    public async Task Attachment_frame_without_ids_is_structurally_invalid(string json) {
        var dto = JsonSerializer.Deserialize(json, InputIpcJsonContext.Default.SendTextWithAttachmentsDto);
        await Assert.That(InputWire.IsStructurallyValid(dto)).IsFalse();
    }

    [Test]
    [Arguments("0123456789abcdef0123456789abcdef", true)]
    [Arguments("0123456789ABCDEF0123456789ABCDEF", true)]
    [Arguments("0123456789abcdef0123456789abcde", false)]
    [Arguments("0123456789abcdef0123456789abcdef0", false)]
    [Arguments("01234567-89ab-cdef-0123-456789abcdef", false)]
    [Arguments("../etc/passwd", false)]
    [Arguments("", false)]
    [Arguments(null, false)]
    public async Task Attachment_id_is_a_guid_n(string? id, bool valid) =>
        await Assert.That(InputWire.IsValidAttachmentId(id)).IsEqualTo(valid);
```

Create `AttachmentIdsTests.cs`:

```csharp
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

/// One definition of an acceptable id list: count, syntax, distinctness under Canonical.
public class AttachmentIdsTests {
    const string A = "0123456789abcdef0123456789abcdef";
    const string B = "fedcba9876543210fedcba9876543210";

    [Test]
    public async Task Null_and_empty_lists_are_acceptable() {
        await Assert.That(AttachmentIds.Validate(null)).IsNull();
        await Assert.That(AttachmentIds.Validate([])).IsNull();
    }

    [Test]
    public async Task Ten_distinct_ids_are_acceptable_and_eleven_are_not() {
        var ten = Enumerable.Range(0, 10).Select(i => Guid.NewGuid().ToString("N")).ToArray();
        await Assert.That(AttachmentIds.Validate(ten)).IsNull();
        await Assert.That(AttachmentIds.Validate([.. ten, Guid.NewGuid().ToString("N")]))
            .IsEqualTo("up to 10 attachments per message");
    }

    [Test]
    public async Task A_malformed_or_null_element_is_refused() {
        await Assert.That(AttachmentIds.Validate([A, "nope"])).IsEqualTo("malformed attachment id");
        await Assert.That(AttachmentIds.Validate([A, null])).IsEqualTo("malformed attachment id");
    }

    [Test]
    public async Task Ids_equal_under_canonical_form_are_a_duplicate() {
        await Assert.That(AttachmentIds.Validate([A, A.ToUpperInvariant()])).IsEqualTo("duplicate attachment id");
        await Assert.That(AttachmentIds.Validate([A, B])).IsNull();
        await Assert.That(AttachmentIds.Canonical(A.ToUpperInvariant())).IsEqualTo(A);
    }
}
```

Create `AttachmentTrailerTests.cs`:

```csharp
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class AttachmentTrailerTests {
    [Test]
    public async Task Shapes_one_two_and_many_paths_behind_the_prefix() {
        await Assert.That(AttachmentTrailer.For([".attached/b1/a.png"])).IsEqualTo("[Attached files: .attached/b1/a.png]");
        await Assert.That(AttachmentTrailer.For(["/x/a.png", "/x/b.pdf"])).IsEqualTo("[Attached files: /x/a.png, /x/b.pdf]");
        await Assert.That(AttachmentTrailer.For(["a", "b", "c"])).StartsWith(AttachmentTrailer.Prefix);
        await Assert.That(AttachmentTrailer.Prefix).IsEqualTo("[Attached files: ");
    }
}
```

Add to the codec tests (find the existing `FrameCodec` round-trip test class under `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/`; create `FrameCodecAttachmentFrameTests.cs` if none exists):

```csharp
    [Test]
    public async Task Send_text_with_attachments_frame_round_trips_and_is_24() {
        await Assert.That((byte)FrameType.SendTextWithAttachments).IsEqualTo((byte)24);
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, LocalFrame.InputJson(FrameType.SendTextWithAttachments, """{"agent_id":"a"}"""), CancellationToken.None);
        ms.Position = 0;
        var read = await FrameCodec.ReadAsync(ms, CancellationToken.None);
        await Assert.That(read!.Type).IsEqualTo(FrameType.SendTextWithAttachments);
        await Assert.That(read.Text).IsEqualTo("""{"agent_id":"a"}""");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/InputWireContractsTests/*'`
Expected: compile errors naming `SendTextWithAttachmentsDto`, `IsValidAttachmentId`, `AttachmentIds`, `AttachmentTrailer`, `FrameType.SendTextWithAttachments`.

- [ ] **Step 3: Implement**

`FrameType.cs` — after `DaemonSettingsPut = 23,` add:

```csharp
    // Composer input with attachments — one-shot; acked on SendTextAck when the delivery settles.
    SendTextWithAttachments = 24, // Text = SendTextWithAttachmentsDto JSON
```

`FrameCodec.cs` — in both lists, change `or FrameType.SendText or FrameType.SendTextAck` to `or FrameType.SendText or FrameType.SendTextAck or FrameType.SendTextWithAttachments`.

`InputIpc.cs` — add after `SendTextDto`:

```csharp
/// The attachment-bearing composer frame. Its own frame type rather than a trailing member on
/// SendTextDto: an older decoder would ignore the member and deliver the text without the files.
public sealed record SendTextWithAttachmentsDto(string AgentId, string Text, string[] AttachmentIds);
```

In `SendTextReasons` add `public const string AttachmentsRefused = "attachments_refused";` (Error names why). In `InputWire` add:

```csharp
    public const int  MaxAttachmentsPerPrompt = 10;
    /// The server's own per-file cap.
    public const long MaxAttachmentBytes      = 10L * 1024 * 1024;

    /// The server mints Guid "N": 32 hex digits, accepted in either case.
    public static bool IsValidAttachmentId(string? id) => id is { Length: 32 } && Guid.TryParseExact(id, "N", out _);

    public static bool IsStructurallyValid(SendTextWithAttachmentsDto? dto) =>
        dto is not null && dto.AgentId is not null && dto.Text is not null && dto.AttachmentIds is not null;
```

Add `[JsonSerializable(typeof(SendTextWithAttachmentsDto))]` to `InputIpcJsonContext`.

Create `AttachmentIds.cs`:

```csharp
namespace Capacitor.Cli.Core.LocalIpc;

/// The one definition of an acceptable attachment id list, run by the uploader over a server
/// response and by the daemon in front of every fetch, whichever lane the ids arrived on.
public static class AttachmentIds {
    public static string Canonical(string id) => id.ToLowerInvariant();

    /// Null when the list is acceptable; otherwise the refusal wording.
    public static string? Validate(IReadOnlyList<string?>? ids) {
        if (ids is null || ids.Count == 0) return null;
        if (ids.Count > InputWire.MaxAttachmentsPerPrompt) return $"up to {InputWire.MaxAttachmentsPerPrompt} attachments per message";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids) {
            if (!InputWire.IsValidAttachmentId(id)) return "malformed attachment id";
            if (!seen.Add(Canonical(id!))) return "duplicate attachment id";
        }
        return null;
    }
}
```

Create `AttachmentTrailer.cs`:

```csharp
namespace Capacitor.Cli.Core.LocalIpc;

/// The line the daemon appends to a prompt to name delivered files. The app recognises a
/// transcript turn by it, so the shape is defined once, here.
public static class AttachmentTrailer {
    public const string Prefix = "[Attached files: ";

    public static string For(IEnumerable<string> paths) => Prefix + string.Join(", ", paths) + "]";
}
```

- [ ] **Step 4: Run the Core suite**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj`
Expected: all green.

- [ ] **Step 5: Commit**

`Add the attachment input frame and id contract to the local wire`

---

### Task 2: Core client — `SendTextWithAttachmentsAsync`

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/LocalControlOps.cs` (interface `ILocalControlOps` and the class; model on `SendTextAsync` at the `// The ack lands only when…` comment)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/LocalControlOpsTests.cs` (the `// ---- SendTextAsync ----` region shows the socket harness)
- Modify: `test/Capacitor.App.Tests.Unit/ScriptedLocalControlOps.cs` (implement the new member so the App suite keeps compiling)

**Interfaces:**
- Produces: `Task<SendTextResult> SendTextWithAttachmentsAsync(string agentId, string text, IReadOnlyList<string> attachmentIds, CancellationToken ct)` on `ILocalControlOps`.

- [ ] **Step 1: Write the failing tests**

Beside the existing `SendTextAsync` tests, using the same fake-daemon socket helper the file already has:

```csharp
    [Test]
    public async Task Send_text_with_attachments_sends_frame_24_with_ids_and_reads_the_ack() {
        await using var daemon = new FakeDaemon(async (frame, stream) => {
            await Assert.That(frame.Type).IsEqualTo(FrameType.SendTextWithAttachments);
            await Assert.That(frame.Text).IsEqualTo("""{"agent_id":"a1","text":"hi","attachment_ids":["0123456789abcdef0123456789abcdef"]}""");
            await FrameCodec.WriteAsync(stream, LocalFrame.InputJson(FrameType.SendTextAck,
                """{"ok":true,"reason":null,"error":null,"outcome":"delivered"}"""), CancellationToken.None);
        });
        var ops = daemon.Ops();
        var result = await ops.SendTextWithAttachmentsAsync("a1", "hi", ["0123456789abcdef0123456789abcdef"], CancellationToken.None);
        await Assert.That(result.Ok).IsTrue();
        await Assert.That(result.Outcome).IsEqualTo(SendTextOutcomes.Delivered);
    }

    [Test]
    public async Task Send_text_with_attachments_maps_eof_to_transport() {
        await using var daemon = new FakeDaemon((_, stream) => { stream.Close(); return Task.CompletedTask; });
        var result = await daemon.Ops().SendTextWithAttachmentsAsync("a1", "hi", ["0123456789abcdef0123456789abcdef"], CancellationToken.None);
        await Assert.That(result.Ok).IsFalse();
        await Assert.That(result.Reason).IsEqualTo(SendTextReasons.Transport);
    }
```

(Adapt `FakeDaemon`/`Ops()` to whatever names the file's existing SendText tests use for the listener and the `LocalControlOps` under test — read lines 20–60 and 640–710 of the file first.)

- [ ] **Step 2: Run to verify failure** — compile error on `SendTextWithAttachmentsAsync`.

- [ ] **Step 3: Implement**

Interface: add `Task<SendTextResult> SendTextWithAttachmentsAsync(string agentId, string text, IReadOnlyList<string> attachmentIds, CancellationToken ct);` after `SendTextAsync`.

Class: refactor the body of `SendTextAsync` into a private `ExchangeSendTextAsync(LocalFrame request, CancellationToken ct)` returning `SendTextResult` (the existing `switch` over `reply.Type`), and add:

```csharp
    public Task<SendTextResult> SendTextAsync(string agentId, string text, CancellationToken ct) =>
        ExchangeSendTextAsync(LocalFrame.InputJson(FrameType.SendText,
            JsonSerializer.Serialize(new SendTextDto(agentId, text), InputIpcJsonContext.Default.SendTextDto)), ct);

    public Task<SendTextResult> SendTextWithAttachmentsAsync(string agentId, string text, IReadOnlyList<string> attachmentIds, CancellationToken ct) =>
        ExchangeSendTextAsync(LocalFrame.InputJson(FrameType.SendTextWithAttachments,
            JsonSerializer.Serialize(new SendTextWithAttachmentsDto(agentId, text, [.. attachmentIds]), InputIpcJsonContext.Default.SendTextWithAttachmentsDto)), ct);
```

`ScriptedLocalControlOps` (App tests): add `public readonly List<(string AgentId, string Text, IReadOnlyList<string> Ids)> SendTextWithAttachmentsPayloads = [];`, `public int SendTextWithAttachmentsCalls;`, and

```csharp
    public Task<SendTextResult> SendTextWithAttachmentsAsync(string agentId, string text, IReadOnlyList<string> attachmentIds, CancellationToken ct) {
        SendTextWithAttachmentsCalls++;
        SendTextWithAttachmentsPayloads.Add((agentId, text, attachmentIds));
        if (ct.IsCancellationRequested) return Task.FromCanceled<SendTextResult>(ct);
        var tcs = _sendTexts.Count > 0 ? _sendTexts.Dequeue() : throw new InvalidOperationException("arm SendText first");
        return tcs.Task.WaitAsync(ct);
    }
```

Any other `ILocalControlOps` implementation in `src/` or `test/` (grep `: ILocalControlOps`) gets the same member.

- [ ] **Step 4: Run Core and App suites** — green.
- [ ] **Step 5: Commit** — `Send attachment ids over the local control socket`

---

### Task 3: Daemon placement — enum, factory member, Codex override, recorded on the agent

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/AttachmentPlacement.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntimeFactory.cs`
- Modify: `src/Capacitor.Cli.Daemon/Harness/Codex/CodexHostedAgentRuntimeFactory.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` — `AgentInstance` gets `public AttachmentPlacement Placement { get; init; } = AttachmentPlacement.Worktree;`; the server launch path sets it from `runtimeFactory.AttachmentPlacementFor(cmd.Kind)` in the `new AgentInstance(...) { … }` initializer after `LogAgentSpawned`.
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs` — `HandleLocalSpawnAsync` sets `Placement = _runtimeFactories.TryGetValue(vendor, out var f) ? f.AttachmentPlacementFor(LaunchKind.Default) : AttachmentPlacement.Worktree` (find the orchestrator's runtime-factory registry field; it is the one `HandleLaunchAgentCore` reads to pick `runtimeFactory`).
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AttachmentPlacementTests.cs`

**Interfaces:**
- Produces: `internal enum AttachmentPlacement { Worktree, DaemonStore }`; `AttachmentPlacement IHostedAgentRuntimeFactory.AttachmentPlacementFor(LaunchKind kind) => AttachmentPlacement.Worktree;`; `AgentInstance.Placement`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// A write-contained runtime that accepts follow-ups is the one case a fetch into the worktree
/// could be steered; only default-kind Codex is that case today.
public class AttachmentPlacementTests {
    [Test]
    public async Task Codex_uses_the_daemon_store_for_default_kind_only() {
        var codex = CodexRuntimeFactoryFixture.Build(); // build the way CodexHostedAgentRuntimeFactory tests already do
        await Assert.That(codex.AttachmentPlacementFor(LaunchKind.Default)).IsEqualTo(AttachmentPlacement.DaemonStore);
        await Assert.That(codex.AttachmentPlacementFor(LaunchKind.Review)).IsEqualTo(AttachmentPlacement.Worktree);
        await Assert.That(codex.AttachmentPlacementFor(LaunchKind.ReviewFlow)).IsEqualTo(AttachmentPlacement.Worktree);
    }

    [Test]
    public async Task Every_other_factory_keeps_the_worktree_default() {
        IHostedAgentRuntimeFactory acp = new FakeRuntimeFactory("cursor"); // any test factory that does not override
        await Assert.That(acp.AttachmentPlacementFor(LaunchKind.Default)).IsEqualTo(AttachmentPlacement.Worktree);
    }

    [Test]
    public async Task Local_spawn_records_the_factory_answer_on_the_agent() {
        // Spawn codex and claude through HandleLocalSpawnAsync with the harness's SpyPtyProcessFactory
        // (see AgentOrchestratorLocalAttachTests for the Spawn frame + stream setup) and assert
        // orch.AgentsForTest["…"].Placement: codex → DaemonStore, claude → Worktree.
    }
}
```

Write the third test fully against the local-attach harness: the existing `AgentOrchestratorLocalAttachTests` shows how to drive `HandleLocalSpawnAsync` with `FrameCodec.Spawn(...)` and read the registered agent; add a `codex` launcher stub to the launchers dictionary if only `claude` is registered there. For the Codex factory fixture, reuse whatever construction `CodexHostedAgentRuntimeFactory`'s own tests use (grep `new CodexHostedAgentRuntimeFactory(` under the daemon test project).

- [ ] **Step 2: Run to verify failure** — compile errors on `AttachmentPlacement`/`AttachmentPlacementFor`/`Placement`.

- [ ] **Step 3: Implement**

`AttachmentPlacement.cs`:

```csharp
namespace Capacitor.Cli.Daemon.Services;

/// Where a fetched attachment lands for an agent: inside its worktree (relative trailer paths, what
/// a workspace-confined file tool reads) or in a daemon-owned directory outside every cwd (absolute
/// paths, for a runtime whose OS sandbox stops it writing there — so it cannot steer the write).
internal enum AttachmentPlacement { Worktree, DaemonStore }
```

`IHostedAgentRuntimeFactory.cs` — add before `StartAsync`:

```csharp
    /// <summary>Where this runtime's attachments land for a launch of <paramref name="kind"/>. The
    /// question is whether the process can be running, write-contained, while a fetch for it happens:
    /// protected kinds refuse follow-ups, so their only fetch precedes the process and the worktree is
    /// safe for them.</summary>
    AttachmentPlacement AttachmentPlacementFor(LaunchKind kind) => AttachmentPlacement.Worktree;
```

`CodexHostedAgentRuntimeFactory.cs`:

```csharp
    public AttachmentPlacement AttachmentPlacementFor(LaunchKind kind) =>
        kind == LaunchKind.Default ? AttachmentPlacement.DaemonStore : AttachmentPlacement.Worktree;
```

`AgentInstance`: add `public AttachmentPlacement Placement { get; init; } = AttachmentPlacement.Worktree;`. Set it in both `new AgentInstance(...)` initializers named above.

- [ ] **Step 4: Run the daemon suite filter** `/*/*/AttachmentPlacementTests/*` — green.
- [ ] **Step 5: Commit** — `Let each runtime factory place attachments by launch kind`

---

### Task 4: `AttachmentStore` — daemon-owned directory, removal, lease, orphan sweep

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/AttachmentStore.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` — construct `_attachmentStore = new AttachmentStore(recordRoot)` beside `_pidRecordRoot = recordRoot;` (line ~712); in `CleanupAgentAsync`, after the worktree removal block: `try { _attachmentStore.Remove(agentId); } catch (Exception ex) { LogCleanupStepFailed(ex, "removing attachments", agentId); }`; add `internal bool IsLiveAttachmentStem(string stem) => _agents.Keys.Any(id => AgentFileNames.For(id) == stem);`; expose `internal AttachmentStore AttachmentStoreForTest => _attachmentStore;`.
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs` — after the host is built, before the daemon connects: `host.Services.GetRequiredService<AgentOrchestrator>().AttachmentStoreForTest.SweepOrphans(orch.IsLiveAttachmentStem, logger)` (name the property `AttachmentStore`, not `…ForTest`, if it is used in production).
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AttachmentStoreTests.cs`

**Interfaces:**
- Produces:

```csharp
internal sealed class AttachmentStore(string stateDir) {
    public string Root { get; }                                   // <stateDir>/attachments
    public string DirectoryFor(string agentId);                   // Root/<AgentFileNames.For(agentId)>
    public void Remove(string agentId);                           // DeleteTreeNoFollow; absent is fine
    public void SweepOrphans(Func<string, bool> isLive, ILogger logger); // dirs whose stem is not live, and every .pending-* anywhere under Root
    public IDisposable Lease(string agentId);                     // Dispose → Remove unless Keep() was called
}
internal sealed class AttachmentStoreLease : IDisposable { public void Keep(); }
```

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class AttachmentStoreTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Directory_is_hashed_under_the_state_dir_and_removed_without_following_links() {
        var store = new AttachmentStore(Tmp.Path);
        var dir = store.DirectoryFor("agent-1");
        await Assert.That(dir).IsEqualTo(Path.Combine(Tmp.Path, "attachments", AgentFileNames.For("agent-1")));
        Directory.CreateDirectory(dir);
        var outside = Tmp.CreateDir("outside");
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "x");
        File.CreateSymbolicLink(Path.Combine(dir, "link"), outside);
        store.Remove("agent-1");
        await Assert.That(Directory.Exists(dir)).IsFalse();
        await Assert.That(File.Exists(Path.Combine(outside, "keep.txt"))).IsTrue();
        store.Remove("agent-1"); // absent is fine
    }

    [Test]
    public async Task Sweep_removes_orphan_directories_and_stale_staging_only() {
        var store = new AttachmentStore(Tmp.Path);
        var live = store.DirectoryFor("live"); Directory.CreateDirectory(live);
        var dead = store.DirectoryFor("dead"); Directory.CreateDirectory(dead);
        var pending = Path.Combine(live, ".pending-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(pending);
        var published = Path.Combine(live, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(published);
        store.SweepOrphans(stem => stem == AgentFileNames.For("live"), NullLogger.Instance);
        await Assert.That(Directory.Exists(live)).IsTrue();
        await Assert.That(Directory.Exists(published)).IsTrue();
        await Assert.That(Directory.Exists(pending)).IsFalse();
        await Assert.That(Directory.Exists(dead)).IsFalse();
    }

    [Test]
    public async Task Lease_removes_on_dispose_unless_kept() {
        var store = new AttachmentStore(Tmp.Path);
        using (var lease = store.Lease("a")) { Directory.CreateDirectory(store.DirectoryFor("a")); }
        await Assert.That(Directory.Exists(store.DirectoryFor("a"))).IsFalse();
        using (var lease = store.Lease("b")) { Directory.CreateDirectory(store.DirectoryFor("b")); lease.Keep(); }
        await Assert.That(Directory.Exists(store.DirectoryFor("b"))).IsTrue();
    }
}
```

- [ ] **Step 2: Run to verify failure** — `AttachmentStore` undefined.

- [ ] **Step 3: Implement**

```csharp
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// Per-agent attachment directories under the daemon's state dir, for runtimes whose placement is
/// DaemonStore. Named by the same hash as the PID record and journal: the agent id crosses the wire
/// unconstrained, so it is never a path segment.
internal sealed class AttachmentStore(string stateDir) {
    public string Root => Path.Combine(stateDir, "attachments");

    public string DirectoryFor(string agentId) => Path.Combine(Root, AgentFileNames.For(agentId));

    public void Remove(string agentId) {
        var dir = DirectoryFor(agentId);
        if (Directory.Exists(dir)) WorktreeManager.DeleteTreeNoFollow(dir);
    }

    public AttachmentStoreLease Lease(string agentId) => new(this, agentId);

    /// Startup only: a directory whose agent is not live, and any staging directory left by a crash.
    public void SweepOrphans(Func<string, bool> isLive, ILogger logger) {
        if (!Directory.Exists(Root)) return;
        foreach (var dir in Directory.EnumerateDirectories(Root)) {
            try {
                var stem = Path.GetFileName(dir);
                if (!isLive(stem)) { WorktreeManager.DeleteTreeNoFollow(dir); continue; }
                foreach (var pending in Directory.EnumerateDirectories(dir, ".pending-*"))
                    WorktreeManager.DeleteTreeNoFollow(pending);
            } catch (Exception ex) {
                logger.LogWarning(ex, "Attachment store sweep: skipped {Dir}", dir);
            }
        }
    }
}
```

`AttachmentStoreLease.cs`:

```csharp
namespace Capacitor.Cli.Daemon.Services;

/// Scoped ownership of an agent's store directory across a launch: disposed without Keep() it
/// removes the directory, so every failure exit between the fetch and registration cleans up.
internal sealed class AttachmentStoreLease(AttachmentStore store, string agentId) : IDisposable {
    bool _keep;
    public void Keep() => _keep = true;
    public void Dispose() { if (!_keep) store.Remove(agentId); }
}
```

`WorktreeManager.DeleteTreeNoFollow` is currently private/static in `WorktreeManager.cs` (line ~363); make it `internal static`.

- [ ] **Step 4: Run** `/*/*/AttachmentStoreTests/*` and the whole daemon suite — green.
- [ ] **Step 5: Commit** — `Add a daemon-owned attachment store with lease and orphan sweep`

---

### Task 5: `AttachmentFetcher` — staged batch, streamed cap, one rename

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/AttachmentBatch.cs`, `AttachmentFetch.cs`, `AttachmentFetcher.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` — delete `DownloadAttachmentsAsync` and `GetUniqueFilePath`; construct `_attachmentFetcher = new AttachmentFetcher(_httpClientFactory, () => _tokens.GetValidTokensForServerAsync(_config.Profiles.Name, _config.ServerUrl), _logger)` in the constructor; callers are rewritten in Tasks 6 and 7 (this task leaves them calling the new fetcher with the old best-effort semantics so the build stays green).
- Modify: `test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj` — add `<PackageReference Include="WireMock.Net" />`.
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AttachmentFetcherTests.cs`

**Interfaces:**
- Produces:

```csharp
internal sealed class AttachmentBatch : IDisposable {
    public IReadOnlyList<string> Paths { get; }   // trailer form: relative for Worktree, absolute for DaemonStore
    public string Directory { get; }              // the published batch directory
    public void Rollback();                       // deletes Directory; idempotent
    public void Dispose();                        // deletes the staging dir if never published
}
internal sealed record AttachmentFetch(AttachmentBatch? Batch, string? FailedId, string? Error);
internal sealed class AttachmentFetcher(IHttpClientFactory http, Func<Task<TokenResolution>> tokens, ILogger logger) {
    /// destinationRoot: <cwd>/.attached (Worktree) or store.DirectoryFor(agentId) (DaemonStore).
    Task<AttachmentFetch> FetchAsync(string destinationRoot, AttachmentPlacement placement, IReadOnlyList<string> ids, CancellationToken ct);
}
```

(`TokenResolution` is whatever `_tokens.GetValidTokensForServerAsync` returns; read `TokenStore` for the type name and use it.)

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// A batch lands whole or not at all, never buffers more than the cap, and never leaves a file behind
/// when it fails.
public class AttachmentFetcherTests : IDisposable {
    [TempDir] public required TempDir Tmp { get; init; }
    readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Dispose();

    sealed class Factory(string baseUrl) : IHttpClientFactory {
        public HttpClient CreateClient(string name) => new() { BaseAddress = new Uri(baseUrl) };
    }

    AttachmentFetcher Fetcher() => new(new Factory(_server.Url!), () => Task.FromResult(AuthFixtures.NoTokens()), NullLogger.Instance);

    static string Id(int n) => new((char)('a' + n), 32);

    void Serve(string id, byte[] body, string name = "f.png") =>
        _server.Given(Request.Create().WithPath($"/api/attachments/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(body)
                .WithHeader("Content-Disposition", $"attachment; filename=\"{name}\""));

    [Test]
    public async Task Success_publishes_one_batch_directory_by_rename_and_leaves_no_staging() {
        Serve(Id(0), [1, 2, 3], "a.png"); Serve(Id(1), [4], "b.pdf");
        var root = Tmp.PathTo(".attached");
        var fetch = await Fetcher().FetchAsync(root, AttachmentPlacement.Worktree, [Id(0), Id(1)], CancellationToken.None);
        await Assert.That(fetch.Batch).IsNotNull();
        await Assert.That(fetch.Batch!.Paths).IsEquivalentTo([$".attached/{Path.GetFileName(fetch.Batch.Directory)}/a.png", $".attached/{Path.GetFileName(fetch.Batch.Directory)}/b.pdf"]);
        await Assert.That(Directory.GetDirectories(root, ".pending-*")).IsEmpty();
        await Assert.That(File.ReadAllBytes(Path.Combine(fetch.Batch.Directory, "a.png"))).IsEquivalentTo([1, 2, 3]);
        await Assert.That(File.Exists(Path.Combine(root, ".gitignore"))).IsTrue();
    }

    [Test]
    public async Task Daemon_store_placement_reports_absolute_paths() {
        Serve(Id(0), [1]);
        var root = Tmp.PathTo("store");
        var fetch = await Fetcher().FetchAsync(root, AttachmentPlacement.DaemonStore, [Id(0)], CancellationToken.None);
        await Assert.That(Path.IsPathRooted(fetch.Batch!.Paths[0])).IsTrue();
        await Assert.That(File.Exists(Path.Combine(root, ".gitignore"))).IsFalse();
    }

    [Test]
    public async Task A_failed_id_leaves_no_new_file_or_directory_and_earlier_batches_alone() {
        Serve(Id(0), [1]);
        var root = Tmp.PathTo(".attached");
        var first = await Fetcher().FetchAsync(root, AttachmentPlacement.Worktree, [Id(0)], CancellationToken.None);
        var before = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories);
        var fetch = await Fetcher().FetchAsync(root, AttachmentPlacement.Worktree, [Id(0), Id(2)], CancellationToken.None); // Id(2) → 404
        await Assert.That(fetch.Batch).IsNull();
        await Assert.That(fetch.FailedId).IsEqualTo(Id(2));
        await Assert.That(Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)).IsEquivalentTo(before);
        await Assert.That(Directory.Exists(first.Batch!.Directory)).IsTrue();
    }

    [Test]
    public async Task Oversize_by_header_and_by_chunked_body_are_refused_and_reading_stops_at_the_cap() {
        _server.Given(Request.Create().WithPath($"/api/attachments/{Id(0)}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Length", (InputWire.MaxAttachmentBytes + 1).ToString()).WithBody(new byte[16]));
        var byHeader = await Fetcher().FetchAsync(Tmp.PathTo("a"), AttachmentPlacement.Worktree, [Id(0)], CancellationToken.None);
        await Assert.That(byHeader.Batch).IsNull();
        await Assert.That(byHeader.Error).Contains("over");

        // Chunked over-cap body through a counting handler: the fetcher must stop reading at cap + 1.
        var counting = new CountingHandler(InputWire.MaxAttachmentBytes * 3);
        var fetcher = new AttachmentFetcher(new HandlerFactory(counting), () => Task.FromResult(AuthFixtures.NoTokens()), NullLogger.Instance);
        var byBody = await fetcher.FetchAsync(Tmp.PathTo("b"), AttachmentPlacement.Worktree, [Id(0)], CancellationToken.None);
        await Assert.That(byBody.Batch).IsNull();
        await Assert.That(counting.BytesRead).IsLessThanOrEqualTo(InputWire.MaxAttachmentBytes + 1 + 65536);
        await Assert.That(Directory.Exists(Tmp.PathTo("b"))).IsFalse().Or.IsTrue(); // root may exist; no files may
        await Assert.That(Directory.Exists(Tmp.PathTo("b")) ? Directory.GetFiles(Tmp.PathTo("b"), "*", SearchOption.AllDirectories) : []).IsEmpty();
    }

    [Test]
    public async Task Empty_disposition_name_is_refused_and_duplicate_names_in_a_batch_are_distinct() {
        Serve(Id(0), [1], "same.txt"); Serve(Id(1), [2], "same.txt");
        var ok = await Fetcher().FetchAsync(Tmp.PathTo("d"), AttachmentPlacement.Worktree, [Id(0), Id(1)], CancellationToken.None);
        await Assert.That(ok.Batch!.Paths.Select(Path.GetFileName)).IsEquivalentTo(["same.txt", "same-2.txt"]);
        Serve(Id(3), [1], "..");
        var bad = await Fetcher().FetchAsync(Tmp.PathTo("e"), AttachmentPlacement.Worktree, [Id(3)], CancellationToken.None);
        await Assert.That(bad.Batch).IsNull();
    }

    [Test]
    public async Task Rollback_deletes_the_published_batch_and_nothing_else() {
        Serve(Id(0), [1]);
        var root = Tmp.PathTo(".attached");
        var a = await Fetcher().FetchAsync(root, AttachmentPlacement.Worktree, [Id(0)], CancellationToken.None);
        var b = await Fetcher().FetchAsync(root, AttachmentPlacement.Worktree, [Id(0)], CancellationToken.None);
        b.Batch!.Rollback();
        await Assert.That(Directory.Exists(b.Batch.Directory)).IsFalse();
        await Assert.That(Directory.Exists(a.Batch!.Directory)).IsTrue();
        b.Batch.Rollback(); // idempotent
    }

    [Test]
    public async Task Stale_staging_directory_is_removed_by_the_next_fetch() {
        Serve(Id(0), [1]);
        var root = Tmp.CreateDir(".attached");
        var stale = Directory.CreateDirectory(Path.Combine(root, ".pending-" + new string('0', 32))).FullName;
        await Fetcher().FetchAsync(root, AttachmentPlacement.Worktree, [Id(0)], CancellationToken.None);
        await Assert.That(Directory.Exists(stale)).IsFalse();
    }
}
```

Add the two test doubles in their own files under `test/Capacitor.Cli.Daemon.Tests.Unit/Services/`: `CountingHandler : HttpMessageHandler` that answers any GET with a chunked (`Content-Length` absent) stream of `totalBytes` bytes and counts bytes actually read through a wrapping stream into `BytesRead`; `HandlerFactory(HttpMessageHandler) : IHttpClientFactory` returning `new HttpClient(handler) { BaseAddress = new Uri("http://attachments.test") }`. `AuthFixtures.NoTokens()` — if no such helper exists, return the `TokenResolution` value the orchestrator treats as "no token" (read `TokenStore.GetValidTokensForServerAsync`'s return type and construct the anonymous/no-token case).

- [ ] **Step 2: Run to verify failure** — compile errors on the new types.

- [ ] **Step 3: Implement**

`AttachmentBatch.cs`:

```csharp
namespace Capacitor.Cli.Daemon.Services;

/// One fetched batch. Until Publish() it is a staging directory that Dispose deletes; after it,
/// Rollback() deletes the published directory so a refused delivery leaves nothing behind.
internal sealed class AttachmentBatch(string stagingDirectory, string publishedDirectory, IReadOnlyList<string> paths) : IDisposable {
    bool _published;
    bool _gone;

    public IReadOnlyList<string> Paths { get; } = paths;
    public string Directory => publishedDirectory;

    internal void Publish() {
        System.IO.Directory.Move(stagingDirectory, publishedDirectory);
        _published = true;
    }

    public void Rollback() {
        if (_gone) return;
        _gone = true;
        var dir = _published ? publishedDirectory : stagingDirectory;
        if (System.IO.Directory.Exists(dir)) WorktreeManager.DeleteTreeNoFollow(dir);
    }

    public void Dispose() { if (!_published) Rollback(); }
}
```

`AttachmentFetch.cs`:

```csharp
namespace Capacitor.Cli.Daemon.Services;

/// The outcome of one batch fetch: a published batch, or the id that ended it and why.
internal sealed record AttachmentFetch(AttachmentBatch? Batch, string? FailedId, string? Error);
```

`AttachmentFetcher.cs` (the body of the old `DownloadAttachmentsAsync`, restructured):

```csharp
using System.Net.Http.Headers;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

internal sealed class AttachmentFetcher(IHttpClientFactory http, Func<Task<TokenResolution>> tokens, ILogger logger) {
    const int CopyBuffer = 64 * 1024;

    public async Task<AttachmentFetch> FetchAsync(string destinationRoot, AttachmentPlacement placement, IReadOnlyList<string> ids, CancellationToken ct) {
        Directory.CreateDirectory(destinationRoot);
        if (placement == AttachmentPlacement.Worktree) {
            var gitignore = Path.Combine(destinationRoot, ".gitignore");
            if (!File.Exists(gitignore)) await File.WriteAllTextAsync(gitignore, "*\n", ct);
        }
        foreach (var stale in Directory.EnumerateDirectories(destinationRoot, ".pending-*")) {
            try { WorktreeManager.DeleteTreeNoFollow(stale); } catch (Exception ex) { logger.LogWarning(ex, "Attachment staging cleanup skipped {Dir}", stale); }
        }

        var batchId   = Guid.NewGuid().ToString("N");
        var staging   = Path.Combine(destinationRoot, ".pending-" + batchId);
        var published = Path.Combine(destinationRoot, batchId);
        Directory.CreateDirectory(staging);
        var paths = new List<string>(ids.Count);
        var batch = new AttachmentBatch(staging, published, paths);

        try {
            foreach (var id in ids) {
                var (fileName, error) = await FetchOneAsync(id, staging, ct);
                if (error is not null) { batch.Dispose(); return new AttachmentFetch(null, id, error); }
                paths.Add(placement == AttachmentPlacement.Worktree
                    ? $"{Path.GetFileName(destinationRoot)}/{batchId}/{fileName}"
                    : Path.Combine(published, fileName!));
            }
            batch.Publish();
            return new AttachmentFetch(batch, null, null);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            batch.Dispose();
            return new AttachmentFetch(null, ids.Count > paths.Count ? ids[paths.Count] : null, ex.Message);
        }
    }

    async Task<(string? FileName, string? Error)> FetchOneAsync(string id, string staging, CancellationToken ct) {
        using var client = http.CreateClient("Attachments");
        var resolution = await tokens();
        if (resolution.Tokens is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", resolution.Tokens.AccessToken);

        using var response = await client.GetAsync($"/api/attachments/{id}", HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return (null, $"server answered {(int)response.StatusCode}");
        if (response.Content.Headers.ContentLength is > InputWire.MaxAttachmentBytes) return (null, "over the 10 MB cap");

        var raw = response.Content.Headers.ContentDisposition?.FileNameStar
               ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
               ?? $"attachment-{id[..8]}";
        var fileName = Path.GetFileName(raw);
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or "..") return (null, "attachment has no usable file name");

        var path = UniquePath(staging, fileName);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[CopyBuffer];
        long written = 0;
        while (true) {
            var want = (int)Math.Min(buffer.Length, InputWire.MaxAttachmentBytes + 1 - written);
            var read = await body.ReadAsync(buffer.AsMemory(0, want), ct);
            if (read == 0) break;
            written += read;
            if (written > InputWire.MaxAttachmentBytes) return (null, "over the 10 MB cap");
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return (Path.GetFileName(path), null);
    }

    static string UniquePath(string directory, string fileName) {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext  = Path.GetExtension(fileName);
        for (var n = 2; ; n++) {
            path = Path.Combine(directory, $"{stem}-{n}{ext}");
            if (!File.Exists(path)) return path;
        }
    }
}
```

(The over-cap early return happens with the `FileStream` still open inside `await using`; the partial file is then deleted with the staging directory by `batch.Dispose()`. Replace `TokenResolution` with the real type name.)

In `AgentOrchestrator`, replace the two `DownloadAttachmentsAsync` call sites with `_attachmentFetcher.FetchAsync(Path.Combine(root, ".attached"), AttachmentPlacement.Worktree, attachmentIds, _shutdownCts.Token)` and use `fetch.Batch?.Paths` where `paths` was used, keeping the current best-effort behaviour for now (Tasks 6/7 make them fail closed). Delete the old logger messages that became unused (`LogAttachmentNotFound`, `LogAttachmentPathEscape`, `LogAttachmentError`).

- [ ] **Step 4: Run** `/*/*/AttachmentFetcherTests/*` then the whole daemon suite — green.
- [ ] **Step 5: Commit** — `Fetch attachments as one staged batch published by a single rename`

---

### Task 6: Daemon delivery — frame 24 handler, pre-checks, rollback, `input/2`

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs` — `HandleLocalSendTextWithAttachmentsAsync`; refactor `AnswerSendTextAsync` into a shared core.
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` — `DeliverInputAsync` pre-checks and rollback.
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs` — route `FrameType.SendTextWithAttachments`; extend the `default:` error text.
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalControlCapabilities.cs` — append `"input/2"`; one sentence in the doc comment.
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalSendTextWithAttachmentsTests.cs` (new), `test/Capacitor.Cli.Daemon.Tests.Unit/Services/DeliverInputAttachmentsTests.cs` (new), the existing capabilities pin test (grep `"input/1"` in the daemon tests and add `"input/2"` to the expected list).

**Interfaces:**
- Consumes: `AttachmentIds.Validate`, `AttachmentTrailer.For`, `AttachmentFetcher.FetchAsync`, `AgentInstance.Placement`, `_attachmentStore.DirectoryFor`.
- Produces: `public Task HandleLocalSendTextWithAttachmentsAsync(string payload, Stream stream, CancellationToken ct)`.

- [ ] **Step 1: Write the failing tests**

`LocalSendTextWithAttachmentsTests.cs` — same harness as `LocalSendTextTests` (copy its `Build`, add a `Send` that calls `HandleLocalSendTextWithAttachmentsAsync`, and a `Payload(agentId, text, ids)` that serialises `SendTextWithAttachmentsDto`):

```csharp
    static string Id(int n) => new((char)('a' + n), 32);

    [Test]
    public async Task Empty_id_list_on_this_frame_is_malformed() {
        await using var orch = Build();
        var ack = await Send(orch, Payload("a1", "hi", []));
        await Assert.That(ack.Reason).IsEqualTo(SendTextReasons.Malformed);
    }

    [Test]
    public async Task Over_count_malformed_and_duplicate_ids_are_attachments_refused_before_the_core() {
        await using var orch = Build();
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "a1", new FakeAcpRuntime());
        await Assert.That((await Send(orch, Payload("a1", "hi", Enumerable.Range(0, 11).Select(Id).ToArray()))).Reason).IsEqualTo(SendTextReasons.AttachmentsRefused);
        await Assert.That((await Send(orch, Payload("a1", "hi", ["nope"]))).Reason).IsEqualTo(SendTextReasons.AttachmentsRefused);
        var dup = await Send(orch, Payload("a1", "hi", [Id(0), Id(0).ToUpperInvariant()]));
        await Assert.That(dup.Reason).IsEqualTo(SendTextReasons.AttachmentsRefused);
        await Assert.That(dup.Error).IsEqualTo("duplicate attachment id");
        await Assert.That(((FakeAcpRuntime)agent.Runtime).Prompts).IsEmpty();
    }

    [Test]
    public async Task Borrowed_cwd_with_worktree_placement_is_refused_and_a_daemon_store_agent_is_served() {
        await using var orch = Build(); // give the harness a WireMock-backed IHttpClientFactory serving Id(0)
        AgentOrchestratorHarness.SeedBorrowedAcpAgent(orch, "b1", new FakeAcpRuntime());
        var refused = await Send(orch, Payload("b1", "hi", [Id(0)]));
        await Assert.That(refused.Reason).IsEqualTo(SendTextReasons.AttachmentsRefused);
        await Assert.That(refused.Error).IsEqualTo("attachments need a daemon-owned worktree");

        var codex = AgentOrchestratorHarness.SeedBorrowedAcpAgent(orch, "c1", new FakeAcpRuntime());
        // seed with Placement = AttachmentPlacement.DaemonStore (extend the harness with an optional parameter)
        var served = await Send(orch, Payload("c1", "hi", [Id(0)]));
        await Assert.That(served.Ok).IsTrue();
        await Assert.That(((FakeAcpRuntime)codex.Runtime).Prompts.Single()).Contains(AttachmentTrailer.Prefix);
    }

    [Test]
    public async Task Quit_command_with_attachments_is_refused_without_stopping() {
        await using var orch = Build();
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "a1", new FakeAcpRuntime());
        var ack = await Send(orch, Payload("a1", "/quit", [Id(0)]));
        await Assert.That(ack.Reason).IsEqualTo(SendTextReasons.AttachmentsRefused);
        await Assert.That(ack.Error).IsEqualTo("a quit command takes no attachments");
        await Assert.That(agent.Status).IsEqualTo("Running");
    }
```

`DeliverInputAttachmentsTests.cs` — drives `orch.DeliverInputAsync(agent, text, ids)` directly (it is `internal`) with a WireMock factory:

```csharp
    [Test]
    public async Task Protected_kind_with_ids_is_dropped_before_any_fetch_and_text_only_still_flows() {
        // Review-kind ACP agent; assert outcome.Kind == Dropped, Reason == DeliveryFailed,
        // Error == "attachments are not accepted by a review participant", server received 0 requests;
        // then DeliverInputAsync(agent, "round 2", null) is Delivered.
    }

    [Test]
    public async Task Failed_fetch_is_a_delivery_failed_drop_naming_the_id_and_the_runtime_gets_nothing() { … Error contains the id … }

    [Test]
    public async Task Successful_fetch_delivers_text_blank_line_trailer_with_worktree_relative_paths() {
        // FakeAcpRuntime.Prompts.Single() == "hello\n\n[Attached files: .attached/<batch>/f.png]"
    }

    [Test]
    public async Task Refusals_after_the_fetch_roll_the_batch_back() {
        // (a) ClaimReap(agent) via SendInputBeforeWriteHookForTest → Dropped ReaperClaimedLate, no <batch> dir under .attached
        // (b) runtime whose SendUserInputAsync throws InputNotAdmittedException → QueueFull, no batch dir
        // (c) runtime whose SendUserInputAsync throws IOException → DeliveryFailed, no batch dir
        // (d) a runtime that succeeds → the batch dir remains
    }

    [Test]
    public async Task Server_caller_reports_only_the_reason_token() {
        // HandleSendInput with a bad id and a DispatchId → CaptureServerConnection.SendInputRejected has (dispatchId, agentId, "delivery_failed") and nothing else
    }
```

Write each with the harness's `SeedAcpAgent`/`FakeAcpRuntime` (read `FakeAcpRuntime` for how it records prompts and how to make its `SendUserInputAsync` throw; add a `Func<Exception?>` hook if it has none). Give `BuildOrchestrator` an optional `IHttpClientFactory? httpClientFactory` parameter so tests can pass a WireMock-backed factory (default stays `StubHttpClientFactory`).

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement**

`LocalControlCapabilities.Current`: `[…, "input/1", "input/2", "settings/1"]`; doc: `"input/2"` routes `SendTextWithAttachments` to `HandleLocalSendTextWithAttachmentsAsync`.

`LocalControlServer` switch: `case FrameType.SendTextWithAttachments: await orchestrator.HandleLocalSendTextWithAttachmentsAsync(first.Text, stream, ct); break;` and add `/SendTextWithAttachments` to the `default:` message.

`AgentOrchestrator.LocalIpc.cs`:

```csharp
    public Task HandleLocalSendTextAsync(string payload, Stream stream, CancellationToken ct) =>
        WriteSendTextAckAsync(AnswerSendTextAsync(payload), stream, ct);

    public Task HandleLocalSendTextWithAttachmentsAsync(string payload, Stream stream, CancellationToken ct) =>
        WriteSendTextAckAsync(AnswerSendTextWithAttachmentsAsync(payload), stream, ct);

    async Task WriteSendTextAckAsync(Task<SendTextAckDto> answering, Stream stream, CancellationToken ct) {
        var ack = await answering;
        try { … existing write + catch … }
    }

    async Task<SendTextAckDto> AnswerSendTextWithAttachmentsAsync(string payload) {
        SendTextWithAttachmentsDto? dto;
        try { dto = JsonSerializer.Deserialize(payload, InputIpcJsonContext.Default.SendTextWithAttachmentsDto); }
        catch (JsonException) { dto = null; }
        if (!InputWire.IsStructurallyValid(dto)) return Refuse(SendTextReasons.Malformed, "expected {\"agent_id\",\"text\",\"attachment_ids\"}");
        if (dto!.AttachmentIds.Length == 0) return Refuse(SendTextReasons.Malformed, "send_text carries no attachments");
        return await AnswerSendTextCoreAsync(dto.AgentId, dto.Text, dto.AttachmentIds);
    }
```

Move the body of `AnswerSendTextAsync` after deserialisation into `AnswerSendTextCoreAsync(string agentId, string text, string[]? attachmentIds)`; the plain handler calls it with `null`. Inside the core, after the `NotRunning` check and before `DeliverInputAsync`:

```csharp
        if (attachmentIds is { Length: > 0 }) {
            if (AttachmentIds.Validate(attachmentIds) is { } invalid) return Refuse(SendTextReasons.AttachmentsRefused, invalid);
            if (agent.Placement == AttachmentPlacement.Worktree && agent.Work == WorkLocation.BorrowedCwd)
                return Refuse(SendTextReasons.AttachmentsRefused, "attachments need a daemon-owned worktree");
            if (!agent.Runtime.EmitsTerminalOutput && IsQuitCommand(text))
                return Refuse(SendTextReasons.AttachmentsRefused, "a quit command takes no attachments");
        }
        try { outcome = await DeliverInputAsync(agent, text, attachmentIds); } …
```

`AgentOrchestrator.DeliverInputAsync` — at the top, before the quit check:

```csharp
        if (attachmentIds is { Length: > 0 }) {
            if (AttachmentIds.Validate(attachmentIds) is { } invalid)
                return InputDeliveryOutcome.Drop(SendInputDropReason.DeliveryFailed, invalid);
            if (agent.Kind != LaunchKind.Default)
                return InputDeliveryOutcome.Drop(SendInputDropReason.DeliveryFailed, "attachments are not accepted by a review participant");
            if (agent.Placement == AttachmentPlacement.Worktree && agent.Work == WorkLocation.BorrowedCwd)
                return InputDeliveryOutcome.Drop(SendInputDropReason.DeliveryFailed, "attachments need a daemon-owned worktree");
            if (!agent.Runtime.EmitsTerminalOutput && IsQuitCommand(text))
                return InputDeliveryOutcome.Drop(SendInputDropReason.DeliveryFailed, "a quit command takes no attachments");
        }
```

Inside `DeliverInSectionAsync`, replace the attachment block:

```csharp
            AttachmentBatch? batch = null;
            var message = text;
            if (attachmentIds is { Length: > 0 }) {
                var root = agent.Placement == AttachmentPlacement.DaemonStore
                    ? _attachmentStore.DirectoryFor(agent.Id)
                    : Path.Combine(agent.Worktree.Path, ".attached");
                var fetch = await _attachmentFetcher.FetchAsync(root, agent.Placement, attachmentIds, _shutdownCts.Token);
                if (fetch.Batch is null) {
                    LogAttachmentFetchFailed(agent.Id, fetch.FailedId, fetch.Error);
                    return InputDeliveryOutcome.Drop(SendInputDropReason.DeliveryFailed, $"attachment {fetch.FailedId} unavailable: {fetch.Error}");
                }
                batch = fetch.Batch;
                message = $"{text}\n\n{AttachmentTrailer.For(batch.Paths)}";
            }
```

Then wrap the rest of the section so that every non-`Delivered` return and every thrown exception calls `batch?.Rollback()` — simplest: `try { … existing late reap check, write, activity clock … } catch { batch?.Rollback(); throw; }` and add `batch?.Rollback();` before each `return InputDeliveryOutcome.Drop(...)` after the fetch. Add `[LoggerMessage(Level = LogLevel.Warning, Message = "Attachment {AttachmentId} for agent {AgentId} unavailable: {Error}")] partial void LogAttachmentFetchFailed(string agentId, string? attachmentId, string? error);`.

- [ ] **Step 4: Run the daemon suite** — green (fix the capabilities pin test).
- [ ] **Step 5: Commit** — `Deliver composer attachments fail-closed through the local frame`

---

### Task 7: Daemon launch — validate, refuse borrowed, fail closed, lease

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` `HandleLaunchAgentCore` (the block at `if (work == WorkLocation.OwnedWorktree) { // Download attachments…`).
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LaunchAttachmentsTests.cs`

- [ ] **Step 1: Write the failing tests** (drive `orch.HandleLaunchAgentForTest(new LaunchAgentCommand(...))` as `AgentStartupFailureTests` does, with a WireMock factory and `CaptureServerConnection`):

```csharp
    [Test] public async Task Malformed_ids_fail_the_launch_with_attachments_refused_and_start_nothing() { … CaptureServerConnection.LaunchFailures.Single().Reason starts with "attachments_refused:"; SpyPtyProcessFactory.Spawns == 0 … }
    [Test] public async Task Missing_attachment_fails_the_launch_with_attachment_unavailable_and_removes_the_worktree() { … reason starts with "attachment_unavailable: <id>"; no worktree dir under WorktreeRoot … }
    [Test] public async Task Borrowed_cwd_launch_with_ids_and_worktree_placement_fails_and_without_ids_proceeds() { … cmd with Borrowed: true, BorrowCwd: <allowed repo>; reason "attachments_refused: attachments need a daemon-owned worktree"; same cmd with AttachmentIds: null → agent registered … }
    [Test] public async Task Successful_fetch_appends_the_trailer_to_the_prompt() { … the spawned PTY's args end with "goal\n\n[Attached files: .attached/<batch>/f.png]" … }
    [Test] public async Task Daemon_store_batch_is_removed_when_start_fails_after_the_fetch() { … codex factory stub whose StartAsync throws after a successful fetch; store.DirectoryFor(agentId) does not exist … }
    [Test] public async Task Daemon_store_batch_is_removed_when_registration_fails_after_start() { … CaptureServerConnection.RegisterAgentAsync throws; directory gone … }
```

Use a `FakeRuntimeFactory` for the Codex cases (vendor `"codex"`, `AttachmentPlacementFor(Default) => DaemonStore`, scripted `StartAsync`). Assert placement of the fetched files: for the Worktree case the batch dir sits under `<worktree>/.attached/`.

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement** — replace the block with:

```csharp
            var placement = runtimeFactory.AttachmentPlacementFor(cmd.Kind);
            AttachmentStoreLease? storeLease = null;
            if (attachmentIds is { Length: > 0 }) {
                if (AttachmentIds.Validate(attachmentIds) is { } invalid)
                    throw new InvalidOperationException($"attachments_refused: {invalid}");
                if (placement == AttachmentPlacement.Worktree && work == WorkLocation.BorrowedCwd)
                    throw new InvalidOperationException("attachments_refused: attachments need a daemon-owned worktree");
                var root = placement == AttachmentPlacement.DaemonStore
                    ? _attachmentStore.DirectoryFor(agentId)
                    : Path.Combine(worktree.Path, ".attached");
                if (placement == AttachmentPlacement.DaemonStore) storeLease = _attachmentStore.Lease(agentId);
                var fetch = await _attachmentFetcher.FetchAsync(root, placement, attachmentIds, _shutdownCts.Token);
                if (fetch.Batch is null)
                    throw new InvalidOperationException($"attachment_unavailable: {fetch.FailedId}: {fetch.Error}");
                var suffix = AttachmentTrailer.For(fetch.Batch.Paths);
                prompt = string.IsNullOrEmpty(prompt) ? suffix : $"{prompt}\n\n{suffix}";
            }
```

Declare `storeLease` at method scope beside `worktree`/`journal`, wrap the remainder so `storeLease?.Dispose()` runs on every exit before `PublishAgent` (both `catch` arms and the typed-exception arm), and call `storeLease?.Keep()` right after `PublishAgent(agent)` succeeds. Set `Placement = placement` on the `AgentInstance`. The existing catches already send `LaunchFailedAsync(agentId, DescribeLaunchFailure(ex))`, which carries the message verbatim.

- [ ] **Step 4: Run the daemon suite** — green.
- [ ] **Step 5: Commit** — `Fail a launch closed when its attachments cannot be delivered`

---

### Task 8: PTY input lane and 150 ms submit delay

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/PtyHostedAgentRuntime.cs`
- Modify: every `new PtyHostedAgentRuntime(` in `src/` keeps compiling (the new `TimeProvider? time = null` parameter is trailing).
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/PtyHostedAgentRuntimeInputLaneTests.cs`

- [ ] **Step 1: Write the failing tests** (use `RecordingPtyProcess` and `FakeTimeProvider`):

```csharp
    [Test]
    public async Task Paste_is_followed_by_one_cr_no_earlier_than_150ms() {
        var pty = new RecordingPtyProcess(); var time = new FakeTimeProvider();
        var rt = new PtyHostedAgentRuntime("claude", pty, approvalsDisabled: false, time);
        var send = rt.SendUserInputAsync("hi");
        await Assert.That(pty.Writes).IsEquivalentTo(["\x1b[200~hi\x1b[201~"]);
        time.Advance(TimeSpan.FromMilliseconds(149));
        await Assert.That(pty.Writes).Count().IsEqualTo(1);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await send;
        await Assert.That(pty.Writes).IsEquivalentTo(["\x1b[200~hi\x1b[201~", "\r"]);
    }

    [Test]
    public async Task Raw_input_during_a_send_lands_after_the_cr_and_nothing_is_lost() {
        var pty = new RecordingPtyProcess(); var time = new FakeTimeProvider();
        var rt = new PtyHostedAgentRuntime("claude", pty, false, time);
        await rt.SendRawInputAsync("a"u8.ToArray());
        var send = rt.SendUserInputAsync("hi");
        var raw = rt.SendRawInputAsync("b"u8.ToArray());
        var key = rt.SendSpecialKeyAsync("Escape");
        await Assert.That(raw.IsCompleted).IsFalse();
        time.Advance(TimeSpan.FromMilliseconds(150));
        await Task.WhenAll(send, raw, key);
        await Assert.That(pty.Writes[0]).IsEqualTo("a");
        await Assert.That(pty.Writes[1]).IsEqualTo("\x1b[200~hi\x1b[201~");
        await Assert.That(pty.Writes[2]).IsEqualTo("\r");
        await Assert.That(pty.Writes.Skip(3)).IsEquivalentTo(["b", "\x1b"], CollectionOrdering.Any);
    }

    [Test]
    public async Task Spray_schedule_holds_the_lane_until_the_last_cr() {
        var pty = new RecordingPtyProcess(); var time = new FakeTimeProvider();
        var rt = new PtyHostedAgentRuntime("codex", pty, approvalsDisabled: true, time);
        var send = rt.SendUserInputAsync("hi");
        var raw = rt.SendRawInputAsync("k"u8.ToArray());
        foreach (var d in PtyHostedAgentRuntime.SubmitCarriageReturnSchedule) time.Advance(d);
        await Task.WhenAll(send, raw);
        await Assert.That(pty.Writes.Last()).IsEqualTo("k");
        await Assert.That(pty.Writes.Count(w => w == "\r")).IsEqualTo(4);
    }

    [Test]
    public async Task Graceful_stop_cannot_interleave_with_a_paste() { … `/exit` and its CR never appear between the paste and its CR … }
```

- [ ] **Step 2: Run to verify failure** (constructor overload / timing).

- [ ] **Step 3: Implement**

Constructor: `internal sealed class PtyHostedAgentRuntime(string vendor, IPtyProcess pty, bool approvalsDisabled = false, TimeProvider? time = null)`; `readonly TimeProvider _time = time ?? TimeProvider.System; readonly SemaphoreSlim _lane = new(1, 1);`. `SingleSubmitDelay` becomes `TimeSpan.FromMilliseconds(150)` with a one-line comment naming the measured 120 ms Codex paste window. Replace `Task.Delay(x)` with `Task.Delay(x, _time)`. Wrap:

```csharp
    public async Task SendUserInputAsync(string text) {
        await _lane.WaitAsync();
        try { await pty.WriteAsync($"\x1b[200~{text}\x1b[201~"); await SubmitAsync(); }
        finally { _lane.Release(); }
    }
    public async Task SendSpecialKeyAsync(string key) {
        var bytes = SpecialKeyMap.ToBytes(key);
        if (bytes.Length == 0) return;
        await _lane.WaitAsync(); try { await pty.WriteAsync(bytes); } finally { _lane.Release(); }
    }
    public async Task SendRawInputAsync(byte[] data) {
        await _lane.WaitAsync(); try { await pty.WriteAsync(data); } finally { _lane.Release(); }
    }
    public async Task RequestGracefulStopAsync() {
        await _lane.WaitAsync(); try { await pty.WriteAsync("/exit"); await SubmitAsync(); } finally { _lane.Release(); }
    }
```

Class doc: one sentence — every write takes the lane so a paste and its submit are never interleaved by another writer.

- [ ] **Step 4: Run the daemon suite** (existing bracketed-paste tests calibrate on the old 50 ms; update them to 150 ms) — green.
- [ ] **Step 5: Commit** — `Serialise PTY writes behind one lane and submit after 150 ms`

---

### Task 9: App staging — `StagedAttachment`, `IntakeRefusal`, `AttachmentTray`

**Files:**
- Create: `src/Capacitor.App/ViewModels/StagedAttachment.cs`, `src/Capacitor.App/ViewModels/IntakeRefusal.cs`, `src/Capacitor.App/ViewModels/AttachmentTray.cs`
- Test: `test/Capacitor.App.Tests.Unit/AttachmentTrayTests.cs`

**Interfaces (produces):**

```csharp
public sealed class StagedAttachment(string fileName, string contentType, ReadOnlyMemory<byte> bytes) {
    public Guid Id { get; } = Guid.NewGuid();
    public string FileName { get; } = fileName;
    public string ContentType { get; } = contentType;
    public ReadOnlyMemory<byte> Bytes { get; } = bytes;
    public string SizeLabel { get; }          // "184 KB" / "2.3 MB" / "512 B"
    public bool IsImage { get; }              // ContentType starts with "image/"
}
public sealed record IntakeRefusal(string Name, string Reason);
public sealed class AttachmentTray : ReactiveObject {
    public ReadOnlyObservableCollection<StagedAttachment> Items { get; }
    public int Count { get; }  public long TotalBytes { get; }  public int Generation { get; }
    public IReadOnlyList<IntakeRefusal> AddAll(IReadOnlyList<StagedAttachment> files);
    public void Remove(StagedAttachment file);
    public IReadOnlyList<StagedAttachment> Snapshot();
    public void RemoveAll(IReadOnlyList<Guid> ids);
    public void Restore(IReadOnlyList<StagedAttachment> snapshot);
    public void Clear();
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Tests.Unit;

public class AttachmentTrayTests {
    static StagedAttachment File(string name, int size = 3) => new(name, "image/png", new byte[size]);

    [Test]
    public async Task Add_all_refuses_oversize_and_over_count_with_the_stated_wording_and_stages_the_rest() {
        var tray = new AttachmentTray();
        var big = new StagedAttachment("report.zip", "application/zip", new byte[InputWire.MaxAttachmentBytes + 1]);
        var refused = tray.AddAll([File("a.png"), big, File("b.png")]);
        await Assert.That(tray.Items.Select(f => f.FileName)).IsEquivalentTo(["a.png", "b.png"]);
        await Assert.That(refused).IsEquivalentTo([new IntakeRefusal("report.zip", "is over 10 MB")]);

        var many = Enumerable.Range(0, 10).Select(i => File($"f{i}.png")).ToList();
        refused = tray.AddAll(many);
        await Assert.That(tray.Count).IsEqualTo(10);
        await Assert.That(refused.Select(r => r.Reason).Distinct()).IsEquivalentTo(["only 10 files per message"]);
        await Assert.That(refused.Select(r => r.Name)).IsEquivalentTo(["f8.png", "f9.png"]);
    }

    [Test]
    public async Task Duplicate_names_get_a_numbered_suffix_before_the_extension() {
        var tray = new AttachmentTray();
        tray.AddAll([File("shot.png"), File("shot.png"), File("shot.png")]);
        await Assert.That(tray.Items.Select(f => f.FileName)).IsEquivalentTo(["shot.png", "shot (2).png", "shot (3).png"]);
    }

    [Test]
    public async Task Remove_all_removes_exactly_the_given_ids_and_generation_counts_real_mutations() {
        var tray = new AttachmentTray();
        var a = File("a.png"); var b = File("b.png"); var c = File("c.png");
        tray.AddAll([a, b]);
        var snapshot = tray.Snapshot();
        var g0 = tray.Generation;
        tray.AddAll([c]);            // added mid-flight
        tray.Remove(b);              // removed mid-flight
        tray.RemoveAll(snapshot.Select(f => f.Id).ToList());
        await Assert.That(tray.Items).IsEquivalentTo([c]);
        await Assert.That(tray.Generation).IsGreaterThan(g0);
        var g1 = tray.Generation;
        tray.RemoveAll([a.Id]);      // nothing to remove
        await Assert.That(tray.Generation).IsEqualTo(g1);
    }

    [Test]
    public async Task Snapshot_is_a_copy_and_restore_replaces_contents() {
        var tray = new AttachmentTray();
        tray.AddAll([File("a.png")]);
        var snapshot = tray.Snapshot();
        tray.Clear();
        await Assert.That(tray.Count).IsEqualTo(0);
        await Assert.That(snapshot).Count().IsEqualTo(1);
        tray.AddAll([File("z.png")]);
        tray.Restore(snapshot);
        await Assert.That(tray.Items.Select(f => f.FileName)).IsEquivalentTo(["a.png"]);
    }

    [Test]
    public async Task Removed_chip_bytes_are_unreferenced_while_a_receipt_still_holds_its_id() {
        var tray = new AttachmentTray();
        WeakReference weak = Stage(tray, out var id);
        tray.RemoveAll([id]);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        await Assert.That(weak.IsAlive).IsFalse();
        await Assert.That(id).IsNotEqualTo(Guid.Empty);

        static WeakReference Stage(AttachmentTray tray, out Guid id) {
            var f = new StagedAttachment("x.bin", "application/octet-stream", new byte[1024]);
            tray.AddAll([f]);
            id = f.Id;
            return new WeakReference(f);
        }
    }

    [Test]
    public async Task Size_label_and_image_flag() {
        await Assert.That(new StagedAttachment("a", "image/png", new byte[512]).SizeLabel).IsEqualTo("512 B");
        await Assert.That(new StagedAttachment("a", "image/png", new byte[184 * 1024]).SizeLabel).IsEqualTo("184 KB");
        await Assert.That(new StagedAttachment("a", "text/plain", new byte[(int)(2.3 * 1024 * 1024)]).SizeLabel).IsEqualTo("2.3 MB");
        await Assert.That(new StagedAttachment("a", "text/plain", new byte[1]).IsImage).IsFalse();
    }
}
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement**

`StagedAttachment.cs`:

```csharp
namespace Capacitor.App.ViewModels;

/// Identity is the Id, minted at staging: two pastes of the same bytes are two chips, and a send
/// clears the chips it sent, not every chip that looks like them. Receipts hold ids, never this
/// object, so the bytes live only in a tray or a retained launch draft.
public sealed class StagedAttachment(string fileName, string contentType, ReadOnlyMemory<byte> bytes) {
    public Guid Id { get; } = Guid.NewGuid();
    public string FileName { get; } = fileName;
    public string ContentType { get; } = contentType;
    public ReadOnlyMemory<byte> Bytes { get; } = bytes;
    public bool IsImage => ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    public string SizeLabel => Bytes.Length switch {
        < 1024 => $"{Bytes.Length} B",
        < 1024 * 1024 => $"{Bytes.Length / 1024} KB",
        _ => $"{Bytes.Length / (1024.0 * 1024.0):0.#} MB",
    };
    internal StagedAttachment Renamed(string fileName) => new(fileName, ContentType, Bytes, Id);
    StagedAttachment(string fileName, string contentType, ReadOnlyMemory<byte> bytes, Guid id) : this(fileName, contentType, bytes) => Id = id;
}
```

(Give `Id` an `init`-able backing so the private constructor can carry it; a `Renamed` copy keeps the identity.)

`AttachmentTray.cs`:

```csharp
using System.Collections.ObjectModel;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The chips staged in one prompt. The single boundary every source passes through, so both limits
/// hold whatever produced the file.
public sealed class AttachmentTray : ReactiveObject {
    readonly ObservableCollection<StagedAttachment> _items = new();
    int _generation;

    public AttachmentTray() => Items = new ReadOnlyObservableCollection<StagedAttachment>(_items);

    public ReadOnlyObservableCollection<StagedAttachment> Items { get; }
    public int Count => _items.Count;
    public long TotalBytes => _items.Sum(f => (long)f.Bytes.Length);
    public int Generation => _generation;

    public IReadOnlyList<IntakeRefusal> AddAll(IReadOnlyList<StagedAttachment> files) {
        var refused = new List<IntakeRefusal>();
        var changed = false;
        foreach (var file in files) {
            if (file.Bytes.Length > InputWire.MaxAttachmentBytes) { refused.Add(new(file.FileName, "is over 10 MB")); continue; }
            if (_items.Count >= InputWire.MaxAttachmentsPerPrompt) { refused.Add(new(file.FileName, $"only {InputWire.MaxAttachmentsPerPrompt} files per message")); continue; }
            _items.Add(Dedup(file));
            changed = true;
        }
        if (changed) Bump();
        return refused;
    }

    public void Remove(StagedAttachment file) { if (_items.Remove(file)) Bump(); }

    public IReadOnlyList<StagedAttachment> Snapshot() => [.. _items];

    public void RemoveAll(IReadOnlyList<Guid> ids) {
        var set = ids.ToHashSet();
        var removed = false;
        for (var i = _items.Count - 1; i >= 0; i--)
            if (set.Contains(_items[i].Id)) { _items.RemoveAt(i); removed = true; }
        if (removed) Bump();
    }

    public void Restore(IReadOnlyList<StagedAttachment> snapshot) {
        _items.Clear();
        foreach (var f in snapshot) _items.Add(f);
        Bump();
    }

    public void Clear() { if (_items.Count == 0) return; _items.Clear(); Bump(); }

    StagedAttachment Dedup(StagedAttachment file) {
        if (_items.All(f => !string.Equals(f.FileName, file.FileName, StringComparison.Ordinal))) return file;
        var stem = Path.GetFileNameWithoutExtension(file.FileName);
        var ext = Path.GetExtension(file.FileName);
        for (var n = 2; ; n++) {
            var candidate = $"{stem} ({n}){ext}";
            if (_items.All(f => !string.Equals(f.FileName, candidate, StringComparison.Ordinal))) return file.Renamed(candidate);
        }
    }

    void Bump() {
        _generation++;
        this.RaisePropertyChanged(nameof(Count));
        this.RaisePropertyChanged(nameof(TotalBytes));
        this.RaisePropertyChanged(nameof(Generation));
    }
}
```

- [ ] **Step 4: Run** `/*/*/AttachmentTrayTests/*` — green.
- [ ] **Step 5: Commit** — `Add the attachment tray and staged chip model`

---

### Task 10: App intake — `AttachmentIntake`

**Files:**
- Create: `src/Capacitor.App/Services/IntakeKind.cs`, `src/Capacitor.App/Services/IntakeResult.cs`, `src/Capacitor.App/Services/AttachmentIntake.cs`
- Test: `test/Capacitor.App.Tests.Unit/AttachmentIntakeTests.cs`, plus test doubles `FakeStorageFile.cs`, `FakeStorageFolder.cs` (implement `Avalonia.Platform.Storage.IStorageFile`/`IStorageFolder`; unused members throw `NotSupportedException`).

**Interfaces (produces):**

```csharp
public enum IntakeKind { Files, Text, Bitmap, Nothing }
public sealed record IntakeResult(IReadOnlyList<StagedAttachment> Accepted, IReadOnlyList<IntakeRefusal> Refused) {
    public static readonly IntakeResult Empty = new([], []);
}
public static class AttachmentIntake {
    public static IntakeKind Classify(IReadOnlyList<DataFormat> formats, bool hasNonBlankText);
    public static Task<IntakeResult> ReadFilesAsync(IEnumerable<IStorageItem> items, CancellationToken ct);
    public static IntakeResult FromBitmap(Bitmap bitmap, TimeProvider time);
    public static string ContentTypeFor(string fileName);
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class AttachmentIntakeTests {
    [Test]
    public async Task Classify_prefers_files_then_text_then_bitmap() {
        await Assert.That(AttachmentIntake.Classify([DataFormat.File, DataFormat.Text, DataFormat.Bitmap], true)).IsEqualTo(IntakeKind.Files);
        await Assert.That(AttachmentIntake.Classify([DataFormat.Text, DataFormat.Bitmap], true)).IsEqualTo(IntakeKind.Text);
        await Assert.That(AttachmentIntake.Classify([DataFormat.Text, DataFormat.Bitmap], false)).IsEqualTo(IntakeKind.Bitmap);
        await Assert.That(AttachmentIntake.Classify([DataFormat.Bitmap], false)).IsEqualTo(IntakeKind.Bitmap);
        await Assert.That(AttachmentIntake.Classify([], false)).IsEqualTo(IntakeKind.Nothing);
    }

    [Test]
    public async Task Read_files_refuses_each_bad_item_by_name_and_keeps_the_valid_sibling_after_it() {
        var items = new IStorageItem[] {
            new FakeStorageFolder("Docs"),
            new FakeStorageFile("a.png", new byte[3]),
            new FakeStorageFile("big.bin", reportedSize: InputWire.MaxAttachmentBytes + 1),
            new FakeStorageFile("b.txt", new byte[2]),
            new FakeStorageFile("nosize.bin", new byte[InputWire.MaxAttachmentBytes + 1], reportedSize: null),
            new FakeStorageFile("c.md", new byte[1]),
            new FakeStorageFile("locked.txt", openThrows: new UnauthorizedAccessException()),
            new FakeStorageFile("d.json", new byte[1]),
            new FakeStorageFile("half.bin", new byte[100], throwAfterBytes: 50),
            new FakeStorageFile("e.csv", new byte[1]),
        };
        var result = await AttachmentIntake.ReadFilesAsync(items, CancellationToken.None);
        await Assert.That(result.Accepted.Select(f => f.FileName)).IsEquivalentTo(["a.png", "b.txt", "c.md", "d.json", "e.csv"]);
        await Assert.That(result.Refused).IsEquivalentTo([
            new IntakeRefusal("Docs", "is a folder"), new IntakeRefusal("big.bin", "is over 10 MB"),
            new IntakeRefusal("nosize.bin", "is over 10 MB"), new IntakeRefusal("locked.txt", "could not be read"),
            new IntakeRefusal("half.bin", "could not be read")]);
        await Assert.That(items.OfType<FakeStorageFile>().Single(f => f.Name == "big.bin").Opened).IsFalse();
        await Assert.That(result.Accepted[0].ContentType).IsEqualTo("image/png");
    }

    [Test]
    public async Task Read_files_propagates_the_callers_cancellation() {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => AttachmentIntake.ReadFilesAsync([new FakeStorageFile("a.png", new byte[1])], cts.Token));
    }

    [Test]
    public async Task Content_types_come_from_the_extension_table() {
        await Assert.That(AttachmentIntake.ContentTypeFor("x.PNG")).IsEqualTo("image/png");
        await Assert.That(AttachmentIntake.ContentTypeFor("x.jpg")).IsEqualTo("image/jpeg");
        await Assert.That(AttachmentIntake.ContentTypeFor("x.pdf")).IsEqualTo("application/pdf");
        await Assert.That(AttachmentIntake.ContentTypeFor("x.cs")).IsEqualTo("text/plain");
        await Assert.That(AttachmentIntake.ContentTypeFor("x.unknownext")).IsEqualTo("application/octet-stream");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task From_bitmap_is_a_png_named_by_the_clock_and_an_oversize_encoding_is_refused() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 14, 10, 30, 5, TimeSpan.Zero));
            using var small = new WriteableBitmap(new Avalonia.PixelSize(4, 4), new Avalonia.Vector(96, 96));
            var ok = AttachmentIntake.FromBitmap(small, time);
            await Assert.That(ok.Accepted.Single().FileName).IsEqualTo("pasted-image-20260914-103005.png");
            await Assert.That(ok.Accepted.Single().ContentType).IsEqualTo("image/png");
            await Assert.That(ok.Accepted.Single().Bytes.Span[..8].ToArray()).IsEquivalentTo([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        });
    }
}
```

(The oversize-bitmap branch is covered by an `internal static IntakeResult FromPngBytes(byte[] png, TimeProvider time)` seam that `FromBitmap` calls after encoding; test it with an 11 MiB array → refusal "is over 10 MB".)

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement** `AttachmentIntake.cs`:

```csharp
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services;

/// Turns clipboard, drop and picker payloads into staged chips. Pure over Avalonia's data-transfer and
/// storage types so it needs no clipboard to test.
public static class AttachmentIntake {
    static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase) {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
        [".pdf"] = "application/pdf", [".txt"] = "text/plain", [".md"] = "text/plain", [".json"] = "application/json",
        [".csv"] = "text/csv", [".log"] = "text/plain", [".xml"] = "application/xml", [".yaml"] = "text/plain", [".yml"] = "text/plain",
        [".cs"] = "text/plain", [".ts"] = "text/plain", [".js"] = "text/plain", [".py"] = "text/plain", [".go"] = "text/plain",
        [".rs"] = "text/plain", [".java"] = "text/plain", [".rb"] = "text/plain", [".sh"] = "text/plain", [".sql"] = "text/plain",
        [".html"] = "text/plain", [".css"] = "text/plain", [".toml"] = "text/plain", [".ini"] = "text/plain",
    };

    public static IntakeKind Classify(IReadOnlyList<DataFormat> formats, bool hasNonBlankText) {
        if (formats.Contains(DataFormat.File)) return IntakeKind.Files;
        if (formats.Contains(DataFormat.Text) && hasNonBlankText) return IntakeKind.Text;
        if (formats.Contains(DataFormat.Bitmap)) return IntakeKind.Bitmap;
        return IntakeKind.Nothing;
    }

    public static string ContentTypeFor(string fileName) =>
        ContentTypes.TryGetValue(Path.GetExtension(fileName), out var type) ? type : "application/octet-stream";

    public static async Task<IntakeResult> ReadFilesAsync(IEnumerable<IStorageItem> items, CancellationToken ct) {
        var accepted = new List<StagedAttachment>();
        var refused = new List<IntakeRefusal>();
        foreach (var item in items) {
            ct.ThrowIfCancellationRequested();
            if (item is not IStorageFile file) { refused.Add(new(item.Name, "is a folder")); continue; }
            try {
                var props = await file.GetBasicPropertiesAsync();
                if (props.Size is { } size && size > (ulong)InputWire.MaxAttachmentBytes) { refused.Add(new(file.Name, "is over 10 MB")); continue; }
                await using var stream = await file.OpenReadAsync();
                var bytes = await ReadCappedAsync(stream, ct);
                if (bytes is null) { refused.Add(new(file.Name, "is over 10 MB")); continue; }
                accepted.Add(new StagedAttachment(file.Name, ContentTypeFor(file.Name), bytes));
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception) {
                refused.Add(new(item.Name, "could not be read"));
            }
        }
        return new IntakeResult(accepted, refused);
    }

    public static IntakeResult FromBitmap(Bitmap bitmap, TimeProvider time) {
        using var ms = new MemoryStream();
        bitmap.Save(ms);
        return FromPngBytes(ms.ToArray(), time);
    }

    internal static IntakeResult FromPngBytes(byte[] png, TimeProvider time) {
        var name = $"pasted-image-{time.GetUtcNow():yyyyMMdd-HHmmss}.png";
        return png.LongLength > InputWire.MaxAttachmentBytes
            ? new IntakeResult([], [new(name, "is over 10 MB")])
            : new IntakeResult([new StagedAttachment(name, "image/png", png)], []);
    }

    /// Null when the stream runs past the cap; reading stops at cap + 1 bytes.
    static async Task<byte[]?> ReadCappedAsync(Stream stream, CancellationToken ct) {
        var limit = InputWire.MaxAttachmentBytes + 1;
        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (ms.Length < limit) {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - ms.Length)), ct);
            if (read == 0) return ms.ToArray();
            ms.Write(buffer, 0, read);
        }
        return null;
    }
}
```

`Classify`'s text-vs-bitmap rule: text wins when non-blank text is present, else bitmap — matching §1.

- [ ] **Step 4: Run** `/*/*/AttachmentIntakeTests/*` — green.
- [ ] **Step 5: Commit** — `Classify and read clipboard, drop and picker payloads into chips`

---

### Task 11: App upload — `IAttachmentUploader` and `ServerAttachmentUploader`

**Files:**
- Create: `src/Capacitor.App/Services/UploadKind.cs`, `UploadOutcome.cs`, `IAttachmentUploader.cs`, `ServerAttachmentUploader.cs`, `NoAttachmentUploader.cs` (always `Unauthorized("not_signed_in")`; used when the app has no server profile)
- Test: `test/Capacitor.App.Tests.Unit/ServerAttachmentUploaderTests.cs`

**Interfaces (produces):**

```csharp
public enum UploadKind { Uploaded, Unauthorized, Rejected, Unreachable }
public sealed record UploadOutcome(UploadKind Kind, IReadOnlyList<string> Ids, string? Reason) {
    public static UploadOutcome Unauthorized(string reason) => new(UploadKind.Unauthorized, [], reason);
    public static UploadOutcome Rejected(string reason) => new(UploadKind.Rejected, [], reason);
    public static UploadOutcome Unreachable(string reason) => new(UploadKind.Unreachable, [], reason);
}
public interface IAttachmentUploader { Task<UploadOutcome> UploadAsync(IReadOnlyList<StagedAttachment> files, CancellationToken ct); }
public sealed class ServerAttachmentUploader(ICapacitorHttpClient? http, ProfileContext? profiles) : IAttachmentUploader;
```

- [ ] **Step 1: Write the failing tests** (WireMock + `RecordingCapacitorHttpClient` from Helpers — read its constructor: it takes an `HttpMessageHandler?` and `AuthStatus`; point the handler at WireMock or construct it with a client whose `BaseAddress` is irrelevant because the uploader builds absolute URLs from `profiles.Resolution.ServerUrl` — build a `ProfileContext` the way `ServerSessionHttp` tests do; grep `ProfileContext` in the App tests for a fixture):

```csharp
    [Test] public async Task Posts_one_multipart_part_per_file_with_name_type_and_filename_and_returns_ids_in_order() {
        // server: POST /api/attachments/upload → 200 [{"id":A,"fileName":"a.png","size":3},{"id":B,"fileName":"b.txt","size":2}]
        // assert request Content-Type multipart/form-data; body contains name="files"; filename="a.png"; Content-Type: image/png
        // outcome Kind Uploaded, Ids [A, B]
    }
    [Test] [Arguments("[]")] [Arguments("[{\"id\":\"A\"}]")]              // fewer
           [Arguments("[{\"id\":\"A\"},{\"id\":\"B\"},{\"id\":\"C\"}]")] // more
           [Arguments("[{\"id\":null},{\"id\":\"B\"}]")] [Arguments("[{\"id\":\"\"},{\"id\":\"B\"}]")]
           [Arguments("[{\"id\":\"nope\"},{\"id\":\"B\"}]")] [Arguments("[{\"id\":\"A\"},{\"id\":\"A\"}]")] [Arguments("not json")]
    public async Task A_200_with_the_wrong_shape_is_rejected_with_the_stated_reason(string body) {
        // two files staged; outcome Kind Rejected, Reason "the server returned an unexpected upload response", Ids empty
    }
    [Test] public async Task Status_codes_map_to_kinds() { /* 400 → Rejected with body text; 401 → Unauthorized; 500 → Unreachable "server_status_500"; refused connection → Unreachable */ }
    [Test] public async Task Not_signed_in_is_unauthorized_without_a_request() { /* RecordingCapacitorHttpClient(status: AuthStatus.SignedOut) → Unauthorized, server received 0 requests */ }
```

Replace `A`/`B` with two real Guid-N strings. Write all four tests in full.

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement** `ServerAttachmentUploader.cs` (mirror `ServerSessionHttp.Responder`):

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services;

/// Uploads staged chips to the server's temp attachment store and hands back their ids. Every
/// failure is a value; nothing throws past cancellation.
public sealed class ServerAttachmentUploader(ICapacitorHttpClient? http, ProfileContext? profiles) : IAttachmentUploader {
    public const string UnexpectedResponse = "the server returned an unexpected upload response";

    public async Task<UploadOutcome> UploadAsync(IReadOnlyList<StagedAttachment> files, CancellationToken ct) {
        var serverUrl = profiles?.Resolution.ServerUrl;
        if (http is null || profiles is null || string.IsNullOrEmpty(serverUrl)) return UploadOutcome.Unauthorized("not_signed_in");
        try {
            var (client, status, _, _) = await http.ForWaitAsync(ct).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return UploadOutcome.Unauthorized("not_signed_in");
                using var content = new MultipartFormDataContent();
                foreach (var file in files) {
                    var part = new ByteArrayContent(file.Bytes.ToArray());
                    part.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType);
                    content.Add(part, "files", file.FileName);
                }
                using var response = await client.PostAsync($"{serverUrl.TrimEnd('/')}/api/attachments/upload", content, ct).ConfigureAwait(false);
                switch (response.StatusCode) {
                    case HttpStatusCode.OK:
                        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        return ParseIds(body, files.Count);
                    case HttpStatusCode.Unauthorized: return UploadOutcome.Unauthorized("not_signed_in");
                    case HttpStatusCode.BadRequest: return UploadOutcome.Rejected(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    default: return UploadOutcome.Unreachable($"server_status_{(int)response.StatusCode}");
                }
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            return UploadOutcome.Unreachable(ex.Message);
        }
    }

    internal static UploadOutcome ParseIds(string body, int expected) {
        try {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() != expected) return UploadOutcome.Rejected(UnexpectedResponse);
            var ids = new List<string>(expected);
            foreach (var element in doc.RootElement.EnumerateArray()) {
                if (!element.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String) return UploadOutcome.Rejected(UnexpectedResponse);
                ids.Add(idProp.GetString()!);
            }
            if (AttachmentIds.Validate(ids) is not null || ids.Count == 0 && expected > 0) return UploadOutcome.Rejected(UnexpectedResponse);
            return new UploadOutcome(UploadKind.Uploaded, ids, null);
        } catch (JsonException) {
            return UploadOutcome.Rejected(UnexpectedResponse);
        }
    }
}
```

(The server serialises `UploadedAttachment(Id, FileName, Size)` with its default camelCase policy; `JsonDocument` is AOT-safe. `ProfileContext`/`AuthStatus` come from the same namespaces `ServerSessionHttp.cs` imports.)

- [ ] **Step 4: Run** `/*/*/ServerAttachmentUploaderTests/*` — green.
- [ ] **Step 5: Commit** — `Upload staged attachments to the server's temp store`

---

### Task 12: `ChatInput` channels — `SendAsync(text, ids)`, `CanAttach`, `AttachHint`

**Files:**
- Modify: `src/Capacitor.App/ViewModels/ChatInput.cs`, `LocalFrameChatInput.cs`, `TerminalChatInput.cs`, `WorkspaceViewModel.cs` (construction site at `ChatInput input = …`)
- Modify tests: `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs` (`ScriptedInput`), `ChatComposerTests.cs`, `LocalFrameChatInputTests.cs` (new cases), `TerminalChatInputTests.cs` (new cases)

**Interfaces (produces):**

```csharp
public abstract class ChatInput : ReactiveObject, IDisposable {
    public abstract SendAvailability Availability { get; }
    public abstract bool CanAcceptText { get; }
    public abstract string Hint { get; }
    public abstract bool CanAttach { get; }
    public abstract string? AttachHint { get; }
    public abstract Task<ChatSendOutcome> SendAsync(string text, IReadOnlyList<string> attachmentIds, CancellationToken ct);
    public virtual void ConfirmLastSend() { }
    public virtual bool CanInterrupt => false;
    public virtual Task InterruptAsync(CancellationToken ct) => Task.CompletedTask;
    public abstract void Dispose();
}
```

Wording: `AttachHint` ∈ { `"attachments need the daemon updated"`, `"attachments aren't available for an in-place session"`, `null` when `CanAttach` }; the sending state's hint while attachments ride is the existing `"Sending…"`.

- [ ] **Step 1: Write the failing tests**

`LocalFrameChatInputTests`:

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Can_attach_needs_input_2_and_an_owned_worktree() {
        await RunOnUiAsync(async () => {
            var rig = new Rig();
            rig.Connected("status/1", "input/1"); rig.Running();
            await Assert.That(rig.Input.CanAttach).IsFalse();
            await Assert.That(rig.Input.AttachHint).IsEqualTo("attachments need the daemon updated");
            rig.Connected("status/1", "input/1", "input/2");
            rig.Presence.OnNext(new AgentPresence(Agent("a1", "pi", hasTerminal: false, workLocation: "borrowed") with { Status = "Running" }, false));
            await Assert.That(rig.Input.CanAttach).IsFalse();
            await Assert.That(rig.Input.AttachHint).IsEqualTo("attachments aren't available for an in-place session");
            rig.Presence.OnNext(new AgentPresence(Agent("a1", "pi", hasTerminal: false, workLocation: "owned") with { Status = "Running" }, false));
            await Assert.That(rig.Input.CanAttach).IsTrue();
            await Assert.That(rig.Input.AttachHint).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Ids_ride_the_attachment_frame_and_attachments_refused_maps_to_its_error() {
        await RunOnUiAsync(async () => {
            var rig = new Rig();
            rig.Connected("status/1", "input/1", "input/2");
            rig.Presence.OnNext(new AgentPresence(Agent("a1", "pi", hasTerminal: false, workLocation: "owned") with { Status = "Running" }, false));
            rig.Ops.QueueSendText(new SendTextResult(false, SendTextReasons.AttachmentsRefused, "attachments need a daemon-owned worktree", null));
            var outcome = await rig.Input.SendAsync("hi", [Id], CancellationToken.None);
            await Assert.That(outcome).IsEqualTo(ChatSendOutcome.Rejected);
            await Assert.That(rig.Ops.SendTextWithAttachmentsPayloads.Single().Ids).IsEquivalentTo([Id]);
            await Assert.That(rig.Ops.SendTextCalls).IsEqualTo(0);
            await Assert.That(rig.Input.Hint).IsEqualTo("attachments need a daemon-owned worktree");
            rig.Ops.QueueSendText(new SendTextResult(true, null, null, SendTextOutcomes.Delivered));
            await rig.Input.SendAsync("plain", [], CancellationToken.None);
            await Assert.That(rig.Ops.SendTextCalls).IsEqualTo(1);
        });
    }
```

`TerminalChatInputTests` (build the rig the way `ChatComposerTests.BuildAttachedAsync` does, then `new TerminalChatInput(terminal, "a1", daemon, ops, presence)`):

```csharp
    [Test] [NotInParallel("AvaloniaSession")]
    public async Task Text_only_stays_on_the_terminal_and_ids_take_one_frame_exchange_closing_the_gate_meanwhile() {
        // SendAsync("hi", [], ct) → client.SentInput has the bracketed paste, ops.SendTextWithAttachmentsCalls == 0
        // arm ops; var send = SendAsync("hi", [Id], ct); CanAcceptText false, Hint "Sending…"; complete Ok → Accepted; CanAcceptText true
        // ops payload Ids == [Id]; client.SentInput unchanged (no CR from the app)
    }
    [Test] [NotInParallel("AvaloniaSession")]
    public async Task Can_attach_follows_input_2_and_work_location() { /* same matrix as the frame input, with hasTerminal: true */ }
```

Update `ScriptedInput` in `ChatTabViewModelTests`: `Sends` records `(string Text, IReadOnlyList<string> Ids, CancellationToken Ct)`; add `public bool CanAttachValue = true; public override bool CanAttach => CanAttachValue; public override string? AttachHint => CanAttachValue ? null : "attachments need the daemon updated";`. Update every `SendAsync(text, ct)` call in the tests to `SendAsync(text, [], ct)`.

- [ ] **Step 2: Run to verify failure** (abstract members not implemented).

- [ ] **Step 3: Implement**

`ChatInput.cs` — as in Interfaces (one-line doc on `CanAttach`: false carries the reason in `AttachHint`).

`LocalFrameChatInput.cs`:

```csharp
    const string AttachCapability = "input/2";
    public override bool CanAttach => Availability == SendAvailability.Ready && HasCapability(AttachCapability) && IsOwnedWorktree;
    public override string? AttachHint =>
        CanAttach ? null
        : !HasCapability(AttachCapability) ? "attachments need the daemon updated"
        : !IsOwnedWorktree ? "attachments aren't available for an in-place session"
        : Hint;
    bool HasCapability(string cap) => _status.Capabilities is { } caps && caps.Contains(cap);
    bool IsOwnedWorktree => string.Equals(_presence.Dto?.WorkLocation, WorkLocationText.Owned, StringComparison.Ordinal);

    public override async Task<ChatSendOutcome> SendAsync(string text, IReadOnlyList<string> attachmentIds, CancellationToken ct) {
        if (_disposed || !CanAcceptText || ct.IsCancellationRequested) return ChatSendOutcome.Rejected;
        if (attachmentIds.Count > 0 && !CanAttach) return ChatSendOutcome.Rejected;
        _sending = true; _notice = null; Raise();
        SendTextResult result;
        try {
            result = attachmentIds.Count == 0
                ? await _ops.SendTextAsync(_agentId, text, ct)
                : await _ops.SendTextWithAttachmentsAsync(_agentId, text, attachmentIds, ct);
        } catch (OperationCanceledException) { return Settle(ChatSendOutcome.Unconfirmed, Unconfirmed); }
          catch (Exception) { return Settle(ChatSendOutcome.Unconfirmed, Unconfirmed); }
        … existing mapping, plus:
            SendTextReasons.AttachmentsRefused => result.Error ?? "attachments were refused",
    }
```

`Raise()` also raises `CanAttach` and `AttachHint`.

`TerminalChatInput.cs` — constructor `(TerminalTabViewModel terminal, string agentId, IDaemonClientService daemon, ILocalControlOps ops, IObservable<AgentPresence> presence)`; subscribe to `daemon.Status.ObserveOn(RxSchedulers.MainThreadScheduler)` and `presence` like `LocalFrameChatInput` does, holding `_status`/`_presence`; `bool _sending`:

```csharp
    public override SendAvailability Availability => _disposed ? SendAvailability.Ended : _sending ? SendAvailability.Sending : _terminal.SendAvailability;
    public override bool CanAcceptText => !_disposed && !_sending && _terminal.CanAcceptText;
    public override string Hint => _sending ? "Sending…" : _notice ?? HintFor(_terminal.SendAvailability, _terminal.State);
    public override bool CanAttach => CanAcceptText && HasCapability("input/2") && IsOwnedWorktree;   // same helpers as the frame input
    public override string? AttachHint => … same three-way wording …

    public override async Task<ChatSendOutcome> SendAsync(string text, IReadOnlyList<string> attachmentIds, CancellationToken ct) {
        if (_disposed || ct.IsCancellationRequested) return ChatSendOutcome.Rejected;
        if (attachmentIds.Count == 0) return _terminal.TrySendText(text) ? ChatSendOutcome.Accepted : ChatSendOutcome.Rejected;
        if (!CanAttach) return ChatSendOutcome.Rejected;
        _sending = true; _notice = null; Raise();
        SendTextResult result;
        try { result = await _ops.SendTextWithAttachmentsAsync(_agentId, text, attachmentIds, ct); }
        catch (OperationCanceledException) { return Settle(ChatSendOutcome.Unconfirmed, Unconfirmed); }
        catch (Exception) { return Settle(ChatSendOutcome.Unconfirmed, Unconfirmed); }
        if (result.Ok) return Settle(ChatSendOutcome.Accepted, null);
        var outcome = result.Reason == SendTextReasons.Transport ? ChatSendOutcome.Unconfirmed : ChatSendOutcome.Rejected;
        return Settle(outcome, LocalFrameChatInput.NoticeFor(result));   // move the reason→wording switch into a shared internal static
    }
```

Move the reason→wording `switch` out of `LocalFrameChatInput.SendAsync` into `internal static string NoticeFor(SendTextResult result)` and the `Unconfirmed` constant into an `internal const` both classes use. `ConfirmLastSend` on the terminal input clears an `Unconfirmed` notice like the frame input does.

`WorkspaceViewModel`: `new TerminalChatInput(Terminal, agentId, daemon, ops, presence)`.

- [ ] **Step 4: Run the App suite** — green.
- [ ] **Step 5: Commit** — `Give both chat channels an attachment path and gate`

---

### Task 13: `ChatTabViewModel` — tray, upload, sent-chip clearing, trailer-aware matching

**Files:**
- Modify: `src/Capacitor.App/ViewModels/ChatTabViewModel.cs`, `src/Capacitor.App/ViewModels/QueuedChatMessage.cs`
- Create: `src/Capacitor.App/Services/IAttachmentSink.cs`
- Modify: `src/Capacitor.App/ViewModels/WorkspaceViewModel.cs` (pass an `IAttachmentUploader` through; add a constructor parameter `IAttachmentUploader uploader`), `src/Capacitor.App/App.axaml.cs` (the `workspaceFactory` lambda passes `new ServerAttachmentUploader(ServerHttp(profiles), profiles)`).
- Test: `test/Capacitor.App.Tests.Unit/ChatAttachmentsTests.cs` (new; harness copied from `ChatTabViewModelTests.Harness` with a `ScriptedUploader`)

**Interfaces (produces):**

```csharp
public interface IAttachmentSink {
    bool CanAttach { get; }
    string? AttachHint { get; }
    void Accept(IntakeResult result);
}
// ChatTabViewModel
public AttachmentTray Tray { get; }
public bool Uploading { get; }
public IAttachmentSink Attachments { get; }   // the VM itself implements it
// ctor gains: IAttachmentUploader uploader (after `input`)
// QueuedChatMessage(string text, int composerEdits, int generation, long? offset, IReadOnlyList<Guid> attachmentIds)
```

- [ ] **Step 1: Write the failing tests** (a `ScriptedUploader : IAttachmentUploader` with `Queue<TaskCompletionSource<UploadOutcome>>` and recorded `Uploads`):

```csharp
    [Test] [NotInParallel("AvaloniaSession")]
    public async Task Send_with_attachments_uploads_first_then_passes_the_ids_and_clears_exactly_the_sent_chips() {
        // stage a, b; ComposerText "hi"; arm upload → Uploaded [A,B]; ScriptedInput pending
        // during upload: Uploading true, ComposerHint "Uploading 2 files…", canSend false
        // after upload: input.Sends.Single().Ids == [A,B]; stage c mid-flight; complete Accepted
        // Tray.Items == [c]; ComposerText ""
    }
    [Test] public async Task Upload_failure_sends_nothing_and_keeps_text_and_chips() { /* Unauthorized → hint "sign in to attach files"; Rejected("x") → hint "x"; input.Sends empty */ }
    [Test] public async Task Can_attach_false_refuses_before_the_upload() { /* ScriptedInput.CanAttachValue=false; uploader.Uploads empty; hint == AttachHint */ }
    [Test] public async Task Rejected_and_unconfirmed_keep_everything() { … }
    [Test] public async Task Unconfirmed_send_is_cleared_by_a_trailer_turn_and_not_by_bare_text() {
        // send with [A] → Unconfirmed; append transcript user line "hi" → still queued, chips kept
        // append "hi\n\n[Attached files: .attached/x/a.png]" → queue empty, sent chips removed, chip added meanwhile kept
    }
    [Test] public async Task Text_only_send_is_still_confirmed_by_bare_text() { … }
    [Test] public async Task Transcript_before_ack_and_ack_first_produce_the_same_tray() { … }
    [Test] public async Task Sink_accept_stages_files_and_shows_one_refusal_line() {
        // Accept(new IntakeResult([a], [new("Docs","is a folder"), new("big.zip","is over 10 MB")]))
        // Tray has a; ComposerHint == "`Docs` is a folder; `big.zip` is over 10 MB"; typing clears the notice
    }
```

Write each fully (the transcript lines follow `ChatTabViewModelTests.UserLine`'s shape: `{"type":"user","message":{"role":"user","content":"<text>"}}` with `\n` escaped as `\\n`).

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement**

`QueuedChatMessage`: add `IReadOnlyList<Guid> attachmentIds` to the primary constructor and `public IReadOnlyList<Guid> AttachmentIds { get; } = attachmentIds;`; `Matches` becomes:

```csharp
    internal bool Matches(string text, int generation, long offset) {
        if (_generation != generation || _offset is not { } baseline || offset < baseline) return false;
        var sent = Normalize(Text); var seen = Normalize(text);
        if (AttachmentIds.Count == 0) return sent == seen;
        return seen.StartsWith(sent, StringComparison.Ordinal)
            && seen.AsSpan(sent.Length).TrimStart('\n').StartsWith(AttachmentTrailer.Prefix, StringComparison.Ordinal)
            && seen.Length > sent.Length;
    }
```

`ChatTabViewModel`:
- fields `readonly IAttachmentUploader _uploader; public AttachmentTray Tray { get; } = new(); bool _uploading; public bool Uploading { get => _uploading; private set => this.RaiseAndSetIfChanged(ref _uploading, value); } string? _intakeNotice;`
- `ComposerHint` combine adds `Uploading`, `_intakeNotice` (a `BehaviorSubject<string?>`), and `Tray.Count`: `uploading ? $"Uploading {Tray.Count} files…" : notice ?? (readOnly ? "" : hint)`; the notice clears on the next `ComposerText` change or the next `Accept`.
- `canSend` adds `!Uploading`.
- `SendCommand`:

```csharp
        SendCommand = ReactiveCommand.CreateFromTask(async () => {
            var snapshot = ComposerText; var edits = _composerEdits;
            var files = Tray.Snapshot();
            if (files.Count > 0 && !_input.CanAttach) { _intakeNotice.OnNext(_input.AttachHint); return; }
            IReadOnlyList<string> ids = [];
            if (files.Count > 0) {
                Uploading = true;
                UploadOutcome upload;
                try { upload = await _uploader.UploadAsync(files, _lifetimeToken); }
                catch (OperationCanceledException) { return; }
                finally { Uploading = false; }
                if (_lifetimeToken.IsCancellationRequested) return;
                if (upload.Kind != UploadKind.Uploaded) {
                    _intakeNotice.OnNext(upload.Kind == UploadKind.Unauthorized ? "sign in to attach files" : upload.Reason ?? "the upload failed");
                    return;
                }
                ids = upload.Ids;
            }
            var chipIds = files.Select(f => f.Id).ToList();
            var queued = new QueuedChatMessage(snapshot, edits, _inputGeneration, TranscriptLength(_path), chipIds);
            … existing body, with `_input.SendAsync(snapshot, ids, _lifetimeToken)` …
            if (outcome == ChatSendOutcome.Accepted) { ClearSentDraft(queued); Tray.RemoveAll(chipIds); }
            …
        }, canSend);
```

- `ConfirmDelivery(queued)`: after `ClearSentDraft(queued)`, `Tray.RemoveAll(queued.AttachmentIds)`.
- `IAttachmentSink`: `public IAttachmentSink Attachments => this;` with `bool IAttachmentSink.CanAttach => _input.CanAttach && !IsReadOnlyParticipant; string? IAttachmentSink.AttachHint => _input.AttachHint; void IAttachmentSink.Accept(IntakeResult r) { var refused = Tray.AddAll(r.Accepted).Concat(r.Refused).ToList(); _intakeNotice.OnNext(refused.Count == 0 ? null : string.Join("; ", refused.Select(x => $"`{x.Name}` {x.Reason}"))); }`. The tray's own count refusals name the files (`"only 10 files per message"`) — render them as `"only 10 files per message — `c.png`, `d.png` not added"` by grouping refusals with that reason.
- `TeardownAsync`: `Tray.Clear()`.

`WorkspaceViewModel` passes `uploader` into `new ChatTabViewModel(agentId, daemon, input, uploader, projection, …)`. Update every `new ChatTabViewModel(` in tests to pass a `ScriptedUploader`/`NoAttachmentUploader`.

- [ ] **Step 4: Run the App suite** — green.
- [ ] **Step 5: Commit** — `Upload and send chat attachments, clearing only the sent chips`

---

### Task 14: Chat view — chip strip, "+" button, paste and drop behaviour

**Files:**
- Create: `src/Capacitor.App/Views/AttachmentChipStrip.axaml`, `AttachmentChipStrip.axaml.cs` (an `ItemsControl` over `Tray.Items`, each chip: 32×32 thumbnail or glyph, name with `TextTrimming="CharacterEllipsis"`, `SizeLabel`, a remove `Button` bound to a `RemoveCommand` on `StagedAttachmentViewModel`), `src/Capacitor.App/ViewModels/StagedAttachmentViewModel.cs` (wraps a `StagedAttachment`, lazy `Thumbnail` decoded on a worker via `Bitmap.DecodeToWidth(stream, 64)` and published on the UI thread; `Dispose` frees it), `src/Capacitor.App/Views/AttachmentDropPaste.cs`.
- Modify: `src/Capacitor.App/Views/ChatTabView.axaml` (strip above `ComposerInput`; a `Button x:Name="AttachButton" Content="+"` in the footer `Grid` left of the hint, `IsEnabled="{Binding Attachments.CanAttach}"`, `ToolTip.Tip="{Binding Attachments.AttachHint}"`), `ChatTabView.axaml.cs` (`AttachmentDropPaste.Attach(card: ComposerCard, textBox: ComposerInput, pickButton: AttachButton, sink: () => (DataContext as ChatTabViewModel)?.Attachments, time: TimeProvider.System)`; give the composer `Border` `x:Name="ComposerCard"`).
- Test: `test/Capacitor.App.Tests.Unit/AttachmentDropPasteTests.cs` (behaviour, with fakes), `test/Capacitor.App.Tests.Unit/ChatTabViewSmokeTests.cs` (extend: chips render/remove; a drop stages; a text paste pastes once).

**Interfaces (produces):**

```csharp
public sealed class AttachmentDropPaste : IDisposable {
    public static AttachmentDropPaste Attach(Control card, TextBox textBox, Button? pickButton, Func<IAttachmentSink?> sink, TimeProvider time,
        Func<Task<IAsyncDataTransfer?>>? clipboard = null,          // test seam; default TopLevel.GetTopLevel(textBox)?.Clipboard?.TryGetDataAsync()
        Func<Task<IReadOnlyList<IStorageFile>>>? picker = null);    // test seam; default OpenFilePickerAsync
    public Task? PendingIntakeForTesting { get; }
    public void Dispose();                                          // cancels the lifetime token, detaches handlers
}
```

- [ ] **Step 1: Write the failing tests**

`AttachmentDropPasteTests` — build a real `TextBox` inside a `Border` under a headless `Window` (see `ChatTabViewSmokeTests.Host` for the window setup), a `RecordingSink : IAttachmentSink` (records `Accept` calls, `CanAttachValue`), a `FakeAsyncDataTransfer : IAsyncDataTransfer` (formats, text, bitmap, files, `Disposed` count, optional `ThrowOnRead`):

```csharp
    [Test] public async Task Files_on_the_clipboard_are_staged_and_the_transfer_is_disposed_once() { … raise textBox.RaiseEvent(new RoutedEventArgs(TextBox.PastingFromClipboardEvent)); await behaviour.PendingIntakeForTesting!; sink.Accepted.Single().Accepted has the file; transfer.Disposed == 1; textBox.Text == "" }
    [Test] public async Task Text_on_the_clipboard_pastes_exactly_once() { … FakeAsyncDataTransfer(text: "hello") and a headless clipboard set to "hello"; after the intake textBox.Text == "hello"; transfer.Disposed == 1 }
    [Test] public async Task Bitmap_is_encoded_staged_and_disposed() { … }
    [Test] public async Task Nothing_and_thrown_read_and_cancellation_dispose_once_and_never_throw() {
        // Nothing → sink untouched, Disposed 1
        // ThrowOnRead → sink.Accepted.Single().Refused == [("clipboard", "the clipboard could not be read")], Disposed 1, no UnobservedTaskException (hook TaskScheduler.UnobservedTaskException, GC.Collect, WaitForPendingFinalizers)
        // dispose the behaviour mid-read (a transfer whose read awaits a TCS) → nothing reaches the sink, Disposed 1, no throw
    }
    [Test] public async Task Second_paste_during_an_intake_is_dropped_and_can_attach_false_pastes_text_but_refuses_files() { … }
    [Test] public async Task Drop_stages_files_and_drag_over_reports_copy_only_when_files_and_can_attach() { … raise DragDrop.DropEvent with a DataTransfer carrying a FakeStorageFile … }
    [Test] public async Task Pick_stages_files_and_a_picker_that_outlives_the_card_stages_nothing_and_faults_nothing() {
        // picker seam returns a TCS-backed task; dispose the behaviour; complete the TCS with files → sink untouched; complete another with an exception → no UnobservedTaskException
    }
```

Write them in full against the seams above.

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement** `AttachmentDropPaste.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

/// Paste, drop and pick for one prompt card. One intake at a time; nothing escapes a handler.
public sealed class AttachmentDropPaste : IDisposable {
    readonly Control _card; readonly TextBox _textBox; readonly Button? _pick;
    readonly Func<IAttachmentSink?> _sink; readonly TimeProvider _time;
    readonly Func<Task<IAsyncDataTransfer?>> _clipboard; readonly Func<Task<IReadOnlyList<IStorageFile>>> _picker;
    readonly CancellationTokenSource _lifetime = new();
    bool _reentrantPaste; Task? _intake;

    public Task? PendingIntakeForTesting => _intake;

    public static AttachmentDropPaste Attach(Control card, TextBox textBox, Button? pickButton, Func<IAttachmentSink?> sink, TimeProvider time,
            Func<Task<IAsyncDataTransfer?>>? clipboard = null, Func<Task<IReadOnlyList<IStorageFile>>>? picker = null) {
        var b = new AttachmentDropPaste(card, textBox, pickButton, sink, time,
            clipboard ?? (() => TopLevel.GetTopLevel(textBox)?.Clipboard?.TryGetDataAsync() ?? Task.FromResult<IAsyncDataTransfer?>(null)),
            picker ?? (async () => {
                var storage = TopLevel.GetTopLevel(textBox)?.StorageProvider;
                return storage is null ? [] : await storage.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true });
            }));
        textBox.AddHandler(TextBox.PastingFromClipboardEvent, b.OnPasting, RoutingStrategies.Bubble);
        DragDrop.SetAllowDrop(card, true);
        card.AddHandler(DragDrop.DragEnterEvent, b.OnDragOver); card.AddHandler(DragDrop.DragOverEvent, b.OnDragOver);
        card.AddHandler(DragDrop.DragLeaveEvent, b.OnDragLeave); card.AddHandler(DragDrop.DropEvent, b.OnDrop);
        if (pickButton is not null) pickButton.Click += b.OnPick;
        return b;
    }

    void OnPasting(object? sender, RoutedEventArgs e) {
        if (_reentrantPaste) return;      // our own TextBox.Paste() for the text case
        e.Handled = true;
        StartIntake(async ct => {
            var transfer = await _clipboard();
            if (transfer is null) return IntakeResult.Empty;
            using (transfer) {
                var text = transfer.Contains(DataFormat.Text) ? await transfer.TryGetTextAsync() : null;
                var kind = AttachmentIntake.Classify(transfer.Formats.ToList(), !string.IsNullOrWhiteSpace(text));
                var sink = _sink();
                switch (kind) {
                    case IntakeKind.Text:
                        _reentrantPaste = true;
                        try { _textBox.Paste(); } finally { _reentrantPaste = false; }
                        return null;
                    case IntakeKind.Files:
                        if (sink is { CanAttach: false }) return Refusal(sink);
                        return await AttachmentIntake.ReadFilesAsync((await transfer.TryGetFilesAsync()) ?? [], ct);
                    case IntakeKind.Bitmap:
                        if (sink is { CanAttach: false }) return Refusal(sink);
                        using (var bitmap = await transfer.TryGetBitmapAsync()) return bitmap is null ? IntakeResult.Empty : AttachmentIntake.FromBitmap(bitmap, _time);
                    default: return null;
                }
            }
        }, "the clipboard could not be read");
    }

    void OnDragOver(object? sender, DragEventArgs e) {
        var ok = e.DataTransfer.Contains(DataFormat.File) && _sink() is { CanAttach: true };
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        _card.Classes.Set("dragOver", ok);
        e.Handled = true;
    }
    void OnDragLeave(object? sender, DragEventArgs e) => _card.Classes.Set("dragOver", false);
    void OnDrop(object? sender, DragEventArgs e) {
        _card.Classes.Set("dragOver", false);
        var files = e.DataTransfer.TryGetFiles()?.ToList() ?? [];
        e.Handled = true;
        StartIntake(ct => {
            var sink = _sink();
            return sink is { CanAttach: false } ? Task.FromResult<IntakeResult?>(Refusal(sink)) : AttachmentIntake.ReadFilesAsync(files, ct)!;
        }, "the dropped files could not be read");
    }
    void OnPick(object? sender, RoutedEventArgs e) => StartIntake(async ct => {
        var sink = _sink();
        if (sink is { CanAttach: false }) return Refusal(sink);
        var picking = _picker();
        IReadOnlyList<IStorageFile> files;
        try { files = await picking.WaitAsync(ct); }
        catch (OperationCanceledException) { _ = picking.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); throw; }
        return await AttachmentIntake.ReadFilesAsync(files, ct);
    }, "the file picker could not be opened");

    static IntakeResult Refusal(IAttachmentSink sink) => new([], [new("attachments", sink.AttachHint ?? "attachments are not available")]);

    void StartIntake(Func<CancellationToken, Task<IntakeResult?>> work, string failureWording) {
        if (_intake is { IsCompleted: false }) return;
        var ct = _lifetime.Token;
        _intake = Dispatcher.UIThread.InvokeAsync(async () => {
            IntakeResult? result;
            try { result = await work(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { Console.Error.WriteLine($"attachment intake: {ex.Message}"); result = new IntakeResult([], [new("clipboard", failureWording)]); }
            if (ct.IsCancellationRequested || result is null) return;
            _sink()?.Accept(result);
        }).GetTask();
    }

    public void Dispose() {
        _lifetime.Cancel();
        _textBox.RemoveHandler(TextBox.PastingFromClipboardEvent, OnPasting);
        _card.RemoveHandler(DragDrop.DragEnterEvent, OnDragOver); _card.RemoveHandler(DragDrop.DragOverEvent, OnDragOver);
        _card.RemoveHandler(DragDrop.DragLeaveEvent, OnDragLeave); _card.RemoveHandler(DragDrop.DropEvent, OnDrop);
        if (_pick is not null) _pick.Click -= OnPick;
        _lifetime.Dispose();
    }
}
```

The refusal `Name` for the failure case is `"clipboard"` for paste, `"dropped files"` for drop, `"file picker"` for pick, so the rendered line reads "`clipboard` the clipboard could not be read" — acceptable; adjust the sink's rendering to omit the backticked name when the reason already starts with "the ". Register the `PastingFromClipboard` handler with `RoutingStrategies.Tunnel` if the bubble registration fires after the TextBox has already pasted (verify in the smoke test; the TextBox raises the event before reading the clipboard, so bubble should be fine).

`ChatTabView.axaml.cs`: field `AttachmentDropPaste? _attachments;`, created in the constructor after `InitializeComponent()`, disposed in `OnDetachedFromVisualTree`. Add a `Styles` entry `Border.dragOver { BorderBrush = KcapPrimaryBrush }` for the card.

- [ ] **Step 4: Run the App suite** — green.
- [ ] **Step 5: Commit** — `Paste, drop and pick attachments into the chat composer`

---

### Task 15: Home — draft snapshot, upload, gate, retained draft and restore

**Files:**
- Modify: `src/Capacitor.App/Services/ILaunchClient.cs` (`LaunchRequest` gains `IReadOnlyList<string>? AttachmentIds = null`; `LaunchPayload.For` sets `AttachmentIds = r.AttachmentIds is { Count: > 0 } ids ? [.. ids] : null`)
- Create: `src/Capacitor.App/Services/LaunchAttachments.cs`, `src/Capacitor.App/ViewModels/LaunchDraft.cs`
- Modify: `src/Capacitor.App/ViewModels/HomeViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/HomeAttachmentsTests.cs` (new), `LaunchRequestTests.cs` (extend), `LaunchAttachmentsTests.cs` (new)

**Interfaces (produces):**

```csharp
public static class LaunchAttachments {
    /// The kcap release whose daemon fails a launch closed on a missing attachment. Set to the version the implementing PR ships in.
    public static readonly Version MinDaemonVersion = new(1, 0, 4);
    public static bool IsCapable(string? daemonVersion);   // SemVer core parses and >= MinDaemonVersion; prerelease/build suffix ignored; unparsable → false
}
public sealed record LaunchDraft(string Machine, bool Remote, string RepoPath, string Vendor, string Goal, int GoalEdits,
    string Model, string? Effort, string? PermissionMode, IReadOnlyList<StagedAttachment> Files, int TrayGeneration);
// HomeViewModel additions
public AttachmentTray Tray { get; }
public bool Uploading { get; }
public bool CanAttach { get; }            // reactive; signed in && machine capable
public string? AttachHint { get; }
public IAttachmentSink Attachments { get; }
// ctor gains: IAttachmentUploader? uploader = null, TimeProvider? time = null
```

Wording: `StartError` on retention: `FriendlyLaunchFailure(reason) + " — your draft is back"` / `" — re-attach the files to send them again"`; unusable id: `UnusableIdMessage + " — re-attach the files if it did not start"`; `attachment_unavailable:` prefix → `"the attached files could not be delivered to the machine"`.

- [ ] **Step 1: Write the failing tests**

`LaunchAttachmentsTests`: `IsCapable("1.0.4")` true, `"1.0.4-alpha.2"` true, `"1.0.3"` false, `"garbage"` false, `null` false, `"2.0.0"` true.

`LaunchRequestTests` (extend): payload has `attachment_ids` only when non-empty.

`HomeAttachmentsTests` (harness from `HomeViewModelTests.Build`, plus `ScriptedUploader`, `FakeTimeProvider`, a `Subject<LaunchFailure>` and a `FakeAgentDirectory`):

```csharp
    [Test] public async Task Can_attach_follows_sign_in_input_2_locally_and_the_version_gate_remotely() { … }
    [Test] public async Task Launch_is_built_from_the_captured_draft_and_the_gate_reruns_against_it() {
        // stage a; Goal "g"; arm upload TCS; Start; while pending: change SelectedRepoPath/vendor; complete upload
        // launch.Last.RepoPath == original; launch.Last.AttachmentIds == [A]; launch.Last.Prompt == "g"
    }
    [Test] public async Task Empty_goal_with_files_launches() { … }
    [Test] public async Task Upload_failure_sets_start_error_and_launches_nothing() { … }
    [Test] public async Task Started_clears_only_what_was_sent() {
        // goal edited during upload → kept; file added during upload → only chip left; file removed during upload → stays gone
    }
    [Test] public async Task Unusable_id_retains_no_draft_and_says_so() { … }
    [Test] public async Task Delayed_failure_restores_a_draft_this_launch_emptied_with_the_real_reason() {
        // Start (goal "g", files [a]) → Started; composer blank; LaunchFailure(LaunchedId, "boom") → StartError "boom — your draft is back"; Goal "g"; Tray [a]
    }
    [Test] public async Task Delayed_failure_does_not_restore_over_user_edits_or_a_changed_target() {
        // (a) user types "new" before failure → "boom — re-attach the files to send them again", Goal "new"
        // (b) user blanked the goal themselves during the request (edit count moved) → no restore
        // (c) add-then-remove a chip during the request → no restore
        // (d) chip added during the request still present at failure → no restore
        // (e) machine/repo changed before failure → no restore
    }
    [Test] public async Task Second_accepted_attachment_launch_replaces_the_retained_bytes_but_the_first_still_reads_re_attach() { … }
    [Test] public async Task Retention_expires_ten_minutes_after_upload_on_the_clock_and_a_late_failure_is_untracked() { … time.Advance(11 min) … }
    [Test] public async Task Row_confirmation_and_buffered_failure_before_registration_settle_the_draft() { … }
    [Test] public async Task Non_attachment_failure_reason_with_files_keeps_the_real_reason() { … }
```

Write each in full.

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement**

`LaunchAttachments.cs`:

```csharp
namespace Capacitor.App.Services;

public static class LaunchAttachments {
    public static readonly Version MinDaemonVersion = new(1, 0, 4);

    public static bool IsCapable(string? daemonVersion) {
        if (string.IsNullOrWhiteSpace(daemonVersion)) return false;
        var core = daemonVersion.Split('-', '+')[0];
        return Version.TryParse(core, out var v) && Normalize(v) >= MinDaemonVersion;
    }

    static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}
```

`HomeViewModel`:
- `Tray`, `Uploading`, `_intakeNotice` (feeds `StartError` when non-null; clears on `Goal` edits and the next intake).
- `CanAttach` OAPH over `signInRequired`, `_machineSelectionChanges`, `_daemons`, the daemon `Status` (for `input/2`): local ⇒ `caps.Contains("input/2")`; remote ⇒ `LaunchAttachments.IsCapable(FindMachine(list, name, _lastViewerId)?.Version)` (extend `MachineOption` with `string? Version` populated from `DaemonInfo.Version`). `AttachHint`: signed out ⇒ `"sign in to attach files"`, else `"attachments need the daemon updated"`, else null.
- `Start` gating: `canLaunch` additionally requires `!(Tray.Count > 0 && !CanAttach)` and `!Uploading`.
- `StartAsync`:

```csharp
        var draft = new LaunchDraft(SelectedMachine, RemoteMachineSelected, SelectedRepoPath, SelectedVendor, Goal, _goalEdits,
            SelectedModel, SelectedEffort, PermissionModeFor(SelectedVendor, SelectedPermissionMode), Tray.Snapshot(), Tray.Generation);
        if (draft.Files.Count > 0 && !CanAttachFor(draft)) { StartError = AttachHintFor(draft); return; }
        IReadOnlyList<string>? ids = null; DateTimeOffset? uploadedAt = null;
        if (draft.Files.Count > 0) {
            Uploading = true;
            UploadOutcome upload;
            try { upload = await _uploader.UploadAsync(draft.Files, _shutdown); } finally { Uploading = false; }
            if (upload.Kind == UploadKind.Unauthorized) { _signInRequired.OnNext(true); StartError = "sign in to attach files"; return; }
            if (upload.Kind != UploadKind.Uploaded) { StartError = upload.Reason ?? "the upload failed"; return; }
            ids = upload.Ids; uploadedAt = _time.GetUtcNow();
            if (draft.Remote) { var owned = await OwnRemoteDaemonsAsync(); if (!owned.Any(d => d.Name == draft.Machine && d.Connected)) { StartError = MachineUnavailableMessage; return; } }
            if (!CanAttachFor(draft)) { StartError = AttachHintFor(draft); return; }
        }
        var request = new LaunchRequest(draft.Machine, draft.RepoPath, draft.Vendor, draft.Goal, draft.Model, draft.Effort, draft.PermissionMode, ids);
        … existing remote ownership check moves above, keyed on draft.Remote/draft.Machine …
        var outcome = await _launch.StartAsync(request, _shutdown);
        … failure handling as today …
        var clearedGoal = _goalEdits == draft.GoalEdits;
        if (clearedGoal) Goal = "";
        var trayUntouched = Tray.Generation == draft.TrayGeneration;
        Tray.RemoveAll(draft.Files.Select(f => f.Id).ToList());
        var clearedTray = trayUntouched && Tray.Count == 0;
        if (NormalizeAgentId(outcome.AgentId) is not { } agentId) { StartError = draft.Files.Count > 0 ? UnusableIdMessage + " — re-attach the files if it did not start" : UnusableIdMessage; return; }
        RecordPendingLaunch(agentId, hadAttachments: draft.Files.Count > 0, uploadedAt);
        if (draft.Files.Count > 0) _retainedDraft = new RetainedDraft(agentId, draft, uploadedAt!.Value, clearedGoal, _goalEdits, clearedTray, Tray.Generation);
```

`Goal`'s setter increments `_goalEdits`. `_pendingLaunches` becomes `Dictionary<string, PendingLaunch>` with `record PendingLaunch(DateTime At, bool HadAttachments, DateTimeOffset? UploadedAt)`. `ApplyFailureIfPending` / the buffered-failure path in `RecordPendingLaunch`:

```csharp
    void ApplyLaunchFailure(string agentId, PendingLaunch pending, string reason) {
        var friendly = FriendlyLaunchFailure(reason);
        if (!pending.HadAttachments) { StartError = friendly; return; }
        var retained = _retainedDraft is { } r && r.AgentId == agentId && _time.GetUtcNow() - r.UploadedAt <= RetentionTtl ? r : null;
        var restorable = retained is not null
            && retained.ClearedGoal && _goalEdits == retained.GoalEditsAfterClear
            && retained.ClearedTray && Tray.Generation == retained.TrayGenerationAfterClear
            && retained.Draft.Machine == SelectedMachine && retained.Draft.RepoPath == SelectedRepoPath && retained.Draft.Vendor == SelectedVendor;
        if (restorable) { Goal = retained!.Draft.Goal; Tray.Restore(retained.Draft.Files); StartError = friendly + " — your draft is back"; }
        else StartError = friendly + " — re-attach the files to send them again";
        if (_retainedDraft?.AgentId == agentId) _retainedDraft = null;
    }
```

`FriendlyLaunchFailure`: add `reason.StartsWith("attachment_unavailable:") ? "the attached files could not be delivered to the machine" : …`. `ConfirmPendingRows` drops `_retainedDraft` when its id is confirmed. A `_time.CreateTimer` at one-minute period (started in the ctor, disposed with the VM) releases `_retainedDraft` and pending entries older than `RetentionTtl = TimeSpan.FromMinutes(10)` from upload. `IAttachmentSink` implemented as in Chat (notice → `StartError`). `Dispose` clears the tray and the retained draft.

- [ ] **Step 4: Run the App suite** — green.
- [ ] **Step 5: Commit** — `Attach files to a launch and keep the draft until the daemon confirms`

---

### Task 16: Launcher view — chip strip, "+" and drop on the goal card

**Files:**
- Modify: `src/Capacitor.App/Views/LauncherPaneView.axaml` (the goal `Border` gets `x:Name="GoalCard"`; `<views:AttachmentChipStrip DataContext="{Binding Tray}"/>` above `GoalInput`; a `Button x:Name="AttachButton" Content="+"` in the chip `WrapPanel`, `IsEnabled="{Binding CanAttach}"`, `ToolTip.Tip="{Binding AttachHint}"`, `ToolTip.ShowOnDisabled="True"`), `LauncherPaneView.axaml.cs` (`AttachmentDropPaste.Attach(GoalCard, GoalInput, AttachButton, () => (DataContext as HomeViewModel)?.Attachments, TimeProvider.System)`, disposed on detach). `StartButton`'s content shows "Uploading…" tooltip while `Uploading`.
- Test: `test/Capacitor.App.Tests.Unit/HomeViewSmokeTests.cs` (extend: staging a file renders a chip; removing it updates; a drop on the card stages).

- [ ] Steps 1–5 as in Task 14, commit `Paste, drop and pick attachments into the launcher`.

---

### Task 17: Composition — uploader wiring in `App.axaml.cs`

**Files:**
- Modify: `src/Capacitor.App/App.axaml.cs` — build `var uploader = new ServerAttachmentUploader(ServerHttp(profiles), profiles);` where `ServerSessionHttp.DetailReader`/`Responder` are built (line ~549); pass it to the `WorkspaceViewModel` factory and to `new HomeViewModel(…, uploader: uploader, time: TimeProvider.System)`. Where no profile exists the `ServerAttachmentUploader` already answers `Unauthorized`.
- Test: `test/Capacitor.App.Tests.Unit/AppStartupTests.cs` (extend if it asserts on constructor wiring; otherwise a build is the check).

- [ ] Build `src/Capacitor.App/Capacitor.App.csproj` with zero warnings (AVLN included), run the App suite, commit `Wire the attachment uploader into the desktop app`.

---

### Task 18: Docs and AOT check

**Files:**
- Modify: `docs/CHANGES.md` — one entry "Desktop attachments (AI-2318)" in the house style: the placement-by-containment rule, the new frame and why not a trailing field, fail-closed batches, the accepted downgrade gap, the web-visible changes.
- README: no CLI surface changed; no edit.

- [ ] Run `~/.dotnet/dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` — expect no output.
- [ ] Run every suite: `~/.dotnet/dotnet test --solution Capacitor.slnx` (expect the ~45 environmental install/config failures noted in memory, nothing else).
- [ ] Run `bash scripts/check-linear-ids.sh`.
- [ ] Commit `Record the desktop attachments design in CHANGES`.

---

## By hand before the PR (record the results in the PR body)

1. Desktop → local Claude (owned worktree): paste a screenshot, attach a PDF; the agent reads both from `.attached/<batch>/`.
2. Desktop → local Codex (default kind): the same two files; the agent reads them from `~/.config/kcap/daemons/<name>/attachments/<hash>/<batch>/` via `view_image`/shell.
3. Home launch with a text file to one ACP vendor, Pi and Antigravity; each reads the relative path.
4. Kill the daemon after a successful upload, restart it on the previous kcap release: Chat "+" disabled with "attachments need the daemon updated".
