# Daemon-advertised vendor model catalogs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The desktop launcher and the web launch dialog list the Pi models a machine can actually launch, by having the daemon ask the installed Pi for its model list and advertise it.

**Architecture:** The daemon spawns `pi --mode rpc` once at startup (and again when Pi's binary or auth/models files change), sends `get_available_models`, and stores the answer as a per-vendor dictionary on `DaemonConfig`. That dictionary rides both advertisement paths, the SignalR `DaemonConnect` and the local status IPC, as an additive `vendor_models` field. The kcap-server registry passes it through to `DaemonInfo`. Both launchers merge it ahead of the server catalog and curated fallback.

**Tech Stack:** .NET 10, NativeAOT (source-generated System.Text.Json), SignalR, TUnit, Avalonia + ReactiveUI (desktop), Blazor + MudBlazor + bUnit (web).

**Spec:** `docs/superpowers/specs/2026-09-28-daemon-advertised-vendor-models-design.md` (read it first; every task argues from it).

**Repositories:** Tasks 1–9 are in kcap-cli (this repository). Tasks 10–12 are in the sibling kcap-server repository at `/Users/tony/dev/kcap-server`, on a branch of its own. The two PRs are independently mergeable: the server ignores an unknown field and the daemon tolerates a server that drops it.

**Build/test commands (kcap-cli):** use `~/.dotnet/dotnet` (the PATH `dotnet` is 8.0). One suite at a time:

```bash
~/.dotnet/dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter '/*/*/<ClassName>/*'
```

## Global Constraints

- Comments: scarce, no history narration, no ticket ids, no review artifacts (CLAUDE.md "Comments"). Do not imitate the long comments already in these files.
- One type per file, named after the type.
- `RS0030`: never call `Environment.GetFolderPath`; take a `UserHome`/`PiPaths`.
- The daemon never uses `Process.Kill(bool)`; tree kills go through `ProcessTree.Kill`.
- The daemon never reads Pi's `auth.json` or `models.json`; it stats them only.
- Test throwaways come from Helpers' `TempDir` (`[TempDir] public required TempDir Tmp { get; init; }`), executables from `tmp.CreateExecutable`, pids via `PidIdentity`.
- Serialized field name on every wire is `vendor_models` (both JSON contexts are `SnakeCaseLower`; `DaemonInfo` in `Capacitor.Remote.Models` uses an explicit `[JsonPropertyName]`).
- `DaemonConfig.VendorModels`, `DaemonConnect.VendorModels` and `DaemonInfoDto.VendorModels` are all `Dictionary<string, VendorModelOption[]>?` so the value passes through unconverted.
- Null = unknown (old daemon). Key absent = no catalog for that vendor. Key present and empty = probed, nothing usable, and it wins over every fallback.
- After any change under `src/Capacitor.Cli*` or `src/Capacitor.Remote.Models`, run `~/.dotnet/dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` and expect no output. (If the machine is under load and publish is killed, note it and rely on CI's publish job.)
- Commit subjects: one imperative clause, ≤80 chars, no issue reference (none is known yet). End every commit message with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- Run `bash scripts/check-linear-ids.sh` before pushing.

## Review Focus

1. **Pi prints a non-response line first** (a `{"type":"ready"}` or log frame before the response): the probe must skip it and still find the response. Pinned in Task 3's fake, which prints a leading `{"type":"noise"}` line.
2. **Pi exits without ever answering** (crashes at startup, or an old build without `get_available_models`): the probe must return null promptly, not hang until the 10 s deadline, and log once. Pinned in Task 3 (`Exits_before_answering_returns_null`).
3. **A Pi response with an unexpected model shape** (`models` present but entries missing `provider`): those entries are skipped and the rest kept, and a fully-invalid envelope returns null, never `[]`. Pinned in Task 2.
4. **The refresh runs while a status subscriber is serializing**: the subscriber must see the old or the new dictionary whole. Pinned in Task 6 by never mutating in place (reference swap) plus a concurrent-serialize test.
5. **The user picked a Pi model on machine A and switches to machine B that offers Pi without it**: `SelectedModel` resets, and a typed custom id does not. Pinned in Task 9.

---

## Task 1: Wire types (`VendorModelOption`, `DaemonConnect`, `DaemonInfoDto`, `DaemonInfo`)

**Files:**
- Create: `src/Capacitor.Cli.Core/VendorModelOption.cs`
- Modify: `src/Capacitor.Cli.Core/Models.cs` (the `DaemonConnect` record, ~line 2120; and `CapacitorJsonContext` `[JsonSerializable]` list, ~line 1263)
- Modify: `src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs` (`DaemonInfoDto`, ~line 25; `StatusIpcJsonContext`, ~line 158)
- Modify: `src/Capacitor.Remote.Models/DaemonInfo.cs`, `src/Capacitor.Remote.Models/RemoteModelsJsonContext.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/DaemonStatusDtoTests.cs`, `test/Capacitor.Remote.Models.Tests.Unit/DaemonInfoJsonTests.cs` (create)

**Interfaces:**
- Produces: `public sealed record VendorModelOption(string Value, string Label);` in namespace `Capacitor.Cli.Core`.
- Produces: `DaemonConnect(..., Dictionary<string, VendorModelOption[]>? VendorModels = null)` as the **last** parameter.
- Produces: `DaemonInfoDto(..., string[]? SupportedVendors = null, Dictionary<string, VendorModelOption[]>? VendorModels = null)`.
- Produces: `DaemonInfo.VendorModels` (`Dictionary<string, VendorModelOptionDto[]>?`, JSON `vendor_models`).

- [ ] **Step 1: Write the failing serialization tests**

Append to `DaemonStatusDtoTests.cs`:

```csharp
    [Test]
    public async Task DaemonConnect_serializes_vendor_models_as_snake_case_and_round_trips() {
        var connect = new DaemonConnect("d", "mac", [], 1, [],
            VendorModels: new Dictionary<string, VendorModelOption[]>(StringComparer.Ordinal) {
                ["pi"] = [new("anthropic/claude-opus-5", "Claude Opus 5 · anthropic")],
                ["kiro"] = [],
            });

        var json = JsonSerializer.Serialize(connect, CapacitorJsonContext.Default.DaemonConnect);
        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.DaemonConnect);

        await Assert.That(json).Contains("\"vendor_models\"");
        await Assert.That(back.VendorModels!["pi"][0].Value).IsEqualTo("anthropic/claude-opus-5");
        await Assert.That(back.VendorModels["kiro"]).IsEmpty();
    }

    [Test]
    public async Task DaemonConnect_without_vendor_models_deserializes_to_null() {
        var json = JsonSerializer.Serialize(new DaemonConnect("d", "mac", [], 1, []), CapacitorJsonContext.Default.DaemonConnect);
        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.DaemonConnect);
        await Assert.That(back.VendorModels).IsNull();
    }

    [Test]
    public async Task DaemonInfoDto_vendor_models_round_trips_through_status_context() {
        var dto = new DaemonStatusDto(
            new DaemonInfoDto("d", "1", "http://s", "connected", 1, 0,
                VendorModels: new Dictionary<string, VendorModelOption[]>(StringComparer.Ordinal) { ["pi"] = [] }),
            []);

        var json = JsonSerializer.Serialize(dto, StatusIpcJsonContext.Default.DaemonStatusDto);
        var back = JsonSerializer.Deserialize(json, StatusIpcJsonContext.Default.DaemonStatusDto)!;

        await Assert.That(json).Contains("\"vendor_models\"");
        await Assert.That(back.Daemon.VendorModels!["pi"]).IsEmpty();
    }

    [Test]
    public async Task DaemonInfoDto_from_an_older_daemon_has_null_vendor_models() {
        var json = """{"daemon":{"name":"d","version":"1","server_url":"http://s","connection":"connected","max_agents":1,"active_agents":0},"agents":[]}""";
        var back = JsonSerializer.Deserialize(json, StatusIpcJsonContext.Default.DaemonStatusDto)!;
        await Assert.That(back.Daemon.VendorModels).IsNull();
    }
```

Create `test/Capacitor.Remote.Models.Tests.Unit/DaemonInfoJsonTests.cs`:

```csharp
using System.Text.Json;
using Capacitor.Remote.Models;

namespace Capacitor.Remote.Models.Tests.Unit;

public class DaemonInfoJsonTests {
    [Test]
    public async Task Vendor_models_deserialize_from_the_registry_shape() {
        var json = """{"name":"d","vendor_models":{"pi":[{"value":"anthropic/claude-opus-5","label":"Claude Opus 5 · anthropic"}]}}""";
        var info = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.DaemonInfo)!;
        await Assert.That(info.VendorModels!["pi"][0].Label).IsEqualTo("Claude Opus 5 · anthropic");
    }

    [Test]
    public async Task Missing_vendor_models_is_null() {
        var info = JsonSerializer.Deserialize("""{"name":"d"}""", RemoteModelsJsonContext.Default.DaemonInfo)!;
        await Assert.That(info.VendorModels).IsNull();
    }
}
```

- [ ] **Step 2: Run the tests, expect compile failures** (`VendorModels` not defined)

```bash
~/.dotnet/dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/DaemonStatusDtoTests/*'
```

- [ ] **Step 3: Implement**

`src/Capacitor.Cli.Core/VendorModelOption.cs`:

```csharp
namespace Capacitor.Cli.Core;

/// One launchable model a daemon advertises for a vendor. Value is what the launch wire carries
/// (for Pi, `provider/id`); Label is what a picker shows.
public sealed record VendorModelOption(string Value, string Label);
```

In `Models.cs`, add as the final parameter of `DaemonConnect`:

```csharp
        // Per-vendor launchable models this daemon probed from the installed CLI. Null from a daemon
        // that predates the field; a missing key means no catalog for that vendor; an empty array
        // means probed and nothing usable.
        Dictionary<string, VendorModelOption[]>? VendorModels = null
```

Add `[JsonSerializable(typeof(Dictionary<string, VendorModelOption[]>))]` to `CapacitorJsonContext` if the generator does not already pick it up transitively (it does for record members; add it only if the build warns).

In `StatusIpc.cs`, add to `DaemonInfoDto` after `SupportedVendors`:

```csharp
    Dictionary<string, VendorModelOption[]>? VendorModels = null
```

and `using Capacitor.Cli.Core;` at the top if missing.

In `Capacitor.Remote.Models/DaemonInfo.cs` add:

```csharp
    [JsonPropertyName("vendor_models")]           public Dictionary<string, VendorModelOptionDto[]>? VendorModels { get; init; }
```

- [ ] **Step 4: Run the tests, expect PASS**; also run the Remote.Models suite.

- [ ] **Step 5: AOT publish check** (Global Constraints) and commit

```bash
git add -A && git commit -m "Add vendor_models to the daemon advertisement wire types" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Task 2: `PiModelCatalogProbe.Parse` (pure parsing)

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Harness/Pi/PiModelCatalogProbe.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Pi/PiModelCatalogProbeParseTests.cs`

**Interfaces:**
- Produces: `internal static class PiModelCatalogProbe` with
  - `internal static PiModelCatalogParse Parse(string line)` where `internal readonly record struct PiModelCatalogParse(bool IsResponse, IReadOnlyList<VendorModelOption>? Models)` (own file `PiModelCatalogParse.cs`). `IsResponse == false` means "not the frame we want, keep reading". `IsResponse == true, Models == null` means the response was invalid. `IsResponse == true, Models == []` means a valid empty catalog.
  - `internal const string Command = "get_available_models";`

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Pi;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

public class PiModelCatalogProbeParseTests {
    const string Ok = """
        {"type":"response","command":"get_available_models","success":true,"data":{"models":[
          {"id":"claude-opus-5","name":"Claude Opus 5","provider":"anthropic"},
          {"id":"gpt-5.5","name":"GPT-5.5","provider":"github-copilot"},
          {"id":"claude-opus-5","name":"Claude Opus 5","provider":"github-copilot"}]}}
        """;

    [Test]
    public async Task Maps_provider_slash_id_and_name_dot_provider_in_pi_order() {
        var parsed = PiModelCatalogProbe.Parse(Ok.ReplaceLineEndings(""));
        await Assert.That(parsed.IsResponse).IsTrue();
        await Assert.That(parsed.Models!.Select(m => m.Value))
            .IsEquivalentTo(["anthropic/claude-opus-5", "github-copilot/gpt-5.5", "github-copilot/claude-opus-5"], CollectionOrdering.Matching);
        await Assert.That(parsed.Models[0].Label).IsEqualTo("Claude Opus 5 · anthropic");
    }

    [Test]
    public async Task Entry_missing_id_or_provider_or_not_an_object_is_skipped_and_name_falls_back_to_id() {
        var line = """{"type":"response","command":"get_available_models","success":true,"data":{"models":[{"name":"x","provider":"p"},{"id":"a","name":"A"},"junk",{"id":"b","provider":"p"}]}}""";
        var parsed = PiModelCatalogProbe.Parse(line);
        await Assert.That(parsed.Models!.Select(m => m.Value)).IsEquivalentTo(["p/b"]);
        await Assert.That(parsed.Models[0].Label).IsEqualTo("b · p");
    }

    [Test]
    [Arguments("""{"type":"response","command":"get_available_models","success":false}""")]
    [Arguments("""{"type":"response","command":"get_available_models","success":true}""")]
    [Arguments("""{"type":"response","command":"get_available_models","success":true,"data":{}}""")]
    [Arguments("""{"type":"response","command":"get_available_models","success":true,"data":{"models":"nope"}}""")]
    public async Task Invalid_envelope_is_a_response_with_null_models(string line) {
        var parsed = PiModelCatalogProbe.Parse(line);
        await Assert.That(parsed.IsResponse).IsTrue();
        await Assert.That(parsed.Models).IsNull();
    }

    [Test]
    public async Task Empty_models_array_is_an_empty_catalog_not_null() {
        var parsed = PiModelCatalogProbe.Parse("""{"type":"response","command":"get_available_models","success":true,"data":{"models":[]}}""");
        await Assert.That(parsed.Models).IsNotNull();
        await Assert.That(parsed.Models!).IsEmpty();
    }

    [Test]
    [Arguments("""{"type":"response","command":"get_state","success":true}""")]
    [Arguments("""{"type":"ready"}""")]
    [Arguments("not json")]
    [Arguments("")]
    public async Task Other_lines_are_not_the_response(string line) {
        await Assert.That(PiModelCatalogProbe.Parse(line).IsResponse).IsFalse();
    }
}
```

- [ ] **Step 2: Run, expect compile failure.**

- [ ] **Step 3: Implement**

`PiModelCatalogParse.cs`:

```csharp
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Harness.Pi;

/// One stdout line classified: not the response (keep reading), the response with an invalid
/// envelope (null models), or the response with its catalog (possibly empty).
internal readonly record struct PiModelCatalogParse(bool IsResponse, IReadOnlyList<VendorModelOption>? Models) {
    public static readonly PiModelCatalogParse NotResponse = new(false, null);
}
```

`PiModelCatalogProbe.cs` (parsing half; Task 3 adds the process half to the same class):

```csharp
using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Harness.Pi;

/// Asks an installed `pi` for the models it can launch here, over its RPC mode, and maps them to
/// `provider/id` values. The answer is Pi's own auth-filtered list; the daemon never reads Pi's
/// auth or models files.
internal static partial class PiModelCatalogProbe {
    internal const string Command = "get_available_models";

    internal static PiModelCatalogParse Parse(string line) {
        if (string.IsNullOrWhiteSpace(line)) return PiModelCatalogParse.NotResponse;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException) { return PiModelCatalogParse.NotResponse; }
        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject || root.Str("type") != "response" || root.Str("command") != Command)
                return PiModelCatalogParse.NotResponse;
            if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                return new(true, null);
            if (!root.TryGetProperty("data", out var data) || !data.IsObject
             || !data.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                return new(true, null);

            var list = new List<VendorModelOption>();
            foreach (var m in models.EnumerateArray()) {
                if (!m.IsObject) continue;
                var id = m.Str("id"); var provider = m.Str("provider");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(provider)) continue;
                var name = m.Str("name") is { Length: > 0 } n ? n : id;
                list.Add(new($"{provider}/{id}", $"{name} · {provider}"));
            }
            return new(true, list);
        }
    }
}
```

`Str` and `IsObject` come from `JsonElementExtensions` in `Capacitor.Cli.Core` (CLAUDE.md: use it rather than checking value kind by hand; check its exact namespace with `rg -n 'static class JsonElementExtensions' src/`).

- [ ] **Step 4: Run, expect PASS.**

- [ ] **Step 5: Commit** `Parse Pi's get_available_models response into model options`

---

## Task 3: `PiModelCatalogProbe.RunAsync` (the process half) and the factory hook

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Harness/Pi/PiModelCatalogProbe.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntimeFactory.cs` (add two default members after `SupportsModelSelection`, ~line 118)
- Modify: `src/Capacitor.Cli.Daemon/Harness/Pi/PiRpcHostedAgentRuntimeFactory.cs` (override both)
- Modify: `src/Capacitor.Cli.Core/Harness/Pi/PiPaths.cs` (add `AuthJson`, `ModelsJson`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Pi/PiModelCatalogProbeTests.cs`, `test/Capacitor.Cli.Core.Tests.Unit/Harness/Pi/PiPathsTests.cs` (extend if it exists, else create)

**Interfaces:**
- Consumes: `IPiRpcProcess` (`WriteLineAsync`, `ReadLinesAsync`, `CloseInputAsync(TimeSpan)`, `TerminateAsync`, `HasExited`, `Pid`), the factory's existing `processSource` seam `Func<ProcessStartInfo, CancellationToken, Task<IPiRpcProcess>>`, `PiLaunchEnvironment.Apply`, `ProcessTree.Kill(int pid)`.
- Produces on `IHostedAgentRuntimeFactory`:

```csharp
    /// The models this vendor can launch on this machine, or null when it publishes no catalog.
    /// Null is also every probe failure: consumers then fall through to their other sources
    /// instead of reading an empty list as "nothing usable".
    Task<IReadOnlyList<VendorModelOption>?> ProbeModelsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VendorModelOption>?>(null);

    /// Files whose fingerprint change means the catalog may have changed. Statted, never read.
    IReadOnlyList<string> CatalogFingerprintPaths => [];
```

- Produces: `internal static Task<IReadOnlyList<VendorModelOption>?> RunAsync(string piPath, string workingDirectory, Func<ProcessStartInfo, CancellationToken, Task<IPiRpcProcess>> processSource, TimeProvider time, ILogger logger, CancellationToken ct)` and `internal static string DirectoryFor(string stateDir) => Path.Combine(stateDir, "pi-probe");` and `internal static ProcessStartInfo BuildStartInfo(string piPath, string workingDirectory)`.
- Produces: `PiPaths.AuthJson => Path.Combine(AgentDir, "auth.json")`, `PiPaths.ModelsJson => Path.Combine(AgentDir, "models.json")`.

- [ ] **Step 1: Write the failing tests**

`PiModelCatalogProbeTests.cs` — two layers: a `FakePiRpcProcess` (already in `PiRpcRuntimeFakes.cs`) for the protocol, and a real fake `pi` executable for the EOF-ordering rule.

```csharp
using System.Diagnostics;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Pi;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

[ParallelLimiter<SubprocessLimit>]
public class PiModelCatalogProbeTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string Response = """{"type":"response","command":"get_available_models","success":true,"data":{"models":[{"id":"claude-opus-5","name":"Claude Opus 5","provider":"anthropic"}]}}""";

    [Test]
    public async Task Start_info_carries_offline_no_extensions_no_session_and_the_probe_directory() {
        var psi = PiModelCatalogProbe.BuildStartInfo("/opt/pi", "/state/pi-probe");
        await Assert.That(psi.ArgumentList).IsEquivalentTo(["--mode", "rpc", "--offline", "--no-extensions", "--no-session"], CollectionOrdering.Matching);
        await Assert.That(psi.WorkingDirectory).IsEqualTo("/state/pi-probe");
        await Assert.That(psi.Environment[PiLaunchEnvironment.PureVariable]).IsEqualTo("1");
        await Assert.That(psi.RedirectStandardInput && psi.RedirectStandardOutput && psi.RedirectStandardError).IsTrue();
    }

    [Test]
    public async Task Sends_the_command_reads_past_noise_and_closes_stdin_only_after_the_response() {
        var fake = new FakePiRpcProcess { AutoStateResponse = null };
        var closedBeforeResponse = false;
        fake.OnWrite = _ => {
            fake.Push("""{"type":"noise"}""");
            closedBeforeResponse = fake.InputCloseCalls > 0;
            fake.Push(Response);
        };
        fake.ExitsOnInputClose = true;

        var models = await PiModelCatalogProbe.RunAsync("/opt/pi", Tmp.Path, (_, _) => Task.FromResult<IPiRpcProcess>(fake),
            TimeProvider.System, NullLogger.Instance, CancellationToken.None);

        await Assert.That(fake.Writes.Single()).Contains("\"get_available_models\"");
        await Assert.That(closedBeforeResponse).IsFalse();
        await Assert.That(fake.InputCloseCalls).IsEqualTo(1);
        await Assert.That(models!.Single().Value).IsEqualTo("anthropic/claude-opus-5");
    }

    [Test]
    public async Task Exits_before_answering_returns_null() {
        var fake = new FakePiRpcProcess { AutoStateResponse = null };
        fake.OnWrite = _ => fake.EndOfStream(exitCode: 1);

        var models = await PiModelCatalogProbe.RunAsync("/opt/pi", Tmp.Path, (_, _) => Task.FromResult<IPiRpcProcess>(fake),
            TimeProvider.System, NullLogger.Instance, CancellationToken.None);

        await Assert.That(models).IsNull();
    }

    [Test]
    public async Task Deadline_expiry_terminates_the_child_and_returns_null() {
        var time = new FakeTimeProvider();
        var fake = new FakePiRpcProcess { AutoStateResponse = null };   // never answers
        var run = PiModelCatalogProbe.RunAsync("/opt/pi", Tmp.Path, (_, _) => Task.FromResult<IPiRpcProcess>(fake),
            time, NullLogger.Instance, CancellationToken.None);

        await Task.Delay(50);
        time.Advance(TimeSpan.FromSeconds(11));

        await Assert.That(await run).IsNull();
        await Assert.That(fake.TerminateCalls).IsGreaterThan(0);
    }

    [Test]
    public async Task Invalid_envelope_returns_null_and_empty_catalog_returns_empty() {
        var bad = new FakePiRpcProcess { AutoStateResponse = null };
        bad.OnWrite = _ => bad.Push("""{"type":"response","command":"get_available_models","success":true}""");
        await Assert.That(await PiModelCatalogProbe.RunAsync("/opt/pi", Tmp.Path, (_, _) => Task.FromResult<IPiRpcProcess>(bad), TimeProvider.System, NullLogger.Instance, CancellationToken.None)).IsNull();

        var empty = new FakePiRpcProcess { AutoStateResponse = null };
        empty.OnWrite = _ => empty.Push("""{"type":"response","command":"get_available_models","success":true,"data":{"models":[]}}""");
        var result = await PiModelCatalogProbe.RunAsync("/opt/pi", Tmp.Path, (_, _) => Task.FromResult<IPiRpcProcess>(empty), TimeProvider.System, NullLogger.Instance, CancellationToken.None);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!).IsEmpty();
    }

    [Test]
    public async Task Probe_directory_is_created_and_emptied() {
        var dir = PiModelCatalogProbe.DirectoryFor(Tmp.Path);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "leftover"), "x");

        PiModelCatalogProbe.PrepareDirectory(dir);

        await Assert.That(Directory.Exists(dir)).IsTrue();
        await Assert.That(Directory.EnumerateFileSystemEntries(dir)).IsEmpty();
    }

    /// The real-process test. The fake pi emulates Pi's EOF-as-shutdown: it reads one command,
    /// waits 500 ms polling stdin, and only answers if stdin is still open — so an implementation
    /// that closes stdin right after writing gets no response and fails here with a null catalog.
    [Test]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Real_child_answers_only_when_stdin_stays_open_until_the_response() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub binary is a POSIX shell script.");
        var cwdFile = Tmp.PathTo("cwd");
        var pi = Tmp.CreateExecutable("pi", $$"""
            #!/bin/sh
            pwd > "{{cwdFile}}"
            echo '{"type":"noise"}'
            read cmd || exit 3
            # poll stdin for EOF during the delay: `read -t` returns >128 on timeout, 1 on EOF
            i=0
            while [ $i -lt 5 ]; do
              if read -t 0.1 _; then :; elif [ $? -le 128 ]; then exit 4; fi
              i=$((i+1))
            done
            echo '{{Response}}'
            while read _; do :; done
            exit 0
            """);
        var probeDir = PiModelCatalogProbe.DirectoryFor(Tmp.Path);
        var logger = NullLoggerFactory.Instance;
        int pid = 0;
        var models = await PiModelCatalogProbe.RunAsync(pi, probeDir,
            (psi, _) => { var p = new PiRpcProcess(psi, logger.CreateLogger<PiRpcProcess>(), TimeProvider.System); pid = p.Pid; return Task.FromResult<IPiRpcProcess>(p); },
            TimeProvider.System, NullLogger.Instance, CancellationToken.None);

        await Assert.That(models!.Single().Value).IsEqualTo("anthropic/claude-opus-5");
        await Assert.That(File.ReadAllText(cwdFile).Trim()).IsEqualTo(Tmp.GetResolvedPath("pi-probe"));
        await Assert.That(await PidIdentity.Capture(pid) is null || await PidIdentity.WaitUntilGoneAsync(pid, TimeSpan.FromSeconds(5))).IsTrue();
    }
}
```

Note on the `read -t 0.1` loop: `sh` on macOS is bash 3.2 in POSIX mode and supports `read -t` with fractional seconds only in bash ≥4; if the shebang `#!/bin/sh` rejects `-t 0.1`, use `#!/bin/bash` with `read -t 0.1` or a `sleep 0.1` loop that checks `[ -t 0 ]`. Verify the stub locally once; `PidIdentity` has `Capture(pid)` returning null for a pid already gone, check its exact signature in `test/Capacitor.Tests.Helpers/`.

Also check `PiRpcProcess` exposes a way to obtain `Pid` before the first read; if not, assert child gone via `fake.HasExited` on a second, fake-process variant and keep the real-process test to the catalog + cwd assertions.

`PiPathsTests` additions:

```csharp
    [Test]
    public async Task Auth_and_models_files_live_in_the_agent_dir() {
        var paths = new PiPaths(new UserHome("/home/u"), null);
        await Assert.That(paths.AuthJson).IsEqualTo("/home/u/.pi/agent/auth.json");
        await Assert.That(paths.ModelsJson).IsEqualTo("/home/u/.pi/agent/models.json");
    }
```

- [ ] **Step 2: Run, expect compile failure.**

- [ ] **Step 3: Implement**

Add to `PiPaths`:

```csharp
    /// Provider credentials Pi keeps after `/login`. The daemon stats this to notice a catalog
    /// change; it never reads it.
    public string AuthJson => Path.Combine(AgentDir, "auth.json");

    /// Custom providers and models. Can carry API keys, so the same stat-only rule applies.
    public string ModelsJson => Path.Combine(AgentDir, "models.json");
```

Add the two default members to `IHostedAgentRuntimeFactory` (text under Interfaces above).

Process half of `PiModelCatalogProbe`:

```csharp
    internal static readonly TimeSpan Deadline  = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(2);

    internal static string DirectoryFor(string stateDir) => Path.Combine(stateDir, "pi-probe");

    /// Always empty before a run: Pi would treat a leftover `.pi/` there as project config.
    internal static void PrepareDirectory(string dir) {
        if (Directory.Exists(dir))
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir)) {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true); else File.Delete(entry);
            }
        Directory.CreateDirectory(dir);
    }

    internal static ProcessStartInfo BuildStartInfo(string piPath, string workingDirectory) {
        var psi = new ProcessStartInfo(piPath, ["--mode", "rpc", "--offline", "--no-extensions", "--no-session"]) {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        PiLaunchEnvironment.Apply(psi.Environment);
        return psi;
    }

    internal static async Task<IReadOnlyList<VendorModelOption>?> RunAsync(
            string piPath, string workingDirectory,
            Func<ProcessStartInfo, CancellationToken, Task<IPiRpcProcess>> processSource,
            TimeProvider time, ILogger logger, CancellationToken ct) {
        IPiRpcProcess process;
        try {
            PrepareDirectory(workingDirectory);
            process = await processSource(BuildStartInfo(piPath, workingDirectory), ct).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogWarning(ex, "pi model catalog probe could not start");
            return null;
        }

        using var deadline = new CancellationTokenSource(Deadline, time);
        using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try {
            await process.WriteLineAsync($$"""{"type":"{{Command}}"}""", linked.Token).ConfigureAwait(false);
            await foreach (var line in process.ReadLinesAsync(linked.Token).ConfigureAwait(false)) {
                var parsed = Parse(line);
                if (!parsed.IsResponse) continue;
                await process.CloseInputAsync(ExitGrace).ConfigureAwait(false);
                await process.WaitForExitAsync(ExitGrace).ConfigureAwait(false);
                if (!process.HasExited) await process.TerminateAsync(ExitGrace).ConfigureAwait(false);
                if (parsed.Models is null) logger.LogWarning("pi model catalog probe: invalid response envelope");
                return parsed.Models;
            }
            logger.LogWarning("pi model catalog probe: pi exited before answering (exit {Code}) {Diag}", process.ExitCode, process.Diagnostics);
            return null;
        } catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested) {
            logger.LogWarning("pi model catalog probe: no response within {Deadline}", Deadline);
            await process.TerminateAsync(ExitGrace).ConfigureAwait(false);
            return null;
        } finally {
            if (process is IDisposable d) d.Dispose();
        }
    }
```

Check how `PiRpcProcess.TerminateAsync` kills (it must go through `ProcessTree.Kill`; read `PiRpcProcess.cs` under `Harness/Pi/` or wherever `class PiRpcProcess` lives, `rg -n 'class PiRpcProcess' src/`). If `CancellationTokenSource(TimeSpan, TimeProvider)` is unavailable, use `time.CreateTimer` to cancel a plain CTS.

Factory overrides in `PiRpcHostedAgentRuntimeFactory`:

```csharp
    public IReadOnlyList<string> CatalogFingerprintPaths =>
        [new PiPaths(config.Home, config.PiAgentDir).AuthJson, new PiPaths(config.Home, config.PiAgentDir).ModelsJson];

    public Task<IReadOnlyList<VendorModelOption>?> ProbeModelsAsync(CancellationToken ct) =>
        IsAvailable()
            ? PiModelCatalogProbe.RunAsync(config.PiPath,
                PiModelCatalogProbe.DirectoryFor(config.Store.StateDirectory(config.Name)),
                _processSource, time, _logger, ct)
            : Task.FromResult<IReadOnlyList<VendorModelOption>?>(null);
```

Find how the factory already builds a `PiPaths` (search `new PiPaths(` in the daemon; reuse the same `UserHome`/agent-dir source it uses) rather than inventing `config.Home`/`config.PiAgentDir` if those names differ.

- [ ] **Step 4: Run, expect PASS** (Daemon suite filtered to `PiModelCatalogProbeTests`, Core suite filtered to `PiPathsTests`).

- [ ] **Step 5: Commit** `Probe an installed Pi for its launchable models`

---

## Task 4: Startup probe, baselines and `DaemonConfig` fields

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/CatalogPathStat.cs`, `src/Capacitor.Cli.Daemon/Services/VendorModelCatalogs.cs`
- Modify: `src/Capacitor.Cli.Daemon/DaemonConfig.cs` (beside `UnattendedVendorBaselines`, ~line 142)
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs` (~lines 606–641, and the `FingerprintUnattendedVendors` helper ~line 1525)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/VendorModelCatalogsTests.cs`, `test/Capacitor.Cli.Daemon.Tests.Unit/DaemonRunnerVendorModelsTests.cs`

**Interfaces:**
- Produces: `public readonly record struct CatalogPathStat(string Path, bool Exists, long Length, long LastWriteTicks)` with `internal static CatalogPathStat Of(string path)`.
- Produces: `DaemonConfig.VendorModels` (`Dictionary<string, VendorModelOption[]>?`) and `DaemonConfig.VendorCatalogBaselines` (`IReadOnlyDictionary<string, CatalogPathStat[]>?`).
- Produces: `internal static class VendorModelCatalogs` with
  - `static Task<Dictionary<string, VendorModelOption[]>> ProbeAsync(IEnumerable<IHostedAgentRuntimeFactory> factories, IEnumerable<string> vendors, CancellationToken ct)` — concurrent, folds non-null answers, ordinal keys.
  - `static Dictionary<string, VendorModelOption[]> Merge(Dictionary<string, VendorModelOption[]>? previous, Dictionary<string, VendorModelOption[]> fresh, IEnumerable<string> probedVendors)` — new dictionary: fresh entry where present, previous entry for a probed vendor whose fresh answer is missing (null re-probe), previous entries for unprobed vendors kept.
  - `static bool Equal(Dictionary<string, VendorModelOption[]>? a, Dictionary<string, VendorModelOption[]>? b)` — same keys, ordered `(Value, Label)` pairs.
  - `static IReadOnlyDictionary<string, CatalogPathStat[]> FingerprintCatalogPaths(IEnumerable<IHostedAgentRuntimeFactory> factories, IEnumerable<string> vendors)`.

- [ ] **Step 1: Write the failing tests**

`VendorModelCatalogsTests.cs`:

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class VendorModelCatalogsTests {
    static Dictionary<string, VendorModelOption[]> Cat(params (string Vendor, VendorModelOption[] Models)[] entries) =>
        entries.ToDictionary(e => e.Vendor, e => e.Models, StringComparer.Ordinal);

    static readonly VendorModelOption A = new("p/a", "A · p");
    static readonly VendorModelOption B = new("p/b", "B · p");

    [Test]
    public async Task Equal_is_true_for_same_keys_and_ordered_pairs_from_fresh_objects() {
        await Assert.That(VendorModelCatalogs.Equal(Cat(("pi", [A, B])), Cat(("pi", [new("p/a", "A · p"), new("p/b", "B · p")])))).IsTrue();
        await Assert.That(VendorModelCatalogs.Equal(null, null)).IsTrue();
        await Assert.That(VendorModelCatalogs.Equal(Cat(), Cat())).IsTrue();
    }

    [Test]
    public async Task Equal_is_false_for_reorder_relabel_extra_key_or_null_vs_empty() {
        await Assert.That(VendorModelCatalogs.Equal(Cat(("pi", [A, B])), Cat(("pi", [B, A])))).IsFalse();
        await Assert.That(VendorModelCatalogs.Equal(Cat(("pi", [A])), Cat(("pi", [new("p/a", "other")])))).IsFalse();
        await Assert.That(VendorModelCatalogs.Equal(Cat(("pi", [A])), Cat(("pi", [A]), ("kiro", [])))).IsFalse();
        await Assert.That(VendorModelCatalogs.Equal(null, Cat())).IsFalse();
    }

    [Test]
    public async Task Merge_takes_fresh_answers_keeps_previous_on_null_reprobe_and_returns_a_new_instance() {
        var previous = Cat(("pi", [A]), ("other", [B]));
        var fresh    = Cat(("pi", [A, B]));            // "other" was probed and answered null

        var merged = VendorModelCatalogs.Merge(previous, fresh, probedVendors: ["pi", "other"]);

        await Assert.That(ReferenceEquals(merged, previous)).IsFalse();
        await Assert.That(merged["pi"]).IsEquivalentTo([A, B], CollectionOrdering.Matching);
        await Assert.That(merged["other"]).IsEquivalentTo([B]);
        await Assert.That(previous["pi"]).IsEquivalentTo([A]);   // untouched
    }

    [Test]
    public async Task Probe_folds_only_non_null_answers_keyed_by_vendor() {
        var factories = new IHostedAgentRuntimeFactory[] {
            new StubCatalogFactory("pi",   [A]),
            new StubCatalogFactory("kiro", []),
            new StubCatalogFactory("claude", null),
        };
        var result = await VendorModelCatalogs.ProbeAsync(factories, ["pi", "kiro", "claude"], CancellationToken.None);
        await Assert.That(result.Keys).IsEquivalentTo(["pi", "kiro"]);
        await Assert.That(result["kiro"]).IsEmpty();
    }

    [Test]
    public async Task Fingerprint_records_an_empty_array_for_a_factory_with_no_paths() {
        var fp = VendorModelCatalogs.FingerprintCatalogPaths([new StubCatalogFactory("claude", null)], ["claude"]);
        await Assert.That(fp["claude"]).IsEmpty();
    }

    [Test]
    public async Task CatalogPathStat_distinguishes_missing_from_present_and_changes_on_write() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("auth.json");
        var missing = CatalogPathStat.Of(path);
        tmp.CreateFile("auth.json", "{}");
        var present = CatalogPathStat.Of(path);
        await Assert.That(missing.Exists).IsFalse();
        await Assert.That(present.Exists).IsTrue();
        await Assert.That(missing).IsNotEqualTo(present);
    }
}
```

`StubCatalogFactory` goes in `test/Capacitor.Cli.Daemon.Tests.Unit/Services/StubCatalogFactory.cs`: implements `IHostedAgentRuntimeFactory` with `Vendor`, `IsAvailable() => true`, `CliPath => "/bin/" + Vendor`, `ProbeModelsAsync` returning the given list, `CatalogFingerprintPaths` from a ctor arg (default `[]`), and `NotSupportedException` for the runtime-creating members. Check the interface's full member list (`sed -n 29,140p src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntimeFactory.cs`) and look at how `SpyHostedAgentLauncher`/other test doubles implement it to copy the required members.

`DaemonRunnerVendorModelsTests.cs` pins that startup baselines cover every advertised vendor:

```csharp
    [Test]
    public async Task Startup_fingerprints_binary_and_catalog_paths_for_every_advertised_vendor() {
        using var tmp = new TempDir();
        var pi = tmp.CreateExecutable("pi", "#!/bin/sh\nexit 0\n");
        var auth = tmp.PathTo("auth.json");
        var factory = new StubCatalogFactory("pi", [new("p/a", "A · p")], paths: [auth]) { CliPathOverride = pi };

        var binaries = DaemonRunner.FingerprintUnattendedVendors(TestBinaries.None, [factory], ["pi"]);
        var catalogs = VendorModelCatalogs.FingerprintCatalogPaths([factory], ["pi"]);

        await Assert.That(binaries["pi"]).IsNotNull();
        await Assert.That(catalogs["pi"].Single().Exists).IsFalse();
    }
```

- [ ] **Step 2: Run, expect compile failure.**

- [ ] **Step 3: Implement**

`CatalogPathStat.cs`:

```csharp
namespace Capacitor.Cli.Daemon.Services;

/// Fingerprint of a file a vendor's model catalog depends on. A missing file is a value of its
/// own, since deleting Pi's auth file does change what Pi can launch.
public readonly record struct CatalogPathStat(string Path, bool Exists, long Length, long LastWriteTicks) {
    internal static CatalogPathStat Of(string path) {
        var info = new FileInfo(path);
        return info.Exists ? new(path, true, info.Length, info.LastWriteTimeUtc.Ticks) : new(path, false, 0, 0);
    }
}
```

`VendorModelCatalogs.cs`:

```csharp
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

internal static class VendorModelCatalogs {
    internal static async Task<Dictionary<string, VendorModelOption[]>> ProbeAsync(
            IEnumerable<IHostedAgentRuntimeFactory> factories, IEnumerable<string> vendors, CancellationToken ct) {
        var wanted = new HashSet<string>(vendors, StringComparer.Ordinal);
        var probes = factories.Where(f => wanted.Contains(f.Vendor))
            .Select(async f => (f.Vendor, Models: await f.ProbeModelsAsync(ct).ConfigureAwait(false)))
            .ToArray();
        var result = new Dictionary<string, VendorModelOption[]>(StringComparer.Ordinal);
        foreach (var (vendor, models) in await Task.WhenAll(probes).ConfigureAwait(false))
            if (models is not null) result[vendor] = [.. models];
        return result;
    }

    internal static Dictionary<string, VendorModelOption[]> Merge(
            Dictionary<string, VendorModelOption[]>? previous, Dictionary<string, VendorModelOption[]> fresh,
            IEnumerable<string> probedVendors) {
        var merged = new Dictionary<string, VendorModelOption[]>(previous ?? [], StringComparer.Ordinal);
        foreach (var (vendor, models) in fresh) merged[vendor] = models;
        return merged;   // a probed vendor absent from fresh keeps its previous entry by construction
    }

    internal static bool Equal(Dictionary<string, VendorModelOption[]>? a, Dictionary<string, VendorModelOption[]>? b) {
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count) return false;
        foreach (var (vendor, models) in a) {
            if (!b.TryGetValue(vendor, out var other)) return false;
            if (!models.AsSpan().SequenceEqual(other)) return false;   // record equality per element
        }
        return true;
    }

    internal static IReadOnlyDictionary<string, CatalogPathStat[]> FingerprintCatalogPaths(
            IEnumerable<IHostedAgentRuntimeFactory> factories, IEnumerable<string> vendors) {
        var byVendor = factories.ToDictionary(f => f.Vendor, StringComparer.Ordinal);
        return vendors.ToDictionary(
            v => v,
            v => byVendor.TryGetValue(v, out var f) ? f.CatalogFingerprintPaths.Select(CatalogPathStat.Of).ToArray() : [],
            StringComparer.Ordinal);
    }
}
```

(`probedVendors` is kept in the signature so the rule is explicit at the call site even though the dictionary-copy construction satisfies it; if the analyzer flags the unused parameter, drop it and the test argument together.)

`DaemonConfig` additions beside `UnattendedVendorBaselines`:

```csharp
    /// Per-vendor launchable models probed from the installed CLIs. Replaced by reference on every
    /// refresh, never mutated: serializers on other threads may be enumerating the current instance.
    public Dictionary<string, VendorModelOption[]>? VendorModels { get; set; }

    /// Fingerprints of each vendor's catalog files, taken before the startup probe; the vendor CLI
    /// watcher's starting point for them.
    public IReadOnlyDictionary<string, Services.CatalogPathStat[]>? VendorCatalogBaselines { get; set; }
```

`DaemonRunner`, replace the baseline line (~637) and add the probe, keeping the "fingerprint BEFORE probe" order:

```csharp
        config.UnattendedVendorBaselines =
            FingerprintUnattendedVendors(config.Binaries, runtimeFactories, config.SupportedVendors);
        config.VendorCatalogBaselines =
            VendorModelCatalogs.FingerprintCatalogPaths(runtimeFactories, config.SupportedVendors);
        config.UnattendedVendorCapabilities =
            ComputeUnattendedVendorCapabilities(runtimeFactories, config, config.UnattendedVendors);
        config.VendorModels =
            await VendorModelCatalogs.ProbeAsync(runtimeFactories, config.SupportedVendors, CancellationToken.None);
        foreach (var (vendor, models) in config.VendorModels) LogVendorModels(logger, vendor, models.Length);
        LogStartupPhase(logger, "vendors probed");
```

Add a `[LoggerMessage(Level = LogLevel.Information, Message = "{Vendor}: {Count} models")] static partial void LogVendorModels(ILogger logger, string vendor, int count);` beside the other startup log messages. If `RunAsync` has a cancellation token in scope at that point, use it instead of `CancellationToken.None`.

- [ ] **Step 4: Run, expect PASS.**

- [ ] **Step 5: Commit** `Probe and fingerprint vendor model catalogs at daemon startup`

---

## Task 5: Advertise on both wires

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/ServerConnection.cs` (`DaemonConnectCoreAsync`, ~line 651)
- Modify: `src/Capacitor.Cli.Daemon/Services/DaemonStatusIpc.cs` (`Snapshot`, ~line 70)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/DaemonStatusIpcTests.cs` (extend), `test/Capacitor.Cli.Daemon.Tests.Unit/Services/RegistrationGateTests.cs` or wherever `DaemonConnect` payloads are captured (`rg -n 'CaptureServerConnection' test/`)

- [ ] **Step 1: Write the failing tests**

In the status IPC tests (find the existing snapshot test and copy its harness):

```csharp
    [Test]
    public async Task Snapshot_carries_the_configured_vendor_models() {
        // build config/orchestrator/ipc exactly as the sibling test does, then:
        config.VendorModels = new(StringComparer.Ordinal) { ["pi"] = [new("p/a", "A · p")] };
        var json = ipc.SnapshotForTest();   // add this seam if Snapshot is private: internal string SnapshotForTest() => Snapshot();
        var dto  = JsonSerializer.Deserialize(json, StatusIpcJsonContext.Default.DaemonStatusDto)!;
        await Assert.That(dto.Daemon.VendorModels!["pi"].Single().Value).IsEqualTo("p/a");
    }
```

For the server registration, find the test that asserts fields on the captured `DaemonConnect` (search `SupportedVendors` in `test/Capacitor.Cli.Daemon.Tests.Unit/Services/`) and add a sibling asserting `VendorModels` is passed through from `config.VendorModels` (same instance or equal content).

- [ ] **Step 2: Run, expect failure** (field null).

- [ ] **Step 3: Implement**

`ServerConnection.DaemonConnectCoreAsync`: add `VendorModels: _config.VendorModels,` to the `new DaemonConnect(...)` argument list (named, beside `UnattendedVendors`).

`DaemonStatusIpc.Snapshot`: pass `config.VendorModels` as the new last argument of `new DaemonInfoDto(...)`.

- [ ] **Step 4: Run, expect PASS.**

- [ ] **Step 5: Commit** `Advertise vendor model catalogs on DaemonConnect and the status IPC`

---

## Task 6: Refresh through `RefreshAdvertisedCapabilities`

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (`RefreshAdvertisedCapabilities`, ~line 1463)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorCapabilityRefreshTests.cs`

**Interfaces:**
- Consumes: `VendorModelCatalogs.ProbeAsync/Merge/Equal`, `_runtimeFactories` (already on the orchestrator), `_statusNotifier.Pulse()`, `_server.ReRegisterAsync()`.

- [ ] **Step 1: Write the failing tests**

Add to `AgentOrchestratorCapabilityRefreshTests` (the harness's `configure:` lambda can register a `StubCatalogFactory` under `"pi"` in the runtime factories; look at `AgentOrchestratorHarness.BuildOrchestrator` for how factories are supplied, and extend it with an optional `factories:` argument if it only takes launchers):

```csharp
    [Test]
    public async Task A_changed_catalog_is_republished_by_reference_swap_and_pulses_status() {
        var stub = new StubCatalogFactory("pi", [new("p/a", "A · p")]);
        var (orch, server, config) = BuildWithCatalog(stub, initial: new(StringComparer.Ordinal) { ["pi"] = [] });
        await using var _ = orch;
        var before = config.VendorModels;
        var v0 = orch.StatusNotifierForTest.Version;

        orch.RefreshAdvertisedCapabilities("test");
        await orch.CapabilityRefreshForTest;

        await Assert.That(ReferenceEquals(config.VendorModels, before)).IsFalse();
        await Assert.That(config.VendorModels!["pi"].Single().Value).IsEqualTo("p/a");
        await Assert.That(server.RegisterDaemonCalls).IsEqualTo(1);
        await Assert.That(orch.StatusNotifierForTest.Version).IsGreaterThan(v0);
    }

    [Test]
    public async Task An_equal_content_reprobe_neither_republishes_nor_swaps() {
        var stub = new StubCatalogFactory("pi", [new("p/a", "A · p")]);
        var (orch, server, config) = BuildWithCatalog(stub, initial: new(StringComparer.Ordinal) { ["pi"] = [new("p/a", "A · p")] });
        await using var _ = orch;
        var before = config.VendorModels;
        var v0 = orch.StatusNotifierForTest.Version;

        orch.RefreshAdvertisedCapabilities("test");
        await orch.CapabilityRefreshForTest;

        await Assert.That(ReferenceEquals(config.VendorModels, before)).IsTrue();
        await Assert.That(server.RegisterDaemonCalls).IsEqualTo(0);
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0);
    }

    [Test]
    public async Task A_null_reprobe_keeps_the_previous_entry() {
        var stub = new StubCatalogFactory("pi", null);
        var (orch, server, config) = BuildWithCatalog(stub, initial: new(StringComparer.Ordinal) { ["pi"] = [new("p/a", "A · p")] });
        await using var _ = orch;

        orch.RefreshAdvertisedCapabilities("test");
        await orch.CapabilityRefreshForTest;

        await Assert.That(config.VendorModels!["pi"].Single().Value).IsEqualTo("p/a");
        await Assert.That(server.RegisterDaemonCalls).IsEqualTo(0);
    }

    [Test]
    public async Task A_status_serializer_racing_a_refresh_sees_a_whole_dictionary() {
        var stub = new StubCatalogFactory("pi", Enumerable.Range(0, 2000).Select(i => new VendorModelOption($"p/{i}", $"{i} · p")).ToArray());
        var (orch, server, config) = BuildWithCatalog(stub, initial: new(StringComparer.Ordinal) { ["pi"] = [] });
        await using var _ = orch;

        var serializing = Task.Run(() => {
            for (var i = 0; i < 200; i++) {
                var snapshot = config.VendorModels;   // read the reference once, as the IPC does
                JsonSerializer.Serialize(new DaemonInfoDto("d", "1", "s", "connected", 1, 0, VendorModels: snapshot), StatusIpcJsonContext.Default.DaemonInfoDto);
            }
        });
        orch.RefreshAdvertisedCapabilities("test");
        await orch.CapabilityRefreshForTest;
        await serializing;   // no InvalidOperationException from a mutated collection
    }
```

`BuildWithCatalog` is a sibling of the existing `Build` that also sets `config.VendorModels = initial` and passes the stub factory to the harness. The catalog tests must not depend on the Claude stub binary; keep `UnattendedVendors` empty for them so `ComputeUnattendedVendorCapabilities` has nothing to probe.

- [ ] **Step 2: Run, expect failure.**

- [ ] **Step 3: Implement** — inside the trigger delegate, after computing `fresh` capabilities:

```csharp
                var catalogVendors = _runtimeFactories.Values
                    .Where(f => f.CatalogFingerprintPaths.Count > 0 || (_config.VendorModels?.ContainsKey(f.Vendor) ?? false))
                    .Select(f => f.Vendor).ToArray();
                var probed         = await VendorModelCatalogs.ProbeAsync(_runtimeFactories.Values, catalogVendors, CancellationToken.None);
                var mergedCatalog  = VendorModelCatalogs.Merge(_config.VendorModels, probed, catalogVendors);
                var catalogChanged = !VendorModelCatalogs.Equal(_config.VendorModels, mergedCatalog);

                if (!republish && !catalogChanged && current is not null && current.SequenceEqual(fresh)) return;

                _config.UnattendedVendorCapabilities = fresh;
                if (catalogChanged) _config.VendorModels = mergedCatalog;
                LogReAdvertising(...);
                await _server.ReRegisterAsync();
                if (catalogChanged) _statusNotifier.Pulse();
```

Keep the existing early-return semantics for the capability half intact; only the catalog condition is added.

- [ ] **Step 4: Run, expect PASS.**

- [ ] **Step 5: Commit** `Re-probe vendor model catalogs on capability refresh`

---

## Task 7: `VendorCliWatcher` watch set and catalog fingerprints

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/VendorCliWatcher.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/VendorCliWatcherTests.cs`

**Interfaces:**
- Produces: `internal static IReadOnlyList<(string Vendor, string CliPath, IReadOnlyList<string> CatalogPaths)> WatchSet(DaemonConfig config, IReadOnlyDictionary<string, IHostedAgentRuntimeFactory> factories)`.
- Changes: `Watched` element type gains `CatalogPaths`; `ForTest` takes the widened tuple, an optional `Func<string, CatalogPathStat> statCatalog`, and optional `catalogBaselines`.

- [ ] **Step 1: Write the failing tests** (extend the `Harness` in the test file with `CatalogStats` dictionary and a `statCatalog` seam)

```csharp
    [Test]
    public async Task A_changed_catalog_path_requests_one_refresh_naming_the_vendor() {
        var h = new Harness(("pi", "/bin/pi", ["/home/u/.pi/agent/auth.json"]));
        h.Stats["/bin/pi"] = Old;
        h.CatalogStats["/home/u/.pi/agent/auth.json"] = new("/home/u/.pi/agent/auth.json", true, 10, 1);
        h.Watcher.PrimeBaselines();

        h.CatalogStats["/home/u/.pi/agent/auth.json"] = new("/home/u/.pi/agent/auth.json", true, 12, 2);
        h.Watcher.Tick();

        await Assert.That(h.Refreshes).Count().IsEqualTo(1);
        await Assert.That(h.Refreshes[0]).Contains("pi");
    }

    [Test]
    public async Task An_unchanged_catalog_path_requests_nothing() { /* same setup, no mutation, Tick, expect empty */ }

    [Test]
    public async Task A_deleted_catalog_file_is_a_change() {
        // baseline Exists=true → current Exists=false → one refresh
    }

    [Test]
    public async Task A_recorded_catalog_baseline_that_differs_at_start_fires_on_the_first_tick() {
        // construct with catalogBaselines: { pi: [stat with ticks 1] }, CatalogStats returns ticks 2, PrimeBaselines, Tick → 1 refresh
    }

    [Test]
    public async Task WatchSet_includes_a_catalog_only_vendor_and_a_vendor_known_only_by_its_catalog() {
        var config = new DaemonConfig {
            UnattendedVendors = ["claude"],
            VendorModels = new(StringComparer.Ordinal) { ["silent"] = [] },
        };
        var factories = new Dictionary<string, IHostedAgentRuntimeFactory>(StringComparer.Ordinal) {
            ["claude"] = new StubCatalogFactory("claude", null),
            ["pi"]     = new StubCatalogFactory("pi", null, paths: ["/a"]),
            ["silent"] = new StubCatalogFactory("silent", null),
        };
        var set = VendorCliWatcher.WatchSet(config, factories);
        await Assert.That(set.Select(w => w.Vendor)).IsEquivalentTo(["claude", "pi", "silent"]);
    }
```

- [ ] **Step 2: Run, expect compile failure.**

- [ ] **Step 3: Implement**

- Widen `Watched` to `IReadOnlyList<(string Vendor, string CliPath, IReadOnlyList<string> CatalogPaths)>`.
- Add `internal Func<string, CatalogPathStat> StatCatalog = CatalogPathStat.Of;` and `readonly Dictionary<string, CatalogPathStat[]> _catalogBaselines`, plus `IReadOnlyDictionary<string, CatalogPathStat[]>? _recordedCatalogs`.
- `WatchSet`:

```csharp
    internal static IReadOnlyList<(string Vendor, string CliPath, IReadOnlyList<string> CatalogPaths)> WatchSet(
            DaemonConfig config, IReadOnlyDictionary<string, IHostedAgentRuntimeFactory> factories) {
        var vendors = new HashSet<string>(config.UnattendedVendors ?? [], StringComparer.Ordinal);
        foreach (var f in factories.Values)
            if (f.CatalogFingerprintPaths.Count > 0 || (config.VendorModels?.ContainsKey(f.Vendor) ?? false)) vendors.Add(f.Vendor);
        return vendors.Where(factories.ContainsKey)
            .Select(v => (v, factories[v].CliPath, factories[v].CatalogFingerprintPaths))
            .Where(w => !string.IsNullOrEmpty(w.CliPath) || w.CatalogFingerprintPaths.Count > 0)
            .OrderBy(w => w.v, StringComparer.Ordinal)
            .ToArray();
    }
```

- `ExecuteAsync`: `Watched = WatchSet(_config, _factories); _recorded = _config.UnattendedVendorBaselines; _recordedCatalogs = _config.VendorCatalogBaselines;`.
- `PrimeBaselines`: seed `_catalogBaselines[vendor]` from `_recordedCatalogs` when present, else `paths.Select(StatCatalog).ToArray()`.
- `Tick`: a vendor changed when the binary changed (existing rule) **or** `!paths.Select(StatCatalog).SequenceEqual(_catalogBaselines[vendor])`; on change, update both baselines. Reason string: `"{vendors} CLI binary or catalog files changed on disk"`.

- [ ] **Step 4: Run, expect PASS**, then run the whole Daemon suite once.

- [ ] **Step 5: Commit** `Watch Pi's catalog files and catalog-only vendors for re-advertisement`

---

## Task 8: Desktop app: `EffectiveModelCatalog`, machine catalog, precedence

**Files:**
- Modify: `src/Capacitor.App/ViewModels/HomeViewModel.cs` (`MachineOption` ~line 29; catalog subscription ~line 443; `ModelChoicesFor` ~line 126; `FindMachine` ~line 1009; `SelectMachineAsync` ~line 957; `ModelCatalog` property ~line 122)
- Modify: `src/Capacitor.App/Views/LauncherPaneView.axaml` (line ~96, the 4th binding)
- Modify: `src/Capacitor.App/Views/LauncherPaneView.axaml.cs` if the picker reads `vm.ModelCatalog` anywhere (`rg -n ModelCatalog src/Capacitor.App/`)
- Test: `test/Capacitor.App.Tests.Unit/HomeViewModelTests.cs`, `test/Capacitor.App.Tests.Unit/FakeDaemonClientService.cs` (add a `vendorModels` parameter to `Snap`)

**Interfaces:**
- Produces: `MachineOption(..., string? Version = null, IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>? VendorModels = null)`.
- Produces: `public IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> EffectiveModelCatalog { get; }` (OAPH, initial `ServerVendorModelCatalog.Empty`), replacing `ModelCatalog`.
- Produces: `internal static IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> MergeCatalogs(IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>? machine, IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> server)` — pure, tested directly.
- Produces: two converters from wire shapes to `IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>`: `internal static ... FromStatus(Dictionary<string, VendorModelOption[]>?)` and `FromRegistry(Dictionary<string, VendorModelOptionDto[]>?)`, both returning null for null input, on a small static class `VendorModelMaps` in `src/Capacitor.App/Services/VendorModelMaps.cs`.

- [ ] **Step 1: Write the failing tests**

Extend `FakeDaemonClientService.Snap` with `Dictionary<string, VendorModelOption[]>? vendorModels = null` passed to `DaemonInfoDto`. Then:

```csharp
    static IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> Server(params (string V, string[] Slugs)[] e) =>
        e.ToDictionary(x => x.V, x => (IReadOnlyList<ModelChoice>)[.. x.Slugs.Select(s => new ModelChoice(s, s))], StringComparer.OrdinalIgnoreCase);

    [Test]
    public async Task MergeCatalogs_machine_key_wins_even_when_empty_else_non_empty_server_else_absent() {
        var machine = new Dictionary<string, IReadOnlyList<ModelChoice>>(StringComparer.OrdinalIgnoreCase) {
            ["pi"] = [new("anthropic/claude-opus-5", "Claude Opus 5 · anthropic")], ["kiro"] = [] };
        var server  = Server(("pi", ["server-pi"]), ("kiro", ["server-kiro"]), ("gemini", ["gemini-3-pro"]), ("cursor", []));
        var merged  = HomeViewModel.MergeCatalogs(machine, server);
        await Assert.That(merged["pi"].Single().Slug).IsEqualTo("anthropic/claude-opus-5");
        await Assert.That(merged["kiro"]).IsEmpty();
        await Assert.That(merged["gemini"].Single().Slug).IsEqualTo("gemini-3-pro");
        await Assert.That(merged.ContainsKey("cursor")).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Local_snapshot_catalog_takes_precedence_and_empty_key_beats_curated() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), TimeProvider.System,
                modelCatalog: Observable.Return(Server(("claude", ["server-claude"]))));

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(vendorModels: new(StringComparer.Ordinal) {
                ["pi"] = [new("anthropic/claude-opus-5", "Claude Opus 5 · anthropic")], ["claude"] = [] }));

            await Assert.That(vm.ModelChoicesFor("pi").Single().Slug).IsEqualTo("anthropic/claude-opus-5");
            await Assert.That(vm.ModelChoicesFor("claude")).IsEmpty();          // machine empty beats server and curated
            await Assert.That(vm.ModelChoicesFor("codex")).IsNotEmpty();        // curated fallback untouched
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Before_any_local_snapshot_the_server_catalog_alone_populates_the_view() {
        // no SnapshotsSubject.OnNext; modelCatalog returns Server(("gemini", ["gemini-3-pro"]))
        // assert EffectiveModelCatalog["gemini"] is present and ModelChoicesFor("pi") is empty (curated has none)
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_selected_remote_machine_supplies_its_own_catalog_and_follows_registry_updates() {
        // daemons: BehaviorSubject<IReadOnlyList<DaemonInfo>> seeded with a remote daemon "box" owned by viewer "u1"
        //   with VendorModels { pi: [{value:"a/x", label:"X · a"}] }, viewerId: _ => Task.FromResult("u1")
        // await vm.SelectMachineAsync("box")
        // assert ModelChoicesFor("pi").Single().Slug == "a/x"
        // daemons.OnNext([same daemon with VendorModels { pi: [{value:"a/y", ...}] }])  → "a/y" without reselecting
        // daemons.OnNext([])                                                        → falls back to server catalog
        // await vm.SelectMachineAsync(local name) → local snapshot's list again
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_same_named_daemon_owned_by_someone_else_never_supplies_the_catalog() {
        // daemons emits [box owned by "u1" with pi:[a/x], box owned by "u2" with pi:[a/z]]; select "box"; expect "a/x"
        // then emit only u2's box → catalog falls back (not a/z)
    }
```

Look at the existing remote-machine tests in `HomeViewModelTests` (search `SelectMachineAsync`) for how `daemons`, `viewerId` and `localMachineId` are supplied and copy that setup.

- [ ] **Step 2: Run, expect compile failure.**

- [ ] **Step 3: Implement**

`VendorModelMaps.cs`:

```csharp
using Capacitor.Cli.Core;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

internal static class VendorModelMaps {
    public static IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>? FromStatus(Dictionary<string, VendorModelOption[]>? wire) =>
        wire?.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ModelChoice>)[.. kv.Value.Select(o => new ModelChoice(o.Value, o.Label))], StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>? FromRegistry(Dictionary<string, VendorModelOptionDto[]>? wire) =>
        wire?.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ModelChoice>)[.. kv.Value.Select(o => new ModelChoice(o.Value, o.Label))], StringComparer.OrdinalIgnoreCase);
}
```

`HomeViewModel`:

```csharp
    internal static IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> MergeCatalogs(
            IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>? machine,
            IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> server) {
        var merged = new Dictionary<string, IReadOnlyList<ModelChoice>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (vendor, models) in server) if (models.Count > 0) merged[vendor] = models;
        if (machine is not null) foreach (var (vendor, models) in machine) merged[vendor] = models;
        return merged;
    }
```

Replace the `modelCatalog` subscription with:

```csharp
        var localCatalog = daemon.Snapshots.Select(s => VendorModelMaps.FromStatus(s.Daemon.VendorModels)).StartWith((IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>?)null);
        var machineCatalog = localCatalog.CombineLatest(_daemons.StartWith((IReadOnlyList<DaemonInfo>)[]), _machineSelectionChanges,
            (local, list, sel) => sel.Remote ? FindMachine(list, sel.Name, _lastViewerId)?.VendorModels : local);
        _effectiveModelCatalog = machineCatalog
            .CombineLatest(modelCatalog ?? Observable.Return(ServerVendorModelCatalog.Empty), MergeCatalogs)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.EffectiveModelCatalog, ServerVendorModelCatalog.Empty)
            .DisposeWith(_disposables);
```

`FindMachine` builds `MachineOption` with `VendorModels: VendorModelMaps.FromRegistry(d.VendorModels)`; `SelectMachineAsync`'s own `new MachineOption(...)` gets the same. `ModelChoicesFor`:

```csharp
    public IReadOnlyList<ModelChoice> ModelChoicesFor(string vendor) =>
        EffectiveModelCatalog.TryGetValue(vendor, out var models) ? models : HostedHarnessCatalog.ModelChoicesFor(vendor);
```

Delete `_modelCatalog`/`ModelCatalog`; rename the binding in `LauncherPaneView.axaml` to `EffectiveModelCatalog`; fix any other reader.

- [ ] **Step 4: Run the App suite, expect PASS** (including the pre-existing `Model_choices_prefer_the_server_catalog_and_fall_back_to_the_curated_list`, which still holds).

- [ ] **Step 5: Commit** `Merge the daemon's model catalog into the launcher picker`

---

## Task 9: Desktop app: selected-model revalidation and picker search

**Files:**
- Modify: `src/Capacitor.App/ViewModels/HomeViewModel.cs`
- Modify: `src/Capacitor.App/Views/LauncherPaneView.axaml.cs` (`RebuildRows` ~line 445–470: `Matches` must test both `Slug` and `Label`)
- Test: `test/Capacitor.App.Tests.Unit/HomeViewModelTests.cs`, `test/Capacitor.App.Tests.Unit/LauncherPaneViewTests.cs` (or the existing converter/smoke test file for the launcher; `rg -n AgentChipTextConverter test/`)

**Interfaces:**
- Produces: `internal static bool ShouldClearSelection(string vendor, string model, IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> previous, IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> next)` — pure.

- [ ] **Step 1: Write the failing tests**

```csharp
    static IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> Cat(params (string V, string[] Slugs)[] e) => Server(e);

    [Test]
    public async Task ShouldClearSelection_only_when_previously_listed_and_now_absent_under_a_present_key() {
        var listedA = Cat(("pi", ["a", "b"]));
        var withoutA = Cat(("pi", ["b"]));
        var noKey = Cat();
        await Assert.That(HomeViewModel.ShouldClearSelection("pi", "a", listedA, withoutA)).IsTrue();     // machine switch / refresh drops it
        await Assert.That(HomeViewModel.ShouldClearSelection("pi", "custom", noKey, withoutA)).IsFalse();  // typed id, never listed
        await Assert.That(HomeViewModel.ShouldClearSelection("pi", "a", noKey, listedA)).IsFalse();        // picked before any catalog
        await Assert.That(HomeViewModel.ShouldClearSelection("pi", "a", listedA, noKey)).IsFalse();        // next has no key: unknown, keep
        await Assert.That(HomeViewModel.ShouldClearSelection("pi", "", listedA, withoutA)).IsFalse();      // Default never clears
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Switching_to_a_machine_whose_pi_list_lacks_the_pick_resets_the_model_and_a_custom_id_survives() {
        // local snapshot pi:[a]; vm.SelectedVendor="pi"; vm.SelectedModel="a"
        // select remote "box" with pi:[b] → SelectedModel == ""
        // vm.SelectedModel = "typed/custom"; emit daemons with box pi:[c] → still "typed/custom"
        // emit box pi:[c, typed/custom] then box pi:[c] → "" (listed then withdrawn)
    }

    [Test]
    public async Task Picker_search_matches_label_and_slug() {
        // Whatever unit the picker exposes for filtering: if RebuildRows is not unit-testable, extract
        // `internal static bool RowMatches(ModelChoice m, string term)` and test it:
        var m = new ModelChoice("github-copilot/claude-opus-5", "Claude Opus 5 · github-copilot");
        await Assert.That(LauncherPaneView.RowMatches(m, "opus")).IsTrue();
        await Assert.That(LauncherPaneView.RowMatches(m, "github-copilot/")).IsTrue();
        await Assert.That(LauncherPaneView.RowMatches(m, "anthropic")).IsFalse();
    }
```

- [ ] **Step 2: Run, expect compile failure.**

- [ ] **Step 3: Implement**

```csharp
    internal static bool ShouldClearSelection(string vendor, string model,
            IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> previous,
            IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> next) {
        if (string.IsNullOrWhiteSpace(model)) return false;
        if (!previous.TryGetValue(vendor, out var was) || !was.Any(m => string.Equals(m.Slug, model, StringComparison.OrdinalIgnoreCase))) return false;
        return next.TryGetValue(vendor, out var now) && !now.Any(m => string.Equals(m.Slug, model, StringComparison.OrdinalIgnoreCase));
    }
```

Subscribe pairwise on the same merged stream that feeds the OAPH (before `ToProperty`, share it with `.Publish().RefCount()` or subscribe to `this.WhenAnyValue(x => x.EffectiveModelCatalog)` and keep the previous value in a field):

```csharp
        this.WhenAnyValue(x => x.EffectiveModelCatalog)
            .Scan((Prev: ServerVendorModelCatalog.Empty, Next: ServerVendorModelCatalog.Empty), (acc, next) => (acc.Next, next))
            .Subscribe(pair => { if (ShouldClearSelection(SelectedVendor, SelectedModel, pair.Prev, pair.Next)) SelectedModel = ""; })
            .DisposeWith(_disposables);
```

In `LauncherPaneView.axaml.cs`, replace the slug-only `Matches` with `RowMatches(m, term)` testing `m.Slug` and `m.Label`, both `OrdinalIgnoreCase`.

- [ ] **Step 4: Run the App suite, expect PASS.**

- [ ] **Step 5: Commit** `Reset a listed Pi model the target machine no longer offers`

- [ ] **Step 6: Manual check** (record the outcome in the PR description): with the local daemon rebuilt and running (`local-kcap-install-recipe` memory), open the desktop launcher, pick Pi, confirm the machine's models are listed with `name · provider` labels; run `pi` and `/login` for a provider you are not yet signed in to (or `touch ~/.pi/agent/auth.json`), wait ~15 s, reopen the flyout, confirm the list changed without a daemon restart.

- [ ] **Step 7: Publish check, Linear-id check, push, open the kcap-cli PR** following `.github/PULL_REQUEST_TEMPLATE.md`. README needs no change (no CLI surface changed).

---

## Task 10 (kcap-server): registry passthrough

**Files (kcap-server):**
- Modify: `src/Capacitor.Api.Public.Abstractions/Agents/Daemon/DaemonConnectArgs.cs` (trailing `Dictionary<string, LaunchModelOption[]>? VendorModels = null`)
- Modify: `src/Capacitor.Api.Public/Sessions/CapacitorHub.cs` (~line 1487: `vendorModels: cmd.VendorModels`)
- Modify: `src/Capacitor.Server.Services/Agents/DaemonRegistry.cs` (`Register` param after `permissionModeVendors`; `DaemonEntry` trailing `IReadOnlyDictionary<string, IReadOnlyList<VendorModelOption>>? VendorModels = null`; `ToInfo()` passes it)
- Modify: `src/Capacitor.Server.Core/DaemonInfo.cs` (trailing `IReadOnlyDictionary<string, IReadOnlyList<VendorModelOption>>? VendorModels = null`)
- Modify: `src/Capacitor.Api.Public.Abstractions/Agents/Daemon/ConnectedDaemon.cs` (trailing `Dictionary<string, LaunchModelOption[]>? VendorModels`), `src/Capacitor.Api.Public/Infrastructure/ConnectedDaemonMapping.cs`
- Test: `test/Capacitor.Server.Tests.Agents/DaemonRegistryTests.cs`

**Interfaces:**
- Consumes: `VendorModelOption(string Value, string Label)` in `Capacitor.Server.Core/Vendors/`, `LaunchModelOption { Value, Label }` in `Capacitor.Api.Public.Abstractions/Agents/`.
- Produces: a mapping helper `internal static IReadOnlyDictionary<string, IReadOnlyList<VendorModelOption>>? ToDomain(this Dictionary<string, LaunchModelOption[]>? wire)` beside the other `ToDomain` extensions in `Capacitor.Api.Public` (find them: `rg -n 'static .* ToDomain\(' src/Capacitor.Api.Public`), and the reverse for `ConnectedDaemonMapping`.

- [ ] **Step 1: Write the failing test** (copy the shape of an existing `Register` test in `DaemonRegistryTests`):

```csharp
    [Test]
    public async Task Vendor_models_pass_through_registration_and_a_re_register_replaces_them() {
        var registry = NewRegistry();   // whatever the file's helper is
        var models = new Dictionary<string, IReadOnlyList<VendorModelOption>> { ["pi"] = [new("anthropic/claude-opus-5", "Claude Opus 5 · anthropic")] };
        await registry.RegisterAsync("c1", "u1", "box", "mac", [], 1, vendorModels: models /* plus the required positional args the helper uses */);
        var info = registry.GetConnectedDaemons("u1").Single();
        await Assert.That(info.VendorModels!["pi"].Single().Value).IsEqualTo("anthropic/claude-opus-5");

        await registry.RegisterAsync("c1", "u1", "box", "mac", [], 1, vendorModels: null);
        await Assert.That(registry.GetConnectedDaemons("u1").Single().VendorModels).IsNull();
    }
```

Adjust to the real `RegisterAsync` signature and the real read API (`GetConnectedDaemons` or whatever the tests already use).

- [ ] **Step 2: Run, expect compile failure.** (`dotnet test` for `test/Capacitor.Server.Tests.Agents`)

- [ ] **Step 3: Implement** each hop as listed under Files, null-stays-null, no defaulting to empty anywhere.

- [ ] **Step 4: Run, expect PASS.**

- [ ] **Step 5: Commit** `Carry daemon vendor model catalogs through the registry`

---

## Task 11 (kcap-server): web view and launch dialog

**Files (kcap-server):**
- Modify: `src/Capacitor.Api.Web.Abstractions/Agents/DaemonView.cs` (trailing `IReadOnlyDictionary<string, IReadOnlyList<VendorModelOptionView>>? VendorModels = null`)
- Modify: `src/Capacitor.Server/Sessions/DaemonWebDataService.cs` (`ToView(DaemonInfo)` maps it with the existing `ToView(VendorModelOption)`)
- Modify: `src/Capacitor.Ui/Components/Agents/LaunchAgentDialog.razor` (`CurrentVendorModels` ~line 621; `OnDaemonChanged` ~line 1057)
- Test: `test/Capacitor.Ui.Tests/LaunchAgentDialogModelPickerTests.cs`

- [ ] **Step 1: Write the failing tests** (use the file's `NewContext` and however it supplies `Daemons` to the dialog):

```csharp
    [Test]
    public async Task Selected_daemon_catalog_wins_over_the_server_catalog_and_an_absent_key_falls_back() {
        // daemons list: "box" with VendorModels { pi: [X · a] }; server options: pi: [server-pi], claude: [opus]
        // render, select daemon "box", vendor "pi" → dropdown rows contain "X · a" and not "server-pi"
        // select vendor "claude" → rows contain "opus" (no daemon key)
    }

    [Test]
    public async Task Switching_daemons_clears_a_model_the_new_daemon_does_not_offer() {
        // box1 pi:[a], box2 pi:[b]; select box1, pi, model a; select box2 → EffectiveModel is ""
    }
```

Read the first hundred lines of the test file for the render helper and how a daemon is chosen (`OnDaemonChanged` may be invoked through the component instance: `cut.Instance` with `InvokeAsync`).

- [ ] **Step 2: Run, expect failure.**

- [ ] **Step 3: Implement**

```csharp
    IReadOnlyList<VendorModelOptionView> CurrentVendorModels {
        get {
            var daemon = Daemons.FirstOrDefault(d => d.Name == _selectedDaemon);
            if (daemon?.VendorModels is { } mine && mine.TryGetValue(_selectedVendor, out var fromDaemon)) return fromDaemon;
            return _modelOptions.TryGetValue(_selectedVendor, out var options) ? options : [];
        }
    }
```

In `OnDaemonChanged`, inside `if (changed) { ... }`, add: if `_selectedModel` is non-empty, not the custom sentinel, and not in the new `CurrentVendorModels` (by `Value`) while the new daemon has a key for `_selectedVendor`, call `ResetModelSelection()`.

- [ ] **Step 4: Run, expect PASS.**

- [ ] **Step 5: Commit** `Prefer the target daemon's model catalog in the launch dialog`

---

## Task 12 (kcap-server): PR

- [ ] Run the server's affected test projects (`Capacitor.Server.Tests.Agents`, `Capacitor.Ui.Tests`, `Capacitor.Api.Web.Tests`), push, open the kcap-server PR per its own PR template, referencing the same Linear issue as the kcap-cli PR and noting that either PR merges first safely.

---

## Self-review notes

- Spec §3.1–3.4 → Tasks 2, 3, 4, 6, 7. §4 → Tasks 1, 5. §5 → Tasks 10, 11. §6 → Tasks 8, 9. §7 compatibility cases → Task 1 (null/absent), Task 8 (old daemon null), Task 6 (probe failure keeps previous). §8 tests → distributed as listed; the "status subscriber receives a second snapshot after a refresh" is covered by the `Pulse` assertion in Task 6 together with the existing `DaemonStatusIpc` push-on-pulse tests. §9 log line → Task 4.
- Type names used across tasks: `VendorModelOption` (Core), `VendorModelOptionDto` (Remote.Models), `CatalogPathStat`, `VendorModelCatalogs`, `PiModelCatalogProbe`, `PiModelCatalogParse`, `StubCatalogFactory` (test), `VendorModelMaps` (App), `EffectiveModelCatalog`, `MergeCatalogs`, `ShouldClearSelection`, `RowMatches`, `WatchSet`.
- Out of scope (loose ends for the tracker): Effort chip → Pi thinking level; mobile launcher adoption; ACP vendors' `session/new` model lists.
