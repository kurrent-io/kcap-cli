# Daemon Settings Backend (PR 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a client change the running daemon's agent capacity over the local control socket, fix the profile capacity precedence, and let `kcap daemon service install --replace --verify` retire the unit a rename leaves behind.

**Architecture:** One new frame pair (`DaemonSettingsPut` 23 / `DaemonSettingsAck` 81) behind a `settings/1` capability, handled by a `DaemonSettingsIpc` service that mutates the live `DaemonConfig`, pulses the status notifier and republishes the registration through the orchestrator's single-flighted re-register. The CLI's verify transaction gains a `--retire <id>` step that removes another unit pinned to the same profile before installing. The desktop app is PR 2; this PR only keeps its two `ILocalControlOps` implementations compiling.

**Tech Stack:** .NET 10, NativeAOT (System.Text.Json source generation only), TUnit on Microsoft Testing Platform, Unix domain sockets.

**Spec:** `docs/superpowers/specs/2026-09-10-ai2535-desktop-daemon-settings-design.md`

## Global Constraints

- `FrameType` values are append-only: new values are exactly `DaemonSettingsPut = 23` and `DaemonSettingsAck = 81`; value 9 stays unused.
- `LocalControlCapabilities.Current` gains exactly `"settings/1"`, appended last, in the same change as its routing case.
- JSON payloads are snake_case, every member always written, new members trailing and nullable. No reflection-based serialization anywhere (IL2026/IL3050 must stay at zero on `dotnet publish -c Release`).
- Capacity validation rule in both daemon and CLI: an integer of at least 1.
- `--retire` requires `--replace --verify`; equal to the target id is an argument error; an absent unit is a no-op; a unit whose plist `KCAP_PROFILE` differs from the pinned profile is refused untouched; with `--retire` a live validated daemon under the target name is contended, never taken over.
- Commit subjects: imperative, one clause, `(#791)` at the end, at most 80 characters. Every commit ends with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- Comments: none that narrate history, tickets, reviews or specs. One or two lines naming a trap or a deliberate decision, or nothing.
- Unused `using`s are build errors (IDE0005). `Environment.GetFolderPath` is banned. Tests never use `Console.SetOut` by hand.
- Build every touched project fully before committing; warnings are cleared in the same commit.
- This is a git worktree: run git as `/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/zany-dancing-coral <args>`, one command per invocation, no heredocs. The branch is `alexeyzimarev/ai-2535-extend-desktop-app-settings-to-configure-the-daemon`.

---

## File map

| File | Responsibility |
|---|---|
| `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs` | Append the two frame values. |
| `src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs` | Route the two values through the UTF-8 text arms. |
| `src/Capacitor.Cli.Core/LocalIpc/LocalFrame.cs` | `SettingsJson` frame helper. |
| `src/Capacitor.Cli.Core/LocalIpc/SettingsIpc.cs` (new) | Payload records, reason constants, structural check, JSON context, capability constant. |
| `src/Capacitor.Cli.Core/LocalIpc/LocalControlOps.cs` | `PutDaemonSettingsAsync` on the interface and the socket client. |
| `src/Capacitor.App/Services/Onboarding/WizardLateBinding.cs` | Forward the new member. |
| `test/Capacitor.App.Tests.Unit/ScriptedLocalControlOps.cs` | Script the new member. |
| `src/Capacitor.Cli.Daemon/Services/DaemonSettingsIpc.cs` (new) | Put handler: validate, apply, pulse, republish, ack. |
| `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` | `RepublishRegistration`. |
| `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs` | Route `DaemonSettingsPut`. |
| `src/Capacitor.Cli.Daemon/Services/LocalControlCapabilities.cs` | Advertise `settings/1`. |
| `src/Capacitor.Cli.Daemon/DaemonRunner.cs` | Register the handler; `ApplyProfileCapacity`. |
| `src/Capacitor.Cli/Commands/DaemonServiceCommands.cs` | Parse and validate `--retire`. |
| `src/Capacitor.Cli/Services/ServiceVerify.cs` | `RetireAsync` inside the install transaction; `VerifyExit.RetireRefused`. |
| `README.md`, `docs/CHANGES.md` | Document the flag and the reasoning. |

---

### Task 1: Core frame pair and payloads

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs`
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs:45-87`
- Modify: `src/Capacitor.Cli.Core/LocalIpc/LocalFrame.cs`
- Create: `src/Capacitor.Cli.Core/LocalIpc/SettingsIpc.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/FrameCodecSettingsTests.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/SettingsWireContractsTests.cs`

**Interfaces:**
- Produces: `FrameType.DaemonSettingsPut` (23), `FrameType.DaemonSettingsAck` (81); `LocalFrame.SettingsJson(FrameType, string)`; `DaemonSettingsPutDto(int? MaxAgents)`; `DaemonSettingsAckDto(bool Ok, string? Reason, int? MaxAgents)`; `DaemonSettingsReasons.Malformed`, `.InvalidMaxAgents`, `.Transport`; `SettingsWire.Capability` (`"settings/1"`), `SettingsWire.HasAnySetting(DaemonSettingsPutDto?)`; `SettingsIpcJsonContext.Default.DaemonSettingsPutDto` / `.DaemonSettingsAckDto`.

- [ ] **Step 1: Write the failing codec test**

Create `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/FrameCodecSettingsTests.cs`:

```csharp
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class FrameCodecSettingsTests {
    static async Task<LocalFrame> RoundTrip(LocalFrame f) {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, f, CancellationToken.None);
        ms.Position = 0;
        return (await FrameCodec.ReadAsync(ms, CancellationToken.None))!;
    }

    [Test]
    [Arguments(FrameType.DaemonSettingsPut)]
    [Arguments(FrameType.DaemonSettingsAck)]
    public async Task Settings_frames_roundtrip_with_text_payload(FrameType type) {
        var f = await RoundTrip(LocalFrame.SettingsJson(type, """{"k":"v"}"""));
        await Assert.That(f.Type).IsEqualTo(type);
        await Assert.That(f.Text).IsEqualTo("""{"k":"v"}""");
    }

    [Test]
    public async Task Settings_frame_values_are_stable_wire_bytes() {
#pragma warning disable TUnitAssertions0005
        await Assert.That((byte)FrameType.DaemonSettingsPut).IsEqualTo((byte)23);
        await Assert.That((byte)FrameType.DaemonSettingsAck).IsEqualTo((byte)81);
#pragma warning restore TUnitAssertions0005
    }
}
```

- [ ] **Step 2: Write the failing wire-contract test**

Create `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/SettingsWireContractsTests.cs`:

```csharp
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class SettingsWireContractsTests {
    [Test]
    public async Task Put_serializes_snake_case() =>
        await Assert.That(JsonSerializer.Serialize(new DaemonSettingsPutDto(2), SettingsIpcJsonContext.Default.DaemonSettingsPutDto))
            .IsEqualTo("""{"max_agents":2}""");

    [Test]
    public async Task Ack_serializes_every_member() {
        await Assert.That(JsonSerializer.Serialize(new DaemonSettingsAckDto(true, null, 2), SettingsIpcJsonContext.Default.DaemonSettingsAckDto))
            .IsEqualTo("""{"ok":true,"reason":null,"max_agents":2}""");
        await Assert.That(JsonSerializer.Serialize(new DaemonSettingsAckDto(false, DaemonSettingsReasons.InvalidMaxAgents, 5), SettingsIpcJsonContext.Default.DaemonSettingsAckDto))
            .IsEqualTo("""{"ok":false,"reason":"invalid_max_agents","max_agents":5}""");
    }

    [Test]
    public async Task Ack_without_max_agents_deserializes_to_null() {
        var ack = JsonSerializer.Deserialize("""{"ok":true,"reason":null}""", SettingsIpcJsonContext.Default.DaemonSettingsAckDto)!;
        await Assert.That(ack.Ok).IsTrue();
        await Assert.That(ack.MaxAgents).IsNull();
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{"max_agents":null}""")]
    public async Task A_put_with_no_setting_has_nothing_to_apply(string json) {
        var dto = JsonSerializer.Deserialize(json, SettingsIpcJsonContext.Default.DaemonSettingsPutDto);
        await Assert.That(SettingsWire.HasAnySetting(dto)).IsFalse();
    }

    [Test]
    public async Task Null_is_not_a_put() =>
        await Assert.That(SettingsWire.HasAnySetting(null)).IsFalse();

    [Test]
    public async Task Capability_string_is_settings_1() =>
        await Assert.That(SettingsWire.Capability).IsEqualTo("settings/1");
}
```

- [ ] **Step 3: Run both to verify they fail**

Run:
```bash
dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/FrameCodecSettingsTests/*"
```
Expected: build error, `FrameType` has no `DaemonSettingsPut`.

- [ ] **Step 4: Append the frame values**

In `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs`, after the `SendText = 22,` line add:

```csharp
    // Daemon settings — one-shot; the ack carries the value in effect.
    DaemonSettingsPut = 23, // Text = DaemonSettingsPutDto JSON
```

and after the `SendTextAck = 80,` line add:

```csharp
    DaemonSettingsAck = 81, // Text = DaemonSettingsAckDto JSON, reply to DaemonSettingsPut
```

- [ ] **Step 5: Route them through the codec's text arms**

In `src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs`, in **both** the `Encode` and `Decode` switch expressions, change the line

```csharp
            or FrameType.SendText or FrameType.SendTextAck => Encoding.UTF8.GetBytes(f.Text),
```
to
```csharp
            or FrameType.SendText or FrameType.SendTextAck
            or FrameType.DaemonSettingsPut or FrameType.DaemonSettingsAck => Encoding.UTF8.GetBytes(f.Text),
```
and the line
```csharp
            or FrameType.SendText or FrameType.SendTextAck => new(t) { Text = Encoding.UTF8.GetString(p) },
```
to
```csharp
            or FrameType.SendText or FrameType.SendTextAck
            or FrameType.DaemonSettingsPut or FrameType.DaemonSettingsAck => new(t) { Text = Encoding.UTF8.GetString(p) },
```

- [ ] **Step 6: Add the frame helper**

In `src/Capacitor.Cli.Core/LocalIpc/LocalFrame.cs`, after `InputJson` add:

```csharp
    /// Constructs a DaemonSettingsPut or DaemonSettingsAck frame, whose payload is UTF-8 JSON
    /// (snake_case via SettingsIpcJsonContext) carried in Text — see SettingsIpc.cs.
    public static LocalFrame SettingsJson(FrameType type, string json) => new(type) { Text = json };
```

- [ ] **Step 7: Create the payload file**

Create `src/Capacitor.Cli.Core/LocalIpc/SettingsIpc.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// JSON payloads for the daemon settings frames. snake_case on the wire; every member always
/// emitted; each setting is nullable so a put names only what it changes and a later setting is
/// additive.
public sealed record DaemonSettingsPutDto(int? MaxAgents);

/// MaxAgents echoes the value in effect after the put, on success and on refusal alike.
public sealed record DaemonSettingsAckDto(bool Ok, string? Reason, int? MaxAgents);

public static class DaemonSettingsReasons {
    public const string Malformed       = "malformed";
    public const string InvalidMaxAgents = "invalid_max_agents";
    /// Client-side only: the request or the reply never crossed the socket.
    public const string Transport       = "transport";
}

public static class SettingsWire {
    /// The HelloReply capability that advertises the DaemonSettingsPut handler.
    public const string Capability = "settings/1";

    /// STJ source-gen leaves a missing member null and `{}` decodes fine, so "nothing to apply" is
    /// decided here rather than by the parser.
    public static bool HasAnySetting(DaemonSettingsPutDto? dto) =>
        dto is not null && dto.MaxAgents is not null;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(DaemonSettingsPutDto))]
[JsonSerializable(typeof(DaemonSettingsAckDto))]
public partial class SettingsIpcJsonContext : JsonSerializerContext;
```

- [ ] **Step 8: Run the two test classes to verify they pass**

Run:
```bash
dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/FrameCodecSettingsTests/*"
dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/SettingsWireContractsTests/*"
```
Expected: all tests pass (3 and 7 respectively).

- [ ] **Step 9: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/zany-dancing-coral add src/Capacitor.Cli.Core/LocalIpc/FrameType.cs src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs src/Capacitor.Cli.Core/LocalIpc/LocalFrame.cs src/Capacitor.Cli.Core/LocalIpc/SettingsIpc.cs test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/FrameCodecSettingsTests.cs test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/SettingsWireContractsTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/zany-dancing-coral commit -q -m "Add the daemon settings frame pair to the local control IPC (#791)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: Client op on `ILocalControlOps`

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/LocalControlOps.cs` (interface at line 21; add the method after `SendTextAsync`)
- Modify: `src/Capacitor.App/Services/Onboarding/WizardLateBinding.cs:11-31`
- Modify: `test/Capacitor.App.Tests.Unit/ScriptedLocalControlOps.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/LocalControlOpsTests.cs`

**Interfaces:**
- Consumes: Task 1's records, reasons, `LocalFrame.SettingsJson`, `SettingsIpcJsonContext`.
- Produces: `Task<DaemonSettingsAckDto> ILocalControlOps.PutDaemonSettingsAsync(DaemonSettingsPutDto put, CancellationToken ct)`. Transport failures throw `LocalControlOpsException` with reasons `daemon_unreachable | daemon_rejected | unexpected_reply | timed_out`, exactly like `PutConsentPolicyAsync`. On the app fake: `ArmPutSettings()`, `QueuePutSettings(bool ok, string? reason, int? maxAgents)`, `QueuePutSettingsFailure(string reason)`, `PutSettingsCalls`, `PutSettingsPayloads`.

- [ ] **Step 1: Write the failing scripted-server tests**

In `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/LocalControlOpsTests.cs`, add a script builder next to `ConsentAckThen` (around line 68):

```csharp
    static ConnScript SettingsAckThen(string json, Action<string>? capture = null) => async (_, s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);                       // expect DaemonSettingsPut
        if (f?.Type == FrameType.DaemonSettingsPut) {
            capture?.Invoke(f.Text);
            await FrameCodec.WriteAsync(s, LocalFrame.SettingsJson(FrameType.DaemonSettingsAck, json), ct);
        }
    };
```

and, after the `// ---- PutConsentPolicyAsync ----` section's last test, add:

```csharp
    // ---- PutDaemonSettingsAsync ----

    [Test]
    public async Task Put_settings_ack_ok_and_sends_snake_case() {
        if (OperatingSystem.IsWindows()) return;

        string? sent = null;
        await WithOpsAsync([SettingsAckThen("""{"ok":true,"reason":null,"max_agents":3}""", t => sent = t)], async ops => {
            var ack = await ops.PutDaemonSettingsAsync(new DaemonSettingsPutDto(3), CancellationToken.None);
            await Assert.That(ack.Ok).IsTrue();
            await Assert.That(ack.MaxAgents).IsEqualTo(3);
        });
        await Assert.That(sent).IsEqualTo("""{"max_agents":3}""");
    }

    [Test] // a refusal is an ack, not an exception — presentation is the caller's job
    public async Task Put_settings_refusal_is_returned_as_is() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([SettingsAckThen("""{"ok":false,"reason":"invalid_max_agents","max_agents":5}""")], async ops => {
            var ack = await ops.PutDaemonSettingsAsync(new DaemonSettingsPutDto(0), CancellationToken.None);
            await Assert.That(ack.Ok).IsFalse();
            await Assert.That(ack.Reason).IsEqualTo(DaemonSettingsReasons.InvalidMaxAgents);
            await Assert.That(ack.MaxAgents).IsEqualTo(5);
        });
    }

    [Test]
    public async Task Put_settings_malformed_ack_is_unexpected_reply() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([SettingsAckThen("not json")], async ops => {
            var ex = await Assert.ThrowsAsync<LocalControlOpsException>(
                async () => await ops.PutDaemonSettingsAsync(new DaemonSettingsPutDto(3), CancellationToken.None));
            await Assert.That(ex!.Reason).IsEqualTo("unexpected_reply");
        });
    }

    [Test]
    public async Task Put_settings_error_frame_is_daemon_rejected() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([ErrorThen("expected Spawn/Attach")], async ops => {
            var ex = await Assert.ThrowsAsync<LocalControlOpsException>(
                async () => await ops.PutDaemonSettingsAsync(new DaemonSettingsPutDto(3), CancellationToken.None));
            await Assert.That(ex!.Reason).IsEqualTo("daemon_rejected");
        });
    }
```

- [ ] **Step 2: Run to verify they fail**

Run:
```bash
dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/LocalControlOpsTests/*"
```
Expected: build error, `ILocalControlOps` has no `PutDaemonSettingsAsync`.

- [ ] **Step 3: Add the interface member and the client implementation**

In `src/Capacitor.Cli.Core/LocalIpc/LocalControlOps.cs`, add to `ILocalControlOps` after `SendTextAsync`:

```csharp
    Task<DaemonSettingsAckDto> PutDaemonSettingsAsync(DaemonSettingsPutDto put, CancellationToken ct);
```

and in `LocalControlOps`, after `SendTextAsync`:

```csharp
    public async Task<DaemonSettingsAckDto> PutDaemonSettingsAsync(DaemonSettingsPutDto put, CancellationToken ct) {
        var json  = JsonSerializer.Serialize(put, SettingsIpcJsonContext.Default.DaemonSettingsPutDto);
        var reply = await ExchangeAsync(LocalFrame.SettingsJson(FrameType.DaemonSettingsPut, json), ReplyTimeout, ct);
        switch (reply.Type) {
            case FrameType.DaemonSettingsAck:
                var ack = DeserializeOrThrow(reply.Text, SettingsIpcJsonContext.Default.DaemonSettingsAckDto, "malformed daemon settings ack reply");
                if (ack is null) throw new LocalControlOpsException(UnexpectedReply, "malformed daemon settings ack reply");
                return ack; // Ok=false with a reason is returned as-is, never thrown
            case FrameType.Error:
                throw new LocalControlOpsException(DaemonRejected, reply.Text);
            default:
                throw new LocalControlOpsException(UnexpectedReply, $"unexpected daemon response to daemon settings put ({reply.Type})");
        }
    }
```

- [ ] **Step 4: Forward it on the two app-side implementations**

In `src/Capacitor.App/Services/Onboarding/WizardLateBinding.cs`, inside `LateBoundLocalControlOps` after `SendTextAsync`:

```csharp
    public Task<DaemonSettingsAckDto> PutDaemonSettingsAsync(DaemonSettingsPutDto put, CancellationToken ct) =>
        bind().PutDaemonSettingsAsync(put, ct);
```

In `test/Capacitor.App.Tests.Unit/ScriptedLocalControlOps.cs`, add the queue and counters beside the existing ones:

```csharp
    readonly Queue<TaskCompletionSource<DaemonSettingsAckDto>> _settingsPuts = new();
    public int PutSettingsCalls;
    public readonly List<DaemonSettingsPutDto> PutSettingsPayloads = [];
```

the arming helpers after `QueueSendText`:

```csharp
    public TaskCompletionSource<DaemonSettingsAckDto> ArmPutSettings() {
        var tcs = new TaskCompletionSource<DaemonSettingsAckDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _settingsPuts.Enqueue(tcs);
        return tcs;
    }

    public void QueuePutSettings(bool ok, string? reason, int? maxAgents) => ArmPutSettings().SetResult(new DaemonSettingsAckDto(ok, reason, maxAgents));
    public void QueuePutSettingsFailure(string reason) => ArmPutSettings().SetException(new LocalControlOpsException(reason, reason));
```

and the member after `SendTextAsync`:

```csharp
    public Task<DaemonSettingsAckDto> PutDaemonSettingsAsync(DaemonSettingsPutDto put, CancellationToken ct) {
        Interlocked.Increment(ref PutSettingsCalls);
        PutSettingsPayloads.Add(put);
        if (ct.IsCancellationRequested) return Task.FromCanceled<DaemonSettingsAckDto>(ct);
        if (_settingsPuts.Count == 0) throw new InvalidOperationException("ScriptedLocalControlOps: unscripted PutDaemonSettings call");
        var tcs = _settingsPuts.Dequeue();
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }
```

Then confirm nothing else implements the interface:
```bash
rtk proxy grep -rn ": ILocalControlOps" src/ test/
```
Expected: exactly `LocalControlOps`, `LateBoundLocalControlOps`, `ScriptedLocalControlOps`. Any other hit gets the same forwarding member.

- [ ] **Step 5: Run the Core tests and build the app projects**

Run:
```bash
dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/LocalControlOpsTests/*"
dotnet build src/Capacitor.App/Capacitor.App.csproj
dotnet build test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj
```
Expected: all `LocalControlOpsTests` pass; both builds succeed with zero warnings.

- [ ] **Step 6: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/zany-dancing-coral add src/Capacitor.Cli.Core/LocalIpc/LocalControlOps.cs src/Capacitor.App/Services/Onboarding/WizardLateBinding.cs test/Capacitor.App.Tests.Unit/ScriptedLocalControlOps.cs test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/LocalControlOpsTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/zany-dancing-coral commit -q -m "Expose a daemon settings put on the local control ops (#791)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: Daemon handler, routing, capability, republish

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/DaemonSettingsIpc.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs:1266-1290` (add `RepublishRegistration` beside `RefreshAdvertisedCapabilities`)
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs:14-17,44-69`
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalControlCapabilities.cs`
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs:402` (registration)
- Modify (constructor call sites): `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalControlHelloTests.cs`, `LocalControlProbeTests.cs`, `ConsentRulesPutV2Tests.cs`, `LaunchConsentIpcTests.cs`, `AgentOrchestratorLocalAttachTests.cs`, `LocalControlOpsV2PutTests.cs`, `DaemonStatusIpcTests.cs`
- Modify: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalControlCapabilitiesTests.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/DaemonSettingsIpcTests.cs`

**Interfaces:**
- Consumes: Task 1 records and context; `DaemonStatusNotifier.Pulse()`; `AgentOrchestrator.RefreshAdvertisedCapabilities(string, bool)`; `DaemonConfig.MaxConcurrentAgents`.
- Produces: `internal sealed partial class DaemonSettingsIpc(DaemonConfig, AgentOrchestrator, DaemonStatusNotifier, ILogger<DaemonSettingsIpc>)` with `Task HandlePutAsync(string payload, Stream stream, CancellationToken ct)`; `internal void AgentOrchestrator.RepublishRegistration(string reason)`; `LocalControlServer` constructor gains `DaemonSettingsIpc settingsIpc` after `statusIpc`.

- [ ] **Step 1: Write the failing real-socket test**

Create `test/Capacitor.Cli.Daemon.Tests.Unit/Services/DaemonSettingsIpcTests.cs`:

```csharp
using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// DaemonSettingsPut over a real Unix socket through the routing switch: a valid put changes the
/// live cap the next launch and the next status snapshot read, and republishes the registration;
/// an invalid or malformed put changes nothing and names why.
/// </summary>
[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class DaemonSettingsIpcTests {
    sealed class NoopRestartStrategy : IRestartStrategy {
        public RestartOutcome Restart() => RestartOutcome.NoOp;
    }

    sealed record Harness(LocalControlServer Server, AgentOrchestrator Orchestrator, SeqCaptureServerConnection Connection, DaemonConfig Config, string SockPath);

    static async Task<Harness> StartAsync(CancellationToken ct) {
        var server = new SeqCaptureServerConnection();
        DaemonConfig? captured = null;
        var orchestrator = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher> { ["claude"] = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude") },
            configure: c => captured = c);
        var config = captured!;

        var stateRoot   = config.Store.StateDirectory(config.Name);
        var consentIpc  = new LaunchConsentIpc(new LaunchConsentBroker(), new LaunchConsentStore(stateRoot, NullLogger.Instance), config, NullLogger<LaunchConsentIpc>.Instance);
        var permissionIpc = new PermissionIpc(new PermissionPromptBroker(), NullLogger<PermissionIpc>.Instance);
        var notifier    = new DaemonStatusNotifier();
        var statusIpc   = new DaemonStatusIpc(config, orchestrator, server, notifier);
        var settingsIpc = new DaemonSettingsIpc(config, orchestrator, notifier, NullLogger<DaemonSettingsIpc>.Instance);
        var restart     = RestartCoordinator.ForTest(config.Store, config.Name, "test", new NoopRestartStrategy());
        var control     = new LocalControlServer(config, orchestrator, restart, consentIpc, permissionIpc, statusIpc, settingsIpc, NullLogger<LocalControlServer>.Instance);
        await control.StartAsync(ct);

        var sockPath = config.Store.SocketPath(config.Name);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(sockPath) && DateTime.UtcNow < deadline) await Task.Delay(20, ct);

        return new Harness(control, orchestrator, server, config, sockPath);
    }

    static async Task StopAsync(Harness h) {
        await h.Orchestrator.DisposeAsync();
        await h.Server.StopAsync(CancellationToken.None);
        h.Server.Dispose();
    }

    static async Task RunAsync(Func<Harness, CancellationToken, Task> body) {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Harness? h = null;
        try {
            h = await StartAsync(cts.Token);
            await Assert.That(File.Exists(h.SockPath)).IsTrue();
            await body(h, cts.Token);
        } finally {
            if (h is not null) await StopAsync(h);
        }
    }

    static async Task<NetworkStream> ConnectAsync(string sockPath, CancellationToken ct) {
        var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await sock.ConnectAsync(new UnixDomainSocketEndPoint(sockPath), ct);
        return new NetworkStream(sock, ownsSocket: true);
    }

    static async Task<DaemonSettingsAckDto> PutAsync(Harness h, string payload, CancellationToken ct) {
        await using var s = await ConnectAsync(h.SockPath, ct);
        await FrameCodec.WriteAsync(s, LocalFrame.SettingsJson(FrameType.DaemonSettingsPut, payload), ct);
        var reply = await FrameCodec.ReadAsync(s, ct);
        await Assert.That(reply!.Type).IsEqualTo(FrameType.DaemonSettingsAck);
        return JsonSerializer.Deserialize(reply.Text, SettingsIpcJsonContext.Default.DaemonSettingsAckDto)!;
    }

    static async Task<DaemonStatusDto> FirstSnapshotAsync(Harness h, CancellationToken ct) {
        await using var s = await ConnectAsync(h.SockPath, ct);
        await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.StatusSubscribe), ct);
        var frame = await FrameCodec.ReadAsync(s, ct);
        await Assert.That(frame!.Type).IsEqualTo(FrameType.DaemonStatus);
        return JsonSerializer.Deserialize(frame.Text, StatusIpcJsonContext.Default.DaemonStatusDto)!;
    }

    [Test]
    public async Task A_valid_put_applies_live_shows_in_the_snapshot_and_republishes() {
        await RunAsync(async (h, ct) => {
            var ack = await PutAsync(h, """{"max_agents":2}""", ct);

            await Assert.That(ack).IsEqualTo(new DaemonSettingsAckDto(true, null, 2));
            await Assert.That(h.Config.MaxConcurrentAgents).IsEqualTo(2);
            await Assert.That((await FirstSnapshotAsync(h, ct)).Daemon.MaxAgents).IsEqualTo(2);

            await h.Orchestrator.CapabilityRefreshForTest;
            await Assert.That(h.Connection.RegisterDaemonCalls).IsEqualTo(1);
        });
    }

    [Test]
    public async Task The_next_launch_over_the_new_cap_is_refused() {
        await RunAsync(async (h, ct) => {
            h.Orchestrator.SeedAgentForTest("s1");
            await PutAsync(h, """{"max_agents":1}""", ct);

            await h.Orchestrator.HandleLaunchAgentForTest(new LaunchAgentCommand(
                AgentId: "cap", Prompt: "hi", Model: "opus", Effort: null,
                RepoPath: "/tmp/does-not-matter", Tools: null, AttachmentIds: null, Vendor: "claude",
                Epoch: h.Orchestrator.DaemonEpochForTest, Seq: 1, CommandId: "cmd-1"));
            await WaitHarness.SpinUntilAsync(() => h.Connection.Rejects.Count > 0, TimeSpan.FromSeconds(10));

            await Assert.That(h.Connection.Rejects.Single().Reason).IsEqualTo(CommandRejectedReason.DaemonCapacity);
            await Assert.That(h.Orchestrator.ReadLiveness("s1")).IsEqualTo(AgentLiveness.Live);
        });
    }

    [Test]
    [Arguments("not json", "malformed")]
    [Arguments("[]", "malformed")]
    [Arguments("{}", "malformed")]
    [Arguments("""{"max_agents":null}""", "malformed")]
    [Arguments("""{"max_agents":0}""", "invalid_max_agents")]
    [Arguments("""{"max_agents":-3}""", "invalid_max_agents")]
    public async Task An_invalid_put_changes_nothing_and_names_why(string payload, string reason) {
        await RunAsync(async (h, ct) => {
            var ack = await PutAsync(h, payload, ct);

            await Assert.That(ack).IsEqualTo(new DaemonSettingsAckDto(false, reason, 5));
            await Assert.That(h.Config.MaxConcurrentAgents).IsEqualTo(5);
            await h.Orchestrator.CapabilityRefreshForTest;
            await Assert.That(h.Connection.RegisterDaemonCalls).IsEqualTo(0);
        });
    }

    [Test]
    public async Task The_core_client_round_trips_a_put() {
        await RunAsync(async (h, ct) => {
            var ops = new LocalControlOps(h.Config.Store, h.Config.Name);

            var ack = await ops.PutDaemonSettingsAsync(new DaemonSettingsPutDto(4), ct);

            await Assert.That(ack).IsEqualTo(new DaemonSettingsAckDto(true, null, 4));
            await Assert.That(h.Config.MaxConcurrentAgents).IsEqualTo(4);
        });
    }

    [Test]
    public async Task Hello_advertises_settings_1() {
        await RunAsync(async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.Hello), ct);
            var reply = await FrameCodec.ReadAsync(s, ct);
            var dto = JsonSerializer.Deserialize(reply!.Text, HelloIpcJsonContext.Default.HelloReplyDto)!;

            await Assert.That(dto.Capabilities).Contains(SettingsWire.Capability);
        });
    }
}
```

- [ ] **Step 2: Update the capability pin**

In `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalControlCapabilitiesTests.cs` change the expected array to:

```csharp
            .IsEquivalentTo(new[] { "consent/1", "consent/2", "consent/3", "status/1", "permission/1", "input/1", "settings/1" },
```

Then find every other whole-list pin and add `"settings/1"` at the end of each:
```bash
rtk proxy grep -rn "\"input/1\"" test/ src/Capacitor.Cli.Daemon/
```
Expected hits to edit: `LocalControlHelloTests.cs` (the `Capabilities` assertion) and any other test listing the full array. `LocalControlCapabilities.cs` itself is edited in Step 5.

- [ ] **Step 3: Run to verify they fail**

Run:
```bash
dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonSettingsIpcTests/*"
```
Expected: build error, `DaemonSettingsIpc` does not exist.

- [ ] **Step 4: Add the republish trigger to the orchestrator**

In `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs`, directly after the `CapabilityRefreshForTest` property (line 1290), add:

```csharp
    /// <summary>
    /// Re-sends the registration on the current connection so the server's copy of a connect
    /// field that changed at runtime (capacity) is overwritten. Rides the capability refresh so a
    /// burst of changes coalesces and the last publication carries the newest config.
    /// </summary>
    internal void RepublishRegistration(string reason) => RefreshAdvertisedCapabilities(reason, republishUnchanged: true);
```

- [ ] **Step 5: Create the handler**

Create `src/Capacitor.Cli.Daemon/Services/DaemonSettingsIpc.cs`:

```csharp
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// Local-socket handler for DaemonSettingsPut. Trust model: the 0600 socket's owner, like every
/// other local frame. Changes the live config only; persisting a value is the caller's job.
internal sealed partial class DaemonSettingsIpc(
    DaemonConfig config, AgentOrchestrator orchestrator, DaemonStatusNotifier notifier, ILogger<DaemonSettingsIpc> logger) {

    public async Task HandlePutAsync(string payload, Stream stream, CancellationToken ct) {
        DaemonSettingsAckDto ack;
        try {
            ack = Apply(JsonSerializer.Deserialize(payload, SettingsIpcJsonContext.Default.DaemonSettingsPutDto));
        } catch (JsonException) {
            ack = Refuse(DaemonSettingsReasons.Malformed);
        }
        var json = JsonSerializer.Serialize(ack, SettingsIpcJsonContext.Default.DaemonSettingsAckDto);
        await FrameCodec.WriteAsync(stream, LocalFrame.SettingsJson(FrameType.DaemonSettingsAck, json), ct);
    }

    // Validate everything before applying anything, so a put is all-or-nothing as settings grow.
    DaemonSettingsAckDto Apply(DaemonSettingsPutDto? dto) {
        if (!SettingsWire.HasAnySetting(dto)) return Refuse(DaemonSettingsReasons.Malformed);
        if (dto!.MaxAgents is < 1) return Refuse(DaemonSettingsReasons.InvalidMaxAgents);

        if (dto.MaxAgents is { } max) {
            config.MaxConcurrentAgents = max;
            notifier.Pulse();
            orchestrator.RepublishRegistration("settings");
            LogCapacity(max);
        }
        return new DaemonSettingsAckDto(true, null, config.MaxConcurrentAgents);
    }

    DaemonSettingsAckDto Refuse(string reason) => new(false, reason, config.MaxConcurrentAgents);

    [LoggerMessage(Level = LogLevel.Information, Message = "Capacity set to {MaxAgents} agents over the local control socket")]
    partial void LogCapacity(int maxAgents);
}
```

- [ ] **Step 6: Route the frame and advertise the capability**

In `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs`:

Constructor: change
```csharp
        RestartCoordinator restart, LaunchConsentIpc consentIpc, PermissionIpc permissionIpc, DaemonStatusIpc statusIpc,
```
to
```csharp
        RestartCoordinator restart, LaunchConsentIpc consentIpc, PermissionIpc permissionIpc, DaemonStatusIpc statusIpc,
        DaemonSettingsIpc settingsIpc,
```

Routing switch: after the `case FrameType.SendText:` line add
```csharp
                case FrameType.DaemonSettingsPut: await settingsIpc.HandlePutAsync(first.Text, stream, ct); break;
```
and in the `default:` arm's message replace `/StatusSubscribe/SendText,` with `/StatusSubscribe/SendText/DaemonSettingsPut,`.

In `src/Capacitor.Cli.Daemon/Services/LocalControlCapabilities.cs`:
- extend the XML doc's last sentence: `... routes SendText to <see cref="AgentOrchestrator.HandleLocalSendTextAsync"/>; and <c>"settings/1"</c> routes DaemonSettingsPut to <see cref="DaemonSettingsIpc"/>.`
- change the list to `["consent/1", "consent/2", "consent/3", "status/1", "permission/1", "input/1", "settings/1"]`.

- [ ] **Step 7: Register the handler**

In `src/Capacitor.Cli.Daemon/DaemonRunner.cs`, directly after `builder.Services.AddSingleton<LaunchConsentIpc>();` (line 402) add:

```csharp
        builder.Services.AddSingleton<DaemonSettingsIpc>();
```

- [ ] **Step 8: Fix every test constructor call**

For each of `LocalControlHelloTests.cs`, `LocalControlProbeTests.cs`, `ConsentRulesPutV2Tests.cs`, `LaunchConsentIpcTests.cs`, `AgentOrchestratorLocalAttachTests.cs`, `LocalControlOpsV2PutTests.cs`, `DaemonStatusIpcTests.cs` under `test/Capacitor.Cli.Daemon.Tests.Unit/Services/`:

1. Where the file constructs `new DaemonStatusIpc(config, orchestrator, connection, new DaemonStatusNotifier())`, hoist the notifier: `var notifier = new DaemonStatusNotifier();` and pass `notifier`. If the file already holds a notifier variable, reuse it.
2. Add before the `new LocalControlServer(` line:
   ```csharp
   var settingsIpc = new DaemonSettingsIpc(config, orchestrator, notifier, NullLogger<DaemonSettingsIpc>.Instance);
   ```
3. Insert `settingsIpc,` after `statusIpc,` in the `new LocalControlServer(...)` argument list.

Variable names differ per file (`orch` vs `orchestrator`, `cfg` vs `config`); match what each file uses. Confirm no call site is missed:
```bash
rtk proxy grep -rn "new LocalControlServer(" test/ src/
```
Expected: every hit passes eight arguments.

- [ ] **Step 9: Run the daemon tests**

Run:
```bash
dotnet build test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj
dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonSettingsIpcTests/*"
dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/LocalControlCapabilitiesTests/*"
dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/LocalControlHelloTests/*"
```
Expected: build succeeds with zero warnings; all three classes pass (10, 1 and the hello tests).

- [ ] **Step 10: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/zany-dancing-coral add src/Capacitor.Cli.Daemon test/Capacitor.Cli.Daemon.Tests.Unit
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/zany-dancing-coral commit -q -m "Apply a capacity change to the running daemon over the local socket (#791)" -m "The ack returns once the live value is set; the server learns through the same single-flighted re-register the vendor-CLI watcher uses, so a burst of puts publishes once with the newest value." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: Profile capacity precedence

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs:103-118` (arg loop) and `:164-168` (profile block)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/DaemonConfigProfileTests.cs`

**Interfaces:**
- Produces: `internal static void DaemonRunner.ApplyProfileCapacity(DaemonConfig config, DaemonSettings? profileDaemon, bool maxAgentsFromArgs)`.

- [ ] **Step 1: Write the failing tests**

In `test/Capacitor.Cli.Daemon.Tests.Unit/DaemonConfigProfileTests.cs`, change the helper's capacity lines

```csharp
        if (config.MaxConcurrentAgents == 5 && profileDaemon is { MaxAgents: var mx and not 5 })
            config.MaxConcurrentAgents = mx;
```
to
```csharp
        DaemonRunner.ApplyProfileCapacity(config, profileDaemon, maxAgentsFromArgs);
```
and the helper signature to `static DaemonConfig ApplyProfileSettings(DaemonConfig config, DaemonSettings? profileDaemon, bool maxAgentsFromArgs = false)`. Add a `using Capacitor.Cli.Daemon;` if the namespace is not already in scope (the test namespace is `Capacitor.Cli.Daemon.Tests.Unit`, so `DaemonRunner` resolves through the parent namespace without one).

Then add, before the `// ── claude_path from profile` section:

```csharp
    // ── max_agents from profile ──────────────────────────────────────────────

    [Test]
    public async Task MaxAgents_FromProfile_Applies() {
        var config = ApplyProfileSettings(new DaemonConfig(), new DaemonSettings { MaxAgents = 3 });

        await Assert.That(config.MaxConcurrentAgents).IsEqualTo(3);
    }

    /// A profile value equal to the default must still be the profile's value, not "unset".
    [Test]
    public async Task MaxAgents_FromProfile_Applies_when_it_equals_the_default() {
        var config = ApplyProfileSettings(new DaemonConfig { MaxConcurrentAgents = 8 }, new DaemonSettings { MaxAgents = 5 });

        await Assert.That(config.MaxConcurrentAgents).IsEqualTo(5);
    }

    [Test]
    public async Task MaxAgents_FromArgs_WinsOverProfile() {
        var config = ApplyProfileSettings(new DaemonConfig { MaxConcurrentAgents = 7 }, new DaemonSettings { MaxAgents = 3 }, maxAgentsFromArgs: true);

        await Assert.That(config.MaxConcurrentAgents).IsEqualTo(7);
    }

    [Test]
    public async Task MaxAgents_NullProfile_KeepsDefault() {
        var config = ApplyProfileSettings(new DaemonConfig(), profileDaemon: null);

        await Assert.That(config.MaxConcurrentAgents).IsEqualTo(5);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run:
```bash
dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonConfigProfileTests/*"
```
Expected: build error, `DaemonRunner` has no `ApplyProfileCapacity`.

- [ ] **Step 3: Implement**

In `src/Capacitor.Cli.Daemon/DaemonRunner.cs`:

Before the arg loop (`for (var i = 0; i < args.Length - 1; i++) {` near line 103) add:
```csharp
        var maxAgentsFromArgs = false;
```
In the loop's `case "--max-agents" when ...` arm, after `config.MaxConcurrentAgents = n;` add:
```csharp
                    maxAgentsFromArgs = true;
```
Replace the profile block
```csharp
        if (config.MaxConcurrentAgents == 5 && profileDaemon is { MaxAgents: var mx and not 5 })
            config.MaxConcurrentAgents = mx;
```
with
```csharp
        ApplyProfileCapacity(config, profileDaemon, maxAgentsFromArgs);
```
Add next to the other `internal static` helpers (for example directly above `ParseSecondsEnv`, line 1078):
```csharp
    /// The profile's max_agents applies whenever --max-agents was not passed; the flag wins when it was.
    internal static void ApplyProfileCapacity(DaemonConfig config, DaemonSettings? profileDaemon, bool maxAgentsFromArgs) {
        if (maxAgentsFromArgs || profileDaemon is null) return;
        config.MaxConcurrentAgents = profileDaemon.MaxAgents;
    }
```
`DaemonSettings` lives in `Capacitor.Cli.Core.Config`; add the `using` only if the file does not already have it.

- [ ] **Step 4: Run to verify they pass**

Run:
```bash
dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonConfigProfileTests/*"
```
Expected: all 13 pass.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/zany-dancing-coral add src/Capacitor.Cli.Daemon/DaemonRunner.cs test/Capacitor.Cli.Daemon.Tests.Unit/DaemonConfigProfileTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/zany-dancing-coral commit -q -m "Apply the profile capacity whenever --max-agents is absent (#791)" -m "Comparing the live value with the default 5 made a profile of exactly 5 read as unset and let the profile override an explicit --max-agents 5." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: `--retire` on `install --replace --verify`

**Files:**
- Modify: `src/Capacitor.Cli/Services/ServiceVerify.cs` (`VerifyExit` block at lines 10-78; `InstallVerifiedAsync` at 695; add `RetireAsync` beside `ApplyReplaceMatrixAsync`)
- Modify: `src/Capacitor.Cli/Commands/DaemonServiceCommands.cs:92-155` (parse, validate, forward) and the usage text at `:634`
- Modify: `README.md:864-874`
- Test: `test/Capacitor.Cli.Tests.Unit/Services/ServiceVerifyRetireTests.cs` (new)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/DaemonCommandsServiceInstallTests.cs`

**Interfaces:**
- Consumes: `ServiceVerify(store, config, manager, validatedDaemonPid, hello, time, readPlist:, plistExists:)`; `IVerifyServiceManager.UnitPath`; `LaunchdUnit.EnvFromPlist`; `ServiceTxnLock.TryAcquire`; existing `ClearLabelAsync`, `WaitForStopConfirmedAsync`, `Say`.
- Produces: `Task<int> ServiceVerify.InstallVerifiedAsync(ServiceSpec spec, bool replace, string? expectedVersion, string? retireServiceId = null)`; `VerifyExit.RetireRefused = 30`, `VerifyExit.RetireRefusedToken = "verify_retire_refused"`, stderr line `retire_reason=foreign_profile | unit_unreadable`; CLI flag `--retire <id>`.

- [ ] **Step 1: Write the failing engine tests**

Create `test/Capacitor.Cli.Tests.Unit/Services/ServiceVerifyRetireTests.cs`:

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// install --replace --verify --retire: the unit a rename leaves behind is removed inside the
/// same transaction, only when it is pinned to the same profile, and a live daemon under the new
/// name is a collision rather than a takeover.
/// </summary>
public class ServiceVerifyRetireTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    const string NewId           = "svc-retire-new";
    const string OldId           = "svc-retire-old";
    const string ExpectedVersion = "1.2.3";
    const string OwnPlistContent = "<plist>own-unit</plist>";

    static string OldPlist(string profile) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
          <key>Label</key><string>io.kurrent.kcap.daemon.{OldId}</string>
          <key>ProgramArguments</key><array><string>/x/kcap-daemon</string><string>--name</string><string>{OldId}</string></array>
          <key>EnvironmentVariables</key><dict><key>KCAP_PROFILE</key><string>{profile}</string></dict>
        </dict></plist>
        """;

    /// <summary>Same state machine as ServiceVerifyInstallTests' fake, plus the retired label's own
    /// presence flag so Query answers per id.</summary>
    sealed class FakeServiceManager(UserHome home) : IVerifyServiceManager {
        public string UnitPath(string serviceId) => LaunchdUnit.PlistPath(home, serviceId);
        public readonly List<string> Calls = [];
        public bool OldUnitInstalled;
        public bool Bootstrapped;
        public int? RunningPid = 4242;

        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => [new GeneratedFile("/fake/new.plist", OwnPlistContent)];

        public ServiceQuery Query(string serviceId, TimeSpan timeout) {
            Calls.Add($"query:{serviceId}");
            if (serviceId == OldId)
                return OldUnitInstalled
                    ? new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, "/x/kcap-daemon", 1111)
                    : new ServiceQuery(LabelProbe.Absent, false, ServiceState.NotInstalled, null, null);
            return Bootstrapped
                ? new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, "/x/kcap-daemon", RunningPid)
                : new ServiceQuery(LabelProbe.Absent, false, ServiceState.NotInstalled, null, null);
        }

        public void WriteAndBootstrap(ServiceSpec spec, TimeSpan timeout) {
            Calls.Add($"writeAndBootstrap:{spec.ServiceId}");
            Bootstrapped = true;
        }

        public bool Uninstall(string serviceId, TimeSpan timeout, out string? error) {
            Calls.Add($"uninstall:{serviceId}");
            if (serviceId == OldId) OldUnitInstalled = false; else Bootstrapped = false;
            error = null;
            return true;
        }

        public bool Start(string serviceId, TimeSpan timeout, out string? error) { error = null; return true; }
        public bool StartBootstrapOnly(string serviceId, TimeSpan timeout, out string? error) => Start(serviceId, timeout, out error);
        public bool Stop(string serviceId, TimeSpan timeout, out string? error) { error = null; return true; }
    }

    string ViableDaemonPath() {
        var dir = Tmp.CreateDir(Guid.NewGuid().ToString("N"));
        var daemonPath = dir.PathTo("kcap-daemon");
        File.WriteAllText(daemonPath, "");
        return daemonPath;
    }

    static ServiceSpec Spec(string daemonPath, string profile) =>
        new(NewId, daemonPath, Path.ChangeExtension(daemonPath, ".log"),
            new Dictionary<string, string> { ["KCAP_PROFILE"] = profile }, []);

    /// The new daemon answers hello only once bootstrapped; the old one never does.
    static Func<string, TimeSpan, Task<HelloProbeResult>> Hello(FakeServiceManager manager) =>
        (id, _) => Task.FromResult(id == NewId && manager.Bootstrapped
            ? new HelloProbeResult(true, 1, ExpectedVersion, NewId)
            : new HelloProbeResult(false, null, null, null));

    ServiceVerify Sut(FakeServiceManager manager, string? oldPlist, Func<string, int?>? validatedPid = null) =>
        new(Daemons.Store, Config.Root, manager,
            validatedPid ?? (id => id == NewId && manager.Bootstrapped ? 4242 : null),
            Hello(manager), TimeProvider.System,
            readPlist: path => path == manager.UnitPath(OldId) ? oldPlist : OwnPlistContent,
            plistExists: path => path == manager.UnitPath(OldId) ? oldPlist is not null : true);

    [Test]
    public async Task An_absent_retired_unit_is_a_no_op() {
        var manager = new FakeServiceManager(Home);
        var sut = Sut(manager, oldPlist: null);

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.Calls).DoesNotContain($"uninstall:{OldId}");
        await Assert.That(manager.Calls).Contains($"writeAndBootstrap:{NewId}");
    }

    [Test]
    public async Task A_unit_pinned_to_another_profile_is_refused_untouched() {
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true };
        var sut = Sut(manager, OldPlist("other"));

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await Assert.That(exit).IsEqualTo(VerifyExit.RetireRefused);
        await Assert.That(manager.Calls.Any(c => c.StartsWith("uninstall:", StringComparison.Ordinal))).IsFalse();
        await Assert.That(manager.Calls.Any(c => c.StartsWith("writeAndBootstrap:", StringComparison.Ordinal))).IsFalse();
        await Assert.That(manager.OldUnitInstalled).IsTrue();
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, NewId)).IsFalse();
    }

    [Test]
    public async Task A_unit_without_a_pinned_profile_is_refused_untouched() {
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true };
        var sut = Sut(manager, OldPlist("mine"));
        var spec = new ServiceSpec(NewId, ViableDaemonPath(), "/x/log", new Dictionary<string, string>(), []);

        var exit = await sut.InstallVerifiedAsync(spec, replace: true, ExpectedVersion, retireServiceId: OldId);

        await Assert.That(exit).IsEqualTo(VerifyExit.RetireRefused);
        await Assert.That(manager.OldUnitInstalled).IsTrue();
    }

    [Test]
    public async Task A_same_profile_unit_is_retired_before_the_new_unit_installs() {
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true };
        var sut = Sut(manager, OldPlist("mine"));

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.OldUnitInstalled).IsFalse();
        await Assert.That(manager.Calls.IndexOf($"uninstall:{OldId}")).IsLessThan(manager.Calls.IndexOf($"writeAndBootstrap:{NewId}"));
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, NewId)).IsFalse();
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, OldId)).IsFalse();
    }

    [Test]
    public async Task A_live_daemon_under_the_new_name_is_contended_not_taken_over() {
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true };
        var sut = Sut(manager, OldPlist("mine"), validatedPid: id => id == NewId ? 9999 : null);

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await Assert.That(exit).IsEqualTo(VerifyExit.Contended);
        await Assert.That(manager.OldUnitInstalled).IsTrue();
        await Assert.That(manager.Calls.Any(c => c.StartsWith("uninstall:", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task An_unreadable_retired_unit_is_refused_untouched() {
        var manager = new FakeServiceManager(Home) { OldUnitInstalled = true };
        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager,
            id => id == NewId && manager.Bootstrapped ? 4242 : null, Hello(manager), TimeProvider.System,
            readPlist: path => path == manager.UnitPath(OldId) ? null : OwnPlistContent,
            plistExists: _ => true);

        var exit = await sut.InstallVerifiedAsync(Spec(ViableDaemonPath(), "mine"), replace: true, ExpectedVersion, retireServiceId: OldId);

        await Assert.That(exit).IsEqualTo(VerifyExit.RetireRefused);
        await Assert.That(manager.OldUnitInstalled).IsTrue();
    }
}
```

- [ ] **Step 2: Write the failing command tests**

In `test/Capacitor.Cli.Tests.Unit/Commands/DaemonCommandsServiceInstallTests.cs`, add after `Replace_without_verify_is_rejected`:

```csharp
    [Test]
    public async Task Retire_without_replace_and_verify_is_rejected() {
        var exit = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), new SystemdServiceManager(Home), "test-id", Home).Install(["--verify", "--retire", "old"], true);
        await Assert.That(exit).IsEqualTo(1);
    }

    /// The comparison is on sanitized ids, so a differently-cased spelling of the target is still the target.
    [Test]
    public async Task Retire_naming_the_target_itself_is_rejected() {
        var exit = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), new SystemdServiceManager(Home), "test-id", Home).Install(["--replace", "--verify", "--retire", "Test-ID"], true);
        await Assert.That(exit).IsEqualTo(1);
    }
```

- [ ] **Step 3: Run to verify they fail**

Run:
```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ServiceVerifyRetireTests/*"
```
Expected: build error, `InstallVerifiedAsync` has no `retireServiceId` parameter.

- [ ] **Step 4: Add the exit code**

In `src/Capacitor.Cli/Services/ServiceVerify.cs`, inside `VerifyExit` after `StartGateDriftToken`:

```csharp
    /// <summary><c>--retire</c> refused to remove the named unit: its plist is unreadable, or it is
    /// not pinned to the profile being installed. Nothing is touched. The stderr line
    /// <c>retire_reason=&lt;reason&gt;</c> names which.</summary>
    public const int RetireRefused = 30;
    public const string RetireRefusedToken = "verify_retire_refused";
```

- [ ] **Step 5: Add the retire step to the transaction**

Change the signature at line 695 to:

```csharp
    public async Task<int> InstallVerifiedAsync(ServiceSpec spec, bool replace, string? expectedVersion, string? retireServiceId = null) {
```

After `var preState = DescribeQuery(pre);` and before `if (!replace) {`, insert:

```csharp
        if (retireServiceId is not null) {
            // A rename's target must be free: a live daemon under the new name is another daemon,
            // not a stale unit for --replace to take over.
            if (validatedDaemonPid(serviceId) is not null) {
                Say(VerifyExit.ContendedToken);
                return VerifyExit.Contended;
            }
            if (await RetireAsync(retireServiceId, spec, forward) is { } retireExit) return retireExit;
        }
```

Add the method directly above `ApplyReplaceMatrixAsync`:

```csharp
    /// <summary>
    /// Removes the unit a rename leaves behind, inside this transaction. Absent is a no-op; a unit
    /// not provably pinned to the profile being installed is refused untouched; a bootout that
    /// cannot be confirmed stops the transaction before anything is written for the new id.
    /// </summary>
    async Task<int?> RetireAsync(string retireId, ServiceSpec spec, DateTimeOffset deadline) {
        var (status, content) = _discriminatedPlistRead(manager.UnitPath(retireId));
        if (status == LaunchdUnit.PlistRead.Absent) return null;

        string? retiredProfile = null;
        var readable = status == LaunchdUnit.PlistRead.Ok;
        if (readable) {
            try { LaunchdUnit.EnvFromPlist(content!).TryGetValue(ProfileVar, out retiredProfile); }
            catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException) { readable = false; }
        }
        if (!readable) return RetireRefusal("unit_unreadable");

        spec.Environment.TryGetValue(ProfileVar, out var pinnedProfile);
        if (string.IsNullOrEmpty(pinnedProfile) || !string.Equals(retiredProfile, pinnedProfile, StringComparison.Ordinal))
            return RetireRefusal("foreign_profile");

        using var retireTxn = ServiceTxnLock.TryAcquire(store, retireId, LockWait);
        if (retireTxn is null) {
            Say(VerifyExit.ContendedToken);
            return VerifyExit.Contended;
        }

        if (await ClearLabelAsync(retireId, deadline) is { } clearExit) return clearExit;
        if (!await WaitForStopConfirmedAsync(retireId, deadline)) {
            Say(VerifyExit.StopUnconfirmedToken);
            return VerifyExit.StopUnconfirmed;
        }
        return null;
    }

    static int RetireRefusal(string reason) {
        Say($"retire_reason={reason}");
        Say(VerifyExit.RetireRefusedToken);
        return VerifyExit.RetireRefused;
    }
```

- [ ] **Step 6: Parse and forward the flag in the command**

In `src/Capacitor.Cli/Commands/DaemonServiceCommands.cs` `Install`, after the `if (replace && !verify) { ... }` block, add:

```csharp
        var retire   = DaemonCommands.ExtractFlagValue(args, "--retire");
        var retireId = retire is null ? null : DaemonStore.Sanitize(retire);

        if (retireId is not null && !(replace && verify)) {
            await Console.Error.WriteLineAsync("install --retire requires --replace --verify.");
            return 1;
        }

        if (retireId is not null && retireId == id) {
            await Console.Error.WriteLineAsync("--retire names the service being installed; nothing to retire.");
            return 1;
        }
```

Change the engine call to:
```csharp
            var exit   = await engine.InstallVerifiedAsync(spec, replace: replace, CapacitorVersion.Current(), retireServiceId: retireId);
```

Update the usage line (`:634`) to:
```csharp
        Console.Error.WriteLine("  install [--name N] [--profile P] [--max-agents N] [--no-start] [--replace] [--verify] [--retire ID]");
```

In `src/Capacitor.Cli.Core/Resources/help-daemon.txt` change line 53 to:
```
  install [--name N] [--profile P] [--max-agents N] [--no-start] [--replace] [--verify] [--retire ID]
```
and after the sentence ending `label, stops a validated live owner, then installs.` (line 64) insert, at the same indentation:
```
                          --retire ID (requires --replace --verify) also removes
                          the unit ID in the same transaction — the one a rename
                          leaves behind. Refused unless that unit is pinned to
                          the same profile; a live daemon under the NEW name is
                          contended, never taken over.
```

- [ ] **Step 7: Document the flag in the README**

In `README.md`, in the code block under the Daemon section (line 854), after the `install --replace --verify` line add:
```
kcap daemon service install --replace --verify --retire OLD   # also remove the unit OLD, e.g. after a rename
```
After the `install --replace --verify` paragraph (line 874) add:

```markdown
`install --replace --verify --retire <id>` additionally removes the unit `<id>` inside the same transaction — the unit a daemon rename leaves behind. The retired unit must be pinned to the same profile as the one being installed (`KCAP_PROFILE` in its plist), or the command refuses with `verify_retire_refused` and touches nothing; an absent unit is a no-op. With `--retire`, a live daemon already running under the *new* name is `verify_contended` rather than taken over.
```

- [ ] **Step 8: Run the CLI tests**

Run:
```bash
dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ServiceVerifyRetireTests/*"
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonCommandsServiceInstallTests/*"
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ServiceVerifyInstallTests/*"
```
Expected: build clean; 6 retire tests, 7 install-command tests and every pre-existing `ServiceVerifyInstallTests` test pass. If `ServiceVerifyInstallTests` fails on a `--replace` case, the retire step ran without `--retire`: the `if (retireServiceId is not null)` guard is missing.

- [ ] **Step 9: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/zany-dancing-coral add src/Capacitor.Cli/Services/ServiceVerify.cs src/Capacitor.Cli/Commands/DaemonServiceCommands.cs src/Capacitor.Cli.Core/Resources README.md test/Capacitor.Cli.Tests.Unit/Services/ServiceVerifyRetireTests.cs test/Capacitor.Cli.Tests.Unit/Commands/DaemonCommandsServiceInstallTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/zany-dancing-coral commit -q -m "Retire the old unit inside install --replace --verify (#791)" -m "The unit bakes the daemon name in as --name, so a rename is a reinstall under a new label; retiring the old one in the same transaction keeps the app to one verb." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: Change notes, full verification, AOT check

**Files:**
- Modify: `docs/CHANGES.md` (prepend a section after the intro paragraphs, before the first `## `)

- [ ] **Step 1: Write the change note**

Insert before `## A hosted agent's teardown cannot hang up the daemon`:

```markdown
## Capacity changes live; a rename restarts the daemon

A client changes the running daemon's agent cap over a new local frame pair, `DaemonSettingsPut`
23 and `DaemonSettingsAck` 81 behind `settings/1`, because the daemon reads the cap per launch off
a mutable field and the server overwrites a repeat connect on the same connection: no restart, and
the server learns through the same single-flighted re-register the vendor-CLI watcher uses. The
caller persists the value itself, to the profile, before the push, so a push that fails leaves a
durable value rather than a live one the next start forgets. The name gets no frame. It keys the
lock, pid, socket and state paths, the launchd label and the server's slot, and the unit bakes it
in as `--name`, so a rename is a reinstall under a new label. `install --replace --verify --retire
<old-id>` removes the old unit inside that transaction, refusing a unit pinned to another profile
and treating a live daemon under the new name as contended rather than as a takeover, so the app
issues one verb and never sequences two destructive commands itself. The daemon now applies the
profile's `max_agents` whenever `--max-agents` is absent; comparing the live value with the
default 5 had made a profile of exactly 5 read as unset.
```

- [ ] **Step 2: Run every touched suite in full**

Run:
```bash
dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj
dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
dotnet build test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj
```
Expected: Core and CLI suites fully green. The daemon suite may show 4-5 environmental timing failures on a local Mac; re-run each failed test alone with `--treenode-filter "/*/*/<Class>/*"` and require it to pass alone before moving on. The app test project must build with zero warnings.

- [ ] **Step 3: AOT publish check**

Run:
```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | rtk proxy grep -E 'IL[23][01][0-9]{2}'
```
Expected: no output.

- [ ] **Step 4: Linear-id check**

Run:
```bash
bash scripts/check-linear-ids.sh
```
Expected: exit 0.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/zany-dancing-coral add docs/CHANGES.md
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/zany-dancing-coral commit -q -m "Record the daemon settings reasoning in the change notes (#791)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## PR

Title: `Change daemon capacity live and retire the old unit on rename`

Body: follow `.github/PULL_REQUEST_TEMPLATE.md`. Reference line: `Closes #791` is **not** used, since PR 2 finishes the issue; write `Part of #791` and `AI-2535`. Push with `git push https://github.com/kurrent-io/kcap-cli.git alexeyzimarev/ai-2535-extend-desktop-app-settings-to-configure-the-daemon`.
