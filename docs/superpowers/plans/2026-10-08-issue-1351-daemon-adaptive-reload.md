# Desktop reload of an Adaptive-loaded daemon — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The desktop app shows when its launchd daemon runs in the background band and offers a verified, consented reload that brings it to Standard priority.

**Architecture:** Part A (CLI) teaches the launchd service manager to report the loaded spawn type, upgrade any plist layout, and run a forced single-daemon refresh with a machine-readable outcome. Part B (app) adds a `Reload` verb to the mutation lane with a pre-dispatch capability gate and positive priority evidence, and renders the outcome as controller state in the rail block beside a Reload button. Part A is shippable alone; Part B depends on it.

**Tech Stack:** .NET 10, NativeAOT CLI, Avalonia + ReactiveUI desktop app, TUnit tests, `launchctl`.

**Spec:** `docs/superpowers/specs/2026-10-07-issue-1351-daemon-adaptive-reload-design.md` (branch `daemon-adaptive-reload`, GitHub issue #1351).

## Global Constraints

- Spawn-type vocabulary: positive = `daemon`, `interactive`; background band = `adaptive`, `background`; anything else (or no line) = unknown. Only positive is ever success.
- Forced refresh exit codes: 0 for `reloaded` and `current`; 1 for every other token. Exactly one `refresh_outcome=<token>` line on stderr per forced run.
- The unforced `kcap daemon service refresh` keeps today's output and exit codes.
- A Reload outcome is never posted to the shared lifecycle attention lane; the controller is its only presenter.
- One type per file, named after the type. Comments scarce: no change narration, no spec coordinates, no review artifacts.
- `Capacitor.App` must not reference `Capacitor.Cli`; shared code goes in `Capacitor.Cli.Core` as `public`.
- Commit subjects: imperative, one clause, `(#1351)` suffix, ≤ 80 chars. Every commit ends with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- Tests: TUnit (`await Assert.That(x).IsEqualTo(y)`; no `HasCount`); `[TempHome]`/`[TempDaemonPaths]`/`[TempConfigRoot]` injection; console capture via `ConsoleOutput.StartCapture()` under bare `[NotInParallel]`; `Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only")` on launchd manager tests.
- Run a single test class with `--treenode-filter "/*/*/ClassName/*"`. App tests run with `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8`.
- After Part A: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` must print nothing.
- README `## CLI commands` → daemon service block and `src/Capacitor.Cli.Core/Resources/help-daemon.txt` change in the same PR as the CLI flag.

## Review Focus

1. A `spawn type` line with a trailing `\r` or mixed case (`Adaptive (6)`) must still classify as background band. Pinned in Task 1.
2. A forced refresh whose daemon answers the restart request with `queued` instead of `restarting` must return `Deferred` and must not boot the job out. Pinned in Task 5.
3. `--force` on a named daemon with no plist, while another daemon's plist exists, must report `unit_missing` and leave the other daemon untouched. Pinned in Task 6.
4. `ReloadServiceAsync` when no canonical server is configured must record the refusal as state, release the claim and leave `IsReloading` false. Pinned in Task 11.
5. A status JSON with `"loaded_spawn_type": ""` must read as unknown: never positive, never background band, indicator unchanged. Pinned in Tasks 1 and 11.

---

# Part A — CLI

### Task 1: Spawn-type vocabulary

**Files:**
- Create: `src/Capacitor.Cli.Core/SpawnTypes.cs`
- Create: `src/Capacitor.Cli.Core/SpawnTypeReading.cs`
- Modify: `src/Capacitor.Cli/Services/LaunchdUnit.cs` (replace `LoadedAsAdaptive`, lines 36–41)
- Modify: `src/Capacitor.Cli/Services/LaunchdServiceManager.cs` (two call sites of `LoadedAsAdaptive`, in `RefreshUnit` and `Bootstrap`)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/SpawnTypesTests.cs` (create)
- Test: `test/Capacitor.Cli.Tests.Unit/Services/LaunchdUnitTests.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Services/LaunchdUnitRefreshTests.cs` (rename the `LoadedAsAdaptive_reads_the_spawn_type_line` case)

**Interfaces:**
- Produces: `public enum SpawnTypeReading { Positive, BackgroundBand, Unknown }`; `public static class SpawnTypes { static SpawnTypeReading Classify(string? word); static bool IsPositive(string? word); static bool IsBackgroundBand(string? word); }` in namespace `Capacitor.Cli.Core`; `static string? LaunchdUnit.LoadedSpawnType(string printStdout)`.

- [ ] **Step 1: Write the failing Core tests**

```csharp
// test/Capacitor.Cli.Core.Tests.Unit/SpawnTypesTests.cs
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Core.Tests.Unit;

public class SpawnTypesTests {
    [Test]
    [Arguments("daemon", SpawnTypeReading.Positive)]
    [Arguments("interactive", SpawnTypeReading.Positive)]
    [Arguments("adaptive", SpawnTypeReading.BackgroundBand)]
    [Arguments("background", SpawnTypeReading.BackgroundBand)]
    [Arguments("app", SpawnTypeReading.Unknown)]
    [Arguments("", SpawnTypeReading.Unknown)]
    [Arguments(null, SpawnTypeReading.Unknown)]
    public async Task Classify_maps_each_word(string? word, SpawnTypeReading expected) {
        await Assert.That(SpawnTypes.Classify(word)).IsEqualTo(expected);
    }

    [Test]
    public async Task Only_positive_words_are_positive() {
        await Assert.That(SpawnTypes.IsPositive("daemon")).IsTrue();
        await Assert.That(SpawnTypes.IsPositive("adaptive")).IsFalse();
        await Assert.That(SpawnTypes.IsPositive(null)).IsFalse();
        await Assert.That(SpawnTypes.IsBackgroundBand("background")).IsTrue();
        await Assert.That(SpawnTypes.IsBackgroundBand("daemon")).IsFalse();
    }
}
```

- [ ] **Step 2: Write the failing LaunchdUnit tests**

Add to `test/Capacitor.Cli.Tests.Unit/Services/LaunchdUnitTests.cs`:

```csharp
    [Test]
    [Arguments("\tspawn type = daemon (3)\n", "daemon")]
    [Arguments("\tspawn type = interactive (4)\n", "interactive")]
    [Arguments("\tspawn type = background (5)\n", "background")]
    [Arguments("\tspawn type = adaptive (6)\n", "adaptive")]
    [Arguments("\tspawn type = app (1)\n", "app")]
    [Arguments("\tspawn type = Adaptive (6)\r\n", "adaptive")]
    public async Task LoadedSpawnType_reads_the_word(string print, string expected) {
        await Assert.That(LaunchdUnit.LoadedSpawnType($"gui/501/x = {{\n{print}}}\n")).IsEqualTo(expected);
    }

    [Test]
    public async Task LoadedSpawnType_is_null_without_the_line() {
        await Assert.That(LaunchdUnit.LoadedSpawnType("gui/501/x = {\n\tstate = running\n}\n")).IsNull();
        await Assert.That(LaunchdUnit.LoadedSpawnType("gui/501/x = {\n\tspawn type = \n}\n")).IsNull();
    }
```

In `LaunchdUnitRefreshTests.cs`, replace the body of `LoadedAsAdaptive_reads_the_spawn_type_line` with:

```csharp
    [Test]
    public async Task LoadedSpawnType_reads_the_spawn_type_line() {
        await Assert.That(LaunchdUnit.LoadedSpawnType(Print("adaptive (6)"))).IsEqualTo("adaptive");
        await Assert.That(LaunchdUnit.LoadedSpawnType(Print("daemon (3)"))).IsEqualTo("daemon");
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/SpawnTypesTests/*"`
Expected: build error, `SpawnTypes` does not exist.

- [ ] **Step 4: Create the classifier in Core**

```csharp
// src/Capacitor.Cli.Core/SpawnTypeReading.cs
namespace Capacitor.Cli.Core;

/// What a launchd spawn-type word proves about a job's scheduling priority.
public enum SpawnTypeReading { Positive, BackgroundBand, Unknown }
```

```csharp
// src/Capacitor.Cli.Core/SpawnTypes.cs
namespace Capacitor.Cli.Core;

/// The words `launchctl print` shows on its `spawn type = <word> (<n>)` line. A Standard job
/// and a plist with no ProcessType both print `daemon`; `adaptive` and `background` are the
/// throttled band. Any other word proves nothing, so it is never read as either.
public static class SpawnTypes {
    public const string Daemon      = "daemon";
    public const string Interactive = "interactive";
    public const string Adaptive    = "adaptive";
    public const string Background  = "background";

    public static SpawnTypeReading Classify(string? word) => word switch {
        Daemon or Interactive  => SpawnTypeReading.Positive,
        Adaptive or Background => SpawnTypeReading.BackgroundBand,
        _                      => SpawnTypeReading.Unknown,
    };

    public static bool IsPositive(string? word)       => Classify(word) == SpawnTypeReading.Positive;
    public static bool IsBackgroundBand(string? word) => Classify(word) == SpawnTypeReading.BackgroundBand;
}
```

- [ ] **Step 5: Replace `LoadedAsAdaptive` in `LaunchdUnit`**

Delete the `LoadedAsAdaptive` method and its doc comment (lines 36–41) and add:

```csharp
    /// <summary>The word on <c>launchctl print</c>'s <c>spawn type = &lt;word&gt; (&lt;n&gt;)</c> line, lower-cased,
    /// or null when the print has no such line. launchd reads the plist only when the job loads, so this
    /// lags a rewritten plist until a reload.</summary>
    public static string? LoadedSpawnType(string printStdout) {
        foreach (var line in printStdout.Split('\n')) {
            var t = line.Trim();
            if (!t.StartsWith("spawn type = ", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = t["spawn type = ".Length..].Trim();
            var end  = rest.IndexOf(' ');
            var word = (end < 0 ? rest : rest[..end]).Trim();
            return word.Length == 0 ? null : word.ToLowerInvariant();
        }
        return null;
    }
```

Add `using Capacitor.Cli.Core;` is already present in the file.

- [ ] **Step 6: Replace the two call sites in `LaunchdServiceManager`**

In `RefreshUnit`, change

```csharp
        var stale  = probe == LabelProbe.Loaded && (LaunchdUnit.LoadedAsAdaptive(printOut) || pinned);
```

to

```csharp
        var stale  = probe == LabelProbe.Loaded && (SpawnTypes.IsBackgroundBand(LaunchdUnit.LoadedSpawnType(printOut)) || pinned);
```

In `Bootstrap`, change

```csharp
        var loaded = label == LabelProbe.Loaded && (acceptAdaptive || !LaunchdUnit.LoadedAsAdaptive(stdout));
```

to

```csharp
        var loaded = label == LabelProbe.Loaded && (acceptAdaptive || SpawnTypes.IsPositive(LaunchdUnit.LoadedSpawnType(stdout)));
```

Add `using Capacitor.Cli.Core;` at the top of `LaunchdServiceManager.cs` if it is not already there.

- [ ] **Step 7: Run both suites**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/SpawnTypesTests/*"`
Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/Launchd*/*"`
Expected: all pass, including every pre-existing `LaunchdUnitRefreshTests` case.

- [ ] **Step 8: Commit**

```bash
git add src/Capacitor.Cli.Core/SpawnTypes.cs src/Capacitor.Cli.Core/SpawnTypeReading.cs src/Capacitor.Cli/Services/LaunchdUnit.cs src/Capacitor.Cli/Services/LaunchdServiceManager.cs test/Capacitor.Cli.Core.Tests.Unit/SpawnTypesTests.cs test/Capacitor.Cli.Tests.Unit/Services/LaunchdUnitTests.cs test/Capacitor.Cli.Tests.Unit/Services/LaunchdUnitRefreshTests.cs
git commit -m "Read launchd's spawn type as a word and classify it (#1351)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 2: Loaded spawn type in the query and the status JSON

**Files:**
- Modify: `src/Capacitor.Cli/Services/IServiceManager.cs` (`ServiceQuery` record, line 21)
- Modify: `src/Capacitor.Cli/Services/LaunchdServiceManager.cs` (`QueryCore`, lines 64–75)
- Modify: `src/Capacitor.Cli/Services/SystemdServiceManager.cs` and `src/Capacitor.Cli/Services/WindowsScheduledTaskServiceManager.cs` (every `new ServiceQuery(...)` — add nothing, the new member defaults to null)
- Modify: `src/Capacitor.Cli/Commands/ServiceStatusJson.cs` (record + `ServiceStatusRender.Render`)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/ServiceStatusJsonTests.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Services/LaunchdQueryTests.cs` (create)

**Interfaces:**
- Consumes: `LaunchdUnit.LoadedSpawnType` (Task 1).
- Produces: `record ServiceQuery(LabelProbe Probe, bool UnitPresent, ServiceState State, string? BinaryPath, int? JobPid, string? LoadedSpawnType = null)`; `ServiceStatusJson.LoadedSpawnType` (`string?`, wire name `loaded_spawn_type`).

- [ ] **Step 1: Write the failing JSON tests**

Add to `ServiceStatusJsonTests.cs`:

```csharp
    [Test]
    public async Task Render_carries_the_loaded_spawn_type() {
        var q = new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, "/u/kcap-daemon", 42, LoadedSpawnType: "adaptive");
        var (json, _) = ServiceStatusRender.Render(q, "default", "/i/kcap-daemon", 42, false, false);
        using var doc = JsonDocument.Parse(json!);
        await Assert.That(doc.RootElement.GetProperty("loaded_spawn_type").GetString()).IsEqualTo("adaptive");
    }

    [Test]
    public async Task Render_emits_null_spawn_type_for_an_unloaded_label() {
        var q = new ServiceQuery(LabelProbe.Absent, true, ServiceState.NotInstalled, "/u/kcap-daemon", null);
        var (json, _) = ServiceStatusRender.Render(q, "default", null, null, false, false);
        using var doc = JsonDocument.Parse(json!);
        await Assert.That(doc.RootElement.GetProperty("loaded_spawn_type").ValueKind).IsEqualTo(JsonValueKind.Null);
    }
```

- [ ] **Step 2: Write the failing query test**

```csharp
// test/Capacitor.Cli.Tests.Unit/Services/LaunchdQueryTests.cs
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>The query reports the loaded job's spawn type word, and nothing when the label is not loaded
/// or the print has no such line.</summary>
public class LaunchdQueryTests {
    [TempHome] public required TempHome Home { get; init; }

    LaunchdServiceManager Manager(int exit, string stdout, string stderr = "") =>
        new(Home, TimeProvider.System, runProcess: (_, _) => (exit, stdout, stderr));

    [Test]
    public async Task Loaded_job_reports_its_spawn_type() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        var q = Manager(0, "gui/501/io.kurrent.kcap.daemon.test = {\n\tstate = running\n\tpid = 7\n\tspawn type = adaptive (6)\n}\n").Query("test");
        await Assert.That(q.Probe).IsEqualTo(LabelProbe.Loaded);
        await Assert.That(q.LoadedSpawnType).IsEqualTo("adaptive");
    }

    [Test]
    public async Task Loaded_job_without_the_line_reports_null() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        var q = Manager(0, "gui/501/io.kurrent.kcap.daemon.test = {\n\tstate = running\n}\n").Query("test");
        await Assert.That(q.LoadedSpawnType).IsNull();
    }

    [Test]
    public async Task Absent_label_reports_null() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        var q = Manager(113, "", "Could not find service").Query("test");
        await Assert.That(q.Probe).IsEqualTo(LabelProbe.Absent);
        await Assert.That(q.LoadedSpawnType).IsNull();
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ServiceStatusJsonTests/*"`
Expected: build error, `ServiceQuery` has no `LoadedSpawnType`.

- [ ] **Step 4: Extend the record, the query and the renderer**

`IServiceManager.cs`:

```csharp
/// <summary>Rich query result: tri-state probe, plist presence, current state, binary path, running job pid,
/// and the loaded job's spawn-type word (launchd only; null when the label is not loaded or the print
/// has no such line).</summary>
record ServiceQuery(LabelProbe Probe, bool UnitPresent, ServiceState State, string? BinaryPath, int? JobPid, string? LoadedSpawnType = null);
```

`LaunchdServiceManager.QueryCore`, replace the final `return`:

```csharp
        var loaded = probe == LabelProbe.Loaded;
        return new ServiceQuery(probe, unitPresent, state, bin,
            loaded ? LaunchdUnit.PidFromPrint(stdout) : null,
            loaded ? LaunchdUnit.LoadedSpawnType(stdout) : null);
```

`ServiceStatusJson.cs`: add a trailing member and pass it through:

```csharp
public sealed record ServiceStatusJson(
    string ServiceId, bool UnitPresent, string State, string? BinaryPath,
    string? InstallBinaryPath, int? JobPid, int? DaemonPid, bool TxnMarker, bool TxnActive,
    string? UnitProfile = null, string? UnitServerUrl = null,
    string? UnitExpectedServer = null, string? UnitConsentSeed = null,
    string? LoadedSpawnType = null);
```

and in `Render`:

```csharp
        var dto = new ServiceStatusJson(
            serviceId, q.UnitPresent, state, q.BinaryPath,
            installBinaryPath, q.JobPid, daemonPid, txnMarker, txnActive,
            unitProfile, unitServerUrl, unitExpectedServer, unitConsentSeed,
            q.LoadedSpawnType);
```

- [ ] **Step 5: Run the tests**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ServiceStatusJsonTests/*"`
Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/LaunchdQueryTests/*"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli/Services/IServiceManager.cs src/Capacitor.Cli/Services/LaunchdServiceManager.cs src/Capacitor.Cli/Commands/ServiceStatusJson.cs test/Capacitor.Cli.Tests.Unit/Commands/ServiceStatusJsonTests.cs test/Capacitor.Cli.Tests.Unit/Services/LaunchdQueryTests.cs
git commit -m "Report the loaded spawn type in the service query and status JSON (#1351)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 3: Priority line in `kcap daemon status`

**Files:**
- Modify: `src/Capacitor.Cli/Commands/DaemonCommands.cs` (`Status`, the `if (manager is not null)` block near line 718; add `DescribePriority`)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/DaemonStatusServingTests.cs`

**Interfaces:**
- Consumes: `ServiceQuery.LoadedSpawnType` (Task 2), `SpawnTypes` (Task 1).
- Produces: `internal static string? DaemonCommands.DescribePriority(string daemonName, string? loadedSpawnType)`.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Test]
    public async Task DescribePriority_names_the_word_and_the_daemon_for_the_background_band() {
        await Assert.That(DaemonCommands.DescribePriority("alexey", "adaptive"))
            .IsEqualTo("  priority: loaded as adaptive — background priority; run `kcap daemon service refresh --name alexey --force` to reload (ends this daemon's hosted agents)");
        await Assert.That(DaemonCommands.DescribePriority("alexey", "background")).IsNotNull();
    }

    [Test]
    public async Task DescribePriority_is_silent_for_positive_and_unknown_words() {
        await Assert.That(DaemonCommands.DescribePriority("alexey", "daemon")).IsNull();
        await Assert.That(DaemonCommands.DescribePriority("alexey", "interactive")).IsNull();
        await Assert.That(DaemonCommands.DescribePriority("alexey", "app")).IsNull();
        await Assert.That(DaemonCommands.DescribePriority("alexey", null)).IsNull();
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonStatusServingTests/*"`
Expected: build error, `DescribePriority` missing.

- [ ] **Step 3: Implement**

Beside `DescribeRunningDaemon` in `DaemonCommands.cs`:

```csharp
    /// <summary>The status line for a service job launchd holds in the background band; null for any
    /// other spawn type, including none.</summary>
    internal static string? DescribePriority(string daemonName, string? loadedSpawnType) =>
        SpawnTypes.IsBackgroundBand(loadedSpawnType)
            ? $"  priority: loaded as {loadedSpawnType} — background priority; run `kcap daemon service refresh --name {daemonName} --force` to reload (ends this daemon's hosted agents)"
            : null;
```

Replace the service block in `Status`:

```csharp
            if (manager is not null) {
                var query = manager.Query(DaemonStore.Sanitize(name));
                if (query.Probe == LabelProbe.Loaded && query.State != ServiceState.NotInstalled)
                    await Console.Out.WriteLineAsync($"  service: {query.State} ({manager.Describe()})");
                if (DescribePriority(name, query.LoadedSpawnType) is { } priority)
                    await Console.Out.WriteLineAsync(priority);
            }
```

`Query` runs the same `launchctl print` the old `Status` call did, so this is one probe, not two. Add `using Capacitor.Cli.Core;` and `using Capacitor.Cli.Services;` if missing.

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonStatusServingTests/*"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/DaemonCommands.cs test/Capacitor.Cli.Tests.Unit/Commands/DaemonStatusServingTests.cs
git commit -m "Say when kcap daemon status finds the service loaded in the background band (#1351)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 4: Layout-independent plist upgrade

**Files:**
- Modify: `src/Capacitor.Cli/Services/LaunchdUnit.cs` (`UpgradeProcessType`, lines 13–24; delete `AdaptiveProcessTypeLine`; add `OffsetOf`)
- Test: `test/Capacitor.Cli.Tests.Unit/Services/LaunchdUnitTests.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Services/LaunchdUnitRefreshTests.cs` (fixture of `Adaptive_plist_this_writer_cannot_upgrade_is_left_alone` and `UpgradeProcessType_switches_the_adaptive_line_and_leaves_the_rest`)

**Interfaces:**
- Produces: `static string? LaunchdUnit.UpgradeProcessType(string plistXml)` — same signature, parser-located. Null means nothing to splice (already Standard, no key, unsupported value, unparseable); callers keep treating null as "use the original".

- [ ] **Step 1: Write the failing tests**

Add to `LaunchdUnitTests.cs`:

```csharp
    const string CanonicalAdaptive =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
        "<plist version=\"1.0\">\n<dict>\n" +
        "\t<key>Label</key>\n\t<string>io.kurrent.kcap.daemon.laptop</string>\n" +
        "\t<key>ProcessType</key>\n\t<string>Adaptive</string>\n" +
        "\t<key>ProgramArguments</key>\n\t<array>\n\t\t<string>/opt/kcap/kcap-daemon</string>\n\t</array>\n" +
        "</dict>\n</plist>\n";

    [Test]
    public async Task UpgradeProcessType_rewrites_the_canonical_layout_and_keeps_every_other_byte() {
        var upgraded = LaunchdUnit.UpgradeProcessType(CanonicalAdaptive);
        await Assert.That(upgraded).IsEqualTo(CanonicalAdaptive.Replace("<string>Adaptive</string>", "<string>Standard</string>"));
    }

    [Test]
    public async Task UpgradeProcessType_keeps_crlf_and_a_missing_final_newline() {
        var crlf = CanonicalAdaptive.Replace("\n", "\r\n").TrimEnd('\r', '\n');
        var upgraded = LaunchdUnit.UpgradeProcessType(crlf);
        await Assert.That(upgraded).IsEqualTo(crlf.Replace("<string>Adaptive</string>", "<string>Standard</string>"));
    }

    [Test]
    public async Task UpgradeProcessType_rewrites_the_writer_layout_and_a_background_value() {
        var writer = LaunchdUnit.Plist(Spec()).Replace("<string>Standard</string>", "<string>Adaptive</string>");
        await Assert.That(LaunchdUnit.UpgradeProcessType(writer)).IsEqualTo(LaunchdUnit.Plist(Spec()));
        var background = CanonicalAdaptive.Replace("Adaptive", "Background");
        await Assert.That(LaunchdUnit.UpgradeProcessType(background)).IsEqualTo(background.Replace("<string>Background</string>", "<string>Standard</string>"));
    }

    [Test]
    public async Task UpgradeProcessType_skips_a_comment_between_key_and_value_and_a_decoy_comment() {
        var decoy = CanonicalAdaptive
            .Replace("\t<key>ProcessType</key>\n\t<string>Adaptive</string>\n",
                "\t<!-- <key>ProcessType</key><string>Adaptive</string> -->\n\t<key>ProcessType</key>\n\t<!-- real value below -->\n\t<string>Adaptive</string>\n");
        var upgraded = LaunchdUnit.UpgradeProcessType(decoy)!;
        await Assert.That(upgraded).Contains("<!-- <key>ProcessType</key><string>Adaptive</string> -->");
        await Assert.That(upgraded).Contains("<!-- real value below -->\n\t<string>Standard</string>");
    }

    [Test]
    [Arguments("<string>Standard</string>")]
    [Arguments("<string>Interactive</string>")]
    [Arguments("<string><![CDATA[Adaptive]]></string>")]
    [Arguments("<string>Adap<!-- x -->tive</string>")]
    public async Task UpgradeProcessType_returns_null_for_values_it_must_not_touch(string value) {
        await Assert.That(LaunchdUnit.UpgradeProcessType(CanonicalAdaptive.Replace("<string>Adaptive</string>", value))).IsNull();
    }

    [Test]
    public async Task UpgradeProcessType_returns_null_for_missing_duplicate_or_nested_keys_and_malformed_xml() {
        await Assert.That(LaunchdUnit.UpgradeProcessType(CanonicalAdaptive.Replace("\t<key>ProcessType</key>\n\t<string>Adaptive</string>\n", ""))).IsNull();
        await Assert.That(LaunchdUnit.UpgradeProcessType(CanonicalAdaptive.Replace("</dict>", "\t<key>ProcessType</key>\n\t<string>Adaptive</string>\n</dict>"))).IsNull();
        var nestedOnly = CanonicalAdaptive.Replace("\t<key>ProcessType</key>\n\t<string>Adaptive</string>\n",
            "\t<key>Inner</key>\n\t<dict>\n\t\t<key>ProcessType</key>\n\t\t<string>Adaptive</string>\n\t</dict>\n");
        await Assert.That(LaunchdUnit.UpgradeProcessType(nestedOnly)).IsNull();
        await Assert.That(LaunchdUnit.UpgradeProcessType("<plist><dict><key>ProcessType</key>")).IsNull();
    }
```

In `LaunchdUnitRefreshTests.cs`, change the fixture of `Adaptive_plist_this_writer_cannot_upgrade_is_left_alone` so it is genuinely unsupported: replace its `Adaptive` value with `Interactive` (keep its current outcome assertion in this task; Task 5 changes it to `UnitUnsupported`). The existing `UpgradeProcessType_switches_the_adaptive_line_and_leaves_the_rest` keeps passing unchanged.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/LaunchdUnitTests/*"`
Expected: the canonical, CRLF, Background, comment and nested cases fail (byte-exact match misses them).

- [ ] **Step 3: Implement**

Replace lines 13–24 of `LaunchdUnit.cs` (the two `*ProcessTypeLine` constants and `UpgradeProcessType`) with:

```csharp
    const string StandardProcessTypeLine = "  <key>ProcessType</key><string>Standard</string>\n";
    const string AdaptiveValue   = "Adaptive";
    const string BackgroundValue = "Background";

    /// <summary>The plist with its top-level <c>ProcessType</c> value switched from Adaptive or Background to
    /// Standard, every other byte intact; null when there is nothing to splice — no such key, a value that
    /// is not one of those two, a value with markup inside it, or a document that does not parse. Null is
    /// not a verdict on the plist: the caller validates <c>upgraded ?? original</c> itself.</summary>
    public static string? UpgradeProcessType(string plistXml) {
        XDocument doc;
        try { doc = XDocument.Parse(plistXml, LoadOptions.SetLineInfo); }
        catch (System.Xml.XmlException) { return null; }

        var topDict = doc.Root?.Element("dict");
        if (topDict is null) return null;

        XElement? value;
        try { value = TopLevelValue(topDict, "ProcessType"); }
        catch (InvalidDataException) { return null; }
        if (value is null || value.Name != "string") return null;

        // Exactly one plain text node: a comment or CDATA inside the element is not a value this
        // writer produced, and the byte check below would not describe it.
        if (value.FirstNode is not XText text || text is XCData || text.NextNode is not null) return null;
        if (text.Value is not (AdaptiveValue or BackgroundValue)) return null;

        // LinePosition is the column of the element name's first character; the '<' is one before.
        var info = (System.Xml.IXmlLineInfo)value;
        if (!info.HasLineInfo()) return null;
        var start = OffsetOf(plistXml, info.LineNumber, info.LinePosition - 1);
        if (start < 0) return null;

        var element = $"<string>{text.Value}</string>";
        if (start + element.Length > plistXml.Length || string.CompareOrdinal(plistXml, start, element, 0, element.Length) != 0) return null;

        var valueStart = start + "<string>".Length;
        var rewritten  = string.Concat(plistXml.AsSpan(0, valueStart), "Standard", plistXml.AsSpan(valueStart + text.Value.Length));
        return DeclaresStandardProcessType(rewritten) ? rewritten : null;
    }

    /// <summary>Character offset of a 1-based line and column, walking the source's own line endings so
    /// LF and CRLF documents both map correctly; -1 when the position is outside the text.</summary>
    static int OffsetOf(string source, int line, int column) {
        var offset = 0;
        for (var current = 1; current < line; current++) {
            var newline = source.IndexOf('\n', offset);
            if (newline < 0) return -1;
            offset = newline + 1;
        }
        var position = offset + column - 1;
        return position >= 0 && position <= source.Length ? position : -1;
    }
```

Update the file's header comment above the constants to the two lines that still hold: "Adaptive lets macOS hold the job at background priority 4, and under load that starves the hub heartbeat into a reconnect storm. Background does the same." Delete every mention of a byte-for-byte match.

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/Launchd*/*"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Services/LaunchdUnit.cs test/Capacitor.Cli.Tests.Unit/Services/LaunchdUnitTests.cs test/Capacitor.Cli.Tests.Unit/Services/LaunchdUnitRefreshTests.cs
git commit -m "Locate the plist ProcessType value through the parser before splicing it (#1351)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 5: Distinct refresh outcomes and positive reload evidence

**Files:**
- Modify: `src/Capacitor.Cli/Services/UnitRefresh.cs`
- Modify: `src/Capacitor.Cli/Services/LaunchdServiceManager.cs` (`RefreshUnit`, `Bootstrap`)
- Modify: `src/Capacitor.Cli/Commands/DaemonServiceCommands.cs` (`Refresh` switch: map the new members so unforced output is unchanged)
- Test: `test/Capacitor.Cli.Tests.Unit/Services/LaunchdUnitRefreshTests.cs`

**Interfaces:**
- Produces: `enum UnitRefresh { Current, NotLoaded, Deferred, Contended, Unverified, UnitMissing, UnitUnreadable, UnitUnsupported, Reloaded, Failed }`. `RefreshUnit(string serviceId, Func<bool> requestRestart, Func<TimeSpan> timeLeft, out string? error, Func<string, string>? stabilize = null)` — unchanged signature; the callback is now named for any restart mode.

- [ ] **Step 1: Update the existing tests and add the new cases**

In `LaunchdUnitRefreshTests.cs`:

- `Current_job_is_left_alone_and_the_daemon_is_not_asked`: expect `UnitRefresh.Current`.
- `Adaptive_plist_this_writer_cannot_upgrade_is_left_alone`: expect `UnitRefresh.UnitUnsupported`, and assert `calls` is empty (no print, no write).
- `Idle_adaptive_job_is_rewritten_then_booted_out_and_bootstrapped`: the call list gains a trailing `print`, the probe that proves the new spawn type: `["print", "bootout", "bootstrap", "print"]`.
- `Unreadable_launchd_state_rewrites_the_plist_but_reloads_nothing`: keep `Unverified`.
- `Timed_out_bootstrap_over_the_old_adaptive_job_is_not_a_reload`: keep its outcome (`Failed` with the restore message), it already exercises the non-positive timeout path.

Add:

```csharp
    [Test]
    public async Task Standard_plist_under_an_adaptive_loaded_job_is_reloaded_without_a_write() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        var path  = Seed(LaunchdUnit.Plist(Spec()));
        var before = File.GetLastWriteTimeUtc(path);
        var calls = new List<string[]>();

        var outcome = Manager(calls, "adaptive (6)").RefreshUnit("test", () => true, Plenty, out _);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Reloaded);
        await Assert.That(File.GetLastWriteTimeUtc(path)).IsEqualTo(before);
        await Assert.That(calls.Select(c => c[0]).ToArray()).IsEquivalentTo(["print", "bootout", "bootstrap", "print"]);
    }

    [Test]
    public async Task Background_plist_under_a_background_job_is_rewritten_and_reloaded() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        var path = Seed(LaunchdUnit.Plist(Spec()).Replace("<string>Standard</string>", "<string>Background</string>"));
        var outcome = Manager([], "background (5)").RefreshUnit("test", () => true, Plenty, out _);
        await Assert.That(outcome).IsEqualTo(UnitRefresh.Reloaded);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(LaunchdUnit.Plist(Spec()));
    }

    [Test]
    public async Task Queued_restart_answer_defers_without_a_bootout() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed(AdaptivePlist());
        var calls = new List<string[]>();
        var outcome = Manager(calls, "adaptive (6)").RefreshUnit("test", () => false, Plenty, out _);
        await Assert.That(outcome).IsEqualTo(UnitRefresh.Deferred);
        await Assert.That(calls.Select(c => c[0]).ToArray()).IsEquivalentTo(["print"]);
    }

    [Test]
    public async Task Missing_and_unreadable_units_are_told_apart() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        var calls = new List<string[]>();
        await Assert.That(Manager(calls, "adaptive (6)").RefreshUnit("test", () => true, Plenty, out _)).IsEqualTo(UnitRefresh.UnitMissing);
        Directory.CreateDirectory(LaunchdUnit.PlistPath(Home, "test")); // a directory where the file should be
        await Assert.That(Manager(calls, "adaptive (6)").RefreshUnit("test", () => true, Plenty, out _)).IsEqualTo(UnitRefresh.UnitUnreadable);
        await Assert.That(calls).IsEmpty();
    }

    [Test]
    public async Task Malformed_plist_is_unsupported_and_untouched() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        var path = Seed("<plist><dict><key>ProcessType</key>");
        var calls = new List<string[]>();
        await Assert.That(Manager(calls, "adaptive (6)").RefreshUnit("test", () => true, Plenty, out _)).IsEqualTo(UnitRefresh.UnitUnsupported);
        await Assert.That(File.ReadAllText(path)).IsEqualTo("<plist><dict><key>ProcessType</key>");
        await Assert.That(calls).IsEmpty();
    }

    [Test]
    public async Task Unloaded_label_with_a_current_plist_is_not_loaded() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed(LaunchdUnit.Plist(Spec()));
        var manager = new LaunchdServiceManager(Home, TimeProvider.System,
            writeUnit: (p, c, _) => File.WriteAllText(p, c),
            runBounded: (_, args, _) => args[0] == "print" ? (113, "", "Could not find service", false) : (0, "", "", false));
        await Assert.That(manager.RefreshUnit("test", () => true, Plenty, out _)).IsEqualTo(UnitRefresh.NotLoaded);
    }

    [Test]
    public async Task Failed_print_and_unknown_spawn_type_are_unverified() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed(LaunchdUnit.Plist(Spec()));
        await Assert.That(Manager([], "daemon (3)", printFails: true).RefreshUnit("test", () => true, Plenty, out _)).IsEqualTo(UnitRefresh.Unverified);
        await Assert.That(Manager([], "app (1)").RefreshUnit("test", () => true, Plenty, out _)).IsEqualTo(UnitRefresh.Unverified);
    }

    [Test]
    public async Task Timed_out_bootstrap_whose_print_has_no_spawn_type_is_not_a_reload() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed(AdaptivePlist());
        var loaded = true;
        var manager = new LaunchdServiceManager(Home, TimeProvider.System,
            writeUnit: (p, c, _) => File.WriteAllText(p, c),
            runBounded: (_, args, _) => args[0] switch {
                "print"     => loaded ? (0, $"gui/501/{Label} = {{\n\tstate = running\n}}\n", "", false) : (113, "", "Could not find service", false),
                "bootout"   => ((Func<(int, string, string, bool)>)(() => { loaded = false; return (0, "", "", false); }))(),
                "bootstrap" => ((Func<(int, string, string, bool)>)(() => { loaded = true; return (137, "", "", true); }))(),
                _           => (0, "", "", false),
            });
        var outcome = manager.RefreshUnit("test", () => true, Plenty, out var error);
        await Assert.That(outcome).IsNotEqualTo(UnitRefresh.Reloaded);
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task Throwing_unit_writer_and_process_start_are_failed() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed(AdaptivePlist());
        var writerThrows = new LaunchdServiceManager(Home, TimeProvider.System,
            writeUnit: (_, _, _) => throw new IOException("disk full"),
            runBounded: (_, _, _) => (0, Print("adaptive (6)"), "", false));
        await Assert.That(writerThrows.RefreshUnit("test", () => true, Plenty, out var writeError)).IsEqualTo(UnitRefresh.Failed);
        await Assert.That(writeError).Contains("disk full");

        var startThrows = new LaunchdServiceManager(Home, TimeProvider.System,
            writeUnit: (p, c, _) => File.WriteAllText(p, c),
            runBounded: (_, _, _) => throw new System.ComponentModel.Win32Exception("launchctl missing"));
        await Assert.That(startThrows.RefreshUnit("test", () => true, Plenty, out var startError)).IsEqualTo(UnitRefresh.Failed);
        await Assert.That(startError).Contains("launchctl missing");
    }
```

Also extend the harness `Manager(...)` so a successful bootstrap's follow-up `print` reports the new spawn type: it already sets `spawnType = "daemon (3)"` on success, so the fourth call in the first new test is that print.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/LaunchdUnitRefreshTests/*"`
Expected: build errors for the new enum members.

- [ ] **Step 3: Replace the enum**

```csharp
// src/Capacitor.Cli/Services/UnitRefresh.cs
namespace Capacitor.Cli.Services;

/// <summary>What <see cref="LaunchdServiceManager.RefreshUnit"/> found or did for one installed job.</summary>
enum UnitRefresh {
    /// <summary>The plist declares Standard and the loaded job's spawn type reads positive.</summary>
    Current,

    /// <summary>The plist is current or was rewritten, but the label is not loaded.</summary>
    NotLoaded,

    /// <summary>The loaded job is still out of date: the daemon refused the restart, or there was not enough
    /// time left to finish a reload.</summary>
    Deferred,

    /// <summary>Another service operation holds the label's transaction lock.</summary>
    Contended,

    /// <summary>launchd's state could not be read, or the loaded spawn type is a word that proves nothing.</summary>
    Unverified,

    /// <summary>No plist at the path.</summary>
    UnitMissing,

    /// <summary>The plist exists but cannot be read.</summary>
    UnitUnreadable,

    /// <summary>The plist cannot be parsed, or does not declare Standard after the attempted upgrade.</summary>
    UnitUnsupported,

    /// <summary>The job was reloaded from the plist on disk and now reads a positive spawn type.</summary>
    Reloaded,

    /// <summary>The reload failed; the error says what launchd is left holding.</summary>
    Failed,
}
```

- [ ] **Step 4: Restructure `RefreshUnit` and `Bootstrap`**

Replace `RefreshUnit` with:

```csharp
    public UnitRefresh RefreshUnit(
            string serviceId, Func<bool> requestRestart, Func<TimeSpan> timeLeft, out string? error,
            Func<string, string>? stabilize = null) {
        error = null;
        var path = LaunchdUnit.PlistPath(home, serviceId);

        switch (LaunchdUnit.TryReadPlist(path, out var original)) {
            case LaunchdUnit.PlistRead.Absent:     return UnitRefresh.UnitMissing;
            case LaunchdUnit.PlistRead.Unreadable: return UnitRefresh.UnitUnreadable;
        }

        var upgraded = LaunchdUnit.UpgradeProcessType(original!);
        if (!LaunchdUnit.DeclaresStandardProcessType(upgraded ?? original!)) return UnitRefresh.UnitUnsupported;

        try {
            return RefreshValidatedUnit(serviceId, path, original!, upgraded, requestRestart, timeLeft, stabilize, out error);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception) {
            error = ex.Message;
            return UnitRefresh.Failed;
        }
    }

    UnitRefresh RefreshValidatedUnit(
            string serviceId, string path, string original, string? upgraded,
            Func<bool> requestRestart, Func<TimeSpan> timeLeft, Func<string, string>? stabilize, out string? error) {
        error = null;
        var (printExit, printOut, printErr, printTimedOut) = RunCtl(RefreshCtlTimeout, LaunchdUnit.PrintArgs(Uid(), serviceId));
        var probe = printTimedOut ? LabelProbe.Unknown : LaunchdUnit.ClassifyPrint(printExit, printOut, printErr);

        var binary = ReadBinaryPathSafe(path);
        var target = binary is not null && stabilize is not null ? stabilize(binary) : binary;
        if (target is not null && LaunchdUnit.WithBinary(upgraded ?? original, target) is { } repointed)
            upgraded = repointed;

        var loadedProgram = LaunchdUnit.LoadedProgram(printOut);
        var pinned = loadedProgram is not null && stabilize is not null && stabilize(loadedProgram) != loadedProgram;
        var spawn  = probe == LabelProbe.Loaded ? LaunchdUnit.LoadedSpawnType(printOut) : null;
        var stale  = probe == LabelProbe.Loaded && (SpawnTypes.IsBackgroundBand(spawn) || pinned);

        // Rewriting the file is safe whatever launchd holds; only the reload depends on what it does.
        if (upgraded is not null) _writeUnit(path, upgraded, null);
        if (probe == LabelProbe.Unknown) return UnitRefresh.Unverified;
        if (probe == LabelProbe.Absent) return UnitRefresh.NotLoaded;
        if (!stale) return SpawnTypes.IsPositive(spawn) ? UnitRefresh.Current : UnitRefresh.Unverified;
        if (timeLeft() < ReloadBudget) return UnitRefresh.Deferred;

        var running = LaunchdUnit.StatusFromPrint(printExit, printOut) == ServiceState.Running;
        if (running && !requestRestart()) return UnitRefresh.Deferred;

        // The accepted restart is already exiting the daemon. Booting out now unloads the job before
        // launchd can relaunch it from the definition it cached at load.
        var (bootoutExit, _, _, bootoutTimedOut) = RunCtl(RefreshCtlTimeout, LaunchdUnit.BootoutArgs(Uid(), serviceId));
        if ((bootoutTimedOut || bootoutExit != 0) && Probe(serviceId).Label == LabelProbe.Loaded) {
            error = "launchctl bootout did not unload the job, so it keeps running in the background band until the next update";
            return UnitRefresh.Failed;
        }

        var reload = Bootstrap(serviceId, path, acceptAnySpawnType: false);
        if (reload.Error is null) return UnitRefresh.Reloaded;

        if (upgraded is not null) _writeUnit(path, original, null);
        var rollback = Bootstrap(serviceId, path, acceptAnySpawnType: true);
        var (bootstrapError, rollbackError) = (reload.Error, rollback.Error);

        if (rollbackError is null) {
            error = $"{bootstrapError}; the previous unit was restored and loaded";
        } else {
            error = (rollback.After ?? Probe(serviceId).Label) == LabelProbe.Absent
                ? $"{bootstrapError}; restoring the previous unit also failed ({rollbackError}), so the daemon is not loaded — run `kcap daemon service start`"
                : $"{bootstrapError}; restoring the previous unit also failed ({rollbackError}), and launchd's state for the job is unclear — check `kcap daemon service status`";
        }

        return UnitRefresh.Failed;
    }
```

Replace `Bootstrap` so a successful bootstrap also proves the spawn type with one probe:

```csharp
    /// <summary>
    /// A bootstrap counts as a reload only when a follow-up probe finds the label loaded with a positive
    /// spawn type, unless <paramref name="acceptAnySpawnType"/> (the rollback of the previous unit, which
    /// only needs the label back). A bootstrap that timed out may still have loaded the job, so the probe
    /// decides for it too. <c>After</c> is that probe's label, so the caller need not probe again.
    /// </summary>
    (string? Error, LabelProbe? After) Bootstrap(string serviceId, string plistPath, bool acceptAnySpawnType) {
        var (exit, _, err, timedOut) = RunCtl(RefreshCtlTimeout, LaunchdUnit.BootstrapArgs(Uid(), plistPath));
        if (!timedOut && exit != 0) return ($"launchctl bootstrap failed (exit {exit}): {err.Trim()}", null);

        var (label, stdout) = Probe(serviceId);
        if (label != LabelProbe.Loaded)
            return (timedOut ? "launchctl bootstrap timed out and was terminated" : "launchctl bootstrap returned but the label is not loaded", label);

        var spawn = LaunchdUnit.LoadedSpawnType(stdout);
        if (acceptAnySpawnType || SpawnTypes.IsPositive(spawn)) return (null, label);

        return (spawn is null
            ? "the job loaded but launchd reports no spawn type for it"
            : $"the job loaded but launchd still reports spawn type {spawn}", label);
    }
```

`ReloadBudget` already reserves a probe after each bootstrap, so the budget constant is unchanged.

- [ ] **Step 5: Keep the unforced command output unchanged**

In `DaemonServiceCommands.Refresh`, the `switch (outcome)` currently names `Reloaded`, `Deferred`, `Unverified`, `Failed`. The new members `Current`, `NotLoaded`, `UnitMissing`, `UnitUnreadable`, `UnitUnsupported` print nothing, as `Unchanged` and `Rewritten` did. Add nothing for them; the `default` falls through silently. `Contended` is produced in Task 6.

- [ ] **Step 6: Run the tests**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/Launchd*/*"`
Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ServiceRepointTests/*"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli/Services/UnitRefresh.cs src/Capacitor.Cli/Services/LaunchdServiceManager.cs src/Capacitor.Cli/Commands/DaemonServiceCommands.cs test/Capacitor.Cli.Tests.Unit/Services/LaunchdUnitRefreshTests.cs
git commit -m "Tell refresh outcomes apart and prove a reload by its spawn type (#1351)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 6: `kcap daemon service refresh --force`

**Files:**
- Modify: `src/Capacitor.Cli/Commands/DaemonServiceCommands.cs` (`DispatchAsync` `"refresh"` arm, `Refresh`, `RequestIdleRestart` → `RequestRestart`, new `RefreshToken`, `RestartRequester` seam)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/DaemonCommandsServiceRefreshTests.cs` (create)

**Interfaces:**
- Consumes: `UnitRefresh` (Task 5), `ServiceTxnLock.TryAcquireAsync(DaemonStore, string, TimeSpan, TimeProvider)`.
- Produces: `internal Task<int> Refresh(bool force = false, Func<string, string>? stabilize = null)`; `internal static string RefreshToken(UnitRefresh outcome)`; `internal Func<string, string, bool>? RestartRequester { get; init; }` (serviceId, mode → accepted).

- [ ] **Step 1: Write the failing tests**

```csharp
// test/Capacitor.Cli.Tests.Unit/Commands/DaemonCommandsServiceRefreshTests.cs
using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>A forced refresh targets one daemon, asks it for a forced restart, and reports exactly one
/// machine-readable outcome line; the unforced run keeps iterating every installed unit.</summary>
[NotInParallel]
public class DaemonCommandsServiceRefreshTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static ServiceSpec Spec(string id) => new(id, "/opt/kcap/kcap-daemon", $"/home/u/.config/kcap/daemon-{id}.log", new Dictionary<string, string>(), []);

    string Seed(string id, string content) {
        Directory.CreateDirectory(LaunchdUnit.AgentsDir(Home));
        var path = LaunchdUnit.PlistPath(Home, id);
        File.WriteAllText(path, content);
        return path;
    }

    static string Print(string id, string spawnType) =>
        $"gui/501/io.kurrent.kcap.daemon.{id} = {{\n\tstate = running\n\tspawn type = {spawnType}\n}}\n";

    LaunchdServiceManager Manager(List<string[]> calls, Dictionary<string, string> spawnTypes) =>
        new(Home, TimeProvider.System,
            writeUnit: (p, c, _) => File.WriteAllText(p, c),
            runBounded: (_, args, _) => {
                calls.Add(args);
                var id = args[^1].Split('.')[^1];
                return args[0] switch {
                    "print" when spawnTypes.TryGetValue(id, out var spawn) => (0, Print(id, spawn), "", false),
                    "print"     => (113, "", "Could not find service", false),
                    "bootstrap" => ((Func<(int, string, string, bool)>)(() => { spawnTypes[Path.GetFileNameWithoutExtension(args[^1]).Split('.')[^1]] = "daemon (3)"; return (0, "", "", false); }))(),
                    _           => (0, "", "", false),
                };
            });

    DaemonServiceCommands Commands(LaunchdServiceManager manager, string id, Func<string, string, bool> restart) =>
        new(Daemons.Store, Config.Root, Resolutions.None(Config.Root), manager, id, Home, TimeProvider.System) { RestartRequester = restart };

    [Test]
    public async Task Forced_run_reloads_only_the_named_daemon_and_prints_one_token() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed("a", LaunchdUnit.Plist(Spec("a")).Replace("Standard", "Adaptive"));
        Seed("b", LaunchdUnit.Plist(Spec("b")).Replace("Standard", "Adaptive"));
        var calls = new List<string[]>();
        var modes = new List<(string Id, string Mode)>();
        using var stderr = ConsoleOutput.StartErrorCapture();
        using var stdout = ConsoleOutput.StartCapture();

        var exit = await Commands(Manager(calls, new() { ["a"] = "adaptive (6)", ["b"] = "adaptive (6)" }), "a",
            (id, mode) => { modes.Add((id, mode)); return true; }).Refresh(force: true, stabilize: s => s);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(modes).IsEquivalentTo([("a", "force")]);
        await Assert.That(calls.All(c => c[^1].EndsWith(".a") || c[^1].EndsWith(".a.plist"))).IsTrue();
        var tokens = stderr.ToString().Split('\n').Where(l => l.StartsWith("refresh_outcome=", StringComparison.Ordinal)).ToArray();
        await Assert.That(tokens).IsEquivalentTo(["refresh_outcome=reloaded"]);
    }

    [Test]
    public async Task Forced_run_on_a_missing_unit_is_unit_missing_and_touches_nothing_else() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed("b", LaunchdUnit.Plist(Spec("b")).Replace("Standard", "Adaptive"));
        var calls = new List<string[]>();
        using var stderr = ConsoleOutput.StartErrorCapture();
        using var stdout = ConsoleOutput.StartCapture();

        var exit = await Commands(Manager(calls, new() { ["b"] = "adaptive (6)" }), "a", (_, _) => true).Refresh(force: true, stabilize: s => s);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(stderr.ToString()).Contains("refresh_outcome=unit_missing");
        await Assert.That(calls).IsEmpty();
    }

    [Test]
    public async Task Forced_run_reports_contended_while_another_operation_holds_the_lock() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed("a", LaunchdUnit.Plist(Spec("a")).Replace("Standard", "Adaptive"));
        using var held = await ServiceTxnLock.TryAcquireAsync(Daemons.Store, "a", TimeSpan.Zero, TimeProvider.System);
        var calls = new List<string[]>();
        var asked = false;
        using var stderr = ConsoleOutput.StartErrorCapture();
        using var stdout = ConsoleOutput.StartCapture();

        var commands = Commands(Manager(calls, new() { ["a"] = "adaptive (6)" }), "a", (_, _) => asked = true);
        var exit = await commands.Refresh(force: true, stabilize: s => s);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(stderr.ToString()).Contains("refresh_outcome=contended");
        await Assert.That(asked).IsFalse();
        await Assert.That(calls).IsEmpty();
    }

    [Test]
    public async Task Unforced_run_iterates_every_unit_and_names_force_when_busy() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed("a", LaunchdUnit.Plist(Spec("a")).Replace("Standard", "Adaptive"));
        Seed("b", LaunchdUnit.Plist(Spec("b")).Replace("Standard", "Adaptive"));
        var calls = new List<string[]>();
        var asked = new List<(string Id, string Mode)>();
        using var stderr = ConsoleOutput.StartErrorCapture();
        using var stdout = ConsoleOutput.StartCapture();

        var exit = await Commands(Manager(calls, new() { ["a"] = "adaptive (6)", ["b"] = "adaptive (6)" }), "a",
            (id, mode) => { asked.Add((id, mode)); return false; }).Refresh(stabilize: s => s);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(asked).IsEquivalentTo([("a", "now"), ("b", "now")]);
        await Assert.That(stdout.ToString()).Contains("kcap daemon service refresh --name a --force");
        await Assert.That(stderr.ToString()).DoesNotContain("refresh_outcome=");
    }

    [Test]
    public async Task Token_mapping_and_exit_codes_follow_the_table() {
        await Assert.That(DaemonServiceCommands.RefreshToken(UnitRefresh.Reloaded)).IsEqualTo("reloaded");
        await Assert.That(DaemonServiceCommands.RefreshToken(UnitRefresh.Current)).IsEqualTo("current");
        await Assert.That(DaemonServiceCommands.RefreshToken(UnitRefresh.NotLoaded)).IsEqualTo("not_loaded");
        await Assert.That(DaemonServiceCommands.RefreshToken(UnitRefresh.Deferred)).IsEqualTo("deferred");
        await Assert.That(DaemonServiceCommands.RefreshToken(UnitRefresh.Contended)).IsEqualTo("contended");
        await Assert.That(DaemonServiceCommands.RefreshToken(UnitRefresh.Unverified)).IsEqualTo("unverified");
        await Assert.That(DaemonServiceCommands.RefreshToken(UnitRefresh.UnitMissing)).IsEqualTo("unit_missing");
        await Assert.That(DaemonServiceCommands.RefreshToken(UnitRefresh.UnitUnreadable)).IsEqualTo("unit_unreadable");
        await Assert.That(DaemonServiceCommands.RefreshToken(UnitRefresh.UnitUnsupported)).IsEqualTo("unit_unsupported");
        await Assert.That(DaemonServiceCommands.RefreshToken(UnitRefresh.Failed)).IsEqualTo("failed");
        await Assert.That(DaemonServiceCommands.ForcedExitCode(UnitRefresh.Current)).IsEqualTo(0);
        await Assert.That(DaemonServiceCommands.ForcedExitCode(UnitRefresh.Deferred)).IsEqualTo(1);
    }
}
```

If `ConsoleOutput.StartErrorCapture()` does not exist under that name in `test/Capacitor.Tests.Helpers`, use the helper's actual stderr capture method; both captures are required because the command writes the token to stderr and the human line to stdout.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonCommandsServiceRefreshTests/*"`
Expected: build errors (`RestartRequester`, `RefreshToken`, `ForcedExitCode`, `Refresh(bool, …)`).

- [ ] **Step 3: Implement**

In `DispatchAsync`, change the arm to `"refresh" => await verbs.Refresh(force: rest.Contains("--force")),`.

Add the seam and helpers to `DaemonServiceCommands`:

```csharp
    /// <summary>Asks a running daemon for a restart in <c>mode</c> and reports whether it accepted. Test seam;
    /// production uses the control socket.</summary>
    internal Func<string, string, bool>? RestartRequester { get; init; }

    static readonly TimeSpan ForcedLockWait = TimeSpan.FromSeconds(10);

    internal static string RefreshToken(UnitRefresh outcome) => outcome switch {
        UnitRefresh.Reloaded        => "reloaded",
        UnitRefresh.Current         => "current",
        UnitRefresh.NotLoaded       => "not_loaded",
        UnitRefresh.Deferred        => "deferred",
        UnitRefresh.Contended       => "contended",
        UnitRefresh.Unverified      => "unverified",
        UnitRefresh.UnitMissing     => "unit_missing",
        UnitRefresh.UnitUnreadable  => "unit_unreadable",
        UnitRefresh.UnitUnsupported => "unit_unsupported",
        _                           => "failed",
    };

    internal static int ForcedExitCode(UnitRefresh outcome) => outcome is UnitRefresh.Reloaded or UnitRefresh.Current ? 0 : 1;

    static string ForceHint(string serviceId) =>
        $"To apply it now, run `kcap daemon service refresh --name {serviceId} --force` (ends this daemon's hosted agents).";
```

Replace `RequestIdleRestart` with:

```csharp
    /// <summary>True only when the daemon accepted a restart in <paramref name="mode"/>: <c>now</c> is applied only
    /// while idle, <c>force</c> ends whatever it hosts. A <c>queued</c> answer is not an acceptance.</summary>
    bool RequestRestart(string serviceId, string mode) {
        if (RestartRequester is { } requester) return requester(serviceId, mode);
        try {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
            var reply = DaemonRestartClient.RequestAsync(store, serviceId, mode, cts.Token).GetAwaiter().GetResult();
            return reply is { Type: Core.LocalIpc.FrameType.RestartAck, Text: "restarting" };
        } catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException) {
            return false;
        }
    }
```

Rewrite `Refresh`:

```csharp
    internal async Task<int> Refresh(bool force = false, Func<string, string>? stabilize = null) {
        stabilize ??= ScriptInstallLayout.Stabilize;

        if (manager is SystemdServiceManager systemd) {
            // unchanged body from today
        }

        if (manager is not LaunchdServiceManager launchd) return 0;
        return force ? await RefreshForcedAsync(launchd, stabilize) : await RefreshAllAsync(launchd, stabilize);
    }

    /// <summary>The unattended post-update pass: every installed unit, never a restart the daemon did not accept.</summary>
    async Task<int> RefreshAllAsync(LaunchdServiceManager launchd, Func<string, string> stabilize) {
        var failed  = 0;
        var started = time.GetTimestamp();
        TimeSpan TimeLeft() => RefreshDeadline - time.GetElapsedTime(started);

        foreach (var serviceId in launchd.ListInstalled()) {
            if (TimeLeft() < LaunchdServiceManager.RefreshCtlTimeout) {
                await Console.Out.WriteLineAsync("Out of time; the remaining daemons are checked on the next update.");
                break;
            }

            using var txn = await ServiceTxnLock.TryAcquireAsync(store, serviceId, TimeSpan.Zero, time);
            if (txn is null) {
                await Console.Out.WriteLineAsync($"Daemon '{serviceId}': another service operation is in progress, so its service unit change waits for the next update.");
                continue;
            }

            var outcome = launchd.RefreshUnit(serviceId, () => RequestRestart(serviceId, "now"), TimeLeft, out var error, stabilize);

            switch (outcome) {
                case UnitRefresh.Reloaded:
                    await Console.Out.WriteLineAsync($"Daemon '{serviceId}': reloaded with its updated service unit.");
                    break;
                case UnitRefresh.Deferred:
                    await Console.Out.WriteLineAsync(
                        $"Daemon '{serviceId}': busy, so its service unit change waits for the next update. {ForceHint(serviceId)}");
                    break;
                case UnitRefresh.Unverified:
                    await Console.Out.WriteLineAsync(
                        $"Daemon '{serviceId}': launchd's state could not be read, so the check waits for the next update.");
                    break;
                case UnitRefresh.Failed:
                    await Console.Error.WriteLineAsync($"Daemon '{serviceId}': {error}");
                    failed++;
                    break;
            }
        }

        return failed == 0 ? 0 : 1;
    }

    /// <summary>One daemon, a forced restart, and one <c>refresh_outcome=</c> line for machine callers.</summary>
    async Task<int> RefreshForcedAsync(LaunchdServiceManager launchd, Func<string, string> stabilize) {
        var started = time.GetTimestamp();
        TimeSpan TimeLeft() => RefreshDeadline - time.GetElapsedTime(started);

        UnitRefresh outcome;
        string?     error = null;
        try {
            using var txn = await ServiceTxnLock.TryAcquireAsync(store, id, ForcedLockWait, time);
            outcome = txn is null
                ? UnitRefresh.Contended
                : launchd.RefreshUnit(id, () => RequestRestart(id, "force"), TimeLeft, out error, stabilize);
        } catch (Exception ex) {
            outcome = UnitRefresh.Failed;
            error   = ex.Message;
        }

        await Console.Error.WriteLineAsync($"refresh_outcome={RefreshToken(outcome)}");
        if (error is not null) await Console.Error.WriteLineAsync($"Daemon '{id}': {error}");
        await Console.Out.WriteLineAsync(outcome switch {
            UnitRefresh.Reloaded        => $"Daemon '{id}': reloaded at standard priority.",
            UnitRefresh.Current         => $"Daemon '{id}': already runs at standard priority.",
            UnitRefresh.NotLoaded       => $"Daemon '{id}': its service is not loaded; run `kcap daemon service start --name {id}`.",
            UnitRefresh.Deferred        => $"Daemon '{id}': the daemon did not accept the restart.",
            UnitRefresh.Contended       => $"Daemon '{id}': another service operation is in progress. Try again shortly.",
            UnitRefresh.Unverified      => $"Daemon '{id}': launchd's state could not be verified.",
            UnitRefresh.UnitMissing     => $"Daemon '{id}': no service unit is installed.",
            UnitRefresh.UnitUnreadable  => $"Daemon '{id}': the service unit cannot be read.",
            UnitRefresh.UnitUnsupported => $"Daemon '{id}': the service unit cannot be brought to Standard; reinstall it with `kcap daemon service install --replace --verify --name {id}`.",
            _                           => $"Daemon '{id}': the reload failed.",
        });
        return ForcedExitCode(outcome);
    }
```

Keep the `using System.Net.Sockets;` import; `SocketException` is already used.

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonCommandsServiceRefreshTests/*"`
Expected: PASS.

- [ ] **Step 5: Build the solution and check AOT**

Run: `dotnet build Capacitor.slnx`
Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: build succeeds; the grep prints nothing.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli/Commands/DaemonServiceCommands.cs test/Capacitor.Cli.Tests.Unit/Commands/DaemonCommandsServiceRefreshTests.cs
git commit -m "Add a forced single-daemon service refresh with a machine-readable outcome (#1351)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 7: README and help text

**Files:**
- Modify: `README.md` (daemon service block, lines 1080–1093, and the `kcap daemon status` paragraph near line 1073)
- Modify: `src/Capacitor.Cli.Core/Resources/help-daemon.txt` (service subcommands, after the `status [--name N] [--json]` entry)

- [ ] **Step 1: README**

In the service code block, replace the `refresh` line with two lines:

```
kcap daemon service refresh                # bring installed units up to this version and onto a script install's `current` path; reloads only an idle daemon (runs after `kcap update`)
kcap daemon service refresh --name N --force   # reload daemon N now even while busy (ends its hosted agents); prints refresh_outcome=<token> on stderr
```

After the `status --json` paragraph, add:

> `status --json` also carries `loaded_spawn_type`, the word launchd reports for the loaded job (`daemon` for Standard, `adaptive` or `background` for the throttled band). `kcap daemon status` prints a `priority:` line when the job runs in that band, because every hosted agent inherits it and terminals lag under load; `kcap daemon service refresh --name N --force` reloads the job to Standard. The forced run exits 0 for `reloaded` and `current`, 1 for every other token (`not_loaded`, `deferred`, `contended`, `unverified`, `unit_missing`, `unit_unreadable`, `unit_unsupported`, `failed`).

- [ ] **Step 2: help text**

After the `status [--name N] [--json]` entry in the service subcommands of `help-daemon.txt`, add:

```
  refresh [--force]       Bring installed units up to this version. Reloads a
                          daemon only when it is idle; --force reloads the
                          named daemon now (ends its hosted agents) and prints
                          refresh_outcome=<token> on stderr.
```

- [ ] **Step 3: Commit**

```bash
git add README.md src/Capacitor.Cli.Core/Resources/help-daemon.txt
git commit -m "Document the forced service refresh and the priority status line (#1351)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

Part A is complete and shippable as its own PR at this point.

# Part B — Desktop app

### Task 8: Snapshot field and the reload executor call

**Files:**
- Modify: `src/Capacitor.App/Services/KcapCli.cs` (`ServiceSnapshot` record, `IKcapCli`, `KcapCli`)
- Modify: `test/Capacitor.App.Tests.Unit/DaemonLifecycleControllerTests.cs` (`FakeKcapCli`, add `ServiceReloadAsync`)
- Test: `test/Capacitor.App.Tests.Unit/KcapCliTests.cs`

**Interfaces:**
- Produces: `ServiceSnapshot(..., bool TxnActive, string? LoadedSpawnType = null)`; `Task<ProcessResult> IKcapCli.ServiceReloadAsync(CancellationToken ct)` running `daemon service refresh --name <daemon> --force`.

- [ ] **Step 1: Write the failing tests**

Add to `KcapCliTests.cs`:

```csharp
    [Test]
    [Arguments("\"loaded_spawn_type\":\"adaptive\",", "adaptive")]
    [Arguments("\"loaded_spawn_type\":null,", null)]
    [Arguments("", null)]
    public async Task ServiceStatusAsync_reads_the_loaded_spawn_type(string field, string? expected) {
        var json = "{\"service_id\":\"daemon-a\",\"unit_present\":true,\"state\":\"running\",\"binary_path\":\"/b\"," +
                   "\"install_binary_path\":\"/b\",\"job_pid\":7,\"daemon_pid\":7,\"txn_marker\":false,\"txn_active\":false," +
                   field + "\"unit_profile\":null}";
        var runner = new FakeProcessRunner { Behavior = _ => Task.FromResult(new ProcessResult(0, json, "", false)) };

        var snapshot = await MakeCli(runner).ServiceStatusAsync(CancellationToken.None);

        await Assert.That(snapshot!.LoadedSpawnType).IsEqualTo(expected);
    }

    [Test]
    public async Task ServiceReloadAsync_runs_the_forced_refresh_for_this_daemon() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);

        await cli.ServiceReloadAsync(CancellationToken.None);

        await Assert.That(runner.SeenArgs).IsEquivalentTo(
            ["daemon", "service", "refresh", "--name", "daemon-a", "--force"], CollectionOrdering.Matching);
        await Assert.That(runner.SeenOptions!.Timeout).IsEqualTo(TimeSpan.FromSeconds(60));
        await Assert.That(runner.SeenOptions.EnvOverlay![KcapCli.ExpectServerUrlVar]).IsEqualTo(CanonicalServer);
    }

    [Test]
    public async Task ServiceReloadAsync_without_a_cli_is_exit_127() {
        var runner = new FakeProcessRunner();
        var cli = new KcapCli(runner, null, "daemon-a", "work", _ => Task.FromResult<string?>(null), CanonicalServer);
        var result = await cli.ServiceReloadAsync(CancellationToken.None);
        await Assert.That(result.ExitCode).IsEqualTo(127);
        await Assert.That(runner.SeenArgs).IsNull();
    }
```

If `MutationEnv()` does not set `KcapCli.ExpectServerUrlVar` to the canonical server (check the existing `ServiceStartVerifiedAsync` test for the key it asserts), assert the same key that test asserts.

- [ ] **Step 2: Run to verify failure**

Run: `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/KcapCliTests/*"`
Expected: build error, `LoadedSpawnType` and `ServiceReloadAsync` missing.

- [ ] **Step 3: Implement**

`ServiceSnapshot`:

```csharp
/// The subset of Capacitor.Cli.Commands.ServiceStatusJson the app reads; snake_case on the wire.
/// `LoadedSpawnType` is launchd's word for the loaded job, null when the label is not loaded or the
/// CLI predates the field; read it through SpawnTypes.
public sealed record ServiceSnapshot(
    string ServiceId, bool UnitPresent, string State, string? BinaryPath, string? InstallBinaryPath,
    int? JobPid, int? DaemonPid, bool TxnMarker, bool TxnActive, string? LoadedSpawnType = null);
```

`IKcapCli`, after `ServiceStartVerifiedAsync`:

```csharp
    /// `daemon service refresh --name <name> --force`: reloads this daemon's launchd job to Standard, ending
    /// whatever it hosts. One `refresh_outcome=<token>` line on stderr; exit 0 only for reloaded/current.
    Task<ProcessResult> ServiceReloadAsync(CancellationToken ct);
```

`KcapCli`:

```csharp
    public Task<ProcessResult> ServiceReloadAsync(CancellationToken ct) {
        var env = MutationEnv(); // throws before any spawn if the instance carries no server
        return CliPath is not { } cliPath
            ? NoCliResult()
            : Run(cliPath, ["daemon", "service", "refresh", "--name", _daemonName, "--force"],
                new RunOptions(EnvOverlay: env, Timeout: MutationTimeout), ct);
    }
```

`FakeKcapCli` (in `DaemonLifecycleControllerTests.cs`), beside `ServiceStartVerifiedAsync`:

```csharp
    public int ReloadCallCount;
    public Func<CancellationToken, Task<ProcessResult>> ReloadBehavior = _ => Task.FromResult(new ProcessResult(0, "", "", false));
    public Task<ProcessResult> ServiceReloadAsync(CancellationToken ct) {
        ReloadCallCount++;
        return ReloadBehavior(ct);
    }
```

Any other `IKcapCli` implementation in the test project (search `: IKcapCli`) gets the same member.

- [ ] **Step 4: Run the tests**

Run: `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/KcapCliTests/*"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Services/KcapCli.cs test/Capacitor.App.Tests.Unit/KcapCliTests.cs test/Capacitor.App.Tests.Unit/DaemonLifecycleControllerTests.cs
git commit -m "Read the loaded spawn type and run a forced refresh from the app's CLI seam (#1351)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 9: The Reload verb in the mutation lane

**Files:**
- Modify: `src/Capacitor.App/Services/Mutation/MutationModel.cs` (`MutationVerb`)
- Modify: `src/Capacitor.App/Services/Mutation/DaemonMutationLane.cs` (`ExecuteActionAsync`, `Dispatch`, `ClassifyOutcomeAsync`, `ClassifyServiceSuccessAsync`; new `ClassifyReloadAsync`)
- Test: `test/Capacitor.App.Tests.Unit/DaemonMutationLaneTests.cs`

**Interfaces:**
- Consumes: `IKcapCli.ServiceReloadAsync`, `ServiceSnapshot.LoadedSpawnType` (Task 8), `SpawnTypes` (Task 1, from Core).
- Produces: `MutationVerb.Reload`; outcomes `Refused("reload_unsupported", Attention)`, `Failed(exit, token, Attention)`, `AttentionSkew("background_band")`, `AttentionSkew("spawn_type_unknown")`, `UnconfirmedNoAttach`, `Succeeded`.

- [ ] **Step 1: Write the failing tests**

Add to `DaemonMutationLaneTests.cs`. The harness's `Ownership(...)` helper needs a `loadedSpawnType` parameter; extend it:

```csharp
    static ServiceSnapshot Ownership(
            int? jobPid = 111, int? daemonPid = 111, bool txnMarker = false, bool txnActive = false,
            string state = "running", bool unitPresent = true, string? loadedSpawnType = "daemon") =>
        new("daemon-a", unitPresent, state, "/opt/kcap/kcapd", "/opt/kcap/kcapd", jobPid, daemonPid, txnMarker, txnActive, loadedSpawnType);
```

Then the cases:

```csharp
    [Test]
    public async Task Reload_is_refused_before_dispatch_when_the_pinned_cli_reports_no_spawn_type() {
        var cli = new FakeKcapCli { StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership(loadedSpawnType: null)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        await using var lane = MakeLane(factory);

        var outcome = await lane.RunAsync(Req(MutationVerb.Reload), CancellationToken.None).WaitAsync(Bounded);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Refused("reload_unsupported", RecoverySurface.Attention));
        await Assert.That(cli.ReloadCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task Reload_is_refused_when_the_pinned_cli_cannot_report_status_at_all() {
        var cli = new FakeKcapCli { StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(null) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        await using var lane = MakeLane(factory);
        var outcome = await lane.RunAsync(Req(MutationVerb.Reload), CancellationToken.None).WaitAsync(Bounded);
        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Refused("reload_unsupported", RecoverySurface.Attention));
        await Assert.That(cli.ReloadCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task Reload_dispatches_the_forced_refresh_once_the_field_is_present() {
        var cli = new FakeKcapCli { StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership(loadedSpawnType: "adaptive")) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        await using var lane = MakeLane(factory, classify: CannedSucceeded);
        await lane.RunAsync(Req(MutationVerb.Reload), CancellationToken.None).WaitAsync(Bounded);
        await Assert.That(cli.ReloadCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task Reload_timeout_is_unconfirmed_and_nonzero_exit_carries_the_token() {
        var timedOut = new FakeKcapCli {
            StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership(loadedSpawnType: "adaptive")),
            ReloadBehavior = _ => Task.FromResult(new ProcessResult(0, "", "", true)),
        };
        await using var lane1 = MakeLane(new RecordingExecutorFactory { Behavior = (_, _) => timedOut });
        await Assert.That(await lane1.RunAsync(Req(MutationVerb.Reload), CancellationToken.None).WaitAsync(Bounded))
            .IsEqualTo(new MutationOutcome.UnconfirmedNoAttach());

        var failed = new FakeKcapCli {
            StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership(loadedSpawnType: "adaptive")),
            ReloadBehavior = _ => Task.FromResult(new ProcessResult(1, "", "refresh_outcome=contended\n", false)),
        };
        await using var lane2 = MakeLane(new RecordingExecutorFactory { Behavior = (_, _) => failed });
        await Assert.That(await lane2.RunAsync(Req(MutationVerb.Reload), CancellationToken.None).WaitAsync(Bounded))
            .IsEqualTo(new MutationOutcome.Failed(1, "contended", RecoverySurface.Attention));

        var noToken = new FakeKcapCli {
            StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership(loadedSpawnType: "adaptive")),
            ReloadBehavior = _ => Task.FromResult(new ProcessResult(1, "", "", false)),
        };
        await using var lane3 = MakeLane(new RecordingExecutorFactory { Behavior = (_, _) => noToken });
        await Assert.That(await lane3.RunAsync(Req(MutationVerb.Reload), CancellationToken.None).WaitAsync(Bounded))
            .IsEqualTo(new MutationOutcome.Failed(1, null, RecoverySurface.Attention));
    }

    [Test]
    public async Task Reload_waits_for_the_successor_within_the_window_then_requires_a_positive_spawn_type() {
        var time = new FakeTimeProvider();
        var observation = new ScriptedObservation {
            Sequence = new Queue<ObservedEvidence?>([
                new ObservedEvidence(false, null, null, null, null, null, null, false),
                new ObservedEvidence(false, null, null, null, null, null, null, false),
                MatchingEvidence(), MatchingEvidence(), MatchingEvidence()]),
        };
        var cli = new FakeKcapCli {
            StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership(loadedSpawnType: "daemon")),
            ReloadBehavior = _ => Task.FromResult(new ProcessResult(0, "", "refresh_outcome=reloaded\n", false)),
        };
        await using var lane = MakeLane(new RecordingExecutorFactory { Behavior = (_, _) => cli }, oneShotFactory: _ => observation, time: time);

        var outcome = await Drive(lane.RunAsync(Req(MutationVerb.Reload), CancellationToken.None), time, DaemonMutationLane.DetachedPollInterval);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Succeeded());
        await Assert.That(observation.CallCount).IsGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task Reload_whose_daemon_never_comes_back_is_unconfirmed_after_the_window() {
        var time = new FakeTimeProvider();
        var observation = new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(new ObservedEvidence(false, null, null, null, null, null, null, false)) };
        var cli = new FakeKcapCli {
            StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership(loadedSpawnType: "adaptive")),
            ReloadBehavior = _ => Task.FromResult(new ProcessResult(0, "", "refresh_outcome=reloaded\n", false)),
        };
        await using var lane = MakeLane(new RecordingExecutorFactory { Behavior = (_, _) => cli }, oneShotFactory: _ => observation, time: time);
        var outcome = await Drive(lane.RunAsync(Req(MutationVerb.Reload), CancellationToken.None), time, DaemonMutationLane.DetachedPollInterval);
        await Assert.That(outcome).IsEqualTo(new MutationOutcome.UnconfirmedNoAttach());
    }

    [Test]
    [Arguments("adaptive", "background_band")]
    [Arguments("background", "background_band")]
    [Arguments("app", "spawn_type_unknown")]
    [Arguments(null, "spawn_type_unknown")]
    public async Task Reload_exit_zero_without_positive_priority_is_a_skew(string? word, string expected) {
        var calls = 0;
        var cli = new FakeKcapCli {
            // The pre-dispatch gate reads a present field; the ownership read after the run reports the word under test.
            StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership(loadedSpawnType: calls++ == 0 ? "adaptive" : word)),
            ReloadBehavior = _ => Task.FromResult(new ProcessResult(0, "", "refresh_outcome=current\n", false)),
        };
        var observation = new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(MatchingEvidence()) };
        await using var lane = MakeLane(new RecordingExecutorFactory { Behavior = (_, _) => cli }, oneShotFactory: _ => observation);
        var outcome = await lane.RunAsync(Req(MutationVerb.Reload), CancellationToken.None).WaitAsync(Bounded);
        await Assert.That(outcome).IsEqualTo(new MutationOutcome.AttentionSkew(expected));
    }
```

`Drive`, `MatchingEvidence`, `ScriptedObservation`, `RecordingExecutorFactory`, `MakeLane`, `Bounded` and `Req` already exist in this file. `FakeKcapCli` is the one in `DaemonLifecycleControllerTests.cs` (same namespace).

- [ ] **Step 2: Run to verify failure**

Run: `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonMutationLaneTests/*"`
Expected: build error, `MutationVerb.Reload` missing.

- [ ] **Step 3: Implement**

`MutationModel.cs`: `public enum MutationVerb { Install, Replace, StartVerified, DetachedStart, Reload }`.

`DaemonMutationLane.ExecuteActionAsync`, after the version-floor check and before `attemptId`:

```csharp
        // An older CLI ignores --force and the daemon name and runs its all-daemon idle refresh, so the
        // capability is proven on the executable that will run the mutation, before anything is spawned.
        if (request.Verb == MutationVerb.Reload) {
            var status = await executor.ServiceStatusAsync(ct).ConfigureAwait(false);
            if (status?.LoadedSpawnType is null) return new MutationOutcome.Refused("reload_unsupported", RecoverySurface.Attention);
        }
```

`Dispatch`: add `MutationVerb.Reload => executor.ServiceReloadAsync(ct),`.

`ClassifyOutcomeAsync`:

```csharp
    Task<MutationOutcome> ClassifyOutcomeAsync(
            MutationRequest request, ProcessResult result, IKcapCli executor, IDaemonObservation observation,
            string? attemptId, CancellationToken ct) =>
        request.Verb switch {
            MutationVerb.DetachedStart => ClassifyDetachedStartAsync(request, result, observation, attemptId, ct),
            MutationVerb.Reload        => ClassifyReloadAsync(request, result, executor, observation, ct),
            _                          => ClassifyServiceVerbAsync(request, result, executor, observation, ct),
        };
```

New method:

```csharp
    // The refresh returns as soon as bootstrap succeeds, before the successor binds its socket, and the
    // one-shot observation never retries: the readiness window gives the new daemon time to answer.
    async Task<MutationOutcome> ClassifyReloadAsync(
            MutationRequest request, ProcessResult result, IKcapCli executor, IDaemonObservation observation, CancellationToken ct) {
        if (result.TimedOut) return new MutationOutcome.UnconfirmedNoAttach();
        if (result.ExitCode != 0)
            return new MutationOutcome.Failed(result.ExitCode, ReasonLine.TrySingle(result.Stderr, "refresh_outcome="), RecoverySurface.Attention);

        var deadline = _time.GetUtcNow() + DetachedConfirmWindow;
        while (true) {
            var evidence = await observation.ObserveAsync(request, ct).ConfigureAwait(false);
            var leg = EvidenceFailureLeg(evidence, request);
            if (leg is null) break;

            var remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                return leg == UnreachableLeg ? new MutationOutcome.UnconfirmedNoAttach() : new MutationOutcome.AttentionSkew(leg);

            var wait = remaining < DetachedPollInterval ? remaining : DetachedPollInterval;
            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
        }

        return await ClassifyServiceSuccessAsync(request, executor, observation, ct, requirePositiveSpawnType: true).ConfigureAwait(false);
    }
```

`ClassifyServiceSuccessAsync` gains `bool requirePositiveSpawnType = false` and, after the `ownership is null` check and before the `JobPid`/`DaemonPid` checks:

```csharp
        if (requirePositiveSpawnType) {
            var spawn = ownership.LoadedSpawnType;
            if (SpawnTypes.IsBackgroundBand(spawn)) return new MutationOutcome.AttentionSkew("background_band");
            if (!SpawnTypes.IsPositive(spawn)) return new MutationOutcome.AttentionSkew("spawn_type_unknown");
        }
```

Add `using Capacitor.Cli.Core;` if the file lacks it (it already imports `Capacitor.Cli.Core`).

- [ ] **Step 4: Run the tests**

Run: `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonMutationLaneTests/*"`
Expected: PASS, including every pre-existing case.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Services/Mutation/MutationModel.cs src/Capacitor.App/Services/Mutation/DaemonMutationLane.cs test/Capacitor.App.Tests.Unit/DaemonMutationLaneTests.cs
git commit -m "Run the daemon reload through the mutation lane with positive priority evidence (#1351)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 10: Reload state, its copy, and the prompt kind

**Files:**
- Create: `src/Capacitor.App/Services/ReloadOutcomeKind.cs`
- Create: `src/Capacitor.App/Services/ReloadState.cs`
- Create: `src/Capacitor.App/Services/ReloadCopy.cs`
- Modify: `src/Capacitor.App/Services/ILifecycleSurface.cs` (`LifecyclePrompt.KindReloadService`)
- Modify: `src/Capacitor.App/ViewModels/LifecyclePromptViewModel.cs` (`TitleFor`, `AcceptButtonText`)
- Modify: `src/Capacitor.App/App.axaml.cs` (`VerbDisplay` gains `Reload => "reload"`)
- Test: `test/Capacitor.App.Tests.Unit/ReloadCopyTests.cs` (create)
- Test: `test/Capacitor.App.Tests.Unit/LifecyclePromptViewModelTests.cs`

**Interfaces:**
- Produces: `public enum ReloadOutcomeKind { Failed, Refused, Skew, Repair, Unconfirmed }`; `public sealed record ReloadState(ReloadOutcomeKind Kind, string Token, int? ExitCode, string DaemonName, long Sequence)` with `static ReloadState? From(MutationOutcome outcome, string daemonName, long sequence)` (null for a success); `public static class ReloadCopy { static string For(ReloadState state); static bool ResolvedByPositivePriority(string token); }`; `LifecyclePrompt.KindReloadService = "reload-service"`.

- [ ] **Step 1: Write the failing tests**

```csharp
// test/Capacitor.App.Tests.Unit/ReloadCopyTests.cs
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

public class ReloadCopyTests {
    static ReloadState State(MutationOutcome outcome) => ReloadState.From(outcome, "alexey", 1)!;

    [Test]
    public async Task A_success_has_no_state() {
        await Assert.That(ReloadState.From(new MutationOutcome.Succeeded(), "alexey", 1)).IsNull();
        await Assert.That(ReloadState.From(new MutationOutcome.SucceededAfterTimeout(), "alexey", 1)).IsNull();
    }

    [Test]
    public async Task Refresh_tokens_and_priority_skews_name_the_daemon() {
        var contended = ReloadCopy.For(State(new MutationOutcome.Failed(1, "contended", RecoverySurface.Attention)));
        await Assert.That(contended).Contains("--name alexey");
        var band = ReloadCopy.For(State(new MutationOutcome.AttentionSkew("background_band")));
        await Assert.That(band).IsEqualTo("The daemon still runs at background priority after the reload. Run `kcap daemon service refresh --name alexey --force` from a terminal and check `kcap daemon status`.");
        var unknown = ReloadCopy.For(State(new MutationOutcome.AttentionSkew("spawn_type_unknown")));
        await Assert.That(unknown).IsEqualTo("The reload finished but the daemon's priority could not be confirmed. Check `kcap daemon status --name alexey`.");
    }

    [Test]
    public async Task Refusals_carry_no_exit_code_and_reload_specific_recovery() {
        await Assert.That(ReloadCopy.For(State(new MutationOutcome.Refused("reload_unsupported", RecoverySurface.Attention))))
            .IsEqualTo("This kcap CLI cannot reload the daemon service. Update kcap, then press Reload again.");
        await Assert.That(ReloadCopy.For(State(new MutationOutcome.Refused("cli_below_floor", RecoverySurface.Attention))))
            .IsEqualTo("This kcap is too old for this app. Update kcap, then press Reload again.");
        await Assert.That(ReloadCopy.For(State(new MutationOutcome.Refused("cli_not_found", RecoverySurface.Attention))))
            .IsEqualTo("kcap CLI not found. Can't manage the daemon from this app.");
    }

    [Test]
    public async Task Ownership_and_evidence_legs_use_neutral_wording() {
        var repair = ReloadCopy.For(State(new MutationOutcome.AttentionRepair("running_without_daemon_pid")));
        await Assert.That(repair).IsEqualTo("The daemon service could not be verified (running_without_daemon_pid). Check `kcap daemon status --name alexey`.");
        var skew = ReloadCopy.For(State(new MutationOutcome.AttentionSkew("ownership_mismatch")));
        await Assert.That(skew).DoesNotContain("restart");
        await Assert.That(skew).DoesNotContain("came back");
    }

    [Test]
    public async Task Unknown_tokens_fall_back_with_or_without_an_exit_code() {
        await Assert.That(ReloadCopy.For(State(new MutationOutcome.Failed(7, null, RecoverySurface.Attention))))
            .IsEqualTo("The daemon reload for alexey failed (exit 7). Check `kcap daemon status --name alexey`; details are in the app log.");
        await Assert.That(ReloadCopy.For(State(new MutationOutcome.Refused("something_new", RecoverySurface.Attention))))
            .IsEqualTo("The daemon reload for alexey did not succeed (something_new). Check `kcap daemon status --name alexey`; details are in the app log.");
        await Assert.That(ReloadCopy.For(State(new MutationOutcome.UnconfirmedNoAttach())))
            .IsEqualTo("The daemon reload is not yet confirmed — check `kcap daemon status --name alexey`.");
    }

    [Test]
    public async Task Only_priority_class_tokens_are_resolved_by_a_positive_read() {
        foreach (var token in new[] { "background_band", "spawn_type_unknown", "deferred", "contended", "unverified" })
            await Assert.That(ReloadCopy.ResolvedByPositivePriority(token)).IsTrue();
        foreach (var token in new[] { "reload_unsupported", "not_loaded", "unit_missing", "unit_unreadable", "unit_unsupported", "failed", "ownership_mismatch", "verify_unknown_7", "unconfirmed" })
            await Assert.That(ReloadCopy.ResolvedByPositivePriority(token)).IsFalse();
    }
}
```

Add to `LifecyclePromptViewModelTests.cs`:

```csharp
    [Test]
    public async Task Reload_service_prompt_has_its_title_and_accept_label() {
        var vm = new LifecyclePromptViewModel(
            new LifecyclePrompt(LifecyclePrompt.KindReloadService, null, null, false, "disclosure"), new TaskCompletionSource<bool>());
        await Assert.That(vm.Title).IsEqualTo("Reload the daemon service");
        await Assert.That(vm.AcceptButtonText).IsEqualTo("Reload now");
        await Assert.That(vm.ShowDeclineButton).IsTrue();
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ReloadCopyTests/*"`
Expected: build errors for the missing types.

- [ ] **Step 3: Implement**

```csharp
// src/Capacitor.App/Services/ReloadOutcomeKind.cs
namespace Capacitor.App.Services;

public enum ReloadOutcomeKind { Failed, Refused, Skew, Repair, Unconfirmed }
```

```csharp
// src/Capacitor.App/Services/ReloadState.cs
using Capacitor.App.Services.Mutation;

namespace Capacitor.App.Services;

/// The last Reload outcome that still stands, rendered by the rail block; null stands for "nothing to
/// show". `ExitCode` exists only for a Failed outcome. `Sequence` orders it against passive status reads.
public sealed record ReloadState(ReloadOutcomeKind Kind, string Token, int? ExitCode, string DaemonName, long Sequence) {
    public static ReloadState? From(MutationOutcome outcome, string daemonName, long sequence) => outcome switch {
        MutationOutcome.Succeeded or MutationOutcome.SucceededAfterTimeout => null,
        MutationOutcome.Failed f           => new(ReloadOutcomeKind.Failed, f.Reason ?? VerifyExitCodes.Token(f.ExitCode), f.ExitCode, daemonName, sequence),
        MutationOutcome.Refused r          => new(ReloadOutcomeKind.Refused, r.Reason, null, daemonName, sequence),
        MutationOutcome.AttentionSkew s    => new(ReloadOutcomeKind.Skew, s.Detail, null, daemonName, sequence),
        MutationOutcome.AttentionRepair r  => new(ReloadOutcomeKind.Repair, r.Detail, null, daemonName, sequence),
        MutationOutcome.UnconfirmedNoAttach => new(ReloadOutcomeKind.Unconfirmed, "unconfirmed", null, daemonName, sequence),
        _                                   => new(ReloadOutcomeKind.Failed, outcome.GetType().Name, null, daemonName, sequence),
    };
}
```

```csharp
// src/Capacitor.App/Services/ReloadCopy.cs
using System.Collections.Frozen;

namespace Capacitor.App.Services;

/// The failure line under the rail's Reload button. Built from the outcome kind and token, naming the
/// daemon and the terminal command, and never a detail the outcome does not carry.
public static class ReloadCopy {
    static readonly FrozenSet<string> PriorityClass =
        FrozenSet.ToFrozenSet(["background_band", "spawn_type_unknown", "deferred", "contended", "unverified"], StringComparer.Ordinal);

    static readonly FrozenSet<string> EvidenceLegs = FrozenSet.ToFrozenSet([
        "stale_txn_marker", "running_without_daemon_pid", "daemon_running_outside_service", "ownership_mismatch",
        "ownership_unknown", "instance_pid_mismatch", "instance_changed_during_classification", "server_or_name_mismatch",
        "pre_slice_evidence", "identity_inconsistent", "missing_capability_consent_3", "daemon_below_floor",
    ], StringComparer.Ordinal);

    /// A positive passive read clears only a failure whose sole complaint was the priority state or a
    /// transient inability to act.
    public static bool ResolvedByPositivePriority(string token) => PriorityClass.Contains(token);

    public static string For(ReloadState state) {
        var name  = state.DaemonName;
        var check = $"Check `kcap daemon status --name {name}`";
        var force = $"`kcap daemon service refresh --name {name} --force`";

        if (state.Kind == ReloadOutcomeKind.Unconfirmed)
            return $"The daemon reload is not yet confirmed — check `kcap daemon status --name {name}`.";

        if (state.Kind == ReloadOutcomeKind.Refused) {
            return state.Token switch {
                "reload_unsupported" => "This kcap CLI cannot reload the daemon service. Update kcap, then press Reload again.",
                "cli_below_floor"    => "This kcap is too old for this app. Update kcap, then press Reload again.",
                _ => App.AttentionCopyFor(state.Token) ?? $"The daemon reload for {name} did not succeed ({state.Token}). {check}; details are in the app log.",
            };
        }

        if (state.Kind is ReloadOutcomeKind.Skew or ReloadOutcomeKind.Repair) {
            if (state.Token == "background_band")
                return $"The daemon still runs at background priority after the reload. Run {force} from a terminal and check `kcap daemon status`.";
            if (state.Token == "spawn_type_unknown")
                return $"The reload finished but the daemon's priority could not be confirmed. {check}.";
            if (EvidenceLegs.Contains(state.Token))
                return $"The daemon service could not be verified ({state.Token}). {check}.";
        }

        var refresh = state.Token switch {
            "deferred"         => $"The daemon did not accept the restart. Run {force} from a terminal.",
            "contended"        => $"Another service operation is in progress for {name}. Try Reload again shortly, or run {force}.",
            "not_loaded"       => $"The daemon service for {name} is not loaded. Run `kcap daemon service start --name {name}`.",
            "unverified"       => $"launchd's state for {name} could not be verified. {check}.",
            "unit_missing"     => $"No service unit is installed for {name}. Run `kcap daemon service install --name {name}`.",
            "unit_unreadable"  => $"The service unit for {name} cannot be read. {check}.",
            "unit_unsupported" => $"The service unit for {name} cannot be brought to Standard. Reinstall it with `kcap daemon service install --replace --verify --name {name}`.",
            "failed"           => $"The daemon reload for {name} failed. {check}; details are in the app log.",
            _                  => null,
        };
        if (refresh is not null) return refresh;

        return state.ExitCode is { } exit
            ? $"The daemon reload for {name} failed (exit {exit}). {check}; details are in the app log."
            : $"The daemon reload for {name} did not succeed ({state.Token}). {check}; details are in the app log.";
    }
}
```

`App.AttentionCopyFor` is `internal static` on the `App` class in `App.axaml.cs`; same assembly, so the call compiles.

`ILifecycleSurface.cs`: add `public const string KindReloadService = "reload-service";` to `LifecyclePrompt`.

`LifecyclePromptViewModel`: in `TitleFor` add `LifecyclePrompt.KindReloadService => "Reload the daemon service",`; in the `AcceptButtonText` switch add `LifecyclePrompt.KindReloadService => "Reload now",`.

`App.axaml.cs` `VerbDisplay`: add `MutationVerb.Reload => "reload",`.

- [ ] **Step 4: Run the tests**

Run: `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ReloadCopyTests/*"`
Run: `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/LifecyclePromptViewModelTests/*"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Services/ReloadOutcomeKind.cs src/Capacitor.App/Services/ReloadState.cs src/Capacitor.App/Services/ReloadCopy.cs src/Capacitor.App/Services/ILifecycleSurface.cs src/Capacitor.App/ViewModels/LifecyclePromptViewModel.cs src/Capacitor.App/App.axaml.cs test/Capacitor.App.Tests.Unit/ReloadCopyTests.cs test/Capacitor.App.Tests.Unit/LifecyclePromptViewModelTests.cs
git commit -m "Model a daemon reload outcome as state with its own copy and prompt (#1351)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 11: Controller — indicator state, passive reads, the reload action, resolution

**Files:**
- Modify: `src/Capacitor.App/Services/DaemonLifecycleController.cs`
- Test: `test/Capacitor.App.Tests.Unit/DaemonLifecycleControllerTests.cs`

**Interfaces:**
- Consumes: `ReloadState`, `ReloadCopy.ResolvedByPositivePriority`, `LifecyclePrompt.KindReloadService` (Task 10); `MutationVerb.Reload` (Task 9); `ServiceSnapshot.LoadedSpawnType` (Task 8); `SpawnTypes` (Task 1).
- Produces on `DaemonLifecycleController`: `IObservable<bool> BackgroundPriority`, `IObservable<ReloadState?> ReloadState`, `IObservable<bool> IsReloading`, `Task ReloadServiceAsync(CancellationToken ct)`, `internal static string ReloadDisclosure(int activeAgents)`, `internal const string StandardPriorityStatus = "Daemon runs at standard priority."`, `internal const string PromptStaleStatus = "The daemon changed while the prompt was open — canceled, nothing changed."`.

- [ ] **Step 1: Write the failing tests**

Add to `DaemonLifecycleControllerTests.cs`. The harness needs a snapshot pusher and a latest-value reader; add to `Harness`:

```csharp
        public void PushSnapshot(int activeAgents) =>
            Client.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(daemon: "daemon-a", active: activeAgents));

        public bool Background() { bool? v = null; using (Controller.BackgroundPriority.Subscribe(x => v = x)) { } return v ?? false; }
        public ReloadState? Reload() { ReloadState? v = null; using (Controller.ReloadState.Subscribe(x => v = x)) { } return v; }
        public bool Reloading() { bool? v = null; using (Controller.IsReloading.Subscribe(x => v = x)) { } return v ?? false; }
```

Then the cases:

```csharp
    // ---- background-priority indicator ----

    [Test]
    [Arguments("adaptive", true)]
    [Arguments("background", true)]
    [Arguments("daemon", false)]
    [Arguments("interactive", false)]
    public async Task Indicator_follows_the_classified_spawn_type(string word, bool expected) {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 100) with { LoadedSpawnType = word });
        h.Start();
        h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the connected read");
        await Assert.That(h.Background()).IsEqualTo(expected);
    }

    [Test]
    public async Task Indicator_is_left_alone_by_an_unknown_word_an_empty_word_or_a_failed_read() {
        await using var h = new Harness();
        var words = new Queue<string?>(["adaptive", "app", "", null]);
        h.Cli.StatusBehavior = _ => {
            var word = words.Dequeue();
            return Task.FromResult<ServiceSnapshot?>(word is null ? null : Snap(state: "running", jobPid: 100, daemonPid: 100) with { LoadedSpawnType = word });
        };
        h.Start();
        h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "first read");
        await Assert.That(h.Background()).IsTrue();

        h.PushConnecting(); h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 2, what: "unknown-word read");
        await Assert.That(h.Background()).IsTrue();

        h.PushConnecting(); h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 3, what: "empty-word read");
        await Assert.That(h.Background()).IsTrue();

        h.PushConnecting(); h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 4, what: "failed read");
        await Assert.That(h.Background()).IsTrue();
    }

    [Test]
    public async Task Every_transition_into_connected_reads_status_without_arming_a_mutation() {
        await using var h = new Harness();
        var words = new Queue<string>(["adaptive", "daemon"]);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 100) with { LoadedSpawnType = words.Dequeue() });
        h.Start();
        h.PushUnreachable();
        await WaitUntilAsync(() => h.Cli.StatusCallCount >= 1, what: "the startup read");
        var afterStartup = h.Cli.StatusCallCount;

        h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == afterStartup + 1, what: "the connected read");
        await Assert.That(h.Background()).IsTrue();

        h.PushConnecting(); h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == afterStartup + 2, what: "the reconnect read");
        await Assert.That(h.Background()).IsFalse();
        await Assert.That(h.Lane.Requests.Count(r => r.Verb != MutationVerb.StartVerified && r.Verb != MutationVerb.Install)).IsEqualTo(0);
    }

    // ---- the reload action ----

    [Test]
    public async Task Reload_prompts_on_every_click_and_a_decline_runs_nothing() {
        await using var h = new Harness();
        h.PushSnapshot(activeAgents: 3);
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(false);

        await h.Controller.ReloadServiceAsync(CancellationToken.None);
        await h.Controller.ReloadServiceAsync(CancellationToken.None);

        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(2);
        await Assert.That(h.Surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindReloadService);
        await Assert.That(h.Surface.Prompts[0].Disclosure).IsEqualTo(DaemonLifecycleController.ReloadDisclosure(3));
        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.Reloading()).IsFalse();
    }

    [Test]
    public async Task Reload_runs_the_verb_records_success_and_rereads_status() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 100) with { LoadedSpawnType = "daemon" });
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);

        await h.Controller.ReloadServiceAsync(CancellationToken.None);

        await Assert.That(h.Lane.Requests.Select(r => r.Verb)).IsEquivalentTo([MutationVerb.Reload]);
        await Assert.That(h.Surface.StatusMessages).Contains(DaemonLifecycleController.StandardPriorityStatus);
        await Assert.That(h.Reload()).IsNull();
        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(1);
        await Assert.That(h.Background()).IsFalse();
        await Assert.That(h.Client.RestartCount).IsEqualTo(1);
    }

    [Test]
    public async Task Reload_records_a_failure_and_a_later_success_clears_it() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        var outcomes = new Queue<MutationOutcome>([
            new MutationOutcome.Failed(1, "unit_missing", RecoverySurface.Attention),
            new MutationOutcome.Succeeded()]);
        h.Lane.Behavior = (_, _) => Task.FromResult(outcomes.Dequeue());

        await h.Controller.ReloadServiceAsync(CancellationToken.None);
        await Assert.That(h.Reload()!.Token).IsEqualTo("unit_missing");
        await Assert.That(h.Reload()!.ExitCode).IsEqualTo(1);

        await h.Controller.ReloadServiceAsync(CancellationToken.None);
        await Assert.That(h.Reload()).IsNull();
    }

    [Test]
    public async Task A_success_followed_by_a_failure_leaves_the_failure_standing() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        var outcomes = new Queue<MutationOutcome>([new MutationOutcome.Succeeded(), new MutationOutcome.AttentionSkew("ownership_mismatch")]);
        h.Lane.Behavior = (_, _) => Task.FromResult(outcomes.Dequeue());

        await h.Controller.ReloadServiceAsync(CancellationToken.None);
        await h.Controller.ReloadServiceAsync(CancellationToken.None);

        await Assert.That(h.Reload()!.Token).IsEqualTo("ownership_mismatch");
    }

    [Test]
    public async Task Second_call_while_the_first_prompt_is_open_is_ignored_and_a_decline_releases_the_claim() {
        await using var h = new Harness();
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Surface.ConfirmBehavior = (_, _) => answer.Task;
        var gate = new TaskCompletionSource<MutationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Lane.Behavior = (_, _) => gate.Task;

        var first  = h.Controller.ReloadServiceAsync(CancellationToken.None);
        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the first prompt");
        var second = h.Controller.ReloadServiceAsync(CancellationToken.None);
        await second; // returns at once: no prompt, no request
        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(1);
        await Assert.That(h.Reloading()).IsTrue();

        answer.SetResult(true);
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the single lane request");
        var third = h.Controller.ReloadServiceAsync(CancellationToken.None);
        await third;
        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(1);

        gate.SetResult(new MutationOutcome.Succeeded());
        await first;
        await Assert.That(h.Reloading()).IsFalse();

        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(false);
        await h.Controller.ReloadServiceAsync(CancellationToken.None);
        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(2);
        await Assert.That(h.Reloading()).IsFalse();
    }

    [Test]
    public async Task Reload_cancels_when_the_attach_changed_while_the_prompt_was_open() {
        await using var h = new Harness();
        h.Start();
        h.PushConnected();
        h.Surface.ConfirmBehavior = (_, _) => { h.PushConnecting(); return Task.FromResult(true); };

        await h.Controller.ReloadServiceAsync(CancellationToken.None);

        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.Surface.StatusMessages.Last()).IsEqualTo(DaemonLifecycleController.PromptStaleStatus);
        await Assert.That(h.Reloading()).IsFalse();
    }

    [Test]
    public async Task Reload_without_a_canonical_server_records_the_refusal_and_releases_the_claim() {
        await using var h = new Harness(canonicalServer: null);
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);

        await h.Controller.ReloadServiceAsync(CancellationToken.None);

        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.Reload()!.Token).IsEqualTo("no_server_configured");
        await Assert.That(h.Reload()!.Kind).IsEqualTo(ReloadOutcomeKind.Refused);
        await Assert.That(h.Reloading()).IsFalse();
    }

    // ---- resolution by passive reads ----

    [Test]
    public async Task A_positive_read_after_a_priority_class_failure_clears_it_but_leaves_other_failures() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 100) with { LoadedSpawnType = "daemon" });

        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Failed(1, "contended", RecoverySurface.Attention));
        await h.Controller.ReloadServiceAsync(CancellationToken.None); // the action's own re-read runs after the record
        await Assert.That(h.Reload()).IsNull();

        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Failed(1, "unit_missing", RecoverySurface.Attention));
        await h.Controller.ReloadServiceAsync(CancellationToken.None);
        await Assert.That(h.Reload()!.Token).IsEqualTo("unit_missing");

        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.AttentionRepair("running_without_daemon_pid"));
        await h.Controller.ReloadServiceAsync(CancellationToken.None);
        await Assert.That(h.Reload()!.Token).IsEqualTo("running_without_daemon_pid");
    }

    [Test]
    public async Task A_read_that_started_before_the_failure_was_recorded_does_not_clear_it() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        var slowRead = new TaskCompletionSource<ServiceSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        h.Cli.StatusBehavior = _ => ++reads == 1 ? slowRead.Task : Task.FromResult<ServiceSnapshot?>(null);
        h.Start();
        h.PushConnected(); // starts the slow passive read

        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Failed(1, "contended", RecoverySurface.Attention));
        await h.Controller.ReloadServiceAsync(CancellationToken.None);
        await Assert.That(h.Reload()!.Token).IsEqualTo("contended");

        slowRead.SetResult(Snap(state: "running", jobPid: 100, daemonPid: 100) with { LoadedSpawnType = "daemon" });
        await Task.Yield();
        await Assert.That(h.Reload()!.Token).IsEqualTo("contended");

        h.PushConnecting(); h.PushConnected(); // a read that starts after the record
        await WaitUntilAsync(() => h.Reload() is null, what: "the later positive read clearing the failure");
    }

    [Test]
    public async Task Disclosure_names_the_count_and_what_else_ends() {
        var text = DaemonLifecycleController.ReloadDisclosure(2);
        await Assert.That(text).IsEqualTo("Reloading restarts the daemon and ends everything it hosts: 2 agents now, plus any agent, launch or evaluation running when it exits. Uncommitted work in their worktrees is lost.");
        await Assert.That(DaemonLifecycleController.ReloadDisclosure(1)).Contains("1 agent now");
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonLifecycleControllerTests/*"`
Expected: build errors for the new members.

- [ ] **Step 3: Implement**

Fields and observables on `DaemonLifecycleController`:

```csharp
    internal const string StandardPriorityStatus = "Daemon runs at standard priority.";
    internal const string PromptStaleStatus      = "The daemon changed while the prompt was open — canceled, nothing changed.";

    readonly BehaviorSubject<bool>         _backgroundPriority = new(false);
    readonly BehaviorSubject<ReloadState?> _reloadState        = new(null);
    readonly BehaviorSubject<bool>         _isReloading        = new(false);
    IDisposable? _snapshots;
    int  _latestActiveAgents;
    int  _reloadClaim;   // 0 free, 1 claimed; Interlocked only
    long _sequence;      // under _lock: both outcome records and read starts

    /// True while launchd holds the service in the background band; unchanged by an unknown word or a failed read.
    public IObservable<bool> BackgroundPriority => _backgroundPriority.DistinctUntilChanged();
    /// The last Reload outcome that still stands, or null. Replays to a late subscriber.
    public IObservable<ReloadState?> ReloadState => _reloadState;
    /// True from the claim before the prompt until the outcome is recorded and the follow-up read is done.
    public IObservable<bool> IsReloading => _isReloading.DistinctUntilChanged();

    internal static string ReloadDisclosure(int activeAgents) =>
        $"Reloading restarts the daemon and ends everything it hosts: {activeAgents} agent{(activeAgents == 1 ? "" : "s")} now, " +
        "plus any agent, launch or evaluation running when it exits. Uncommitted work in their worktrees is lost.";
```

Add `using System.Reactive.Linq;` and `using System.Reactive.Subjects;`.

In `Start()`, after the attach subscription:

```csharp
        _snapshots = _client.Snapshots.Subscribe(snap => _latestActiveAgents = snap.Daemon.ActiveAgents);
```

In `OnAttachStatus`, after `if (!isFirstTerminalOutcome) return;` becomes:

```csharp
        if (!isFirstTerminalOutcome) {
            // A later Connected is a daemon that came up after the arm, or came back after a reload
            // outside the app: read its spawn type, arm nothing.
            if (status.State == AttachState.Connected) _ = PassiveReadAsync();
            return;
        }
```

Status reads go through one method that stamps the read and feeds the indicator:

```csharp
    /// Every service status read: stamped before it starts so a positive result can be ordered against a
    /// failure recorded while it was in flight, then fed to the indicator and the reload state.
    async Task<ServiceSnapshot?> ReadStatusAsync(CancellationToken ct) {
        long readStart;
        lock (_lock) readStart = ++_sequence;
        var snap = await _cli.ServiceStatusAsync(ct).ConfigureAwait(false);
        NoteSnapshot(snap, readStart);
        return snap;
    }

    async Task PassiveReadAsync() {
        try { await ReadStatusAsync(_lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex) { Console.Error.WriteLine($"kcap: daemon lifecycle passive status read failed: {ex.Message}"); }
    }

    void NoteSnapshot(ServiceSnapshot? snap, long readStart) {
        if (snap is null) return;
        bool? background = null;
        var clear = false;
        lock (_lock) {
            switch (SpawnTypes.Classify(snap.LoadedSpawnType)) {
                case SpawnTypeReading.BackgroundBand:
                    background = true;
                    break;
                case SpawnTypeReading.Positive:
                    background = false;
                    if (_reloadState.Value is { } standing && ReloadCopy.ResolvedByPositivePriority(standing.Token) && standing.Sequence < readStart)
                        clear = true;
                    break;
            }
        }
        if (background is { } b) _backgroundPriority.OnNext(b);
        if (clear) _reloadState.OnNext(null);
    }
```

Change `QueryStatusAsync` to call `ReadStatusAsync(ct)` instead of `_cli.ServiceStatusAsync(ct)`, and `QueryForStartupBranchAsync` likewise for both of its reads.

The action:

```csharp
    /// The rail's Reload button. One operation at a time, claimed before the prompt: the confirmation
    /// surface queues dialogs and releases its gate when one is answered, so a claim taken on acceptance
    /// would let a second queued prompt be accepted while the first reload is still running.
    public async Task ReloadServiceAsync(CancellationToken ct) {
        if (Interlocked.CompareExchange(ref _reloadClaim, 1, 0) != 0) return;
        _isReloading.OnNext(true);
        try {
            if (RequireAppRestart()) return;
            var gen0   = CurrentGeneration();
            var prompt = new LifecyclePrompt(LifecyclePrompt.KindReloadService, null, CliVersion, false, ReloadDisclosure(_latestActiveAgents));
            var accepted = await _surface.ConfirmAsync(prompt, ct).ConfigureAwait(false);
            if (!accepted || RequireAppRestart()) return;
            if (CurrentGeneration() != gen0) {
                _surface.Status(PromptStaleStatus);
                return;
            }

            var profileName = await _resolveProfileName().ConfigureAwait(false);
            var refusal = MutationRequestFactory.TryBuild(MutationVerb.Reload, profileName, _canonicalServer, _client.DaemonName, out var request);
            var outcome = refusal ?? await _runMutation(request!, ct).ConfigureAwait(false);
            Record(outcome);
            if (refusal is null) _ = _client.RestartLoopAsync(); // the mutation may have restarted the daemon; reattach is idempotent
            if (outcome is MutationOutcome.Succeeded or MutationOutcome.SucceededAfterTimeout) _surface.Status(StandardPriorityStatus);

            try { await ReadStatusAsync(ct).ConfigureAwait(false); } // the indicator follows evidence, not the click
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        } finally {
            _isReloading.OnNext(false);
            Volatile.Write(ref _reloadClaim, 0);
        }
    }

    void Record(MutationOutcome outcome) {
        ReloadState? state;
        lock (_lock) state = Services.ReloadState.From(outcome, _client.DaemonName, ++_sequence);
        _reloadState.OnNext(state);
    }
```

`Services.ReloadState.From` is written with the namespace prefix because the class has a property named `ReloadState`.

In `DisposeAsync`, dispose `_snapshots` beside `_subscription`.

- [ ] **Step 4: Run the tests**

Run: `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonLifecycleControllerTests/*"`
Expected: PASS, every pre-existing case included. If a pre-existing case counts `StatusCallCount` exactly and now sees one more read after a `PushConnected`, that case pushed Connected after the arm; raise its expected count by one and say why in the assertion's `what`.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Services/DaemonLifecycleController.cs test/Capacitor.App.Tests.Unit/DaemonLifecycleControllerTests.cs
git commit -m "Let the lifecycle controller offer and record a consented daemon reload (#1351)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 12: The presenter leaves Reload outcomes to the controller

**Files:**
- Modify: `src/Capacitor.App/App.axaml.cs` (`PresentOutcomeAsync`)
- Test: `test/Capacitor.App.Tests.Unit/AppMutationLaneWiringTests.cs`

**Interfaces:**
- Consumes: `MutationVerb.Reload` (Task 9).

- [ ] **Step 1: Write the failing test**

Add to `AppMutationLaneWiringTests.cs`, following the shape of the existing `PresentOutcomeAsync` calls there (same `surface`, `runMutation`, `terminalPathAsync`, `cliVersion` fakes):

```csharp
    [Test]
    public async Task Reload_outcomes_are_acknowledged_without_posting_to_the_attention_lane() {
        var surface = new FakeLifecycleSurface();
        var request = new MutationRequest(MutationVerb.Reload, "default", "https://cap.example.test", "daemon-a");
        var presented = 0;

        foreach (MutationOutcome outcome in new MutationOutcome[] {
                new MutationOutcome.Failed(1, "unit_missing", RecoverySurface.Attention),
                new MutationOutcome.AttentionSkew("background_band"),
                new MutationOutcome.Refused("reload_unsupported", RecoverySurface.Attention),
                new MutationOutcome.UnconfirmedNoAttach() }) {
            await AppUnderTest.PresentOutcomeAsync(
                surface, new OutcomeEnvelope(request, outcome), (_, _) => throw new InvalidOperationException("no re-mutation"),
                _ => Task.FromResult<string?>("/usr/bin"), () => "1.0.0", CancellationToken.None, markPresented: () => presented++);
        }

        await Assert.That(surface.AttentionMessages).IsEmpty();
        await Assert.That(surface.StatusMessages).IsEmpty();
        await Assert.That(surface.Prompts).IsEmpty();
        await Assert.That(presented).IsEqualTo(4);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/AppMutationLaneWiringTests/*"`
Expected: FAIL — the `unit_missing` and `background_band` envelopes are logged only today, but `UnconfirmedNoAttach` posts an attention line.

- [ ] **Step 3: Implement**

At the top of `PresentOutcomeAsync`, before the retire check:

```csharp
        // The controller that started a reload awaits its outcome and renders it as state in the rail;
        // posting it here too would put a withdrawable condition on a lane nothing can withdraw from.
        if (envelope.Request.Verb == MutationVerb.Reload) {
            var (_, reloadToken) = ClassifyForPresentation(envelope.Outcome);
            Console.Error.WriteLine($"kcap: daemon reload outcome {envelope.Outcome.GetType().Name} ({reloadToken ?? "-"}) is presented by the lifecycle controller");
            markPresented?.Invoke();
            return;
        }
```

- [ ] **Step 4: Run the tests**

Run: `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/AppMutationLaneWiringTests/*"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/App.axaml.cs test/Capacitor.App.Tests.Unit/AppMutationLaneWiringTests.cs
git commit -m "Keep reload outcomes off the shared attention lane (#1351)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 13: Rail block, view model properties and composition wiring

**Files:**
- Modify: `src/Capacitor.App/ViewModels/MainWindowViewModel.cs` (constructor parameters, properties, `WhenActivated` wiring beside `RestartPending`)
- Modify: `src/Capacitor.App/Views/SessionRailView.axaml` (footer block)
- Modify: `src/Capacitor.App/App.axaml.cs` (`BuildAndShowMainWindow` signature and the call that builds the window near line 621)
- Test: `test/Capacitor.App.Tests.Unit/MainWindowViewModelTests.cs`
- Test: `test/Capacitor.App.Tests.Unit/MainWindowSmokeTests.cs`

**Interfaces:**
- Consumes: controller observables and `ReloadServiceAsync` (Task 11), `ReloadCopy.For` (Task 10).
- Produces on `MainWindowViewModel`: `bool BackgroundPriority`, `string? BackgroundPriorityText`, `string? ReloadFailureText`, `bool IsReloading`, `bool CanReload`, `bool ShowsReloadBlock`, `ReactiveCommand<Unit, Unit> ReloadDaemonCommand`, `internal const string BackgroundPriorityMessage`. Constructor parameters `IObservable<bool>? backgroundPriority = null, IObservable<ReloadState?>? reloadState = null, IObservable<bool>? isReloading = null, Func<CancellationToken, Task>? reloadDaemon = null`.

- [ ] **Step 1: Write the failing view model tests**

Add to `MainWindowViewModelTests.cs`, beside the `RestartPending` tests (same `AvaloniaSession.WithImmediateRxScheduler` shape):

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Background_priority_shows_only_while_connected_and_the_failure_text_always() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var background = new BehaviorSubject<bool>(true);
            var reload = new BehaviorSubject<ReloadState?>(new ReloadState(ReloadOutcomeKind.Failed, "unit_missing", 1, "daemon-a", 1));
            var reloading = new BehaviorSubject<bool>(false);
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                backgroundPriority: background, reloadState: reload, isReloading: reloading, reloadDaemon: _ => Task.CompletedTask);
            using var activation = vm.Activator.Activate();

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            await Assert.That(vm.BackgroundPriority).IsFalse();
            await Assert.That(vm.BackgroundPriorityText).IsNull();
            await Assert.That(vm.CanReload).IsFalse();
            await Assert.That(vm.ReloadFailureText).IsEqualTo(ReloadCopy.For(reload.Value!));
            await Assert.That(vm.ShowsReloadBlock).IsTrue();

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            await Assert.That(vm.BackgroundPriority).IsTrue();
            await Assert.That(vm.BackgroundPriorityText).IsEqualTo(MainWindowViewModel.BackgroundPriorityMessage);
            await Assert.That(vm.CanReload).IsTrue();

            reloading.OnNext(true);
            await Assert.That(vm.IsReloading).IsTrue();
            await Assert.That(vm.CanReload).IsFalse();

            reloading.OnNext(false);
            reload.OnNext(null);
            background.OnNext(false);
            await Assert.That(vm.ReloadFailureText).IsNull();
            await Assert.That(vm.ShowsReloadBlock).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_view_model_built_while_unreachable_shows_the_replayed_failure() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            var reload = new BehaviorSubject<ReloadState?>(new ReloadState(ReloadOutcomeKind.Unconfirmed, "unconfirmed", null, "daemon-a", 1));
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System, reloadState: reload);
            using var activation = vm.Activator.Activate();
            await Assert.That(vm.ReloadFailureText).IsEqualTo(ReloadCopy.For(reload.Value!));
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Reload_command_runs_the_controller_action_only_when_it_can() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var calls = 0;
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                backgroundPriority: new BehaviorSubject<bool>(true), isReloading: new BehaviorSubject<bool>(false),
                reloadDaemon: _ => { calls++; return Task.CompletedTask; });
            using var activation = vm.Activator.Activate();

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            await vm.ReloadDaemonCommand.Execute().ToTask();
            await Assert.That(calls).IsEqualTo(1);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            var canExecute = false;
            using (vm.ReloadDaemonCommand.CanExecute.Subscribe(x => canExecute = x)) { }
            await Assert.That(canExecute).IsFalse();
        });
    }
```

- [ ] **Step 2: Write the failing smoke test**

Add to `MainWindowSmokeTests.cs`, copying the window setup of `Rail_version_warns_when_an_update_is_pending` (same `AvaloniaSession.DispatchAsync`, `FakeDaemonClientService`, `MainWindow` with the view model as `DataContext`, `window.Show()`, `Dispatcher.UIThread.RunJobs()`, `window.UpdateLayout()`, `rail = window.FindDescendantOfType<SessionRailView>()`):

```csharp
    /// The priority block carries three things: the indicator line, the Reload button, and a failure line
    /// that outlives a lowered indicator until a success clears it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rail_priority_block_binds_indicator_button_and_failure() {
        var shown = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            var background = new BehaviorSubject<bool>(false);
            var reload = new BehaviorSubject<ReloadState?>(new ReloadState(ReloadOutcomeKind.Failed, "unit_missing", 1, "daemon-a", 1));
            var reloading = new BehaviorSubject<bool>(false);
            service.SnapshotsSubject.OnNext(Snap(daemon: "daemon-a", version: "1.1.0", serverUrl: "http://localhost:9999", connection: "connected", active: 2, max: 5));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                tenantName: "kurrent", backgroundPriority: background, reloadState: reload, isReloading: reloading,
                reloadDaemon: _ => Task.CompletedTask);
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var rail    = window.FindDescendantOfType<SessionRailView>()!;
            var block   = rail.FindControl<Border>("RailPriorityBlock")!;
            var line    = rail.FindControl<TextBlock>("RailPriorityText")!;
            var button  = rail.FindControl<Button>("RailReloadButton")!;
            var failure = rail.FindControl<TextBlock>("RailReloadFailureText")!;

            var withFailureOnly = (Block: block.IsVisible, Line: line.IsVisible, Button: button.IsEnabled, Failure: failure.IsVisible, FailureText: failure.Text,
                Warning: ReferenceEquals(failure.Foreground, window.FindResource("KcapWarningBrush")));

            background.OnNext(true);
            reloading.OnNext(true);
            Dispatcher.UIThread.RunJobs();
            var whileReloading = (Line: line.IsVisible, Button: button.IsEnabled, LineText: line.Text);

            reloading.OnNext(false);
            reload.OnNext(null);
            background.OnNext(false);
            Dispatcher.UIThread.RunJobs();
            var cleared = block.IsVisible;

            window.Close();
            Dispatcher.UIThread.RunJobs();
            return (withFailureOnly, whileReloading, cleared);
        });

        await Assert.That(shown.withFailureOnly.Block).IsTrue();
        await Assert.That(shown.withFailureOnly.Line).IsFalse();
        await Assert.That(shown.withFailureOnly.Button).IsTrue();
        await Assert.That(shown.withFailureOnly.Failure).IsTrue();
        await Assert.That(shown.withFailureOnly.FailureText).Contains("No service unit is installed for daemon-a");
        await Assert.That(shown.withFailureOnly.Warning).IsTrue();
        await Assert.That(shown.whileReloading.Line).IsTrue();
        await Assert.That(shown.whileReloading.LineText).IsEqualTo(MainWindowViewModel.BackgroundPriorityMessage);
        await Assert.That(shown.whileReloading.Button).IsFalse();
        await Assert.That(shown.cleared).IsFalse();
    }
```

- [ ] **Step 3: Run to verify failure**

Run: `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/MainWindowViewModelTests/*"`
Expected: build error, the new constructor parameters do not exist.

- [ ] **Step 4: View model**

Constructor: add the four optional parameters at the end of the parameter list, after `appMenuInWindow`:

```csharp
            bool? appMenuInWindow = null,
            IObservable<bool>? backgroundPriority = null, IObservable<ReloadState?>? reloadState = null,
            IObservable<bool>? isReloading = null, Func<CancellationToken, Task>? reloadDaemon = null) {
```

Members, beside `RestartPending`:

```csharp
    internal const string BackgroundPriorityMessage = "Daemon runs at background priority — agent terminals lag under load.";

    ObservableAsPropertyHelper<bool>? _backgroundPriority;
    /// The attached daemon runs in launchd's background band. Connected only: it describes the daemon on screen.
    public bool BackgroundPriority => _backgroundPriority?.Value ?? false;

    ObservableAsPropertyHelper<string?>? _backgroundPriorityText;
    public string? BackgroundPriorityText => _backgroundPriorityText?.Value;

    ObservableAsPropertyHelper<string?>? _reloadFailureText;
    /// The standing reload outcome's line. Not gated on the attach: a reload that left the daemon
    /// unreachable is exactly the outcome this line exists to show.
    public string? ReloadFailureText => _reloadFailureText?.Value;

    ObservableAsPropertyHelper<bool>? _isReloading;
    public bool IsReloading => _isReloading?.Value ?? false;

    ObservableAsPropertyHelper<bool>? _canReload;
    /// Connected and no reload in flight: the reload needs a daemon to ask.
    public bool CanReload => _canReload?.Value ?? false;

    ObservableAsPropertyHelper<bool>? _showsReloadBlock;
    public bool ShowsReloadBlock => _showsReloadBlock?.Value ?? false;

    public ReactiveCommand<Unit, Unit> ReloadDaemonCommand { get; }
```

In the constructor body, before `this.WhenActivated(...)`:

```csharp
        var reloadAction = reloadDaemon ?? (_ => Task.CompletedTask);
        ReloadDaemonCommand = ReactiveCommand.CreateFromTask(
            (CancellationToken ct) => reloadAction(ct),
            this.WhenAnyValue(x => x.CanReload).ObserveOn(RxSchedulers.MainThreadScheduler));
```

Inside the `WhenActivated` block, right after the `_restartPendingText` wiring:

```csharp
            var backgroundSource = (backgroundPriority ?? Observable.Return(false)).ObserveOn(RxSchedulers.MainThreadScheduler);
            var reloadingSource  = (isReloading ?? Observable.Return(false)).ObserveOn(RxSchedulers.MainThreadScheduler);
            var reloadSource     = (reloadState ?? Observable.Return<ReloadState?>(null)).ObserveOn(RxSchedulers.MainThreadScheduler);

            var backgroundWhileConnected = status
                .CombineLatest(backgroundSource, (st, bg) => bg && st.State == AttachState.Connected)
                .DistinctUntilChanged();
            _backgroundPriority = backgroundWhileConnected
                .ToProperty(this, x => x.BackgroundPriority, false)
                .DisposeWith(disposables);
            _backgroundPriorityText = backgroundWhileConnected.Select(b => b ? BackgroundPriorityMessage : null)
                .ToProperty(this, x => x.BackgroundPriorityText, (string?)null)
                .DisposeWith(disposables);

            _isReloading = reloadingSource
                .ToProperty(this, x => x.IsReloading, false)
                .DisposeWith(disposables);
            _canReload = status
                .CombineLatest(reloadingSource, (st, reloading) => st.State == AttachState.Connected && !reloading)
                .DistinctUntilChanged()
                .ToProperty(this, x => x.CanReload, false)
                .DisposeWith(disposables);

            var failureText = reloadSource.Select(state => state is null ? null : ReloadCopy.For(state));
            _reloadFailureText = failureText
                .ToProperty(this, x => x.ReloadFailureText, (string?)null)
                .DisposeWith(disposables);
            _showsReloadBlock = backgroundWhileConnected
                .CombineLatest(failureText, (bg, text) => bg || text is not null)
                .DistinctUntilChanged()
                .ToProperty(this, x => x.ShowsReloadBlock, false)
                .DisposeWith(disposables);
```

Add `using Capacitor.App.Services;` if the file lacks it (it already has it for `AttachStatus`).

- [ ] **Step 5: Rail markup**

In `SessionRailView.axaml`, inside the footer `StackPanel` that ends with the `DockPanel` holding `RailDaemonRow` (the one closed right before `</Border>` near line 253), insert as that `StackPanel`'s first child:

```xml
                    <!-- A daemon launchd holds in the background band throttles every hosted agent; the
                         block offers the reload and keeps its last outcome readable until a success clears it. -->
                    <Border x:Name="RailPriorityBlock" Classes="backgroundPriority" Margin="0,0,0,8" Padding="10,8"
                            CornerRadius="6" Background="{StaticResource KcapWarningDimBrush}"
                            IsVisible="{Binding ShowsReloadBlock}">
                        <StackPanel Spacing="6">
                            <TextBlock x:Name="RailPriorityText" Text="{Binding BackgroundPriorityText}" TextWrapping="Wrap"
                                       FontSize="12" LineHeight="17" Foreground="{StaticResource KcapWarningBrush}"
                                       IsVisible="{Binding BackgroundPriority}" />
                            <Button x:Name="RailReloadButton" Classes="kcapGhost" Content="Reload" Padding="10,4" FontSize="12.5"
                                    HorizontalAlignment="Left"
                                    Command="{Binding ReloadDaemonCommand}" IsEnabled="{Binding CanReload}" />
                            <TextBlock x:Name="RailReloadFailureText" Text="{Binding ReloadFailureText}" TextWrapping="Wrap"
                                       FontSize="12" LineHeight="17" Foreground="{StaticResource KcapWarningBrush}"
                                       IsVisible="{Binding ReloadFailureText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
                        </StackPanel>
                    </Border>
```

Warning tone is correct here: both lines are needs-you status, not identity.

- [ ] **Step 6: Composition root**

`BuildAndShowMainWindow` gains four optional parameters after `settingsAction`:

```csharp
            Action<FeedbackCategory>? openFeedback = null, IObservable<Action?>? settingsAction = null,
            IObservable<bool>? backgroundPriority = null, IObservable<ReloadState?>? reloadState = null,
            IObservable<bool>? isReloading = null, Func<CancellationToken, Task>? reloadDaemon = null) {
```

and passes them into the `new MainWindowViewModel(...)` call: `backgroundPriority: backgroundPriority, reloadState: reloadState, isReloading: isReloading, reloadDaemon: reloadDaemon`.

The call near line 621 adds, after `settingsAction: _appMenu.SettingsAction`:

```csharp
                backgroundPriority: lifecycle.BackgroundPriority, reloadState: lifecycle.ReloadState,
                isReloading: lifecycle.IsReloading, reloadDaemon: lifecycle.ReloadServiceAsync
```

- [ ] **Step 7: Run the tests**

Run: `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/MainWindowViewModelTests/*"`
Run: `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/MainWindowSmokeTests/*"`
Expected: PASS. Then build the whole app project and clear any Avalonia `AVLN` XAML warning: `dotnet build src/Capacitor.App/Capacitor.App.csproj --no-incremental 2>&1 | grep -E "AVLN|warning" ` must print nothing new.

- [ ] **Step 8: Commit**

```bash
git add src/Capacitor.App/ViewModels/MainWindowViewModel.cs src/Capacitor.App/Views/SessionRailView.axaml src/Capacitor.App/App.axaml.cs test/Capacitor.App.Tests.Unit/MainWindowViewModelTests.cs test/Capacitor.App.Tests.Unit/MainWindowSmokeTests.cs
git commit -m "Show the background-priority block with a Reload button in the rail footer (#1351)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 14: Whole-solution check and live verification

**Files:** none new.

- [ ] **Step 1: Build and test everything**

Run: `dotnet build Capacitor.slnx`
Run: `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 dotnet test --solution Capacitor.slnx`
Expected: build succeeds; every suite green. A daemon-suite timing test that fails alone is environmental (re-run it by itself once); anything else is a regression from this plan.

- [ ] **Step 2: AOT publish**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: nothing printed.

- [ ] **Step 3: Live check on a Mac whose daemon is loaded as Adaptive**

The diagnosing machine is one: its plist is already Standard on disk while launchd still holds Adaptive.

1. Run the published `kcap daemon status` against that daemon; expect the `priority: loaded as adaptive` line naming the daemon.
2. Launch the dev app (via a `.app` wrapper and `open`, never from a background shell). The rail footer shows the background-priority block with Reload enabled.
3. Click Reload while agents run: the prompt names their count and the loss. Decline; nothing changes.
4. At a quiet moment, click Reload and accept. Within the 10 s window the daemon reconnects; the status lane says "Daemon runs at standard priority."; the block disappears.
5. Confirm from a terminal: `launchctl print gui/$(id -u)/io.kurrent.kcap.daemon.<name> | grep "spawn type"` reads `daemon (3)`; `ps -o pid,pri -p $(pgrep kcap-daemon)` reads priority 20, not 4; the daemon pid changed.
6. Run `kcap daemon service refresh --name <name> --force` again from the terminal: stderr `refresh_outcome=current`, exit 0.

Record the observed numbers in the PR description's Verification section.

- [ ] **Step 4: Open the pull requests**

Part A (Tasks 1–7) and Part B (Tasks 8–13) can go as two PRs on this branch's history or as one; either way the description follows `.github/PULL_REQUEST_TEMPLATE.md`, references `Closes #1351` on its reference line, and carries the live numbers from Step 3. The README change lands with Part A.
