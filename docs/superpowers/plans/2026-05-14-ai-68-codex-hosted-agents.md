# AI-68 — Hosted Codex agents on macOS and Linux (CLI/daemon repo) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `vendor: codex` support to the daemon's hosted-agent path so the dashboard can launch a Codex CLI under PTY supervision with the same overlay/pre-trust/permission-bridge guarantees Claude already has on macOS and Linux.

**Architecture:** Extract a vendor-neutral `IHostedAgentLauncher` strategy out of `AgentOrchestrator` (`ClaudeLauncher` first, behavior-preserving), then add `CodexLauncher` alongside. `LocalPermissionBridge` splits its URL space by `{vendor}` segment. Codex pre-trust mutates `~/.codex/config.toml` via Tomlyn. The CLI Codex hook bounces `PermissionRequest` through the daemon when `KAPACITOR_DAEMON_URL` is set, fail-closed on any error.

**Tech Stack:** .NET 10 NativeAOT, SignalR client, TUnit, WireMock.Net, Tomlyn (new dep for TOML round-trip), `System.Text.Json.Nodes`.

**Spec:** `docs/superpowers/specs/2026-05-14-ai-68-codex-hosted-agents-design.md` (commit `b549d30`)

**Scope:** This plan covers §10.1 of the spec — the CLI/daemon repo (this worktree). §10.2 (kapacitor-server repo: new required `Vendor` on `DaemonCommands.LaunchAgentCommand`, five-arg `RequestPermission` hub method, six-arg `PermissionRequested` broadcast, `ClaudeCodePermissionExtension.Vendor`, dashboard vendor selector, vendor-aware permission UI) ships as a paired PR with its own plan in that worktree. The two PRs land together; staging is single-controlled per the spec.

**Out of scope (per spec §11):** Windows hosted Codex (AI-72), Codex PR-review launches (AI-632 — orchestrator fails fast), sandbox/approval selector in launch dialog (AI-633 — v1 hardcodes `workspace-write` + `on-request`).

---

## File structure

**Modify (CLI / daemon repo)**

- `Directory.Packages.props` — add `Tomlyn` version
- `src/Kapacitor.Daemon/Kapacitor.Daemon.csproj` — add `<PackageReference Include="Tomlyn" />`
- `src/Kapacitor.Core/Models.cs` — add `Vendor` to `LaunchAgentCommand` + `AgentRunStarted`; update `JsonSerializerContext`
- `src/Kapacitor.Core/CodexPaths.cs` — convert `Home` / `Sessions` / `UserHooksJson` from init-once to computed expression-bodied properties
- `src/Kapacitor.Daemon/DaemonConfig.cs` — add `CodexPath` field
- `src/Kapacitor.Daemon/Services/AgentOrchestrator.cs` — route through `IHostedAgentLauncher`; add `AgentInstance.Vendor`; vendor-aware cleanup (both success and failed-launch paths)
- `src/Kapacitor.Daemon/Services/LocalPermissionBridge.cs` — per-vendor URL routing; vendor-shaped response builders
- `src/Kapacitor.Daemon/Services/ServerConnection.cs` — `RequestPermissionAsync` gains required `string vendor` parameter; invokes the five-arg server hub method
- `src/kapacitor/Commands/CodexHookCommand.cs` — `HandlePermissionRequest` gains the bridge-bounce branch (fail-closed)
- `src/kapacitor/Commands/PermissionRequestCommand.cs` — post target updated to `/claude/permission-request`; loopback validation helper extracted
- `src/kapacitor/Commands/PluginCommand.cs` — delegate `EntryReferencesKapacitorCodexHook` + `CodexHookEvents` to `Kapacitor.Core/CodexHooksParser.cs`
- `src/kapacitor/Program.cs` — DI registration: `ClaudeLauncher`, `CodexLauncher`, vendor-keyed dictionary
- `README.md` — hosted Codex section, daemon config, codex-hook daemon-bridge note
- `src/Kapacitor.Core/Resources/help-codex-hook.txt` — daemon-bridge behaviour
- `test/kapacitor.Tests.Unit/LaunchAgentCommandWireFormatTests.cs` — extend for `Vendor`
- `test/kapacitor.Tests.Unit/LocalPermissionBridgeTests.cs` — extend for per-vendor URL + Claude regression
- `test/kapacitor.Tests.Unit/CodexHookCommandTests.cs` — extend for bridge-bounce branch
- `test/kapacitor.Tests.Unit/PluginCommandCodexTests.cs` — re-target predicate to `CodexHooksParser`

**Create (CLI / daemon repo)**

- `src/Kapacitor.Core/CodexHooksParser.cs` — shared `~/.codex/hooks.json` parser (predicate + event list + `HasKapacitorHooksFor` helper)
- `src/Kapacitor.Daemon/Services/IHostedAgentLauncher.cs` — interface + `LauncherContext` + `LaunchArgs`
- `src/Kapacitor.Daemon/Services/ClaudeLauncher.cs` — extracted Claude impl (behavior-preserving)
- `src/Kapacitor.Daemon/Services/CodexLauncher.cs` — new Codex impl
- `src/Kapacitor.Daemon/Services/CodexConfigWriter.cs` — Tomlyn-based pre-trust writer
- `src/Kapacitor.Daemon/Services/CodexHooksNotInstalledException.cs` — typed preflight exception
- `src/kapacitor/Commands/DaemonBridgeUrl.cs` — shared loopback-validation helper
- `test/kapacitor.Tests.Unit/CodexHooksParserTests.cs`
- `test/kapacitor.Tests.Unit/CodexConfigWriterTests.cs`
- `test/kapacitor.Tests.Unit/AgentOrchestratorVendorTests.cs`
- `test/kapacitor.Tests.Unit/CodexLauncherTests.cs`
- `test/kapacitor.Tests.Unit/DaemonBridgeUrlTests.cs`

---

## Tasks

### Task 1: Add Tomlyn dependency and verify AOT baseline

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/Kapacitor.Daemon/Kapacitor.Daemon.csproj`

- [ ] **Step 1: Check current Tomlyn latest stable version**

```bash
dotnet package search Tomlyn --exact-match
```

Note the latest stable version (likely `0.20.x` at time of writing). Use it in the next step.

- [ ] **Step 2: Add Tomlyn to central package management**

Add a `<PackageVersion>` line alphabetically in the `<ItemGroup>` of `Directory.Packages.props`:

```xml
<PackageVersion Include="Tomlyn" Version="0.20.0" />
```

(Substitute the version returned by step 1.)

- [ ] **Step 3: Reference Tomlyn from the daemon project**

In `src/Kapacitor.Daemon/Kapacitor.Daemon.csproj`, find the `<ItemGroup>` containing `<ProjectReference>` and add a new `<ItemGroup>` for package references (or extend an existing one):

```xml
<ItemGroup>
    <PackageReference Include="Tomlyn" />
</ItemGroup>
```

- [ ] **Step 4: Restore packages**

```bash
dotnet restore src/Kapacitor.Daemon/Kapacitor.Daemon.csproj
```

Expected: succeeds; the new package shows in the lock file.

- [ ] **Step 5: AOT publish baseline (both projects)**

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | tee /tmp/aot-cli.log
dotnet publish src/Kapacitor.Daemon/Kapacitor.Daemon.csproj -c Release 2>&1 | tee /tmp/aot-daemon.log
grep -E 'IL[23][01][0-9]{2}' /tmp/aot-cli.log /tmp/aot-daemon.log
```

Expected: `grep` returns no matches.

If matches appear: the design's §5.3 fallback (typed model via `[TomlSerializable]` source-gen context) is the resolution path. Do not proceed until clean.

- [ ] **Step 6: Commit**

```bash
git add Directory.Packages.props src/Kapacitor.Daemon/Kapacitor.Daemon.csproj
git commit -m "[AI-68] deps: add Tomlyn for Codex config.toml pre-trust"
```

---

### Task 2: Extract `CodexHooksParser` to Kapacitor.Core

The predicate `EntryReferencesKapacitorCodexHook` lives today on `PluginCommand` in the CLI project. The daemon's `CodexLauncher.Prepare` preflight (Task 18) needs it, but `Kapacitor.Daemon` only references `Kapacitor.Core`. Move the parsing/predicate logic into Core; keep file-write logic in `PluginCommand`.

**Files:**
- Create: `src/Kapacitor.Core/CodexHooksParser.cs`
- Create: `test/kapacitor.Tests.Unit/CodexHooksParserTests.cs`
- Modify: `src/kapacitor/Commands/PluginCommand.cs`
- Modify: `test/kapacitor.Tests.Unit/PluginCommandCodexTests.cs`

- [ ] **Step 1: Write failing tests for the parser**

Create `test/kapacitor.Tests.Unit/CodexHooksParserTests.cs`:

```csharp
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace kapacitor.Tests.Unit;

public class CodexHooksParserTests {
    [Test]
    public async Task EntryReferencesKapacitorCodexHook_returns_true_when_command_contains_marker() {
        var entry = JsonNode.Parse("""{"hooks":[{"type":"command","command":"kapacitor codex-hook","timeout":30}]}""");
        await Assert.That(CodexHooksParser.EntryReferencesKapacitorCodexHook(entry)).IsTrue();
    }

    [Test]
    public async Task EntryReferencesKapacitorCodexHook_returns_false_when_command_does_not_match() {
        var entry = JsonNode.Parse("""{"hooks":[{"type":"command","command":"echo hi","timeout":30}]}""");
        await Assert.That(CodexHooksParser.EntryReferencesKapacitorCodexHook(entry)).IsFalse();
    }

    [Test]
    public async Task EntryReferencesKapacitorCodexHook_returns_false_for_null() {
        await Assert.That(CodexHooksParser.EntryReferencesKapacitorCodexHook(null)).IsFalse();
    }

    [Test]
    public async Task HasKapacitorHooksFor_returns_true_when_all_events_have_kapacitor_entry() {
        var root = JsonNode.Parse("""
            {"hooks":{
                "SessionStart":[{"hooks":[{"type":"command","command":"kapacitor codex-hook"}]}],
                "Stop":[{"hooks":[{"type":"command","command":"kapacitor codex-hook"}]}],
                "PermissionRequest":[{"hooks":[{"type":"command","command":"kapacitor codex-hook"}]}]
            }}
        """) as JsonObject;
        var events = new[] { "SessionStart", "Stop", "PermissionRequest" };
        await Assert.That(CodexHooksParser.HasKapacitorHooksFor(root!, events)).IsTrue();
    }

    [Test]
    public async Task HasKapacitorHooksFor_returns_false_when_one_event_missing() {
        var root = JsonNode.Parse("""
            {"hooks":{
                "SessionStart":[{"hooks":[{"type":"command","command":"kapacitor codex-hook"}]}],
                "Stop":[{"hooks":[{"type":"command","command":"echo something else"}]}]
            }}
        """) as JsonObject;
        var events = new[] { "SessionStart", "Stop", "PermissionRequest" };
        await Assert.That(CodexHooksParser.HasKapacitorHooksFor(root!, events)).IsFalse();
    }

    [Test]
    public async Task CodexHookEvents_lists_all_six_events_in_canonical_order() {
        await Assert.That(CodexHooksParser.CodexHookEvents).IsEquivalentTo(
            new[] { "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest", "Stop" }
        );
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexHooksParserTests/*"
```

Expected: all five tests fail with "CodexHooksParser does not exist".

- [ ] **Step 3: Implement `CodexHooksParser`**

Create `src/Kapacitor.Core/CodexHooksParser.cs`:

```csharp
using System.Text.Json.Nodes;

namespace kapacitor;

/// <summary>
/// Parsing helpers for <c>~/.codex/hooks.json</c> (and <c>&lt;repo&gt;/.codex/hooks.json</c>
/// in project-scope installs). Shared by the CLI's <c>plugin install --codex</c>
/// command (which writes the file) and the daemon's <c>CodexLauncher</c> preflight
/// (which reads it before spawning a hosted Codex agent).
/// </summary>
public static class CodexHooksParser {
    /// <summary>Hook event names Codex CLI emits.</summary>
    public static readonly string[] CodexHookEvents = [
        "SessionStart",
        "UserPromptSubmit",
        "PreToolUse",
        "PostToolUse",
        "PermissionRequest",
        "Stop"
    ];

    /// <summary>
    /// Returns true if <paramref name="entry"/> is a hooks.json group whose
    /// <c>hooks[].command</c> contains <c>kapacitor codex-hook</c>.
    /// </summary>
    public static bool EntryReferencesKapacitorCodexHook(JsonNode? entry) {
        if (entry?["hooks"] is not JsonArray hooks) return false;

        foreach (var hook in hooks) {
            if (hook?["command"] is JsonValue jv &&
                jv.TryGetValue<string>(out var cmd) &&
                cmd.Contains("kapacitor codex-hook")) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if every event in <paramref name="events"/> has at least one
    /// hooks.json entry that invokes <c>kapacitor codex-hook</c>.
    /// </summary>
    public static bool HasKapacitorHooksFor(JsonObject root, IEnumerable<string> events) {
        if (root["hooks"] is not JsonObject hooks) return false;

        foreach (var evt in events) {
            if (hooks[evt] is not JsonArray entries) return false;

            var any = false;
            foreach (var entry in entries) {
                if (EntryReferencesKapacitorCodexHook(entry)) {
                    any = true;
                    break;
                }
            }

            if (!any) return false;
        }

        return true;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexHooksParserTests/*"
```

Expected: all five pass.

- [ ] **Step 5: Refactor `PluginCommand` to delegate to the parser**

In `src/kapacitor/Commands/PluginCommand.cs`:

Replace the local `CodexHookEvents` static array and the `EntryReferencesKapacitorCodexHook` method with delegations:

```csharp
// At top of file, replace the local CodexHookEvents declaration
static readonly string[] CodexHookEvents = CodexHooksParser.CodexHookEvents;
```

Delete the local `EntryReferencesKapacitorCodexHook` method body and replace all call sites with `CodexHooksParser.EntryReferencesKapacitorCodexHook(...)`. The `PluginCommand.cs:286` definition is removed.

- [ ] **Step 6: Update `PluginCommandCodexTests`**

In `test/kapacitor.Tests.Unit/PluginCommandCodexTests.cs`, replace any test that directly invokes the moved predicate (`PluginCommand.EntryReferencesKapacitorCodexHook`) with `CodexHooksParser.EntryReferencesKapacitorCodexHook`. Keep tests covering install/remove file-write behavior in this file unchanged.

- [ ] **Step 7: Run the full unit test suite**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: all tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/Kapacitor.Core/CodexHooksParser.cs \
        test/kapacitor.Tests.Unit/CodexHooksParserTests.cs \
        src/kapacitor/Commands/PluginCommand.cs \
        test/kapacitor.Tests.Unit/PluginCommandCodexTests.cs
git commit -m "[AI-68] core: extract CodexHooksParser for daemon-side preflight reuse"
```

---

### Task 3: Convert `CodexPaths` properties from init-once to computed

`CodexPaths.Home` / `Sessions` / `UserHooksJson` today are initialised once via field-initializer, which caches the resolved path forever. Tests that scope `HOME` after any caller initialises `CodexPaths` silently hit the real `~/.codex`. Switch to expression-bodied properties so `PathHelpers.HomeDirectory` is re-read each access.

**Files:**
- Modify: `src/Kapacitor.Core/CodexPaths.cs`
- Create: `test/kapacitor.Tests.Unit/CodexPathsHomeIsolationTests.cs`

- [ ] **Step 1: Write failing isolation test**

Create `test/kapacitor.Tests.Unit/CodexPathsHomeIsolationTests.cs`:

```csharp
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace kapacitor.Tests.Unit;

public class CodexPathsHomeIsolationTests {
    [Test]
    public async Task Home_reflects_current_HOME_env_var() {
        var tmp = Directory.CreateTempSubdirectory("kapacitor-codexpaths-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");

        try {
            Environment.SetEnvironmentVariable("HOME", tmp.FullName);
            // CodexPaths.Home must re-read HOME on every access, not cache the
            // value from first initialisation.
            await Assert.That(CodexPaths.Home).IsEqualTo(Path.Combine(tmp.FullName, ".codex"));
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Sessions_reflects_current_HOME_env_var() {
        var tmp = Directory.CreateTempSubdirectory("kapacitor-codexpaths-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");

        try {
            Environment.SetEnvironmentVariable("HOME", tmp.FullName);
            await Assert.That(CodexPaths.Sessions).IsEqualTo(Path.Combine(tmp.FullName, ".codex", "sessions"));
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task UserHooksJson_reflects_current_HOME_env_var() {
        var tmp = Directory.CreateTempSubdirectory("kapacitor-codexpaths-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");

        try {
            Environment.SetEnvironmentVariable("HOME", tmp.FullName);
            await Assert.That(CodexPaths.UserHooksJson).IsEqualTo(Path.Combine(tmp.FullName, ".codex", "hooks.json"));
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            tmp.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexPathsHomeIsolationTests/*"
```

Expected: tests may pass or fail depending on whether `PathHelpers.HomeDirectory` itself caches. If they pass on the init-once form, that's because the static initialisers run on first touch and `HOME` happens to match. Force the failure by touching `CodexPaths.Home` once at startup before changing `HOME`:

If the tests pass already, add to each `try` block, before `SetEnvironmentVariable`:
```csharp
_ = CodexPaths.Home; // force static init under the original HOME
```

Re-run. Expected: now they fail because `CodexPaths.Home` retains the original HOME's resolution.

- [ ] **Step 3: Convert properties to computed**

In `src/Kapacitor.Core/CodexPaths.cs`, replace the init-once declarations:

```csharp
public static string Home          { get; } = Path.Combine(PathHelpers.HomeDirectory, ".codex");
public static string Sessions      { get; } = Path.Combine(Path.Combine(PathHelpers.HomeDirectory, ".codex"), "sessions");
public static string UserHooksJson { get; } = Path.Combine(Path.Combine(PathHelpers.HomeDirectory, ".codex"), "hooks.json");
```

With expression-bodied properties:

```csharp
public static string Home          => Path.Combine(PathHelpers.HomeDirectory, ".codex");
public static string Sessions      => Path.Combine(Home, "sessions");
public static string UserHooksJson => Path.Combine(Home, "hooks.json");
```

(Also simplifies the nested `Path.Combine` calls by reusing `Home`.)

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexPathsHomeIsolationTests/*"
```

Expected: all three pass.

- [ ] **Step 5: Run the full unit test suite to confirm no regressions**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Kapacitor.Core/CodexPaths.cs test/kapacitor.Tests.Unit/CodexPathsHomeIsolationTests.cs
git commit -m "[AI-68] core: make CodexPaths computed so tests can scope HOME"
```

---

### Task 4: Extract `DaemonBridgeUrl` loopback-validation helper

`PermissionRequestCommand.cs:70` today contains an inline check that `KAPACITOR_DAEMON_URL` is `http://127.0.0.1:...`. The Codex bridge bounce (Task 22) needs the same validation. Extract into a shared static helper in the CLI project.

**Files:**
- Create: `src/kapacitor/Commands/DaemonBridgeUrl.cs`
- Create: `test/kapacitor.Tests.Unit/DaemonBridgeUrlTests.cs`
- Modify: `src/kapacitor/Commands/PermissionRequestCommand.cs`

- [ ] **Step 1: Write failing tests for the helper**

Create `test/kapacitor.Tests.Unit/DaemonBridgeUrlTests.cs`:

```csharp
using kapacitor.Commands;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace kapacitor.Tests.Unit;

public class DaemonBridgeUrlTests {
    [Test]
    public async Task TryParseLoopback_accepts_http_127_0_0_1() {
        var ok = DaemonBridgeUrl.TryParseLoopback("http://127.0.0.1:54321/abc123", out var baseUrl);
        await Assert.That(ok).IsTrue();
        await Assert.That(baseUrl).IsEqualTo("http://127.0.0.1:54321/abc123");
    }

    [Test]
    public async Task TryParseLoopback_rejects_https() {
        var ok = DaemonBridgeUrl.TryParseLoopback("https://127.0.0.1:54321/abc123", out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryParseLoopback_rejects_non_loopback_host() {
        var ok = DaemonBridgeUrl.TryParseLoopback("http://example.com:54321/abc123", out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryParseLoopback_rejects_null() {
        var ok = DaemonBridgeUrl.TryParseLoopback(null, out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryParseLoopback_rejects_empty() {
        var ok = DaemonBridgeUrl.TryParseLoopback("", out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryParseLoopback_rejects_malformed_uri() {
        var ok = DaemonBridgeUrl.TryParseLoopback("not-a-url", out _);
        await Assert.That(ok).IsFalse();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonBridgeUrlTests/*"
```

Expected: all six tests fail with "DaemonBridgeUrl does not exist".

- [ ] **Step 3: Implement the helper**

Create `src/kapacitor/Commands/DaemonBridgeUrl.cs`:

```csharp
namespace kapacitor.Commands;

/// <summary>
/// Loopback validation for <c>KAPACITOR_DAEMON_URL</c>. Both the Claude and
/// Codex permission-request hook CLI commands must refuse to POST permission
/// payloads to anything other than an HTTP loopback URL — non-loopback or
/// HTTPS values usually indicate a misconfigured environment variable, and
/// we don't want hook payloads leaving the loopback interface.
/// </summary>
public static class DaemonBridgeUrl {
    /// <summary>
    /// True when <paramref name="daemonUrl"/> is a valid <c>http://127.0.0.1:.../...</c>
    /// URL. Returns the parsed-and-normalised form via <paramref name="baseUrl"/>
    /// (callers append <c>/{vendor}/permission-request</c> themselves).
    /// </summary>
    public static bool TryParseLoopback(string? daemonUrl, out string baseUrl) {
        baseUrl = "";

        if (string.IsNullOrWhiteSpace(daemonUrl)) return false;
        if (!Uri.TryCreate(daemonUrl, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != "http") return false;
        if (uri.Host != "127.0.0.1") return false;

        baseUrl = daemonUrl.TrimEnd('/');
        return true;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonBridgeUrlTests/*"
```

Expected: all six pass.

- [ ] **Step 5: Replace the inline check in `PermissionRequestCommand`**

In `src/kapacitor/Commands/PermissionRequestCommand.cs:70` (or wherever the inline validation lives), replace the loopback check with a call to `DaemonBridgeUrl.TryParseLoopback`. Behavior must be identical — same accept set, same reject set, same exit code on failure.

Run existing Claude permission-request tests:

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/PermissionRequestCommand*"
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/kapacitor/Commands/DaemonBridgeUrl.cs \
        test/kapacitor.Tests.Unit/DaemonBridgeUrlTests.cs \
        src/kapacitor/Commands/PermissionRequestCommand.cs
git commit -m "[AI-68] cli: extract DaemonBridgeUrl loopback helper for Codex reuse"
```

---

### Task 5: Define `CodexHooksNotInstalledException`

Typed exception for `CodexLauncher.Prepare`'s preflight (Task 18). The orchestrator catches this type specifically to emit an actionable `LaunchFailed`; other exceptions from `Prepare` are swallowed best-effort.

**Files:**
- Create: `src/Kapacitor.Daemon/Services/CodexHooksNotInstalledException.cs`

- [ ] **Step 1: Create the exception**

```csharp
namespace kapacitor.Daemon.Services;

/// <summary>
/// Thrown by <see cref="CodexLauncher"/>'s Prepare preflight when neither the
/// user-scope (<c>~/.codex/hooks.json</c>) nor project-scope
/// (<c>&lt;worktree&gt;/.codex/hooks.json</c>) hooks file has a
/// <c>kapacitor codex-hook</c> entry for SessionStart / Stop / PermissionRequest.
/// The orchestrator catches this type and emits <see cref="LaunchFailed"/> with
/// the exception's message; the user sees an actionable instruction to run
/// <c>kapacitor plugin install --codex</c>.
/// </summary>
internal sealed class CodexHooksNotInstalledException : Exception {
    public CodexHooksNotInstalledException(string message) : base(message) { }
}
```

- [ ] **Step 2: Build to verify no syntax issues**

```bash
dotnet build src/Kapacitor.Daemon/Kapacitor.Daemon.csproj
```

Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Kapacitor.Daemon/Services/CodexHooksNotInstalledException.cs
git commit -m "[AI-68] daemon: add CodexHooksNotInstalledException for preflight fail-fast"
```

---

### Task 6: Implement `CodexConfigWriter` (Tomlyn-based pre-trust)

**Files:**
- Create: `src/Kapacitor.Daemon/Services/CodexConfigWriter.cs`
- Create: `test/kapacitor.Tests.Unit/CodexConfigWriterTests.cs`

- [ ] **Step 1: Write failing tests**

Create `test/kapacitor.Tests.Unit/CodexConfigWriterTests.cs`:

```csharp
using kapacitor.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Tomlyn;
using Tomlyn.Model;

namespace kapacitor.Tests.Unit;

public class CodexConfigWriterTests {
    static DirectoryInfo NewScopedHome() {
        var tmp = Directory.CreateTempSubdirectory("kapacitor-codexconfig-test-");
        Environment.SetEnvironmentVariable("HOME", tmp.FullName);
        return tmp;
    }

    [Test]
    public async Task Writes_initial_projects_table_when_config_toml_missing() {
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var tmp = NewScopedHome();
        try {
            CodexConfigWriter.TrustWorktree("/tmp/some-worktree", NullLogger.Instance);

            var configPath = Path.Combine(CodexPaths.Home, "config.toml");
            await Assert.That(File.Exists(configPath)).IsTrue();

            var root = Toml.ToModel(File.ReadAllText(configPath));
            var projects = (TomlTable)root["projects"];
            var entry = (TomlTable)projects["/tmp/some-worktree"];
            await Assert.That((string)entry["trust_level"]).IsEqualTo("trusted");
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Writes_to_fresh_home_creates_codex_directory() {
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var tmp = NewScopedHome();
        // Explicitly NOT pre-creating .codex
        try {
            CodexConfigWriter.TrustWorktree("/tmp/wt", NullLogger.Instance);

            var codexDir = Path.Combine(tmp.FullName, ".codex");
            await Assert.That(Directory.Exists(codexDir)).IsTrue();
            await Assert.That(File.Exists(Path.Combine(codexDir, "config.toml"))).IsTrue();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Adds_entry_to_existing_config_preserving_other_tables() {
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var tmp = NewScopedHome();
        try {
            var codexDir = Directory.CreateDirectory(Path.Combine(tmp.FullName, ".codex")).FullName;
            File.WriteAllText(Path.Combine(codexDir, "config.toml"), """
                model = "gpt-5.5"

                [mcp_servers.linear]
                url = "https://mcp.linear.app/mcp"

                [projects."/existing/path"]
                trust_level = "trusted"
                """);

            CodexConfigWriter.TrustWorktree("/tmp/new-wt", NullLogger.Instance);

            var root = Toml.ToModel(File.ReadAllText(Path.Combine(codexDir, "config.toml")));
            await Assert.That((string)root["model"]).IsEqualTo("gpt-5.5");
            var mcp = (TomlTable)((TomlTable)root["mcp_servers"])["linear"];
            await Assert.That((string)mcp["url"]).IsEqualTo("https://mcp.linear.app/mcp");

            var projects = (TomlTable)root["projects"];
            await Assert.That((string)((TomlTable)projects["/existing/path"])["trust_level"]).IsEqualTo("trusted");
            await Assert.That((string)((TomlTable)projects["/tmp/new-wt"])["trust_level"]).IsEqualTo("trusted");
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Updates_trust_level_if_present_but_not_trusted() {
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var tmp = NewScopedHome();
        try {
            var codexDir = Directory.CreateDirectory(Path.Combine(tmp.FullName, ".codex")).FullName;
            File.WriteAllText(Path.Combine(codexDir, "config.toml"), """
                [projects."/tmp/wt"]
                trust_level = "ask"
                """);

            CodexConfigWriter.TrustWorktree("/tmp/wt", NullLogger.Instance);

            var root = Toml.ToModel(File.ReadAllText(Path.Combine(codexDir, "config.toml")));
            var entry = (TomlTable)((TomlTable)root["projects"])["/tmp/wt"];
            await Assert.That((string)entry["trust_level"]).IsEqualTo("trusted");
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task No_op_when_trust_level_already_trusted() {
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var tmp = NewScopedHome();
        try {
            var codexDir = Directory.CreateDirectory(Path.Combine(tmp.FullName, ".codex")).FullName;
            var configPath = Path.Combine(codexDir, "config.toml");
            File.WriteAllText(configPath, """
                [projects."/tmp/wt"]
                trust_level = "trusted"
                """);
            var originalMtime = File.GetLastWriteTimeUtc(configPath);

            await Task.Delay(20); // ensure mtime resolution gap
            CodexConfigWriter.TrustWorktree("/tmp/wt", NullLogger.Instance);

            await Assert.That(File.GetLastWriteTimeUtc(configPath)).IsEqualTo(originalMtime);
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Atomic_rename_leaves_no_tmp_files() {
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var tmp = NewScopedHome();
        try {
            CodexConfigWriter.TrustWorktree("/tmp/wt-1", NullLogger.Instance);
            CodexConfigWriter.TrustWorktree("/tmp/wt-2", NullLogger.Instance);

            var codexDir = Path.Combine(tmp.FullName, ".codex");
            var leftover = Directory.GetFiles(codexDir).Where(f => Path.GetFileName(f).Contains(".tmp-")).ToList();
            await Assert.That(leftover).IsEmpty();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Concurrent_writers_serialise_safely() {
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var tmp = NewScopedHome();
        try {
            var tasks = Enumerable.Range(0, 20)
                .Select(i => Task.Run(() => CodexConfigWriter.TrustWorktree($"/tmp/wt-{i}", NullLogger.Instance)))
                .ToArray();
            await Task.WhenAll(tasks);

            var configPath = Path.Combine(CodexPaths.Home, "config.toml");
            var root = Toml.ToModel(File.ReadAllText(configPath));
            var projects = (TomlTable)root["projects"];
            for (var i = 0; i < 20; i++) {
                var entry = (TomlTable)projects[$"/tmp/wt-{i}"];
                await Assert.That((string)entry["trust_level"]).IsEqualTo("trusted");
            }
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Malformed_existing_config_is_skipped_not_overwritten() {
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var tmp = NewScopedHome();
        try {
            var codexDir = Directory.CreateDirectory(Path.Combine(tmp.FullName, ".codex")).FullName;
            var configPath = Path.Combine(codexDir, "config.toml");
            const string garbage = "{{{ not valid TOML";
            File.WriteAllText(configPath, garbage);

            CodexConfigWriter.TrustWorktree("/tmp/wt", NullLogger.Instance);

            // File untouched, no throw
            await Assert.That(File.ReadAllText(configPath)).IsEqualTo(garbage);
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            tmp.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexConfigWriterTests/*"
```

Expected: all eight tests fail with "CodexConfigWriter does not exist".

- [ ] **Step 3: Implement `CodexConfigWriter`**

Create `src/Kapacitor.Daemon/Services/CodexConfigWriter.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Tomlyn;
using Tomlyn.Model;

namespace kapacitor.Daemon.Services;

/// <summary>
/// Writes the per-worktree pre-trust entry into <c>~/.codex/config.toml</c>:
/// <code>
/// [projects."/abs/worktree/path"]
/// trust_level = "trusted"
/// </code>
/// Tomlyn's TomlTable mode round-trips preserve all other top-level tables
/// (model, mcp_servers, plugins, marketplaces, ad-hoc user keys) but DO NOT
/// preserve user comments or original formatting. This is acceptable because
/// the file is daemon-managed and human edits are expected to be sparse.
/// </summary>
internal static class CodexConfigWriter {
    static readonly Lock _writeLock = new();

    public static void TrustWorktree(string worktreePath, ILogger logger) {
        lock (_writeLock) {
            var configPath = Path.Combine(CodexPaths.Home, "config.toml");

            TomlTable root;
            if (File.Exists(configPath)) {
                try {
                    root = Toml.ToModel(File.ReadAllText(configPath));
                } catch (Exception ex) {
                    logger.LogWarning(ex, "Failed to parse {Path}; aborting pre-trust", configPath);
                    return;
                }
            } else {
                root = new TomlTable();
            }

            if (root["projects"] is not TomlTable projects) {
                projects         = new TomlTable();
                root["projects"] = projects;
            }

            if (projects[worktreePath] is not TomlTable entry) {
                entry                  = new TomlTable();
                projects[worktreePath] = entry;
            }

            var alreadyTrusted = entry["trust_level"] is string s &&
                                 string.Equals(s, "trusted", StringComparison.Ordinal);

            if (alreadyTrusted) return;

            entry["trust_level"] = "trusted";

            try {
                // First-time users have no ~/.codex; create it before the atomic rename.
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                WriteTomlAtomic(configPath, root);
            } catch (Exception ex) {
                logger.LogWarning(ex, "Failed to write {Path}; pre-trust not persisted", configPath);
            }
        }
    }

    static void WriteTomlAtomic(string path, TomlTable root) {
        var tmp = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, Toml.FromModel(root));

        try {
            File.Move(tmp, path, overwrite: true);
        } catch {
            try { File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexConfigWriterTests/*"
```

Expected: all eight pass.

- [ ] **Step 5: AOT verification gate**

```bash
dotnet publish src/Kapacitor.Daemon/Kapacitor.Daemon.csproj -c Release 2>&1 | tee /tmp/aot-daemon.log
grep -E 'IL[23][01][0-9]{2}' /tmp/aot-daemon.log
```

Expected: no matches. If matches appear from Tomlyn usage, escalate to the spec §5.3 fallback (typed source-gen context) before continuing.

- [ ] **Step 6: Commit**

```bash
git add src/Kapacitor.Daemon/Services/CodexConfigWriter.cs \
        test/kapacitor.Tests.Unit/CodexConfigWriterTests.cs
git commit -m "[AI-68] daemon: CodexConfigWriter pre-trusts worktrees in ~/.codex/config.toml"
```

---

### Task 7: Add `Vendor` to `LaunchAgentCommand` and validate

**Files:**
- Modify: `src/Kapacitor.Core/Models.cs`
- Modify: `test/kapacitor.Tests.Unit/LaunchAgentCommandWireFormatTests.cs`

- [ ] **Step 1: Write failing wire-format test**

In `test/kapacitor.Tests.Unit/LaunchAgentCommandWireFormatTests.cs` add:

```csharp
[Test]
public async Task Vendor_field_round_trips_through_json_serializer() {
    var cmd = new LaunchAgentCommand(
        AgentId: "agent-1",
        Prompt: null,
        Model: "claude-sonnet-4-6",
        Effort: null,
        RepoPath: "/tmp/repo",
        Tools: null,
        AttachmentIds: null,
        Vendor: "codex"
    );

    var json = JsonSerializer.Serialize(cmd, kapacitor.ModelsJsonContext.Default.LaunchAgentCommand);
    var back = JsonSerializer.Deserialize<LaunchAgentCommand>(json, kapacitor.ModelsJsonContext.Default.LaunchAgentCommand);

    await Assert.That(back.Vendor).IsEqualTo("codex");
}
```

(Use whatever `JsonSerializerContext` name matches the existing pattern in the file; substitute accordingly.)

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/LaunchAgentCommandWireFormatTests/Vendor_field_round_trips_through_json_serializer"
```

Expected: fails with "Vendor argument not found" or similar compile error.

- [ ] **Step 3: Add `Vendor` to `LaunchAgentCommand`**

In `src/Kapacitor.Core/Models.cs`, update the record:

```csharp
public readonly record struct LaunchAgentCommand(
        string             AgentId,
        string?            Prompt,
        string             Model,
        string?            Effort,
        string             RepoPath,
        string[]?          Tools,
        string[]?          AttachmentIds,
        string             Vendor,
        LaunchKind         Kind    = LaunchKind.Default,
        ReviewLaunchInfo?  Review  = null,
        string?            BaseRef = null
    );
```

(Required positional `Vendor` moves before the optional positionals to satisfy C# syntax.)

- [ ] **Step 4: Fix any compile errors at call sites**

Build:

```bash
dotnet build src/Kapacitor.Daemon/Kapacitor.Daemon.csproj
```

Any call site that constructs `LaunchAgentCommand` positionally is now broken — fix by passing `vendor: "claude"` explicitly. (Server-side construction lives in the kapacitor-server repo and is not touched here.)

- [ ] **Step 5: Run wire-format test to verify it passes**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/LaunchAgentCommandWireFormatTests/*"
```

Expected: all pass.

- [ ] **Step 6: Add vendor validation in `AgentOrchestrator.HandleLaunchAgent`**

In `src/Kapacitor.Daemon/Services/AgentOrchestrator.cs`, near the top of `HandleLaunchAgent` (right after destructuring `cmd`), add:

```csharp
if (cmd.Vendor is not ("claude" or "codex")) {
    await _server.LaunchFailedAsync(cmd.AgentId, $"Unknown vendor: {cmd.Vendor}");
    return;
}
```

This is a placeholder validation — Task 14 wires it to the launcher dictionary. Don't reference `_launchers` yet; just the validate-and-fail behaviour.

- [ ] **Step 7: Run the full unit test suite**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: all tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/Kapacitor.Core/Models.cs \
        src/Kapacitor.Daemon/Services/AgentOrchestrator.cs \
        test/kapacitor.Tests.Unit/LaunchAgentCommandWireFormatTests.cs
git commit -m "[AI-68] models: required Vendor field on LaunchAgentCommand"
```

---

### Task 8: Add `Vendor` to `AgentRunStarted`

**Files:**
- Modify: `src/Kapacitor.Core/Models.cs`
- Modify: `src/Kapacitor.Daemon/Services/AgentOrchestrator.cs`
- Modify: `test/kapacitor.Tests.Unit/LaunchAgentCommandWireFormatTests.cs`

- [ ] **Step 1: Write failing test**

Append to `LaunchAgentCommandWireFormatTests.cs`:

```csharp
[Test]
public async Task AgentRunStarted_vendor_serialises_into_json_body() {
    var evt = new AgentRunStarted(
        Prompt: "do a thing",
        Model: "claude-sonnet-4-6",
        Effort: null,
        RepoPath: "/tmp/repo",
        WorktreePath: "/tmp/wt",
        Vendor: "codex"
    );

    var json = JsonSerializer.Serialize(evt, kapacitor.ModelsJsonContext.Default.AgentRunStarted);
    await Assert.That(json).Contains("\"Vendor\":\"codex\"");
}
```

(Use whatever JSON-property casing matches the existing convention; if snake_case is in use, expect `"vendor":"codex"`.)

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/LaunchAgentCommandWireFormatTests/AgentRunStarted_vendor_serialises_into_json_body"
```

Expected: compile error (no Vendor parameter on `AgentRunStarted`).

- [ ] **Step 3: Update `AgentRunStarted`**

In `src/Kapacitor.Core/Models.cs`:

```csharp
record AgentRunStarted(
        string? Prompt,
        string? Model,
        string? Effort,
        string? RepoPath,
        string? WorktreePath,
        string  Vendor
    );
```

- [ ] **Step 4: Fix the orchestrator's `AppendAgentRunEventAsync` call site**

In `src/Kapacitor.Daemon/Services/AgentOrchestrator.cs`, the `_server.AppendAgentRunEventAsync(agentId, new AgentRunStarted(...))` site near line 341 now needs `vendor: cmd.Vendor` as the seventh positional:

```csharp
_ = _server.AppendAgentRunEventAsync(
    agentId,
    new AgentRunStarted(prompt, model, effort, repoPath, worktree.Path, cmd.Vendor)
);
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/Kapacitor.Core/Models.cs \
        src/Kapacitor.Daemon/Services/AgentOrchestrator.cs \
        test/kapacitor.Tests.Unit/LaunchAgentCommandWireFormatTests.cs
git commit -m "[AI-68] models: required Vendor field on AgentRunStarted"
```

---

### Task 9: Update `ServerConnection.RequestPermissionAsync` to take `vendor`

> **Superseded by AI-702 (2026-05-27).** The 5-arg wire-shape change planned in this task was rolled back. `JsonHubProtocol.BindArguments` strict-count-matches arguments against the target hub method and does not honour C# default values, so adding a 5th positional `vendor` arg without a coordinated server-side hub method bump made every hosted-agent permission prompt fall back to deny. The fix kept `vendor` local to `LocalPermissionBridge` (used in `BuildHookResponseJson` to shape the Claude vs Codex response envelope) and removed it from `ServerConnection.RequestPermissionAsync` and from `_hub.InvokeAsync(...)`. The instructions below are kept as a historical record of what was attempted — do **not** follow them on a fresh implementation.

**Files:**
- Modify: `src/Kapacitor.Daemon/Services/ServerConnection.cs`
- Modify: `src/Kapacitor.Daemon/Services/LocalPermissionBridge.cs`

- [ ] **Step 1: Update the `ServerConnection.RequestPermissionAsync` signature**

In `src/Kapacitor.Daemon/Services/ServerConnection.cs`, find the existing `RequestPermissionAsync` method (around line 283). Add a required `string vendor` parameter just before `CancellationToken ct`, and pass it as the fifth positional to the hub `InvokeAsync` call:

```csharp
public async Task<PermissionDecision> RequestPermissionAsync(
        string            sessionId,
        string?           toolName,
        JsonElement?      toolInput,
        JsonElement?      suggestions,
        string            vendor,
        CancellationToken ct = default
    ) =>
    await _hub.InvokeAsync<PermissionDecision>(
        "RequestPermission",
        sessionId, toolName, toolInput, suggestions, vendor,
        ct
    );
```

- [ ] **Step 2: Update the bridge's call site**

In `src/Kapacitor.Daemon/Services/LocalPermissionBridge.cs` around line 188, the existing call:

```csharp
decision = await server.RequestPermissionAsync(sessionId, toolName, toolInput, suggestions, ct);
```

Becomes:

```csharp
decision = await server.RequestPermissionAsync(sessionId, toolName, toolInput, suggestions, "claude", ct);
```

This is a stub value — Task 21 replaces it with the vendor extracted from the URL segment. The interim "claude" keeps behaviour identical to today while we land the parameter change incrementally.

- [ ] **Step 3: Build to verify no compile errors**

```bash
dotnet build src/Kapacitor.Daemon/Kapacitor.Daemon.csproj
```

Expected: succeeds.

- [ ] **Step 4: Run the full unit test suite**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: all tests pass — the bridge's behaviour is unchanged (still passes `"claude"` to the server) so the existing `LocalPermissionBridgeTests` continue to work against a test double that ignores the new parameter.

If any existing test double or fake stubs `RequestPermissionAsync` and doesn't accept the new parameter, update it to match the new signature.

- [ ] **Step 5: Commit**

```bash
git add src/Kapacitor.Daemon/Services/ServerConnection.cs \
        src/Kapacitor.Daemon/Services/LocalPermissionBridge.cs
git commit -m "[AI-68] daemon: ServerConnection.RequestPermissionAsync gains vendor param"
```

---

### Task 10: Update `PermissionRequestCommand` to post to `/claude/permission-request`

The bridge URL split in Task 21 makes the legacy `/{token}/permission-request` path 404. The Claude permission-request CLI hook must migrate to `/claude/permission-request` in lockstep.

**Files:**
- Modify: `src/kapacitor/Commands/PermissionRequestCommand.cs`

- [ ] **Step 1: Locate the post target**

`src/kapacitor/Commands/PermissionRequestCommand.cs:63` constructs the POST URL today as `daemonUrl + "/permission-request"`. Update to `daemonUrl + "/claude/permission-request"`.

- [ ] **Step 2: Build to verify no compile errors**

```bash
dotnet build src/kapacitor/kapacitor.csproj
```

Expected: succeeds.

- [ ] **Step 3: Run existing Claude permission-request tests**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/PermissionRequestCommand*"
```

Some tests may fail because they assert the old URL. Update the assertions to match `/claude/permission-request`.

Re-run:

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/PermissionRequestCommand*"
```

Expected: all pass.

- [ ] **Step 4: Commit**

```bash
git add src/kapacitor/Commands/PermissionRequestCommand.cs \
        test/kapacitor.Tests.Unit/  # if any test files were updated
git commit -m "[AI-68] cli: Claude permission-request posts to /claude/permission-request"
```

---

### Task 11: Define `IHostedAgentLauncher`, `LauncherContext`, `LaunchArgs`

**Files:**
- Create: `src/Kapacitor.Daemon/Services/IHostedAgentLauncher.cs`

- [ ] **Step 1: Create the interface and supporting records**

Create `src/Kapacitor.Daemon/Services/IHostedAgentLauncher.cs`:

```csharp
using kapacitor.Daemon.Pty;

namespace kapacitor.Daemon.Services;

/// <summary>
/// Vendor-specific launch strategy. <see cref="AgentOrchestrator"/> handles
/// lifecycle concerns (PTY spawn, status, heartbeat, cleanup); each impl owns
/// the vendor-specific bits: CLI binary path, args, settings overlay, pre-trust,
/// MCP config, and per-vendor cleanup.
/// </summary>
internal interface IHostedAgentLauncher {
    /// <summary>Vendor token this launcher handles ("claude" or "codex").</summary>
    string Vendor { get; }

    /// <summary>Absolute path or bare command for the CLI. Pulled from DaemonConfig.</summary>
    string CliPath { get; }

    /// <summary>
    /// Per-vendor preparation BEFORE the PTY is spawned. Implementations:
    ///   • Overlay vendor-specific settings dir from source repo into worktree
    ///   • Pre-trust the worktree path in the vendor's config file
    ///   • Write any vendor-specific config (MCP, etc.)
    ///   • Merge dialog-selected tools into vendor-specific permission shape
    ///   • Run fail-fast preflight checks (e.g. required CLI hooks installed)
    /// Two failure modes are supported and the orchestrator distinguishes them:
    ///   • Filesystem / parse errors are swallowed inside the launcher with a
    ///     warning log so a settings-overlay glitch never blocks launch.
    ///   • Typed preflight exceptions (e.g. CodexHooksNotInstalledException)
    ///     propagate out and the orchestrator converts them into LaunchFailed
    ///     with the exception's user-facing message. Use these sparingly.
    /// </summary>
    void Prepare(LauncherContext ctx);

    /// <summary>Build the argv array passed to the CLI.</summary>
    LaunchArgs BuildArgs(LauncherContext ctx);

    /// <summary>Per-vendor cleanup AFTER the agent exits / is stopped.</summary>
    void Cleanup(AgentInstance agent);
}

internal sealed record LauncherContext(
        string                       AgentId,
        string                       SourceRepoPath,
        WorktreeInfo                 Worktree,
        string?                      Prompt,
        string                       Model,
        string?                      Effort,
        string[]?                    Tools,
        bool                         IsReview,
        ReviewLaunchInfo?            Review,
        ReviewLaunchBuilder.Result?  ReviewLaunch
    );

internal readonly record struct LaunchArgs(string[] Args, string? McpConfigPath);
```

- [ ] **Step 2: Build to verify**

```bash
dotnet build src/Kapacitor.Daemon/Kapacitor.Daemon.csproj
```

Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Kapacitor.Daemon/Services/IHostedAgentLauncher.cs
git commit -m "[AI-68] daemon: define IHostedAgentLauncher strategy interface"
```

---

### Task 12: Extract `ClaudeLauncher` (behavior-preserving refactor)

Move all Claude-specific code from `AgentOrchestrator.cs` into `ClaudeLauncher.cs`. No behavior change. Existing tests are the regression net.

**Files:**
- Create: `src/Kapacitor.Daemon/Services/ClaudeLauncher.cs`
- Modify: `src/Kapacitor.Daemon/Services/AgentOrchestrator.cs`

- [ ] **Step 1: Create `ClaudeLauncher` with the extracted methods**

Create `src/Kapacitor.Daemon/Services/ClaudeLauncher.cs`. Move these existing private static methods from `AgentOrchestrator.cs` verbatim:

- `OverlayDirectory` (line ~951)
- `SymlinkClaudeProjectDir` (line ~973)
- `RemoveClaudeProjectSymlink` (line ~998)
- `WriteMcpConfig` (line ~1137)
- `ReadMcpJsonServerNames` (line ~1122)
- `TrustWorktreeInClaudeConfig` (line ~1006)
- `LoadJsonObject` (line ~1091)
- `WriteJsonAtomic` (line ~1107)
- `MergeToolPermissions` (line ~902)
- `TrustWriteLock` (line ~817), `ValidEffortLevels` (line ~818), `IndentedJsonOpts` (line ~800) — needed by methods above

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using kapacitor.Commands;
using Microsoft.Extensions.Logging;

namespace kapacitor.Daemon.Services;

internal sealed partial class ClaudeLauncher(
        DaemonConfig          config,
        ILogger<ClaudeLauncher> logger
    ) : IHostedAgentLauncher {

    public string Vendor  => "claude";
    public string CliPath => config.ClaudePath;

    static readonly Lock                  TrustWriteLock   = new();
    static readonly HashSet<string>       ValidEffortLevels = ["low", "medium", "high", "max"];
    static readonly JsonSerializerOptions IndentedJsonOpts  = new() { WriteIndented = true };

    public void Prepare(LauncherContext ctx) {
        // Overlay .claude/ local settings from source repo into worktree.
        try {
            var sourceClaudeDir = Path.Combine(ctx.SourceRepoPath, ".claude");
            var destClaudeDir   = Path.Combine(ctx.Worktree.Path, ".claude");

            if (Directory.Exists(sourceClaudeDir)) {
                OverlayDirectory(sourceClaudeDir, destClaudeDir);
            }

            SymlinkClaudeProjectDir(ctx.SourceRepoPath, ctx.Worktree.Path);
        } catch (Exception ex) {
            LogOverlayFailed(ex, ctx.AgentId);
        }

        try {
            WriteMcpConfig(ctx.SourceRepoPath, ctx.Worktree.Path);
        } catch (Exception ex) {
            LogMcpConfigFailed(ex, ctx.AgentId);
        }

        try {
            TrustWorktreeInClaudeConfig(ctx.Worktree.Path);
        } catch (Exception ex) {
            LogTrustWorktreeFailed(ex, ctx.AgentId);
        }

        try {
            if (ctx.Tools is { Length: > 0 }) {
                MergeToolPermissions(ctx.Worktree.Path, ctx.Tools);
            }
        } catch (Exception ex) {
            LogToolPermissionsFailed(ex, ctx.AgentId);
        }
    }

    public LaunchArgs BuildArgs(LauncherContext ctx) {
        var args = new List<string>();
        string? mcpConfigPath = null;

        if (ctx.IsReview && ctx.ReviewLaunch is { } launch) {
            mcpConfigPath = launch.McpConfigPath;

            args.Add("--mcp-config");
            args.Add(launch.McpConfigPath);
            args.Add("--system-prompt");
            args.Add(launch.SystemPrompt);

            if (!string.IsNullOrEmpty(ctx.Model)) {
                args.Add("--model");
                args.Add(ctx.Model);
            }
        } else {
            if (!string.IsNullOrEmpty(ctx.Effort)) {
                args.Add("--effort");
                args.Add(ctx.Effort);
            }
            if (!string.IsNullOrEmpty(ctx.Model)) {
                args.Add("--model");
                args.Add(ctx.Model);
            }
            if (!string.IsNullOrEmpty(ctx.Prompt)) {
                args.Add("--");
                args.Add(ctx.Prompt);
            }
        }

        return new LaunchArgs(args.ToArray(), mcpConfigPath);
    }

    public void Cleanup(AgentInstance agent) {
        try { RemoveClaudeProjectSymlink(agent.Worktree.Path); } catch (Exception ex) {
            LogCleanupSymlinkFailed(ex, agent.Id);
        }

        if (agent.McpConfigPath is not null) {
            try { File.Delete(agent.McpConfigPath); } catch (Exception ex) {
                LogCleanupMcpConfigFailed(ex, agent.Id);
            }
        }
    }

    // === All the verbatim moved static helpers below ===
    // Copy OverlayDirectory, SymlinkClaudeProjectDir, RemoveClaudeProjectSymlink,
    // WriteMcpConfig, ReadMcpJsonServerNames, TrustWorktreeInClaudeConfig,
    // LoadJsonObject, WriteJsonAtomic, MergeToolPermissions HERE — paste them
    // verbatim from AgentOrchestrator.cs (with `static` keyword preserved).

    // === LoggerMessage source-generated methods (move corresponding ones from orchestrator) ===
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to overlay .claude settings for agent {AgentId} (continuing)")]
    partial void LogOverlayFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to write .mcp.json for agent {AgentId} (continuing)")]
    partial void LogMcpConfigFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to pre-trust worktree for agent {AgentId} (continuing)")]
    partial void LogTrustWorktreeFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to merge tool permissions for agent {AgentId} (continuing)")]
    partial void LogToolPermissionsFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to remove claude project symlink for agent {AgentId}")]
    partial void LogCleanupSymlinkFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to remove mcp config for agent {AgentId}")]
    partial void LogCleanupMcpConfigFailed(Exception ex, string agentId);
}
```

- [ ] **Step 2: Remove the moved code from `AgentOrchestrator.cs`**

In `src/Kapacitor.Daemon/Services/AgentOrchestrator.cs`:

- Delete the static methods listed in Step 1 (`OverlayDirectory`, `SymlinkClaudeProjectDir`, `RemoveClaudeProjectSymlink`, `WriteMcpConfig`, `ReadMcpJsonServerNames`, `TrustWorktreeInClaudeConfig`, `LoadJsonObject`, `WriteJsonAtomic`, `MergeToolPermissions`).
- Delete the corresponding `LoggerMessage` partial methods (`LogOverlayFailed`, `LogMcpConfigFailed`, `LogTrustWorktreeFailed`, `LogToolPermissionsFailed`).
- Keep `TrustWriteLock`, `ValidEffortLevels`, `IndentedJsonOpts` for now (Task 14 removes any stragglers).

Leave `HandleLaunchAgent` calling the (now-removed) methods — the build will break. That's expected; Task 14 wires it through the launcher dictionary.

- [ ] **Step 3: Build to confirm the expected breakage**

```bash
dotnet build src/Kapacitor.Daemon/Kapacitor.Daemon.csproj
```

Expected: build errors in `AgentOrchestrator.cs` because the deleted methods are still called from `HandleLaunchAgent`. **This is the intentional checkpoint** — fixing it is Task 14.

- [ ] **Step 4: Temporarily stub the calls so the build compiles**

This task is purely the extraction. To keep the working tree green between tasks, comment out the call sites in `HandleLaunchAgent` and replace each with `// TODO Task 14: dispatch through launcher`. Restore in Task 14.

After commenting:

```bash
dotnet build src/Kapacitor.Daemon/Kapacitor.Daemon.csproj
```

Expected: succeeds. Note: behaviour is broken (hosted Claude won't pre-trust or overlay) until Task 14 lands. **Do not push this commit alone.**

- [ ] **Step 5: Commit (mark as WIP)**

```bash
git add src/Kapacitor.Daemon/Services/ClaudeLauncher.cs \
        src/Kapacitor.Daemon/Services/AgentOrchestrator.cs
git commit -m "[AI-68] daemon: WIP extract ClaudeLauncher (next: wire through orchestrator)"
```

---

### Task 13: Add `AgentInstance.Vendor` field

**Files:**
- Modify: `src/Kapacitor.Daemon/Services/AgentOrchestrator.cs`

- [ ] **Step 1: Add `Vendor` to the `AgentInstance` record**

In `src/Kapacitor.Daemon/Services/AgentOrchestrator.cs` around line 15, update the record:

```csharp
public record AgentInstance(
        string                  Id,
        string?                 Prompt,
        string                  Model,
        string?                 Effort,
        string                  RepoPath,
        string                  Vendor,
        IPtyProcess             Process,
        WorktreeInfo            Worktree,
        CancellationTokenSource ReadCts
    ) {
    // ... existing properties unchanged
}
```

- [ ] **Step 2: Build to confirm call-site breakages are visible**

```bash
dotnet build src/Kapacitor.Daemon/Kapacitor.Daemon.csproj
```

Expected: at least one error at the `new AgentInstance(...)` site in `HandleLaunchAgent` (line ~332). Task 14 fixes it; pass through `vendor: cmd.Vendor` for now to keep building.

- [ ] **Step 3: Patch the construction site**

In `HandleLaunchAgent`, find the `new AgentInstance(...)` call and insert `vendor: cmd.Vendor` as the sixth positional:

```csharp
var agent = new AgentInstance(agentId, prompt, model, effort, repoPath, cmd.Vendor, process, worktree, cts) {
    McpConfigPath = mcpConfigPath
};
```

Build:

```bash
dotnet build src/Kapacitor.Daemon/Kapacitor.Daemon.csproj
```

Expected: succeeds.

- [ ] **Step 4: Run the full unit test suite**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: all tests pass (Vendor is a passive field at this point).

- [ ] **Step 5: Commit**

```bash
git add src/Kapacitor.Daemon/Services/AgentOrchestrator.cs
git commit -m "[AI-68] daemon: AgentInstance carries Vendor for cleanup dispatch"
```

---

### Task 14: Wire `AgentOrchestrator` to dispatch through launcher dictionary

**Files:**
- Modify: `src/Kapacitor.Daemon/Services/AgentOrchestrator.cs`
- Create: `test/kapacitor.Tests.Unit/AgentOrchestratorVendorTests.cs`

- [ ] **Step 1: Write failing test for vendor routing**

Create `test/kapacitor.Tests.Unit/AgentOrchestratorVendorTests.cs`:

```csharp
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace kapacitor.Tests.Unit;

public class AgentOrchestratorVendorTests {
    [Test]
    public async Task Launch_with_unknown_vendor_emits_launch_failed_and_does_not_spawn_pty() {
        // Test harness: substitute IPtyProcessFactory + ServerConnection with
        // spies that capture calls. Construct an orchestrator with an empty
        // launcher dictionary so any vendor lookup fails before spawn.
        var ptySpy    = new SpyPtyProcessFactory();
        var serverSpy = new SpyServerConnection();
        var launchers = new Dictionary<string, IHostedAgentLauncher>();
        var orch = NewOrchestratorUnderTest(launchers, ptySpy, serverSpy);

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "agent-x", Prompt: null, Model: "m", Effort: null,
            RepoPath: "/tmp/repo", Tools: null, AttachmentIds: null,
            Vendor: "bogus"
        ));

        await Assert.That(ptySpy.SpawnCount).IsEqualTo(0);
        await Assert.That(serverSpy.LaunchFailedReasons).Contains(r => r.Contains("Unknown vendor"));
    }

    // (Additional vendor-routing tests in Step 4.)
}
```

You will need a test harness factory that constructs `AgentOrchestrator` with mocked dependencies. If one exists, reuse it; otherwise add `internal static AgentOrchestrator NewOrchestratorUnderTest(...)` next to the orchestrator (kept `internal` so `InternalsVisibleTo` exposes it to tests).

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentOrchestratorVendorTests/*"
```

Expected: fails (the existing orchestrator does not yet do vendor lookup; spawn happens regardless).

- [ ] **Step 3: Update orchestrator constructor + `HandleLaunchAgent` dispatch**

In `src/Kapacitor.Daemon/Services/AgentOrchestrator.cs`:

1. Add `_launchers` field and constructor parameter:

```csharp
readonly IReadOnlyDictionary<string, IHostedAgentLauncher> _launchers;

public AgentOrchestrator(
        DaemonConfig                                       config,
        ServerConnection                                   server,
        WorktreeManager                                    worktreeManager,
        RepoMatcher                                        repoMatcher,
        IPtyProcessFactory                                 ptyFactory,
        IHttpClientFactory                                 httpClientFactory,
        LocalPermissionBridge                              permissionBridge,
        IReadOnlyDictionary<string, IHostedAgentLauncher>  launchers,
        ILogger<AgentOrchestrator>                         logger
    ) {
    // ... existing assignments
    _launchers = launchers;
    // ... event wiring unchanged
}
```

2. Update `HandleLaunchAgent` to look up the launcher and dispatch through it. Replace the commented-out Claude-specific code (from Task 12 Step 4) with:

```csharp
// At the top, after destructuring + vendor validation:
if (!_launchers.TryGetValue(cmd.Vendor, out var launcher)) {
    await _server.LaunchFailedAsync(agentId, $"No launcher registered for vendor: {cmd.Vendor}");
    return;
}

// Review-kind for Codex is unsupported in v1:
if (isReview && cmd.Vendor == "codex") {
    await _server.LaunchFailedAsync(agentId, "PR review for Codex is not yet supported");
    return;
}

// (existing review-launch validation against origin remote stays, only for Claude)
// ... existing worktree create, attachment download ...

// Replace the commented-out OverlayDirectory / TrustWorktreeInClaudeConfig /
// WriteMcpConfig / MergeToolPermissions block with:
var launcherCtx = new LauncherContext(
    AgentId: agentId,
    SourceRepoPath: repoPath,
    Worktree: worktree,
    Prompt: prompt,
    Model: model,
    Effort: effort,
    Tools: tools,
    IsReview: isReview,
    Review: cmd.Review,
    ReviewLaunch: isReview && cmd.Review is { } reviewArgs
        ? await ReviewLaunchBuilder.BuildAsync(_config.ServerUrl ?? "", reviewArgs.Owner, reviewArgs.Repo, reviewArgs.PrNumber)
        : null
);

try {
    launcher.Prepare(launcherCtx);
} catch (CodexHooksNotInstalledException ex) {
    await _server.LaunchFailedAsync(agentId, ex.Message);
    return;
} catch (Exception ex) {
    _logger.LogWarning(ex, "Launcher Prepare soft-failure for agent {AgentId} (continuing)", agentId);
}

var launchArgs = launcher.BuildArgs(launcherCtx);
mcpConfigPath = launchArgs.McpConfigPath;

// (existing env-var dictionary construction stays unchanged)

var process = _ptyFactory.Spawn(launcher.CliPath, launchArgs.Args, worktree.Path, env);
```

3. Update `CleanupAgentAsync` (around line 858) to dispatch through launcher:

```csharp
async Task CleanupAgentAsync(string agentId) {
    if (!_agents.TryRemove(agentId, out var agent)) return;

    try { await agent.Process.DisposeAsync(); } catch (Exception ex) { LogCleanupStepFailed(ex, "disposing process", agentId); }

    if (_launchers.TryGetValue(agent.Vendor, out var launcher)) {
        try { launcher.Cleanup(agent); } catch (Exception ex) { LogCleanupStepFailed(ex, "launcher.Cleanup", agentId); }
    }

    try { await WorktreeManager.RemoveAsync(agent.Worktree); } catch (Exception ex) { LogCleanupStepFailed(ex, "removing worktree", agentId); }

    try { await _server.AgentUnregisteredAsync(agentId); } catch (Exception ex) { LogCleanupStepFailed(ex, "unregistering", agentId); }
}
```

The old `RemoveClaudeProjectSymlink(agent.Worktree.Path)` and `File.Delete(agent.McpConfigPath)` calls move into `ClaudeLauncher.Cleanup` (already done in Task 12).

4. Update the failed-launch catch block (around line 361). The existing block calls `RemoveClaudeProjectSymlink` + worktree removal + mcpConfig delete directly. Replace:

```csharp
} catch (Exception ex) {
    LogLaunchFailed(ex, agentId);

    if (worktree != null) {
        if (_launchers.TryGetValue(cmd.Vendor, out var launcherForCleanup)) {
            try {
                // Build a transient AgentInstance just for cleanup dispatch.
                // The launcher's Cleanup signature takes AgentInstance, so we
                // construct one with the failed-launch context. PTY is not yet
                // alive at this point so Process is set to a no-op stub.
                var transient = new AgentInstance(
                    agentId, prompt, model, effort, repoPath, cmd.Vendor,
                    NoopPtyProcess.Instance, worktree, new CancellationTokenSource()
                ) {
                    McpConfigPath = mcpConfigPath
                };
                launcherForCleanup.Cleanup(transient);
            } catch (Exception cleanupEx) {
                LogCleanupStepFailed(cleanupEx, "launcher.Cleanup (failed-launch)", agentId);
            }
        }

        try { await WorktreeManager.RemoveAsync(worktree); } catch { /* best-effort */ }
    }

    await _server.LaunchFailedAsync(agentId, ex.Message);
}
```

Add a small `NoopPtyProcess` internal class (or use a real null impl) — the launcher's `Cleanup` doesn't touch the process, but the `AgentInstance` constructor requires non-null. Define in the same file:

```csharp
internal sealed class NoopPtyProcess : IPtyProcess {
    public static readonly NoopPtyProcess Instance = new();
    public int  Pid       => 0;
    public bool HasExited => true;
    public int? ExitCode  => 0;
    public ValueTask DisposeAsync() => default;
    public Task WaitForExitAsync(TimeSpan _) => Task.CompletedTask;
    public Task TerminateAsync(TimeSpan _) => Task.CompletedTask;
    public IAsyncEnumerable<byte[]> ReadOutputAsync(CancellationToken _) => AsyncEnumerable.Empty<byte[]>();
    public ValueTask WriteAsync(string _) => default;
    public ValueTask WriteAsync(byte[] _) => default;
    public void Resize(ushort _, ushort __) { }
}
```

(Match the actual `IPtyProcess` interface — adjust the stub if the interface differs.)

- [ ] **Step 4: Add the remaining vendor-routing tests**

Extend `AgentOrchestratorVendorTests.cs`:

```csharp
[Test]
public async Task Launch_with_vendor_claude_calls_claude_launcher() {
    var claudeSpy = new SpyHostedAgentLauncher("claude");
    var codexSpy  = new SpyHostedAgentLauncher("codex");
    var launchers = new Dictionary<string, IHostedAgentLauncher> {
        ["claude"] = claudeSpy,
        ["codex"]  = codexSpy
    };
    var orch = NewOrchestratorUnderTest(launchers, new SpyPtyProcessFactory(), new SpyServerConnection());

    await orch.HandleLaunchAgentForTest(NewClaudeCommand());

    await Assert.That(claudeSpy.BuildArgsCalls).IsEqualTo(1);
    await Assert.That(codexSpy.BuildArgsCalls).IsEqualTo(0);
}

[Test]
public async Task Launch_with_vendor_codex_calls_codex_launcher() {
    var claudeSpy = new SpyHostedAgentLauncher("claude");
    var codexSpy  = new SpyHostedAgentLauncher("codex");
    var launchers = new Dictionary<string, IHostedAgentLauncher> {
        ["claude"] = claudeSpy,
        ["codex"]  = codexSpy
    };
    var orch = NewOrchestratorUnderTest(launchers, new SpyPtyProcessFactory(), new SpyServerConnection());

    await orch.HandleLaunchAgentForTest(NewCodexCommand());

    await Assert.That(codexSpy.BuildArgsCalls).IsEqualTo(1);
    await Assert.That(claudeSpy.BuildArgsCalls).IsEqualTo(0);
}

[Test]
public async Task Launch_review_kind_with_vendor_codex_emits_launch_failed() {
    var serverSpy = new SpyServerConnection();
    var orch = NewOrchestratorUnderTest(
        new Dictionary<string, IHostedAgentLauncher> {
            ["codex"] = new SpyHostedAgentLauncher("codex")
        }, new SpyPtyProcessFactory(), serverSpy);

    await orch.HandleLaunchAgentForTest(NewCodexCommand() with {
        Kind = LaunchKind.Review,
        Review = new ReviewLaunchInfo("owner", "repo", 42)
    });

    await Assert.That(serverSpy.LaunchFailedReasons).Contains(r => r.Contains("PR review for Codex"));
}

[Test]
public async Task Cleanup_calls_vendor_specific_cleanup_method() {
    var claudeSpy = new SpyHostedAgentLauncher("claude");
    var codexSpy  = new SpyHostedAgentLauncher("codex");
    var orch = NewOrchestratorUnderTest(
        new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy, ["codex"] = codexSpy },
        new SpyPtyProcessFactory(), new SpyServerConnection());

    await orch.HandleLaunchAgentForTest(NewCodexCommand());
    await orch.CleanupAllForTest();

    await Assert.That(codexSpy.CleanupCalls).IsEqualTo(1);
    await Assert.That(claudeSpy.CleanupCalls).IsEqualTo(0);
}

[Test]
public async Task Codex_hooks_not_installed_exception_during_prepare_yields_actionable_launch_failed() {
    var codexSpy = new SpyHostedAgentLauncher("codex") {
        PrepareThrows = new CodexHooksNotInstalledException("Codex hooks not installed. Run `kapacitor plugin install --codex` and try again.")
    };
    var serverSpy = new SpyServerConnection();
    var orch = NewOrchestratorUnderTest(
        new Dictionary<string, IHostedAgentLauncher> { ["codex"] = codexSpy },
        new SpyPtyProcessFactory(), serverSpy);

    await orch.HandleLaunchAgentForTest(NewCodexCommand());

    await Assert.That(serverSpy.LaunchFailedReasons).Contains(r => r.Contains("Codex hooks not installed"));
    await Assert.That(codexSpy.BuildArgsCalls).IsEqualTo(0);
}
```

Implement the `SpyHostedAgentLauncher`, `SpyPtyProcessFactory`, `SpyServerConnection`, `NewClaudeCommand`, `NewCodexCommand` helpers in the same test file (or a shared `TestSpies.cs`).

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentOrchestratorVendorTests/*"
```

Expected: all five pass.

- [ ] **Step 6: Run the full unit test suite**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: all tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Kapacitor.Daemon/Services/AgentOrchestrator.cs \
        test/kapacitor.Tests.Unit/AgentOrchestratorVendorTests.cs
git commit -m "[AI-68] daemon: orchestrator dispatches launches through IHostedAgentLauncher"
```

---

### Task 15: Add `CodexPath` to `DaemonConfig`

**Files:**
- Modify: `src/Kapacitor.Daemon/DaemonConfig.cs`

- [ ] **Step 1: Add the field**

In `src/Kapacitor.Daemon/DaemonConfig.cs` after `ClaudePath`:

```csharp
public string ClaudePath { get; set; } = "claude";
public string CodexPath  { get; set; } = "codex";
```

- [ ] **Step 2: Build to verify**

```bash
dotnet build src/Kapacitor.Daemon/Kapacitor.Daemon.csproj
```

Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Kapacitor.Daemon/DaemonConfig.cs
git commit -m "[AI-68] daemon: add CodexPath config field"
```

---

### Task 16: Implement `CodexLauncher`

Largest single task — encompasses overlay, hook preflight, pre-trust, args building, no cleanup needed.

**Files:**
- Create: `src/Kapacitor.Daemon/Services/CodexLauncher.cs`
- Create: `test/kapacitor.Tests.Unit/CodexLauncherTests.cs`

- [ ] **Step 1: Write failing tests for `BuildArgs`**

Create `test/kapacitor.Tests.Unit/CodexLauncherTests.cs`:

```csharp
using kapacitor.Daemon.Services;
using kapacitor.Daemon.Pty;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace kapacitor.Tests.Unit;

public class CodexLauncherTests {
    static CodexLauncher NewLauncher() =>
        new(new DaemonConfig { CodexPath = "codex" }, NullLogger<CodexLauncher>.Instance);

    static LauncherContext NewCtx(
        string? prompt = null,
        string  model  = "gpt-5.3-codex",
        string? effort = null
    ) => new(
        AgentId: "a-1",
        SourceRepoPath: "/tmp/repo",
        Worktree: new WorktreeInfo(Path: "/tmp/wt", BranchName: "wt-branch"),
        Prompt: prompt,
        Model: model,
        Effort: effort,
        Tools: null,
        IsReview: false,
        Review: null,
        ReviewLaunch: null
    );

    [Test]
    public async Task BuildArgs_includes_workspace_write_sandbox_and_on_request_approval() {
        var args = NewLauncher().BuildArgs(NewCtx()).Args;
        await Assert.That(args).Contains("--sandbox").And.Contains("workspace-write");
        await Assert.That(args).Contains("--ask-for-approval").And.Contains("on-request");
    }

    [Test]
    public async Task BuildArgs_maps_effort_max_to_xhigh() {
        var args = NewLauncher().BuildArgs(NewCtx(effort: "max")).Args;
        await Assert.That(string.Join(' ', args)).Contains("model_reasoning_effort=\"xhigh\"");
    }

    [Test]
    [Arguments("low")]
    [Arguments("medium")]
    [Arguments("high")]
    public async Task BuildArgs_passes_effort_through_unchanged(string effort) {
        var args = NewLauncher().BuildArgs(NewCtx(effort: effort)).Args;
        await Assert.That(string.Join(' ', args)).Contains($"model_reasoning_effort=\"{effort}\"");
    }

    [Test]
    [Arguments(null)]
    [Arguments("auto")]
    public async Task BuildArgs_omits_effort_when_null_or_auto(string? effort) {
        var args = NewLauncher().BuildArgs(NewCtx(effort: effort)).Args;
        await Assert.That(string.Join(' ', args)).DoesNotContain("model_reasoning_effort");
    }

    [Test]
    public async Task BuildArgs_appends_prompt_after_double_dash_when_present() {
        var args = NewLauncher().BuildArgs(NewCtx(prompt: "do a thing")).Args;
        var dashIdx = Array.IndexOf(args, "--");
        await Assert.That(dashIdx).IsGreaterThan(-1);
        await Assert.That(args[dashIdx + 1]).IsEqualTo("do a thing");
    }

    [Test]
    public async Task BuildArgs_emits_no_alt_screen_flag() {
        var args = NewLauncher().BuildArgs(NewCtx()).Args;
        await Assert.That(args).Contains("--no-alt-screen");
    }

    [Test]
    public async Task BuildArgs_includes_cd_with_worktree_path() {
        var args = NewLauncher().BuildArgs(NewCtx()).Args;
        var cdIdx = Array.IndexOf(args, "--cd");
        await Assert.That(cdIdx).IsGreaterThan(-1);
        await Assert.That(args[cdIdx + 1]).IsEqualTo("/tmp/wt");
    }

    [Test]
    public async Task BuildArgs_includes_model_when_set() {
        var args = NewLauncher().BuildArgs(NewCtx(model: "gpt-5.4")).Args;
        var mIdx = Array.IndexOf(args, "-m");
        await Assert.That(mIdx).IsGreaterThan(-1);
        await Assert.That(args[mIdx + 1]).IsEqualTo("gpt-5.4");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexLauncherTests/*"
```

Expected: all fail with "CodexLauncher does not exist".

- [ ] **Step 3: Implement `CodexLauncher.BuildArgs`**

Create `src/Kapacitor.Daemon/Services/CodexLauncher.cs` with the BuildArgs path:

```csharp
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace kapacitor.Daemon.Services;

internal sealed partial class CodexLauncher(
        DaemonConfig            config,
        ILogger<CodexLauncher>  logger
    ) : IHostedAgentLauncher {

    public string Vendor  => "codex";
    public string CliPath => config.CodexPath;

    static readonly string[] CriticalHookEvents = ["SessionStart", "Stop", "PermissionRequest"];

    public void Prepare(LauncherContext ctx) {
        // Implementation in Step 5 (overlay + preflight + config writer)
        throw new NotImplementedException();
    }

    public LaunchArgs BuildArgs(LauncherContext ctx) {
        var args = new List<string>();

        args.Add("--cd");
        args.Add(ctx.Worktree.Path);

        args.Add("--sandbox");
        args.Add("workspace-write");

        args.Add("--ask-for-approval");
        args.Add("on-request");

        if (!string.IsNullOrEmpty(ctx.Model)) {
            args.Add("-m");
            args.Add(ctx.Model);
        }

        var effort = ctx.Effort;
        if (!string.IsNullOrEmpty(effort) && !string.Equals(effort, "auto", StringComparison.OrdinalIgnoreCase)) {
            var mapped = string.Equals(effort, "max", StringComparison.OrdinalIgnoreCase) ? "xhigh" : effort;
            args.Add("-c");
            args.Add($"model_reasoning_effort=\"{mapped}\"");
        }

        args.Add("--no-alt-screen");

        if (!string.IsNullOrEmpty(ctx.Prompt)) {
            args.Add("--");
            args.Add(ctx.Prompt);
        }

        return new LaunchArgs(args.ToArray(), McpConfigPath: null);
    }

    public void Cleanup(AgentInstance agent) {
        // No-op: ~/.codex/config.toml trust entries are intentionally persistent.
        // Worktree paths are unique per run; cumulative entries are harmless.
    }
}
```

- [ ] **Step 4: Run `BuildArgs` tests to verify they pass**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexLauncherTests/*"
```

Expected: all eight BuildArgs tests pass.

- [ ] **Step 5: Write failing tests for `Prepare` (overlay + preflight + config writer)**

Append to `CodexLauncherTests.cs`:

```csharp
[Test]
public async Task Prepare_overlays_codex_settings_dir_from_source_repo() {
    var sourceRepo = Directory.CreateTempSubdirectory("kapacitor-codexlauncher-src-").FullName;
    var worktree = Directory.CreateTempSubdirectory("kapacitor-codexlauncher-wt-").FullName;
    var home = Directory.CreateTempSubdirectory("kapacitor-codexlauncher-home-").FullName;
    var originalHome = Environment.GetEnvironmentVariable("HOME");
    Environment.SetEnvironmentVariable("HOME", home);

    try {
        var srcCodex = Directory.CreateDirectory(Path.Combine(sourceRepo, ".codex")).FullName;
        File.WriteAllText(Path.Combine(srcCodex, "hooks.json"), """
            {"hooks":{
                "SessionStart":[{"hooks":[{"type":"command","command":"kapacitor codex-hook"}]}],
                "Stop":[{"hooks":[{"type":"command","command":"kapacitor codex-hook"}]}],
                "PermissionRequest":[{"hooks":[{"type":"command","command":"kapacitor codex-hook"}]}]
            }}
            """);

        var ctx = NewCtxWith(source: sourceRepo, worktree: worktree);
        NewLauncher().Prepare(ctx);

        await Assert.That(File.Exists(Path.Combine(worktree, ".codex", "hooks.json"))).IsTrue();
    } finally {
        Environment.SetEnvironmentVariable("HOME", originalHome);
        Directory.Delete(sourceRepo, recursive: true);
        Directory.Delete(worktree, recursive: true);
        Directory.Delete(home, recursive: true);
    }
}

[Test]
public async Task Prepare_throws_when_no_hooks_json_anywhere() {
    var sourceRepo = Directory.CreateTempSubdirectory("kapacitor-codexlauncher-src-").FullName;
    var worktree = Directory.CreateTempSubdirectory("kapacitor-codexlauncher-wt-").FullName;
    var home = Directory.CreateTempSubdirectory("kapacitor-codexlauncher-home-").FullName;
    var originalHome = Environment.GetEnvironmentVariable("HOME");
    Environment.SetEnvironmentVariable("HOME", home);

    try {
        var ctx = NewCtxWith(source: sourceRepo, worktree: worktree);

        await Assert.That(() => NewLauncher().Prepare(ctx))
            .Throws<CodexHooksNotInstalledException>()
            .WithMessage(msg => msg.Contains("kapacitor plugin install --codex"));
    } finally {
        Environment.SetEnvironmentVariable("HOME", originalHome);
        Directory.Delete(sourceRepo, recursive: true);
        Directory.Delete(worktree, recursive: true);
        Directory.Delete(home, recursive: true);
    }
}

[Test]
public async Task Prepare_succeeds_when_user_scope_hooks_json_has_all_three_critical_events() {
    var sourceRepo = Directory.CreateTempSubdirectory("kapacitor-codexlauncher-src-").FullName;
    var worktree = Directory.CreateTempSubdirectory("kapacitor-codexlauncher-wt-").FullName;
    var home = Directory.CreateTempSubdirectory("kapacitor-codexlauncher-home-").FullName;
    var originalHome = Environment.GetEnvironmentVariable("HOME");
    Environment.SetEnvironmentVariable("HOME", home);

    try {
        Directory.CreateDirectory(Path.Combine(home, ".codex"));
        File.WriteAllText(Path.Combine(home, ".codex", "hooks.json"), """
            {"hooks":{
                "SessionStart":[{"hooks":[{"type":"command","command":"kapacitor codex-hook"}]}],
                "Stop":[{"hooks":[{"type":"command","command":"kapacitor codex-hook"}]}],
                "PermissionRequest":[{"hooks":[{"type":"command","command":"kapacitor codex-hook"}]}]
            }}
            """);

        var ctx = NewCtxWith(source: sourceRepo, worktree: worktree);
        NewLauncher().Prepare(ctx);

        // config.toml gets pre-trusted
        var configPath = Path.Combine(home, ".codex", "config.toml");
        await Assert.That(File.Exists(configPath)).IsTrue();
    } finally {
        Environment.SetEnvironmentVariable("HOME", originalHome);
        Directory.Delete(sourceRepo, recursive: true);
        Directory.Delete(worktree, recursive: true);
        Directory.Delete(home, recursive: true);
    }
}

[Test]
public async Task Prepare_succeeds_when_project_scope_hooks_json_present_after_overlay() {
    // hooks live in <source>/.codex/hooks.json — the overlay copies them into worktree before preflight runs.
    var sourceRepo = Directory.CreateTempSubdirectory("kapacitor-codexlauncher-src-").FullName;
    var worktree = Directory.CreateTempSubdirectory("kapacitor-codexlauncher-wt-").FullName;
    var home = Directory.CreateTempSubdirectory("kapacitor-codexlauncher-home-").FullName;
    var originalHome = Environment.GetEnvironmentVariable("HOME");
    Environment.SetEnvironmentVariable("HOME", home);

    try {
        Directory.CreateDirectory(Path.Combine(sourceRepo, ".codex"));
        File.WriteAllText(Path.Combine(sourceRepo, ".codex", "hooks.json"), """
            {"hooks":{
                "SessionStart":[{"hooks":[{"type":"command","command":"kapacitor codex-hook"}]}],
                "Stop":[{"hooks":[{"type":"command","command":"kapacitor codex-hook"}]}],
                "PermissionRequest":[{"hooks":[{"type":"command","command":"kapacitor codex-hook"}]}]
            }}
            """);

        var ctx = NewCtxWith(source: sourceRepo, worktree: worktree);
        NewLauncher().Prepare(ctx);
        // succeeds — no throw, config.toml gets written
        await Assert.That(File.Exists(Path.Combine(home, ".codex", "config.toml"))).IsTrue();
    } finally {
        Environment.SetEnvironmentVariable("HOME", originalHome);
        Directory.Delete(sourceRepo, recursive: true);
        Directory.Delete(worktree, recursive: true);
        Directory.Delete(home, recursive: true);
    }
}

[Test]
public async Task Prepare_invokes_codex_config_writer_with_worktree_path() {
    var sourceRepo = Directory.CreateTempSubdirectory("kapacitor-codexlauncher-src-").FullName;
    var worktree = Directory.CreateTempSubdirectory("kapacitor-codexlauncher-wt-").FullName;
    var home = Directory.CreateTempSubdirectory("kapacitor-codexlauncher-home-").FullName;
    var originalHome = Environment.GetEnvironmentVariable("HOME");
    Environment.SetEnvironmentVariable("HOME", home);

    try {
        Directory.CreateDirectory(Path.Combine(home, ".codex"));
        File.WriteAllText(Path.Combine(home, ".codex", "hooks.json"), """
            {"hooks":{
                "SessionStart":[{"hooks":[{"type":"command","command":"kapacitor codex-hook"}]}],
                "Stop":[{"hooks":[{"type":"command","command":"kapacitor codex-hook"}]}],
                "PermissionRequest":[{"hooks":[{"type":"command","command":"kapacitor codex-hook"}]}]
            }}
            """);

        var ctx = NewCtxWith(source: sourceRepo, worktree: worktree);
        NewLauncher().Prepare(ctx);

        var configToml = File.ReadAllText(Path.Combine(home, ".codex", "config.toml"));
        await Assert.That(configToml).Contains($"[projects.\"{worktree}\"]");
        await Assert.That(configToml).Contains("trust_level = \"trusted\"");
    } finally {
        Environment.SetEnvironmentVariable("HOME", originalHome);
        Directory.Delete(sourceRepo, recursive: true);
        Directory.Delete(worktree, recursive: true);
        Directory.Delete(home, recursive: true);
    }
}

static LauncherContext NewCtxWith(string source, string worktree) => new(
    AgentId: "a-1",
    SourceRepoPath: source,
    Worktree: new WorktreeInfo(Path: worktree, BranchName: "br"),
    Prompt: null,
    Model: "gpt-5.3-codex",
    Effort: null,
    Tools: null,
    IsReview: false,
    Review: null,
    ReviewLaunch: null
);
```

- [ ] **Step 6: Run tests to verify they fail**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexLauncherTests/Prepare_*"
```

Expected: all `Prepare_*` tests throw `NotImplementedException`.

- [ ] **Step 7: Implement `CodexLauncher.Prepare`**

Replace the `Prepare` body in `CodexLauncher.cs`:

```csharp
public void Prepare(LauncherContext ctx) {
    // 1. Overlay source/.codex into worktree FIRST so project-scope hooks
    //    (kapacitor plugin install --codex --project) become visible to the
    //    preflight in step 2.
    try {
        var sourceCodexDir = Path.Combine(ctx.SourceRepoPath, ".codex");
        var destCodexDir   = Path.Combine(ctx.Worktree.Path, ".codex");

        if (Directory.Exists(sourceCodexDir)) {
            OverlayDirectory(sourceCodexDir, destCodexDir);
        }
    } catch (Exception ex) {
        LogOverlayFailed(ex, ctx.AgentId);
    }

    // 2. Hook preflight (fail-fast). Either worktree-scope (after overlay)
    //    OR user-scope is sufficient.
    if (!HooksInstalledIn(Path.Combine(ctx.Worktree.Path, ".codex", "hooks.json")) &&
        !HooksInstalledIn(CodexPaths.UserHooksJson)) {
        throw new CodexHooksNotInstalledException(
            "Codex hooks not installed. Run `kapacitor plugin install --codex` " +
            "(user scope) or `kapacitor plugin install --codex --project` " +
            "(project scope) and try again."
        );
    }

    // 3. Pre-trust the worktree in ~/.codex/config.toml.
    try {
        CodexConfigWriter.TrustWorktree(ctx.Worktree.Path, logger);
    } catch (Exception ex) {
        LogTrustFailed(ex, ctx.AgentId);
    }

    if (ctx.Tools is { Length: > 0 }) {
        LogToolsIgnoredForCodex(ctx.AgentId, ctx.Tools.Length);
    }
}

static bool HooksInstalledIn(string hooksPath) {
    if (!File.Exists(hooksPath)) return false;
    try {
        var root = JsonNode.Parse(File.ReadAllText(hooksPath)) as JsonObject;
        return root is not null && CodexHooksParser.HasKapacitorHooksFor(root, CriticalHookEvents);
    } catch {
        return false;
    }
}

static void OverlayDirectory(string source, string dest) {
    Directory.CreateDirectory(dest);
    foreach (var file in Directory.GetFiles(source)) {
        var destFile = Path.Combine(dest, Path.GetFileName(file));
        if (!File.Exists(destFile)) File.Copy(file, destFile);
    }
    foreach (var dir in Directory.GetDirectories(source)) {
        OverlayDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }
}

[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to overlay .codex settings for agent {AgentId} (continuing)")]
partial void LogOverlayFailed(Exception ex, string agentId);

[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to pre-trust worktree for agent {AgentId} (continuing)")]
partial void LogTrustFailed(Exception ex, string agentId);

[LoggerMessage(Level = LogLevel.Debug, Message = "Tools array of length {Count} ignored for vendor=codex (no allowlist concept) — agent {AgentId}")]
partial void LogToolsIgnoredForCodex(string agentId, int count);
```

`OverlayDirectory` is duplicated between `ClaudeLauncher` and `CodexLauncher`. Lift to a shared helper if both are in the same assembly. Either:
- Add `internal static class FileSystemOverlay` in `kapacitor.Daemon.Services` with the single `OverlayDirectory` method, OR
- Keep two copies for now and lift in a follow-up cleanup.

For this task, **lift to `FileSystemOverlay`** to avoid the duplication landing in main. Update both launchers to call `FileSystemOverlay.OverlayDirectory(...)`.

- [ ] **Step 8: Run `Prepare_*` tests to verify they pass**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexLauncherTests/Prepare_*"
```

Expected: all five `Prepare_*` tests pass.

- [ ] **Step 9: Run the full unit test suite**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: all tests pass.

- [ ] **Step 10: Commit**

```bash
git add src/Kapacitor.Daemon/Services/CodexLauncher.cs \
        src/Kapacitor.Daemon/Services/ClaudeLauncher.cs \
        src/Kapacitor.Daemon/Services/FileSystemOverlay.cs \
        test/kapacitor.Tests.Unit/CodexLauncherTests.cs
git commit -m "[AI-68] daemon: CodexLauncher with overlay + hook preflight + pre-trust"
```

---

### Task 17: `LocalPermissionBridge` per-vendor URL routing + response shapes

**Files:**
- Modify: `src/Kapacitor.Daemon/Services/LocalPermissionBridge.cs`
- Modify: `test/kapacitor.Tests.Unit/LocalPermissionBridgeTests.cs`

- [ ] **Step 1: Write failing tests for new URL shape**

In `test/kapacitor.Tests.Unit/LocalPermissionBridgeTests.cs` add:

```csharp
[Test]
public async Task Claude_path_returns_claude_response_shape() {
    // Reuse the existing test fixture pattern — substitute ServerConnection so
    // RequestPermissionAsync returns a known PermissionDecision. POST to
    // /{token}/claude/permission-request. Assert body has hookSpecificOutput
    // with applyPermissions/updatedInput fields when the decision carries them.
    // (Concrete body: mirror the existing "Claude shape" assertion from the
    // pre-split test.)
    Assert.Fail("regression: bridge URL must include /claude/ segment");
}

[Test]
public async Task Codex_path_returns_codex_response_shape() {
    // POST to /{token}/codex/permission-request. Assert body is
    // {"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}
    // WITHOUT applyPermissions/updatedInput.
    Assert.Fail("not implemented");
}

[Test]
public async Task Legacy_path_without_vendor_returns_404() {
    // POST to /{token}/permission-request — must return 404.
    Assert.Fail("not implemented");
}

[Test]
public async Task Unknown_vendor_returns_404() {
    // POST to /{token}/bogus/permission-request — must return 404.
    Assert.Fail("not implemented");
}

[Test]
public async Task Codex_path_invokes_server_with_vendor_codex() {
    // Capture ServerConnection.RequestPermissionAsync's vendor parameter.
    // POST to /{token}/codex/permission-request; assert captured vendor == "codex".
    Assert.Fail("not implemented");
}

[Test]
public async Task Claude_path_invokes_server_with_vendor_claude() {
    // POST to /{token}/claude/permission-request; assert captured vendor == "claude".
    Assert.Fail("not implemented");
}

[Test]
public async Task Codex_path_strips_apply_permissions_from_server_decision() {
    // Server decision includes applyPermissions; Codex response body must NOT contain it.
    Assert.Fail("not implemented");
}
```

Flesh out each test with concrete HttpClient POSTs against the bridge fixture, asserting status codes / body contents as listed.

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/LocalPermissionBridgeTests/*"
```

Expected: new tests fail; existing tests may still pass (legacy URL still works) until Step 3.

- [ ] **Step 3: Update the bridge URL routing**

In `src/Kapacitor.Daemon/Services/LocalPermissionBridge.cs`:

1. Widen the HTTP prefix to allow any sub-path:

```csharp
// In StartAsync:
listener.Prefixes.Add($"http://127.0.0.1:{port}/{token}/");
```

2. Replace the path-match block (around line 138):

```csharp
const string Suffix = "/permission-request";
var path = context.Request.Url?.AbsolutePath;

if (path is null || !path.StartsWith($"/{_token}/", StringComparison.Ordinal) ||
    !path.EndsWith(Suffix, StringComparison.Ordinal) ||
    context.Request.HttpMethod != "POST") {
    context.Response.StatusCode = 404;
    context.Response.Close();
    return;
}

var vendorStart = _token.Length + 2;
var vendorEnd   = path.Length - Suffix.Length;
var vendor      = vendorEnd > vendorStart ? path[vendorStart..vendorEnd] : "";

if (vendor is not ("claude" or "codex")) {
    context.Response.StatusCode = 404;
    context.Response.Close();
    return;
}
```

3. Pass `vendor` to the server call:

```csharp
decision = await server.RequestPermissionAsync(sessionId, toolName, toolInput, suggestions, vendor, ct);
```

4. Add vendor-shaped response builder:

```csharp
static string BuildHookResponseJson(PermissionDecision decision, string vendor) =>
    vendor switch {
        "claude" => BuildClaudeResponse(decision),
        "codex"  => BuildCodexResponse(decision),
        _        => throw new InvalidOperationException($"Unsupported vendor: {vendor}")
    };

static string BuildClaudeResponse(PermissionDecision decision) {
    // Current BuildHookResponseJson body verbatim — including applyPermissions / updatedInput
    var decisionNode = new JsonObject { ["behavior"] = decision.Behavior };
    if (decision.ApplyPermissions is { } ap) decisionNode["applyPermissions"] = JsonNode.Parse(ap.GetRawText());
    if (decision.UpdatedInput is { } ui)     decisionNode["updatedInput"]     = JsonNode.Parse(ui.GetRawText());

    var payload = new JsonObject {
        ["hookSpecificOutput"] = new JsonObject {
            ["hookEventName"] = "PermissionRequest",
            ["decision"]      = decisionNode
        }
    };
    return payload.ToJsonString();
}

static string BuildCodexResponse(PermissionDecision decision) {
    var payload = new JsonObject {
        ["hookSpecificOutput"] = new JsonObject {
            ["hookEventName"] = "PermissionRequest",
            ["decision"]      = new JsonObject { ["behavior"] = decision.Behavior }
        }
    };
    return payload.ToJsonString();
}
```

Replace the existing `BuildHookResponseJson(decision)` call (around line 195) with `BuildHookResponseJson(decision, vendor)`.

- [ ] **Step 4: Run bridge tests to verify they pass**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/LocalPermissionBridgeTests/*"
```

Expected: all pass.

- [ ] **Step 5: Add Claude-CLI-hook regression test**

In `LocalPermissionBridgeTests.cs` add:

```csharp
[Test]
public async Task Claude_cli_hook_post_target_lands_at_new_url() {
    // Spin up the bridge with a captured ServerConnection. Drive
    // PermissionRequestCommand against it. Assert the captured request URL
    // ends with "/claude/permission-request" (not the legacy unprefixed path).
    Assert.Fail("regression for Task 10 URL migration");
}
```

Implement against the existing test scaffolding. Run:

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/LocalPermissionBridgeTests/Claude_cli_hook_post_target_lands_at_new_url"
```

Expected: passes.

- [ ] **Step 6: Run full unit test suite**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: all tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Kapacitor.Daemon/Services/LocalPermissionBridge.cs \
        test/kapacitor.Tests.Unit/LocalPermissionBridgeTests.cs
git commit -m "[AI-68] daemon: LocalPermissionBridge per-vendor URL routing + response shapes"
```

---

### Task 18: `CodexHookCommand.HandlePermissionRequest` bridge bounce (fail-closed)

**Files:**
- Modify: `src/kapacitor/Commands/CodexHookCommand.cs`
- Modify: `test/kapacitor.Tests.Unit/CodexHookCommandTests.cs`

- [ ] **Step 1: Write failing tests**

In `test/kapacitor.Tests.Unit/CodexHookCommandTests.cs` add:

```csharp
[Test]
public async Task PermissionRequest_with_daemon_url_set_posts_to_bridge_and_forwards_response_to_stdout() {
    using var bridge = WireMockServer.Start();
    bridge.Given(Request.Create().WithPath("/abc/codex/permission-request").UsingPost())
          .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}"""));

    Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", $"http://127.0.0.1:{bridge.Ports[0]}/abc");
    try {
        var stdin = new StringReader("""{"hook_event_name":"PermissionRequest","session_id":"s1"}""");
        var stdout = new StringWriter();
        Console.SetOut(stdout);

        var exit = await CodexHookCommand.Handle("http://server", stdin);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stdout.ToString()).Contains("\"behavior\":\"allow\"");
    } finally {
        Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", null);
    }
}

[Test]
public async Task PermissionRequest_with_daemon_url_emits_deny_and_exits_nonzero_on_500() {
    using var bridge = WireMockServer.Start();
    bridge.Given(Request.Create().WithPath("/abc/codex/permission-request").UsingPost())
          .RespondWith(Response.Create().WithStatusCode(500));

    Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", $"http://127.0.0.1:{bridge.Ports[0]}/abc");
    try {
        var stdin = new StringReader("""{"hook_event_name":"PermissionRequest","session_id":"s1"}""");
        var stdout = new StringWriter();
        Console.SetOut(stdout);

        var exit = await CodexHookCommand.Handle("http://server", stdin);

        await Assert.That(exit).IsNotEqualTo(0);
        await Assert.That(stdout.ToString()).Contains("\"behavior\":\"deny\"");
    } finally {
        Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", null);
    }
}

[Test]
public async Task PermissionRequest_with_daemon_url_emits_deny_on_connection_refused() {
    Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", "http://127.0.0.1:1/abc");  // port 1 = guaranteed refused
    try {
        var stdin = new StringReader("""{"hook_event_name":"PermissionRequest","session_id":"s1"}""");
        var stdout = new StringWriter();
        Console.SetOut(stdout);

        var exit = await CodexHookCommand.Handle("http://server", stdin);

        await Assert.That(exit).IsNotEqualTo(0);
        await Assert.That(stdout.ToString()).Contains("\"behavior\":\"deny\"");
    } finally {
        Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", null);
    }
}

[Test]
public async Task PermissionRequest_with_non_loopback_daemon_url_emits_deny_without_posting() {
    using var bridge = WireMockServer.Start();
    Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", $"http://example.com:{bridge.Ports[0]}/abc");
    try {
        var stdin = new StringReader("""{"hook_event_name":"PermissionRequest","session_id":"s1"}""");
        var stdout = new StringWriter();
        Console.SetOut(stdout);

        var exit = await CodexHookCommand.Handle("http://server", stdin);

        await Assert.That(exit).IsNotEqualTo(0);
        await Assert.That(stdout.ToString()).Contains("\"behavior\":\"deny\"");
        await Assert.That(bridge.LogEntries).IsEmpty();
    } finally {
        Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", null);
    }
}

[Test]
public async Task PermissionRequest_with_https_daemon_url_emits_deny_without_posting() {
    using var bridge = WireMockServer.Start();
    Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", $"https://127.0.0.1:{bridge.Ports[0]}/abc");
    try {
        var stdin = new StringReader("""{"hook_event_name":"PermissionRequest","session_id":"s1"}""");
        var stdout = new StringWriter();
        Console.SetOut(stdout);

        var exit = await CodexHookCommand.Handle("http://server", stdin);

        await Assert.That(exit).IsNotEqualTo(0);
        await Assert.That(stdout.ToString()).Contains("\"behavior\":\"deny\"");
        await Assert.That(bridge.LogEntries).IsEmpty();
    } finally {
        Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", null);
    }
}

[Test]
public async Task PermissionRequest_without_daemon_url_still_uses_legacy_stub() {
    Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", null);
    using var server = WireMockServer.Start();
    server.Given(Request.Create().WithPath("/hooks/permission-request/codex").UsingPost())
          .RespondWith(Response.Create().WithStatusCode(200));

    var stdin = new StringReader("""{"hook_event_name":"PermissionRequest","session_id":"s1"}""");
    var stdout = new StringWriter();
    Console.SetOut(stdout);

    var exit = await CodexHookCommand.Handle($"http://127.0.0.1:{server.Ports[0]}", stdin);

    await Assert.That(exit).IsEqualTo(0);
    await Assert.That(stdout.ToString()).Contains("\"behavior\":\"allow\"");
    await Assert.That(server.LogEntries).HasCount().EqualTo(1); // server saw the informational POST
}

[Test]
public async Task PermissionRequest_with_daemon_url_does_not_double_post_to_server_hooks_endpoint() {
    using var bridge = WireMockServer.Start();
    using var server = WireMockServer.Start();
    bridge.Given(Request.Create().WithPath("/abc/codex/permission-request").UsingPost())
          .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}"""));
    server.Given(Request.Create().WithPath("/hooks/permission-request/codex").UsingPost())
          .RespondWith(Response.Create().WithStatusCode(200));

    Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", $"http://127.0.0.1:{bridge.Ports[0]}/abc");
    try {
        var stdin = new StringReader("""{"hook_event_name":"PermissionRequest","session_id":"s1"}""");
        await CodexHookCommand.Handle($"http://127.0.0.1:{server.Ports[0]}", stdin);

        await Assert.That(server.LogEntries).IsEmpty();  // server saw NO POST when daemon is set
        await Assert.That(bridge.LogEntries).HasCount().EqualTo(1);
    } finally {
        Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", null);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexHookCommandTests/PermissionRequest_*"
```

Expected: the four bridge-branch tests fail; the no-daemon test may pass (stub is current behaviour); the no-double-post test fails because today the stub always posts.

- [ ] **Step 3: Implement the bridge bounce branch**

In `src/kapacitor/Commands/CodexHookCommand.cs`, replace `HandlePermissionRequest`:

```csharp
static async Task<int> HandlePermissionRequest(string baseUrl, JsonNode node) {
    var daemonUrl = Environment.GetEnvironmentVariable("KAPACITOR_DAEMON_URL");

    if (daemonUrl is null) {
        return await HandlePermissionRequestStub(baseUrl, node);
    }

    return await HandlePermissionRequestViaBridge(daemonUrl, node);
}

static async Task<int> HandlePermissionRequestStub(string baseUrl, JsonNode node) {
    await PostHookAsync(baseUrl, "permission-request/codex", node.ToJsonString());

    var response = new JsonObject {
        ["hookSpecificOutput"] = new JsonObject {
            ["hookEventName"] = "PermissionRequest",
            ["decision"]      = new JsonObject { ["behavior"] = "allow" }
        }
    };
    Console.Write(response.ToJsonString());
    return 0;
}

static async Task<int> HandlePermissionRequestViaBridge(string daemonUrl, JsonNode node) {
    if (!DaemonBridgeUrl.TryParseLoopback(daemonUrl, out var baseUrl)) {
        Console.Error.WriteLine($"[kapacitor] codex-hook permission-request: KAPACITOR_DAEMON_URL must be http loopback, got: {daemonUrl}");
        return EmitDenyAndExitNonzero();
    }

    using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

    try {
        using var content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync($"{baseUrl}/codex/permission-request", content);

        if (!resp.IsSuccessStatusCode) {
            Console.Error.WriteLine($"[kapacitor] codex-hook permission-request bridge: HTTP {(int)resp.StatusCode}");
            return EmitDenyAndExitNonzero();
        }

        var body = await resp.Content.ReadAsStringAsync();
        Console.Write(body);
        return 0;
    } catch (Exception ex) {
        Console.Error.WriteLine($"[kapacitor] codex-hook permission-request bridge error: {ex.Message}");
        return EmitDenyAndExitNonzero();
    }
}

static int EmitDenyAndExitNonzero() {
    var response = new JsonObject {
        ["hookSpecificOutput"] = new JsonObject {
            ["hookEventName"] = "PermissionRequest",
            ["decision"]      = new JsonObject { ["behavior"] = "deny" }
        }
    };
    Console.Write(response.ToJsonString());
    return 1;
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexHookCommandTests/*"
```

Expected: all pass.

- [ ] **Step 5: Run the full unit test suite**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/kapacitor/Commands/CodexHookCommand.cs \
        test/kapacitor.Tests.Unit/CodexHookCommandTests.cs
git commit -m "[AI-68] cli: codex-hook PermissionRequest bridges through daemon (fail-closed)"
```

---

### Task 19: DI registration in `Program.cs`

**Files:**
- Modify: `src/kapacitor/Program.cs` (daemon entry point — verify path)

- [ ] **Step 1: Locate the daemon's DI setup**

Find the daemon's `Program.cs` or composition root where `AgentOrchestrator` is registered. (May live in `src/Kapacitor.Daemon/Program.cs` rather than the CLI's `src/kapacitor/Program.cs`. Use:)

```bash
grep -rn "AgentOrchestrator" src/ --include="*.cs" | grep -v "Tests"
```

Identify the registration site.

- [ ] **Step 2: Register both launchers and the dictionary**

Add to the DI registration block:

```csharp
services.AddSingleton<IHostedAgentLauncher, ClaudeLauncher>();
services.AddSingleton<IHostedAgentLauncher, CodexLauncher>();
services.AddSingleton<IReadOnlyDictionary<string, IHostedAgentLauncher>>(sp =>
    sp.GetServices<IHostedAgentLauncher>().ToDictionary(l => l.Vendor));
```

- [ ] **Step 3: Build to verify**

```bash
dotnet build src/Kapacitor.Daemon/Kapacitor.Daemon.csproj
```

Expected: succeeds.

- [ ] **Step 4: Run integration tests**

```bash
dotnet run --project test/kapacitor.Tests.Integration/kapacitor.Tests.Integration.csproj
```

Expected: all pass. (Integration tests exercise the real DI graph and would catch missing registrations.)

- [ ] **Step 5: Commit**

```bash
git add src/kapacitor/Program.cs  # or wherever Program.cs lives
git commit -m "[AI-68] daemon: DI registers ClaudeLauncher + CodexLauncher"
```

---

### Task 20: README + help-text updates

**Files:**
- Modify: `README.md`
- Modify: `src/Kapacitor.Core/Resources/help-codex-hook.txt`

- [ ] **Step 1: Read current README for context**

```bash
cat README.md | head -100
```

Find the `## Getting started` quick-start, the `kapacitor agent start` section, the `kapacitor codex-hook` section, and any daemon-config docs.

- [ ] **Step 2: Update README sections**

In `README.md`:

- Quick-start (under `## Getting started`): add a one-liner noting that the daemon now supports `vendor: codex` hosted agents (macOS + Linux) in addition to Claude.
- `kapacitor agent start` section: add a "Hosted Codex" subsection mentioning the prerequisites (`kapacitor plugin install --codex` first; defaults to `--sandbox workspace-write` + `--ask-for-approval on-request`).
- `kapacitor codex-hook` section: note that `PermissionRequest` bounces through the local daemon when `KAPACITOR_DAEMON_URL` is set; outside daemon context, the stub allow-response is unchanged.
- Daemon-config section (or add one): document both `ClaudePath` (default `"claude"`) and `CodexPath` (default `"codex"`).

- [ ] **Step 3: Update help text**

In `src/Kapacitor.Core/Resources/help-codex-hook.txt`, add a one-paragraph note explaining the bridge-bounce behaviour for hosted (daemon-launched) Codex agents.

- [ ] **Step 4: Build to bundle the new resource**

```bash
dotnet build src/Kapacitor.Core/Kapacitor.Core.csproj
```

Expected: succeeds.

- [ ] **Step 5: Commit**

```bash
git add README.md src/Kapacitor.Core/Resources/help-codex-hook.txt
git commit -m "[AI-68] docs: README + help text for hosted Codex agents"
```

---

### Task 21: Final AOT verification gate

**Files:** (none modified — verification only)

- [ ] **Step 1: AOT publish both projects**

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | tee /tmp/aot-cli.log
dotnet publish src/Kapacitor.Daemon/Kapacitor.Daemon.csproj -c Release 2>&1 | tee /tmp/aot-daemon.log
```

- [ ] **Step 2: Grep for warnings**

```bash
grep -E 'IL[23][01][0-9]{2}' /tmp/aot-cli.log /tmp/aot-daemon.log
```

Expected: zero matches.

If matches: identify the responsible call site (likely a Tomlyn pathway), apply the spec §5.3 fallback (typed source-gen context), re-run.

- [ ] **Step 3: Run all tests**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
dotnet run --project test/kapacitor.Tests.Integration/kapacitor.Tests.Integration.csproj
```

Expected: all green.

- [ ] **Step 4: Manual smoke checklist (PR-time)**

The following are PR-review manual checks per AI-68 acceptance — document in the PR description but do NOT run as part of this task:

- Launch a Codex hosted agent against a real worktree, run a multi-turn session with a tool call and a permission prompt, verify the terminal renders in the dashboard and the permission round-trips.
- Repeat the launch on a fresh machine with NO `~/.codex/hooks.json` — verify `LaunchFailed` with the actionable "Run `kapacitor plugin install --codex`" message.
- Repeat the launch on a machine with project-scope hooks (`<source-repo>/.codex/hooks.json`) but no user-scope hooks — verify launch succeeds after overlay.

- [ ] **Step 5: Commit (no changes — just marker)**

```bash
git commit --allow-empty -m "[AI-68] verify: AOT clean + all tests green"
```

---

## Self-review

After writing all 21 tasks, the plan checks against the spec:

- ✅ §2 Spike — captured in plan preamble; no implementation step needed
- ✅ §3.1 LaunchAgentCommand.Vendor — Task 7
- ✅ §3.2 AgentRunStarted.Vendor — Task 8
- ✅ §3.3 LocalPermissionBridge URLs — Task 17
- ✅ §3.4 DaemonConfig.CodexPath — Task 15
- ⚠️ §3.5 Server-side — explicitly scoped out; cross-references kapacitor-server's paired plan
- ✅ §4.1 IHostedAgentLauncher — Task 11
- ✅ §4.2 ClaudeLauncher — Task 12
- ✅ §4.3 CodexLauncher — Task 16
- ✅ §4.4 Orchestrator changes — Tasks 13, 14
- ✅ §4.5 Composition root — Task 19
- ✅ §5 CodexConfigWriter — Tasks 1, 6
- ✅ §6 LocalPermissionBridge per-vendor — Task 17
- ✅ §7.0 Claude CLI hook URL — Task 10
- ✅ §7.1-7.3 Codex CLI hook bridge — Task 18
- ✅ §8 Tests — distributed across the relevant tasks
- ✅ §9 Docs — Task 20
- ✅ §10.1 File touch list — covered task-by-task
- ⚠️ §10.2 Server repo — scoped out; paired plan
- ✅ §12 Acceptance mapping — Task 21 manual checklist

No placeholders found. Type names and method signatures referenced in later tasks (`LauncherContext`, `LaunchArgs`, `CodexHooksParser.HasKapacitorHooksFor`, `DaemonBridgeUrl.TryParseLoopback`, `CodexHooksNotInstalledException`) match their defining tasks (11, 2, 4, 5 respectively).

---

## Execution handoff

Plan complete and saved to `docs/superpowers/plans/2026-05-14-ai-68-codex-hosted-agents.md`. Two execution options:

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

Which approach?
