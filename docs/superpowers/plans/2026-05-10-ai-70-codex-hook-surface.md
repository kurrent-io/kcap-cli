# AI-70 Codex Hook Surface Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring live Codex sessions into the Capacitor dashboard. Adds a `kapacitor codex-hook` dispatcher in the CLI, a `kapacitor plugin install --codex` mode that writes `~/.codex/hooks.json`, end-to-end vendor threading from spawned watcher down to `SendTranscriptBatch`, and a vendor-aware extension to the existing server hook routes.

**Architecture:** Codex's six hook events (`SessionStart`, `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `PermissionRequest`, `Stop`) all invoke a single binary with `hook_event_name` in the JSON payload, so the **CLI** ships **one** dispatcher command (`codex-hook`) that branches on event name. The CLI maps Codex's event vocabulary onto Capacitor's canonical hook vocabulary before posting (Codex `Stop` → `/hooks/session-end/codex`; pass-through `UserPromptSubmit`/`PreToolUse`/`PostToolUse` are swallowed in v1 since neither vendor consumes them). The **server** keeps a single route per hook event but adds a vendor segment via ASP.NET route default — `/hooks/session-start/{vendor=claude}` matches both `/hooks/session-start` (legacy Claude) and `/hooks/session-start/codex`. Existing DTOs (`SessionStartHook`, `SessionEndHook`) loosen two required fields (`Source`, `Reason`) to nullable; handlers gain a `string vendor` parameter, default the loosened fields per vendor, and **400 if vendor=="claude" and the field is null** — preserving Claude's contract while letting the path-driven Codex case relax cleanly. The watcher is already vendor-aware on the wire (AI-75 added `vendor` as the 6th `SendTranscriptBatch` arg) but hardcodes `"claude"`; we thread an actual `--vendor` argument through `kapacitor watch`, `WatcherManager.SpawnWatcher`, and `WatcherManager.InlineDrainAsync`.

**Tech Stack:** .NET 10 NativeAOT (CLI) / .NET 10 (server), TUnit, WireMock.Net for HTTP mocking, `KurrentDbFixture` for server integration tests, `System.Text.Json.Nodes` for hook payload manipulation. JSON config writes use `JsonObject`/`JsonArray` constructors (not collection-expression syntax — see CLAUDE.md "JsonArray collection expressions" warning).

**Linear:** [AI-70](https://linear.app/kurrent/issue/AI-70/codex-hook-surface-cli-command-plugin-install-server-route-group)

## Cross-repo working directories

This plan spans two git repositories.

- **CLI repo** (`@kurrent/kapacitor` npm package, NativeAOT binary):
  - Path: `/Users/alexey/dev/eventstore/kapacitor`
  - Worktree for this issue: `/Users/alexey/dev/eventstore/kapacitor/.capacitor/worktrees/agent-05e74395770b4a`
  - Branch: `capacitor/agent-05e74395770b4a`
- **Server repo** (Kurrent.Capacitor):
  - Path: `/Users/alexey/dev/eventstore/kapacitor-server`
  - Create a worktree for AI-70 server work before starting Task 10: from the server repo root, `git worktree add .capacitor/worktrees/ai-70 -b ai-70-codex-hook-surface main` (or use the `superpowers:using-git-worktrees` skill).

Each repo gets its own branch and PR — both titled with the `[AI-70]` prefix.

## Wire contract (CLI ↔ server)

| Codex event from Codex CLI | CLI POSTs to | Server route template | Notes |
|---|---|---|---|
| `SessionStart` | `/hooks/session-start/codex` | `/hooks/session-start/{vendor=claude}` | CLI omits `source` (server defaults to `Startup` for codex). |
| `Stop` | `/hooks/session-end/codex` | `/hooks/session-end/{vendor=claude}` | CLI omits `reason` (server defaults to `UserExit` for codex). Codex has no separate session-end hook per AI-67 spike. |
| `PermissionRequest` | `/hooks/permission-request/codex` | `/hooks/permission-request/{vendor=claude}` | DTO already vendor-neutral; v1 CLI returns `{behavior: "allow"}` stub locally without server long-poll. |
| `UserPromptSubmit` | _(swallowed by CLI)_ | _(none)_ | Informational; neither vendor consumes them in v1. |
| `PreToolUse`        | _(swallowed by CLI)_ | _(none)_ | Same. |
| `PostToolUse`       | _(swallowed by CLI)_ | _(none)_ | Same. |

Existing Claude clients keep posting to `/hooks/session-start` (no vendor segment) — the route default `{vendor=claude}` catches them unchanged.

## Out of scope (follow-ups)

- Full daemon-bridge translation for Codex permissions — minimal stub returns `{behavior: "allow"}` in v1; richer translation lands with hosted Codex agents in AI-68.
- `.codex/config.toml` inline `[[hooks.X]]` writer — `~/.codex/hooks.json` is sufficient per Codex precedence rules.
- Server-side pass-through routes for `UserPromptSubmit`/`PreToolUse`/`PostToolUse` — not needed in v1.
- Top-clusters / plan-content / slug-resolution for Codex SessionStart — Claude-specific UX; can be added later if Codex sessions also benefit.
- Live end-to-end smoke (start real Codex, verify dashboard) — gated on both repos shipping; covered in AI-67's parent acceptance.

---

## File map

### CLI repo

**Create:**
- `src/kapacitor/Commands/CodexHookCommand.cs`
- `src/Kapacitor.Core/Resources/help-codex-hook.txt`
- `test/kapacitor.Tests.Unit/CodexHookCommandTests.cs`
- `test/kapacitor.Tests.Unit/PluginCommandCodexTests.cs`
- `test/kapacitor.Tests.Unit/StatusCommandHooksTests.cs`
- `test/kapacitor.Tests.Unit/WatcherManagerSpawnArgsTests.cs`

**Modify:**
- `src/Kapacitor.Core/CodexPaths.cs` — expose `Home`, add `UserHooksJson`.
- `src/kapacitor/Commands/WatchCommand.cs` — add `string vendor = "claude"` parameter; replace hardcoded `"claude"` in `SendTranscriptBatch`.
- `src/kapacitor/WatcherManager.cs` — add `vendor` to `SpawnWatcher`, `EnsureWatcherRunning`, `InlineDrainAsync`; extract `BuildSpawnArgs` for testability.
- `src/kapacitor/Program.cs` — register `codex-hook` command; parse `--vendor` flag in `watch`.
- `src/kapacitor/Commands/PluginCommand.cs` — add `--codex` install/remove modes.
- `src/kapacitor/Commands/StatusCommand.cs` — add Hooks section; expose `IsClaudePluginInstalled` / `IsCodexHooksInstalled`.
- `src/Kapacitor.Core/Resources/help-usage.txt`, `help-plugin.txt`, `help-status.txt` — surface `--codex`.
- `test/kapacitor.Tests.Unit/CodexPathsTests.cs`, `WatchCommandTests.cs`, `HookForwardingTests.cs` — extend coverage.

### Server repo

**Create:**
- `test/kapacitor.Tests.Integration/CodexHookRoundTripTests.cs`

**Modify:**
- `src/Kurrent.Capacitor/Models.cs` — relax `SessionStartHook.Source` and `SessionEndHook.Reason` to nullable; add optional `TurnId` to `HookBase` for Codex turn-scoped hooks.
- `src/Kurrent.Capacitor/Sessions/RouteGroups.cs` — change existing `/session-start`, `/session-end`, `/permission-request` route templates to include `{vendor=claude}`.
- `src/Kurrent.Capacitor/Sessions/SessionHookHandlers.cs` — add `string vendor` parameter to `HandleSessionStart`, `HandleSessionEnd`, `HandlePermissionRequest`; validate Claude-required fields in handler; default fields for codex vendor; tag canonical event metadata with vendor.

---

# Part 1 — CLI repo

## Task 1: Expose `CodexPaths.Home` and `CodexPaths.UserHooksJson`

The `Home` field is currently `private static readonly`. The new `CodexHookCommand`, `PluginCommand --codex`, and `StatusCommand` all need the path to `~/.codex/hooks.json`. Make `Home` accessible and add a `UserHooksJson` property.

**Repo:** CLI

**Files:**
- Modify: `src/Kapacitor.Core/CodexPaths.cs:3-6`
- Test: `test/kapacitor.Tests.Unit/CodexPathsTests.cs` (existing — add one assertion)

- [ ] **Step 1: Write the failing test**

Append to `test/kapacitor.Tests.Unit/CodexPathsTests.cs` (inside the existing `CodexPathsTests` class):

```csharp
[Test]
public async Task UserHooksJson_resolves_under_home_codex() {
    var expected = Path.Combine(PathHelpers.HomeDirectory, ".codex", "hooks.json");
    await Assert.That(CodexPaths.UserHooksJson).IsEqualTo(expected);
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj \
  --treenode-filter "/*/*/CodexPathsTests/UserHooksJson_resolves_under_home_codex"
```

Expected: FAIL — `UserHooksJson` does not exist.

- [ ] **Step 3: Implement minimal change**

In `src/Kapacitor.Core/CodexPaths.cs`, change line 4 and add a property. Inline the path twice rather than reference `Home` from another static initializer (C# static initializer order is brittle when fields reference each other):

```csharp
static class CodexPaths {
    public static string Home          { get; } = Path.Combine(PathHelpers.HomeDirectory, ".codex");
    public static string Sessions      { get; } = Path.Combine(Path.Combine(PathHelpers.HomeDirectory, ".codex"), "sessions");
    public static string UserHooksJson { get; } = Path.Combine(Path.Combine(PathHelpers.HomeDirectory, ".codex"), "hooks.json");
    // ... rest unchanged ...
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj \
  --treenode-filter "/*/*/CodexPathsTests/UserHooksJson_resolves_under_home_codex"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Kapacitor.Core/CodexPaths.cs test/kapacitor.Tests.Unit/CodexPathsTests.cs
git commit -m "[AI-70] CodexPaths: expose Home and UserHooksJson"
```

---

## Task 2: Thread `vendor` through `kapacitor watch`

`WatchCommand.RunWatch` currently sends `"claude"` hardcoded as the 6th `SendTranscriptBatch` arg. AI-75 added the wire-protocol slot — we just need to plumb a real value down from the CLI. Codex hook will spawn watchers with `--vendor codex`.

**Repo:** CLI

**Files:**
- Modify: `src/kapacitor/Commands/WatchCommand.cs:11-19, 436`
- Modify: `src/kapacitor/Program.cs:420-450`
- Test: `test/kapacitor.Tests.Unit/WatchCommandTests.cs` (existing)

- [ ] **Step 1: Write the failing test**

Append to `test/kapacitor.Tests.Unit/WatchCommandTests.cs`:

```csharp
[Test]
public async Task RunWatch_signature_accepts_vendor_arg() {
    // We can't run a real watcher in a unit test (it'd open SignalR). The
    // hook round-trip integration test exercises the wire path; this guards
    // the signature.
    var method      = typeof(WatchCommand).GetMethod(nameof(WatchCommand.RunWatch))!;
    var vendorParam = method.GetParameters().FirstOrDefault(p => p.Name == "vendor");
    await Assert.That(vendorParam).IsNotNull();
    await Assert.That(vendorParam!.HasDefaultValue).IsTrue();
    await Assert.That(vendorParam.DefaultValue).IsEqualTo("claude");
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj \
  --treenode-filter "/*/*/WatchCommandTests/RunWatch_signature_accepts_vendor_arg"
```

Expected: FAIL — `vendorParam` is null.

- [ ] **Step 3: Add `vendor` param to `RunWatch`**

In `src/kapacitor/Commands/WatchCommand.cs:11-19`, change the signature:

```csharp
public static async Task<int> RunWatch(
        string  baseUrl,
        string  sessionId,
        string  transcriptPath,
        string? agentId,
        string? cwd,
        bool    skipTitle = false,
        int?    parentPid = null,
        string  vendor    = "claude"
    ) {
```

`DrainNewLines` is a static method that doesn't currently have `vendor` in scope. Change its signature:

```csharp
static async Task DrainNewLines(
        HubConnection     hubConnection,
        string            sessionId,
        string            transcriptPath,
        string?           agentId,
        WatchState        state,
        string            vendor,
        CancellationToken ct
    ) {
```

In the `DrainNewLines` body at line 436, replace the hardcoded `"claude"`:

```csharp
await hubConnection.InvokeAsync(
    "SendTranscriptBatch",
    sessionId,
    agentId,
    newLines.ToArray(),
    newLineNumbers.ToArray(),
    repoJson,
    vendor,
    ct
);
```

Update both call sites in `RunWatch` (lines ~190 and ~209) to pass `vendor`:

```csharp
await DrainNewLines(hubConnection, sessionId, transcriptPath, agentId, state, vendor, cts.Token);
// ...
await DrainNewLines(hubConnection, sessionId, transcriptPath, agentId, state, vendor, CancellationToken.None);
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj \
  --treenode-filter "/*/*/WatchCommandTests/RunWatch_signature_accepts_vendor_arg"
```

Expected: PASS.

- [ ] **Step 5: Wire `--vendor` flag into the CLI dispatcher**

In `src/kapacitor/Program.cs:420-450` (the `case "watch":` block), add `--vendor` parsing:

```csharp
case "watch": {
    var     watchSessionId = args[1].Replace("-", "");
    var     watchPath      = args[2];
    string? watchAgentId   = null;
    string? watchCwd       = null;
    var     agentIdIdx     = Array.IndexOf(args, "--agent-id");

    if (agentIdIdx >= 0 && agentIdIdx + 1 < args.Length) {
        watchAgentId = args[agentIdIdx + 1].Replace("-", "");
    }

    var cwdIdx = Array.IndexOf(args, "--cwd");

    if (cwdIdx >= 0 && cwdIdx + 1 < args.Length) {
        watchCwd = args[cwdIdx + 1];
    }

    var watchSkipTitle = Array.IndexOf(args, "--skip-title") >= 0;

    int? parentPid    = null;
    var  parentPidIdx = Array.IndexOf(args, "--parent-pid");

    if (parentPidIdx >= 0 && parentPidIdx + 1 < args.Length && int.TryParse(args[parentPidIdx + 1], out var ppid)) {
        parentPid = ppid;
    }

    var watchVendor = GetArg(args, "--vendor") ?? "claude";

    return await WatchCommand.RunWatch(
        baseUrl!, watchSessionId, watchPath, watchAgentId, watchCwd,
        watchSkipTitle, parentPid, watchVendor
    );
}
```

Also extend the usage line above (line 421):

```csharp
case "watch" when args.Length < 3:
    Console.Error.WriteLine("Usage: kapacitor watch <sessionId> <transcriptPath> [--agent-id <agentId>] [--cwd <cwd>] [--skip-title] [--parent-pid <pid>] [--vendor claude|codex]");

    return 1;
```

- [ ] **Step 6: Verify build still passes**

```bash
dotnet build src/kapacitor/kapacitor.csproj
```

Expected: success, no errors.

- [ ] **Step 7: Commit**

```bash
git add src/kapacitor/Commands/WatchCommand.cs src/kapacitor/Program.cs test/kapacitor.Tests.Unit/WatchCommandTests.cs
git commit -m "[AI-70] kapacitor watch: thread --vendor through SendTranscriptBatch"
```

---

## Task 3: Thread `vendor` through `WatcherManager.SpawnWatcher` / `EnsureWatcherRunning`

`SpawnWatcher` builds the `kapacitor watch` argument string. Add a `vendor` parameter; when non-default it appends `--vendor codex`. Extract arg-building into a testable helper.

**Repo:** CLI

**Files:**
- Modify: `src/kapacitor/WatcherManager.cs:16-79, 163-179`
- Test: new `test/kapacitor.Tests.Unit/WatcherManagerSpawnArgsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/kapacitor.Tests.Unit/WatcherManagerSpawnArgsTests.cs`:

```csharp
namespace kapacitor.Tests.Unit;

public class WatcherManagerSpawnArgsTests {
    [Test]
    public async Task BuildSpawnArgs_default_vendor_omits_flag() {
        var args = WatcherManager.BuildSpawnArgs(
            key: "abc", transcriptPath: "/tmp/t.jsonl",
            agentId: null, sessionIdOverride: null,
            cwd: null, skipTitle: false, parentPid: null, vendor: "claude"
        );

        await Assert.That(args).Contains("watch abc \"/tmp/t.jsonl\"");
        await Assert.That(args).DoesNotContain("--vendor");
    }

    [Test]
    public async Task BuildSpawnArgs_codex_vendor_appends_flag() {
        var args = WatcherManager.BuildSpawnArgs(
            key: "abc", transcriptPath: "/tmp/t.jsonl",
            agentId: null, sessionIdOverride: null,
            cwd: null, skipTitle: false, parentPid: null, vendor: "codex"
        );

        await Assert.That(args).Contains("--vendor codex");
    }

    [Test]
    public async Task BuildSpawnArgs_with_agent_uses_session_override() {
        var args = WatcherManager.BuildSpawnArgs(
            key: "sess-agent", transcriptPath: "/tmp/t.jsonl",
            agentId: "agent1", sessionIdOverride: "sess",
            cwd: "/repo", skipTitle: true, parentPid: 4242, vendor: "claude"
        );

        await Assert.That(args).Contains("watch sess \"/tmp/t.jsonl\" --agent-id agent1");
        await Assert.That(args).Contains("--cwd \"/repo\"");
        await Assert.That(args).Contains("--skip-title");
        await Assert.That(args).Contains("--parent-pid 4242");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj \
  --treenode-filter "/*/*/WatcherManagerSpawnArgsTests/*"
```

Expected: FAIL with compile error (`BuildSpawnArgs` not defined).

- [ ] **Step 3: Extract `BuildSpawnArgs` and add `vendor` parameter**

In `src/kapacitor/WatcherManager.cs`, add the new internal helper above `SpawnWatcher`:

```csharp
internal static string BuildSpawnArgs(
        string  key,
        string  transcriptPath,
        string? agentId,
        string? sessionIdOverride,
        string? cwd,
        bool    skipTitle,
        int?    parentPid,
        string  vendor
    ) {
    var sessionId = sessionIdOverride ?? key;

    var arguments = agentId is not null
        ? $"watch {sessionId} \"{transcriptPath}\" --agent-id {agentId}"
        : $"watch {key} \"{transcriptPath}\"";

    if (cwd is not null) {
        arguments += $" --cwd \"{cwd}\"";
    }

    if (skipTitle) {
        arguments += " --skip-title";
    }

    if (parentPid is { } ppid && ppid > 1) {
        arguments += $" --parent-pid {ppid}";
    }

    if (vendor != "claude") {
        arguments += $" --vendor {vendor}";
    }

    return arguments;
}
```

Then update `SpawnWatcher` to take the vendor param and use the helper. Replace lines 16-79 with:

```csharp
public static async Task SpawnWatcher(
        string  baseUrl,
        string  key,
        string  transcriptPath,
        string? agentId,
        string? sessionIdOverride = null,
        string? cwd               = null,
        bool    skipTitle         = false,
        string  vendor            = "claude"
    ) {
    try {
        var watcherDir = GetWatcherDir();
        Directory.CreateDirectory(watcherDir);

        var kapacitorPath = Environment.ProcessPath ?? "kapacitor";
        var parentPid     = ProcessHelpers.GetParentPid();
        var arguments     = BuildSpawnArgs(key, transcriptPath, agentId, sessionIdOverride, cwd, skipTitle, parentPid, vendor);

        var psi = new ProcessStartInfo(kapacitorPath, arguments) {
            RedirectStandardOutput = true,
            RedirectStandardInput  = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            Environment            = { ["KAPACITOR_URL"] = baseUrl }
        };

        var process = Process.Start(psi);

        if (process is null) {
            await Console.Error.WriteLineAsync($"Failed to spawn watcher for {key}");

            return;
        }

        process.StandardInput.Close();
        process.StandardOutput.Close();
        process.StandardError.Close();

        await File.WriteAllTextAsync(GetPidFilePath(key), process.Id.ToString());
        await Console.Out.WriteLineAsync($"Spawned watcher for {key} (PID {process.Id})");
    } catch (Exception ex) {
        await Console.Error.WriteLineAsync($"Failed to spawn watcher for {key}: {ex.Message}");
    }
}
```

And update `EnsureWatcherRunning` (lines 163-179):

```csharp
public static async Task EnsureWatcherRunning(
        string  baseUrl,
        string  key,
        string  transcriptPath,
        string? agentId,
        string? sessionIdOverride = null,
        string? cwd               = null,
        bool    skipTitle         = false,
        string  vendor            = "claude"
    ) {
    if (IsWatcherAlive(key)) {
        return;
    }

    await Console.Out.WriteLineAsync($"Watcher {key} not running, respawning...");
    await SpawnWatcher(baseUrl, key, transcriptPath, agentId, sessionIdOverride, cwd, skipTitle, vendor);
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj \
  --treenode-filter "/*/*/WatcherManagerSpawnArgsTests/*"
```

Expected: PASS (all three).

- [ ] **Step 5: Commit**

```bash
git add src/kapacitor/WatcherManager.cs test/kapacitor.Tests.Unit/WatcherManagerSpawnArgsTests.cs
git commit -m "[AI-70] WatcherManager: thread vendor through SpawnWatcher"
```

---

## Task 4: Thread `vendor` through `WatcherManager.InlineDrainAsync`

Inline drain is invoked from `session-end` and `subagent-stop` (Claude). The Codex hook's `Stop` handler will also call it. Currently the `TranscriptBatch` doesn't set `Vendor`, so server-side normalizer-selection falls back to Claude even for Codex. Fix the parameter and the field.

**Repo:** CLI

**Files:**
- Modify: `src/kapacitor/WatcherManager.cs:217-301`
- Test: extend `test/kapacitor.Tests.Unit/HookForwardingTests.cs` — there's already an `InlineDrainTests` class with `PostsCorrectBatch`

- [ ] **Step 1: Write the failing test**

Append to `test/kapacitor.Tests.Unit/HookForwardingTests.cs` inside the `InlineDrainTests` class:

```csharp
[Test]
public async Task PostsCorrectBatch_with_codex_vendor_when_specified() {
    const string sessionId = "test-session-codex-drain";

    _server.Given(Request.Create().WithPath($"/api/sessions/{sessionId}/last-line").UsingGet())
        .RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"last_line_number": -1}""")
        );

    _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
        .RespondWith(Response.Create().WithStatusCode(200));

    var dir            = Path.Combine(Path.GetTempPath(), $"kapacitor_test_{Guid.NewGuid():N}");
    var transcriptPath = Path.Combine(dir, "rollout.jsonl");
    Directory.CreateDirectory(dir);

    try {
        await File.WriteAllTextAsync(
            transcriptPath,
            """{"timestamp":"2026-05-07T15:50:21.989Z","type":"session_meta","payload":{}}""" + "\n"
        );

        await WatcherManager.InlineDrainAsync(_server.Url!, sessionId, transcriptPath, agentId: null, vendor: "codex");

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/transcript").UsingPost());

        await Assert.That(requests.Count).IsEqualTo(1);

        var root = JsonDocument.Parse(requests[0].RequestMessage.Body!).RootElement;
        await Assert.That(root.GetProperty("vendor").GetString()).IsEqualTo("codex");
    } finally {
        Directory.Delete(dir, recursive: true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj \
  --treenode-filter "/*/*/InlineDrainTests/PostsCorrectBatch_with_codex_vendor_when_specified"
```

Expected: FAIL — compile error (vendor parameter doesn't exist).

- [ ] **Step 3: Add `vendor` parameter and set on TranscriptBatch**

In `src/kapacitor/WatcherManager.cs:217`, change the signature:

```csharp
public static async Task InlineDrainAsync(
        string  baseUrl,
        string  sessionId,
        string  transcriptPath,
        string? agentId,
        string  vendor = "claude"
    ) {
```

In the same method around line 274-279, set `Vendor`:

```csharp
var batch = new TranscriptBatch {
    SessionId   = sessionId,
    AgentId     = agentId,
    Lines       = [..newLines],
    LineNumbers = [..newLineNumbers],
    Vendor      = vendor == "claude" ? null : vendor
};
```

(Match the convention from `SessionImporter.PostTranscriptBatch`: `null` for Claude so old server behaviour is unchanged, the literal string for any other vendor.)

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj \
  --treenode-filter "/*/*/InlineDrainTests/*"
```

Expected: PASS (both `PostsCorrectBatch` and the new codex test).

- [ ] **Step 5: Commit**

```bash
git add src/kapacitor/WatcherManager.cs test/kapacitor.Tests.Unit/HookForwardingTests.cs
git commit -m "[AI-70] InlineDrainAsync: thread vendor onto TranscriptBatch"
```

---

## Task 5: Add `kapacitor codex-hook` dispatcher

Single command that reads stdin, branches on `hook_event_name`, and POSTs to the canonical Capacitor hook routes with the `/codex` vendor segment. Defines the wire contract that the server-side route changes (Tasks 10-11) consume.

**Repo:** CLI

**Files:**
- Create: `src/kapacitor/Commands/CodexHookCommand.cs`
- Modify: `src/kapacitor/Program.cs` (register the command)
- Test: `test/kapacitor.Tests.Unit/CodexHookCommandTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/kapacitor.Tests.Unit/CodexHookCommandTests.cs`:

```csharp
using System.Text.Json;
using kapacitor.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace kapacitor.Tests.Unit;

public class CodexHookCommandTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task SessionStart_posts_to_session_start_codex_with_normalized_session_id() {
        _server.Given(Request.Create().WithPath("/hooks/session-start/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var payload = """
            {
              "hook_event_name": "SessionStart",
              "session_id": "019e0322-05fc-7570-be65-75719c3ea861",
              "transcript_path": "/tmp/rollout.jsonl",
              "cwd": "/tmp",
              "model": "gpt-5"
            }
            """;

        var exit = await CodexHookCommand.Handle(_server.Url!, new StringReader(payload));

        await Assert.That(exit).IsEqualTo(0);

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/codex").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        var root = JsonDocument.Parse(requests[0].RequestMessage.Body!).RootElement;
        await Assert.That(root.GetProperty("session_id").GetString()).IsEqualTo("019e032205fc7570be6575719c3ea861");
        await Assert.That(root.GetProperty("home_dir").GetString()).IsNotNull();
    }

    [Test]
    public async Task Stop_maps_to_session_end_codex_route() {
        _server.Given(Request.Create().WithPath("/hooks/session-end/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var payload = """
            {
              "hook_event_name": "Stop",
              "session_id": "abc",
              "transcript_path": "/tmp/rollout.jsonl",
              "cwd": "/tmp"
            }
            """;

        var exit = await CodexHookCommand.Handle(_server.Url!, new StringReader(payload));
        await Assert.That(exit).IsEqualTo(0);

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/codex").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        // Confirm Stop is NOT posted to /hooks/stop or /hooks/codex/stop —
        // this guards the URL-mapping decision against future regressions.
        var wrong1 = _server.FindLogEntries(Request.Create().WithPath("/hooks/stop").UsingPost());
        var wrong2 = _server.FindLogEntries(Request.Create().WithPath("/hooks/codex/stop").UsingPost());
        await Assert.That(wrong1.Count).IsEqualTo(0);
        await Assert.That(wrong2.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PermissionRequest_returns_default_allow_decision() {
        _server.Given(Request.Create().WithPath("/hooks/permission-request/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var payload = """
            {
              "hook_event_name": "PermissionRequest",
              "session_id": "abc",
              "transcript_path": "/tmp/r.jsonl",
              "cwd": "/tmp",
              "tool_name": "shell",
              "tool_input": { "command": "ls" }
            }
            """;

        var stdoutWriter = new StringWriter();
        Console.SetOut(stdoutWriter);

        var exit = await CodexHookCommand.Handle(_server.Url!, new StringReader(payload));

        await Assert.That(exit).IsEqualTo(0);
        var stdout = stdoutWriter.ToString();
        var doc    = JsonDocument.Parse(stdout);
        await Assert.That(doc.RootElement
            .GetProperty("hookSpecificOutput")
            .GetProperty("decision")
            .GetProperty("behavior")
            .GetString())
            .IsEqualTo("allow");
    }

    [Test]
    public async Task UserPromptSubmit_PreToolUse_PostToolUse_are_swallowed() {
        // v1: pass-through events not consumed server-side. CLI should
        // exit 0 without making any HTTP request.
        foreach (var evt in new[] { "UserPromptSubmit", "PreToolUse", "PostToolUse" }) {
            var payload = $$"""
                {
                  "hook_event_name": "{{evt}}",
                  "session_id": "abc",
                  "transcript_path": "/tmp/r.jsonl",
                  "cwd": "/tmp"
                }
                """;

            var exit = await CodexHookCommand.Handle(_server.Url!, new StringReader(payload));
            await Assert.That(exit).IsEqualTo(0);
        }

        // No HTTP requests should have been issued.
        await Assert.That(_server.LogEntries.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task Unknown_event_returns_zero_and_no_request() {
        var payload = """{"hook_event_name": "BogusEvent", "session_id": "abc"}""";

        var exit = await CodexHookCommand.Handle(_server.Url!, new StringReader(payload));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task Missing_hook_event_name_returns_zero_silently() {
        var payload = """{"session_id": "abc"}""";

        var exit = await CodexHookCommand.Handle(_server.Url!, new StringReader(payload));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task Malformed_json_returns_zero_silently() {
        var payload = "{not json";

        var exit = await CodexHookCommand.Handle(_server.Url!, new StringReader(payload));

        await Assert.That(exit).IsEqualTo(0);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj \
  --treenode-filter "/*/*/CodexHookCommandTests/*"
```

Expected: FAIL — `CodexHookCommand` doesn't exist.

- [ ] **Step 3: Create the dispatcher**

Create `src/kapacitor/Commands/CodexHookCommand.cs`:

```csharp
using System.Text;
using System.Text.Json.Nodes;

namespace kapacitor.Commands;

/// <summary>
/// Single-binary dispatcher for Codex hooks. Codex invokes the same command
/// for every hook event with <c>hook_event_name</c> in the JSON payload, so
/// we collapse the six event handlers behind one CLI entry point rather than
/// minting one subcommand per event the way the Claude path does.
/// </summary>
/// <remarks>
/// Wire contract (Codex event → server route):
///   SessionStart      → POST /hooks/session-start/codex
///   Stop              → POST /hooks/session-end/codex (Codex has no separate session-end hook)
///   PermissionRequest → POST /hooks/permission-request/codex (informational; CLI returns local stub)
///   UserPromptSubmit  → swallowed (v1 — neither vendor consumes them)
///   PreToolUse        → swallowed
///   PostToolUse       → swallowed
/// </remarks>
static class CodexHookCommand {
    public static async Task<int> Handle(string baseUrl, TextReader stdin) {
        var body = await stdin.ReadToEndAsync();

        JsonNode? node;

        try {
            node = JsonNode.Parse(body);
        } catch {
            // Best effort — never crash the host CLI on a malformed payload.
            return 0;
        }

        if (node is null) return 0;

        var eventName = node["hook_event_name"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(eventName)) return 0;

        // Normalize session_id to dashless GUID, inject home_dir, and tag the
        // agent host id when running inside a daemon-spawned agent. Mirrors
        // the Claude hook path in Program.cs but without the disabled-session
        // and plan_content branches (those are Claude-specific).
        NormalizeGuidField(node, "session_id");

        node["home_dir"] = PathHelpers.HomeDirectory;

        var agentHostId = Environment.GetEnvironmentVariable("KAPACITOR_AGENT_ID");
        if (agentHostId is not null) {
            node["agent_host_id"] = agentHostId;
        }

        return eventName switch {
            "SessionStart"      => await HandleSessionStart(baseUrl, node),
            "Stop"              => await HandleStop(baseUrl, node),
            "PermissionRequest" => await HandlePermissionRequest(baseUrl, node),
            "UserPromptSubmit"
              or "PreToolUse"
              or "PostToolUse"  => 0,  // v1: swallow informational events
            _                   => 0   // unknown — silently ignore
        };
    }

    static async Task<int> HandleSessionStart(string baseUrl, JsonNode node) {
        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(node.ToJsonString());

        var exit = await PostHookAsync(baseUrl, "session-start/codex", enriched);
        if (exit != 0) return exit;

        var enrichedNode = JsonNode.Parse(enriched);
        var sessionId    = enrichedNode?["session_id"]?.GetValue<string>();
        var transcript   = enrichedNode?["transcript_path"]?.GetValue<string>();
        var cwd          = enrichedNode?["cwd"]?.GetValue<string>();

        if (sessionId is not null && transcript is not null) {
            await WatcherManager.EnsureWatcherRunning(
                baseUrl, sessionId, transcript,
                agentId: null, sessionIdOverride: null, cwd: cwd,
                skipTitle: false, vendor: "codex"
            );
        }

        return 0;
    }

    static async Task<int> HandleStop(string baseUrl, JsonNode node) {
        var sessionId  = node["session_id"]?.GetValue<string>();
        var transcript = node["transcript_path"]?.GetValue<string>();

        // Codex Stop is the closest analog to Claude's session-end (Codex has
        // no separate session-end hook — see AI-67 spike). Kill the watcher
        // BEFORE posting so the transcript is fully drained before the server
        // computes session-end stats.
        if (sessionId is not null) {
            await WatcherManager.KillWatcher(sessionId);

            if (transcript is not null) {
                await WatcherManager.InlineDrainAsync(baseUrl, sessionId, transcript, agentId: null, vendor: "codex");
            }
        }

        // Note the URL: session-END/codex, not stop/codex. The CLI translates
        // Codex's hook event name into Capacitor's canonical hook vocabulary
        // before posting, so the server doesn't have to know about Codex's
        // missing session-end concept.
        return await PostHookAsync(baseUrl, "session-end/codex", node.ToJsonString());
    }

    static async Task<int> HandlePermissionRequest(string baseUrl, JsonNode node) {
        // v1 stub — record the event server-side and return a default allow
        // so recording-only sessions don't block. Full daemon-bridge
        // translation lands in AI-68 with hosted Codex agents.
        await PostHookAsync(baseUrl, "permission-request/codex", node.ToJsonString());

        var response = new JsonObject {
            ["hookSpecificOutput"] = new JsonObject {
                ["hookEventName"] = "PermissionRequest",
                ["decision"] = new JsonObject { ["behavior"] = "allow" }
            }
        };

        Console.Write(response.ToJsonString());
        return 0;
    }

    static async Task<int> PostHookAsync(string baseUrl, string endpoint, string body) {
        using var client  = await HttpClientExtensions.CreateAuthenticatedClientAsync();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        try {
            var resp = await client.PostWithRetryAsync($"{baseUrl}/hooks/{endpoint}", content);
            if (!resp.IsSuccessStatusCode) {
                Console.Error.WriteLine($"[kapacitor] codex-hook {endpoint}: HTTP {(int)resp.StatusCode}");
                return 1;
            }

            return 0;
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl, ex);
            return 1;
        }
    }

    static void NormalizeGuidField(JsonNode node, string fieldName) {
        var value = node[fieldName]?.GetValue<string>();

        if (value is not null && value.Contains('-')) {
            node[fieldName] = value.Replace("-", "");
        }
    }
}
```

- [ ] **Step 4: Register the command in `Program.cs`**

In `src/kapacitor/Program.cs`, add the command case before the `if (!hookCommands.Contains(command))` guard (around line 504):

```csharp
    case "codex-hook":
        return await CodexHookCommand.Handle(baseUrl!, Console.In);
```

(Don't add `codex-hook` to `offlineCommands` — like Claude hooks, it needs the server URL and should fail clearly when missing.)

- [ ] **Step 5: Run all CodexHookCommand tests**

```bash
dotnet test test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj \
  --treenode-filter "/*/*/CodexHookCommandTests/*"
```

Expected: PASS (all seven).

- [ ] **Step 6: Verify build still passes**

```bash
dotnet build src/kapacitor/kapacitor.csproj
```

Expected: success.

- [ ] **Step 7: Commit**

```bash
git add src/kapacitor/Commands/CodexHookCommand.cs src/kapacitor/Program.cs test/kapacitor.Tests.Unit/CodexHookCommandTests.cs
git commit -m "[AI-70] kapacitor codex-hook: dispatcher for SessionStart/Stop/PermissionRequest"
```

---

## Task 6: `kapacitor plugin install --codex` writes `~/.codex/hooks.json`

When `--codex` is passed, write a hooks.json that maps each Codex event to `{type: "command", command: "kapacitor codex-hook", timeout: 30}`. Project mode (`--project --codex`) writes to `<cwd>/.codex/hooks.json` and prints a warning that the user must trust the directory in Codex.

**Repo:** CLI

**Files:**
- Modify: `src/kapacitor/Commands/PluginCommand.cs`
- Test: new `test/kapacitor.Tests.Unit/PluginCommandCodexTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/kapacitor.Tests.Unit/PluginCommandCodexTests.cs`:

```csharp
using System.Text.Json.Nodes;
using kapacitor.Commands;

namespace kapacitor.Tests.Unit;

public class PluginCommandCodexTests {
    [Test]
    public async Task InstallCodexHooks_writes_all_six_events() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        var ok = PluginCommand.InstallCodexHooks(path);
        await Assert.That(ok).IsTrue();

        var root  = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var hooks = root["hooks"]!.AsObject();

        foreach (var evt in new[] { "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest", "Stop" }) {
            await Assert.That(hooks[evt]).IsNotNull();
            var entries = hooks[evt]!.AsArray();
            await Assert.That(entries.Count).IsEqualTo(1);

            var inner = entries[0]!["hooks"]!.AsArray();
            await Assert.That(inner[0]!["type"]!.GetValue<string>()).IsEqualTo("command");
            await Assert.That(inner[0]!["command"]!.GetValue<string>()).IsEqualTo("kapacitor codex-hook");
            await Assert.That(inner[0]!["timeout"]!.GetValue<int>()).IsEqualTo(30);
        }
    }

    [Test]
    public async Task InstallCodexHooks_preserves_other_top_level_keys() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        await File.WriteAllTextAsync(path, """{"some_other_setting": true}""");

        PluginCommand.InstallCodexHooks(path);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        await Assert.That(root["some_other_setting"]!.GetValue<bool>()).IsTrue();
        await Assert.That(root["hooks"]).IsNotNull();
    }

    [Test]
    public async Task InstallCodexHooks_overwrites_existing_kapacitor_entries() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        await File.WriteAllTextAsync(path, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "kapacitor codex-hook", "timeout": 5 }] },
                  { "hooks": [{ "type": "command", "command": "/usr/local/bin/other", "timeout": 5 }] }
                ]
              }
            }
            """);

        PluginCommand.InstallCodexHooks(path);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var sessionStart = root["hooks"]!["SessionStart"]!.AsArray();

        var commands = sessionStart
            .SelectMany(e => e!["hooks"]!.AsArray())
            .Select(h => h!["command"]!.GetValue<string>())
            .ToList();

        await Assert.That(commands).Contains("kapacitor codex-hook");
        await Assert.That(commands).Contains("/usr/local/bin/other");
        await Assert.That(commands.Count(c => c == "kapacitor codex-hook")).IsEqualTo(1);
    }

    [Test]
    public async Task RemoveCodexHooks_clears_all_kapacitor_entries() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        PluginCommand.InstallCodexHooks(path);
        var ok = PluginCommand.RemoveCodexHooks(path);
        await Assert.That(ok).IsTrue();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var hooks = root["hooks"]?.AsObject();

        if (hooks is not null) {
            foreach (var (_, value) in hooks) {
                var commands = value!.AsArray()
                    .SelectMany(e => e!["hooks"]!.AsArray())
                    .Select(h => h!["command"]!.GetValue<string>());

                foreach (var cmd in commands) {
                    await Assert.That(cmd).DoesNotContain("kapacitor codex-hook");
                }
            }
        }
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kapacitor-test-{Guid.NewGuid().ToString("N")[..8]}"
        );
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj \
  --treenode-filter "/*/*/PluginCommandCodexTests/*"
```

Expected: FAIL — `InstallCodexHooks` and `RemoveCodexHooks` don't exist.

- [ ] **Step 3: Implement `InstallCodexHooks` / `RemoveCodexHooks` and wire `--codex`**

Replace the body of `src/kapacitor/Commands/PluginCommand.cs` with:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace kapacitor.Commands;

public static class PluginCommand {
    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    static readonly string[] CodexHookEvents = [
        "SessionStart",
        "UserPromptSubmit",
        "PreToolUse",
        "PostToolUse",
        "PermissionRequest",
        "Stop"
    ];

    const string CodexHookCommand = "kapacitor codex-hook";

    public static async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) {
            PrintUsage();

            return 1;
        }

        return args[1] switch {
            "install" => await Install(args),
            "remove"  => await Remove(args),
            _         => PrintUsage()
        };
    }

    static async Task<int> Install(string[] args) {
        if (args.Contains("--codex")) {
            return await InstallCodex(args);
        }

        return await InstallClaude(args);
    }

    static async Task<int> Remove(string[] args) {
        if (args.Contains("--codex")) {
            return await RemoveCodex(args);
        }

        return await RemoveClaude(args);
    }

    static async Task<int> InstallClaude(string[] args) {
        var pluginPath = SetupCommand.ResolvePluginPath();

        if (pluginPath is null) {
            await Console.Error.WriteLineAsync("Plugin directory not found. Re-install kapacitor via npm:");
            await Console.Error.WriteLineAsync("  npm install -g @kurrent/kapacitor");

            return 1;
        }

        var scope = args.Contains("--project") ? "project" : "user";

        var settingsPath = scope == "project"
            ? Path.Combine(Environment.CurrentDirectory, ".claude", "settings.local.json")
            : ClaudePaths.UserSettings;

        var installed = SetupCommand.InstallPlugin(settingsPath, pluginPath);

        if (installed) {
            await Console.Out.WriteLineAsync($"Plugin installed ({scope}: {settingsPath})");
        } else {
            await Console.Error.WriteLineAsync("Could not update settings file.");

            return 1;
        }

        return 0;
    }

    static async Task<int> RemoveClaude(string[] args) {
        var scope = args.Contains("--project") ? "project" : "user";

        var settingsPath = scope == "project"
            ? Path.Combine(Environment.CurrentDirectory, ".claude", "settings.local.json")
            : ClaudePaths.UserSettings;

        if (!File.Exists(settingsPath)) {
            await Console.Out.WriteLineAsync("Nothing to remove — settings file not found.");

            return 0;
        }

        try {
            var text = await File.ReadAllTextAsync(settingsPath);

            if (JsonNode.Parse(text) is not JsonObject root) {
                await Console.Out.WriteLineAsync("Nothing to remove.");

                return 0;
            }

            var changed = false;

            if (root["enabledPlugins"] is JsonObject enabled) {
                changed |= enabled.Remove("kapacitor@kapacitor");
                changed |= enabled.Remove("kapacitor@kurrent");
            }

            if (root["extraKnownMarketplaces"] is JsonObject marketplaces) {
                changed |= marketplaces.Remove("kapacitor");
                changed |= marketplaces.Remove("kurrent");
            }

            if (changed) {
                await File.WriteAllTextAsync(settingsPath, root.ToJsonString(WriteOpts));
                await Console.Out.WriteLineAsync($"Plugin removed ({scope}: {settingsPath})");
            } else {
                await Console.Out.WriteLineAsync("Plugin was not installed.");
            }

            return 0;
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"Could not update settings: {ex.Message}");

            return 1;
        }
    }

    static async Task<int> InstallCodex(string[] args) {
        var scope = args.Contains("--project") ? "project" : "user";

        var hooksPath = scope == "project"
            ? Path.Combine(Environment.CurrentDirectory, ".codex", "hooks.json")
            : CodexPaths.UserHooksJson;

        if (!InstallCodexHooks(hooksPath)) {
            await Console.Error.WriteLineAsync("Could not write Codex hooks file.");

            return 1;
        }

        await Console.Out.WriteLineAsync($"Codex hooks installed ({scope}: {hooksPath})");

        if (scope == "project") {
            await Console.Out.WriteLineAsync(
                "Note: Codex requires the project's .codex directory to be trusted. " +
                "Run `codex` once in this directory and accept the trust prompt."
            );
        }

        return 0;
    }

    static async Task<int> RemoveCodex(string[] args) {
        var scope = args.Contains("--project") ? "project" : "user";

        var hooksPath = scope == "project"
            ? Path.Combine(Environment.CurrentDirectory, ".codex", "hooks.json")
            : CodexPaths.UserHooksJson;

        if (!File.Exists(hooksPath)) {
            await Console.Out.WriteLineAsync("Nothing to remove — hooks file not found.");

            return 0;
        }

        if (RemoveCodexHooks(hooksPath)) {
            await Console.Out.WriteLineAsync($"Codex hooks removed ({scope}: {hooksPath})");
        } else {
            await Console.Out.WriteLineAsync("Codex hooks were not installed.");
        }

        return 0;
    }

    /// <summary>
    /// Writes (or merges into) <paramref name="hooksPath"/> a hooks.json that
    /// invokes <c>kapacitor codex-hook</c> for every Codex event. Existing
    /// non-kapacitor entries are preserved; existing kapacitor entries are
    /// replaced (so the timeout/command stay current after a CLI upgrade).
    /// </summary>
    public static bool InstallCodexHooks(string hooksPath) {
        try {
            JsonObject root = [];

            if (File.Exists(hooksPath)) {
                try {
                    if (JsonNode.Parse(File.ReadAllText(hooksPath)) is JsonObject obj) root = obj;
                } catch {
                    // Malformed — start fresh
                }
            }

            if (root["hooks"] is not JsonObject hooks) {
                hooks         = [];
                root["hooks"] = hooks;
            }

            foreach (var evt in CodexHookEvents) {
                var kapacitorEntry = new JsonObject {
                    ["hooks"] = new JsonArray(
                        new JsonObject {
                            ["type"]    = "command",
                            ["command"] = CodexHookCommand,
                            ["timeout"] = 30
                        }
                    )
                };

                if (hooks[evt] is not JsonArray entries) {
                    hooks[evt] = new JsonArray(kapacitorEntry);
                    continue;
                }

                var preserved = new JsonArray();

                foreach (var entry in entries) {
                    if (entry is null) continue;

                    var hasKapacitorCommand = entry["hooks"] is JsonArray inner && inner.Any(h =>
                        h?["command"]?.GetValue<string>()?.Contains("kapacitor codex-hook") == true);

                    if (!hasKapacitorCommand) {
                        preserved.Add(entry.DeepClone());
                    }
                }

                preserved.Add(kapacitorEntry);
                hooks[evt] = preserved;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(hooksPath)!);
            File.WriteAllText(hooksPath, root.ToJsonString(WriteOpts));

            return true;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Removes every entry in <paramref name="hooksPath"/> whose command
    /// invokes <c>kapacitor codex-hook</c>. Other entries are preserved.
    /// Returns true if any entries were removed.
    /// </summary>
    public static bool RemoveCodexHooks(string hooksPath) {
        try {
            if (!File.Exists(hooksPath)) return false;

            if (JsonNode.Parse(File.ReadAllText(hooksPath)) is not JsonObject root) return false;
            if (root["hooks"] is not JsonObject hooks) return false;

            var changed = false;

            foreach (var evt in CodexHookEvents) {
                if (hooks[evt] is not JsonArray entries) continue;

                var preserved = new JsonArray();

                foreach (var entry in entries) {
                    if (entry is null) continue;

                    var hasKapacitorCommand = entry["hooks"] is JsonArray inner && inner.Any(h =>
                        h?["command"]?.GetValue<string>()?.Contains("kapacitor codex-hook") == true);

                    if (hasKapacitorCommand) {
                        changed = true;
                    } else {
                        preserved.Add(entry.DeepClone());
                    }
                }

                hooks[evt] = preserved;
            }

            if (changed) {
                File.WriteAllText(hooksPath, root.ToJsonString(WriteOpts));
            }

            return changed;
        } catch {
            return false;
        }
    }

    static int PrintUsage() {
        Console.Error.WriteLine("Usage: kapacitor plugin <install|remove> [--project] [--codex]");

        return 1;
    }
}
```

- [ ] **Step 4: Run all PluginCommand tests**

```bash
dotnet test test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj \
  --treenode-filter "/*/*/PluginCommandCodexTests/*"
```

Expected: PASS (all four).

- [ ] **Step 5: Verify existing Claude install/remove tests still pass**

```bash
dotnet test test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj \
  --treenode-filter "/*/*/SetupCommandTests/*"
```

Expected: PASS (all five — no regressions).

- [ ] **Step 6: Commit**

```bash
git add src/kapacitor/Commands/PluginCommand.cs test/kapacitor.Tests.Unit/PluginCommandCodexTests.cs
git commit -m "[AI-70] kapacitor plugin install --codex writes ~/.codex/hooks.json"
```

---

## Task 7: `kapacitor status` reports Codex hook installation

Add a "Hooks" section that shows installation state for both Claude (`~/.claude/settings.json`'s `enabledPlugins["kapacitor@kapacitor"]`) and Codex (`~/.codex/hooks.json` with at least one entry referencing `kapacitor codex-hook`).

**Repo:** CLI

**Files:**
- Modify: `src/kapacitor/Commands/StatusCommand.cs`
- Test: new `test/kapacitor.Tests.Unit/StatusCommandHooksTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/kapacitor.Tests.Unit/StatusCommandHooksTests.cs`:

```csharp
using kapacitor.Commands;

namespace kapacitor.Tests.Unit;

public class StatusCommandHooksTests {
    [Test]
    public async Task DetectsClaudePlugin_when_enabled() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "settings.json");

        await File.WriteAllTextAsync(path, """
            { "enabledPlugins": { "kapacitor@kapacitor": true } }
            """);

        await Assert.That(StatusCommand.IsClaudePluginInstalled(path)).IsTrue();
    }

    [Test]
    public async Task DetectsClaudePlugin_disabled_when_false() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "settings.json");

        await File.WriteAllTextAsync(path, """
            { "enabledPlugins": { "kapacitor@kapacitor": false } }
            """);

        await Assert.That(StatusCommand.IsClaudePluginInstalled(path)).IsFalse();
    }

    [Test]
    public async Task DetectsClaudePlugin_missing_when_file_absent() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "settings.json");

        await Assert.That(StatusCommand.IsClaudePluginInstalled(path)).IsFalse();
    }

    [Test]
    public async Task DetectsCodexHooks_when_kapacitor_command_present() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        await File.WriteAllTextAsync(path, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "kapacitor codex-hook", "timeout": 30 }] }
                ]
              }
            }
            """);

        await Assert.That(StatusCommand.IsCodexHooksInstalled(path)).IsTrue();
    }

    [Test]
    public async Task DetectsCodexHooks_missing_when_no_kapacitor_command() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        await File.WriteAllTextAsync(path, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "/usr/local/bin/other", "timeout": 5 }] }
                ]
              }
            }
            """);

        await Assert.That(StatusCommand.IsCodexHooksInstalled(path)).IsFalse();
    }

    [Test]
    public async Task DetectsCodexHooks_missing_when_file_absent() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        await Assert.That(StatusCommand.IsCodexHooksInstalled(path)).IsFalse();
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kapacitor-test-{Guid.NewGuid().ToString("N")[..8]}"
        );
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj \
  --treenode-filter "/*/*/StatusCommandHooksTests/*"
```

Expected: FAIL — both helpers don't exist.

- [ ] **Step 3: Add detection helpers and a Hooks section to `StatusCommand.HandleAsync`**

Replace the body of `src/kapacitor/Commands/StatusCommand.cs`:

```csharp
using System.Text.Json.Nodes;
using kapacitor.Auth;

namespace kapacitor.Commands;

public static class StatusCommand {
    public static async Task<int> HandleAsync(string? baseUrl) {
        // Server
        Console.Write("  Server:  ");

        if (baseUrl is null) {
            await Console.Out.WriteLineAsync("not configured");
        } else {
            Console.Write($"{baseUrl} ");

            try {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(5);
                var resp = await http.GetAsync($"{baseUrl}/auth/config");
                await Console.Out.WriteLineAsync(resp.IsSuccessStatusCode ? "✓ reachable" : $"✗ HTTP {(int)resp.StatusCode}");
            } catch {
                await Console.Out.WriteLineAsync("✗ unreachable");
            }
        }

        // Auth
        Console.Write("  Auth:    ");
        var tokens = await TokenStore.GetValidTokensAsync();

        if (tokens is not null) {
            var remaining = tokens.ExpiresAt - DateTimeOffset.UtcNow;

            var expiryText = remaining.TotalHours > 1
                ? $"expires in {remaining.TotalHours:F0}h"
                : $"expires in {remaining.TotalMinutes:F0}m";
            await Console.Out.WriteLineAsync($"{tokens.GitHubUsername} ({tokens.Provider}) ✓ token valid ({expiryText})");
        } else {
            var rawTokens = await TokenStore.LoadAsync();

            await Console.Out.WriteLineAsync(rawTokens is not null
                ? $"{rawTokens.GitHubUsername} ({rawTokens.Provider}) ✗ token expired"
                : "not authenticated (run: kapacitor login)");
        }

        // Hooks
        await Console.Out.WriteAsync("  Hooks:   ");

        var claudeInstalled = IsClaudePluginInstalled(ClaudePaths.UserSettings);
        var codexInstalled  = IsCodexHooksInstalled(CodexPaths.UserHooksJson);

        var parts = new List<string>();
        parts.Add(claudeInstalled ? "Claude ✓" : "Claude ✗");
        parts.Add(codexInstalled ? "Codex ✓" : "Codex ✗");

        await Console.Out.WriteLineAsync(string.Join("  ", parts));

        // Agent
        Console.Write("  Agent:   ");

        var pidPath = PathHelpers.ConfigPath("agent.pid");

        if (File.Exists(pidPath)) {
            var firstLine = (await File.ReadAllTextAsync(pidPath))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (int.TryParse(firstLine, out var pid)) {
                try {
                    System.Diagnostics.Process.GetProcessById(pid);
                    await Console.Out.WriteLineAsync($"running (PID {pid})");
                } catch (ArgumentException) {
                    await Console.Out.WriteLineAsync("not running (stale PID file)");
                }
            } else {
                await Console.Out.WriteLineAsync("unknown (invalid PID file)");
            }
        } else {
            await Console.Out.WriteLineAsync("not running");
        }

        return 0;
    }

    /// <summary>
    /// True iff <paramref name="settingsPath"/> exists and has
    /// <c>enabledPlugins["kapacitor@kapacitor"] == true</c>.
    /// </summary>
    public static bool IsClaudePluginInstalled(string settingsPath) {
        try {
            if (!File.Exists(settingsPath)) return false;
            if (JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject root) return false;
            if (root["enabledPlugins"] is not JsonObject enabled) return false;

            return enabled["kapacitor@kapacitor"]?.GetValue<bool>() == true;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// True iff <paramref name="hooksPath"/> exists and any hook entry under any
    /// event references the <c>kapacitor codex-hook</c> command.
    /// </summary>
    public static bool IsCodexHooksInstalled(string hooksPath) {
        try {
            if (!File.Exists(hooksPath)) return false;
            if (JsonNode.Parse(File.ReadAllText(hooksPath)) is not JsonObject root) return false;
            if (root["hooks"] is not JsonObject hooks) return false;

            foreach (var (_, value) in hooks) {
                if (value is not JsonArray entries) continue;

                foreach (var entry in entries) {
                    if (entry?["hooks"] is not JsonArray inner) continue;

                    foreach (var hook in inner) {
                        if (hook?["command"]?.GetValue<string>()?.Contains("kapacitor codex-hook") == true) {
                            return true;
                        }
                    }
                }
            }

            return false;
        } catch {
            return false;
        }
    }
}
```

- [ ] **Step 4: Run StatusCommand tests**

```bash
dotnet test test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj \
  --treenode-filter "/*/*/StatusCommandHooksTests/*"
```

Expected: PASS (all six).

- [ ] **Step 5: Commit**

```bash
git add src/kapacitor/Commands/StatusCommand.cs test/kapacitor.Tests.Unit/StatusCommandHooksTests.cs
git commit -m "[AI-70] kapacitor status: report Claude and Codex hook installation"
```

---

## Task 8: Help text updates

Surface `--codex` flags in `--help` output and add a per-command help page.

**Repo:** CLI

**Files:**
- Modify: `src/Kapacitor.Core/Resources/help-usage.txt`
- Modify: `src/Kapacitor.Core/Resources/help-plugin.txt`
- Modify: `src/Kapacitor.Core/Resources/help-status.txt`
- Create: `src/Kapacitor.Core/Resources/help-codex-hook.txt`

- [ ] **Step 1: Update `help-usage.txt`**

In `src/Kapacitor.Core/Resources/help-usage.txt`, change the Plugin section:

```
Plugin:
  plugin install [--project] [--codex]   Register hooks (Claude or --codex for Codex)
  plugin remove  [--project] [--codex]   Remove hooks (Claude or --codex for Codex)
```

Append to the Hook Commands section (just below `{hookCommands}`):

```
  codex-hook                       Codex hooks dispatcher (single binary, branches on hook_event_name)
```

- [ ] **Step 2: Replace `help-plugin.txt`**

```
kapacitor plugin — Manage hooks for Claude Code and Codex CLI

Usage: kapacitor plugin <subcommand> [options]

Subcommands:
  install             Register the plugin / hooks
  remove              Remove the plugin / hooks

Options:
  --project           Apply to current project only (default: user-wide)
  --codex             Target Codex CLI (~/.codex/hooks.json) instead of Claude Code

Examples:
  kapacitor plugin install                  # Install Claude Code plugin (user)
  kapacitor plugin install --codex          # Install Codex hooks (~/.codex/hooks.json)
  kapacitor plugin install --project --codex  # Install Codex hooks in <repo>/.codex/hooks.json
  kapacitor plugin remove --codex           # Remove Codex hooks
```

- [ ] **Step 3: Replace `help-status.txt`**

```
kapacitor status — Show server, auth, hook, and agent status

Usage: kapacitor status

Reports:
  Server     Configured base URL and reachability check
  Auth       Stored credentials and token expiry
  Hooks      Whether the Claude plugin and Codex hooks are installed
  Agent      Whether the agent daemon is running (PID file check)
```

- [ ] **Step 4: Create `help-codex-hook.txt`**

```
kapacitor codex-hook — Codex hooks dispatcher (internal — used by ~/.codex/hooks.json)

Usage: kapacitor codex-hook < <hook-payload-json>

Reads a Codex hook payload from stdin, branches on `hook_event_name`, and
forwards to the Capacitor server. Wired via `kapacitor plugin install --codex`.

Hook events handled (Codex event → server route):
  SessionStart        → POST /hooks/session-start/codex (also spawns watcher)
  Stop                → POST /hooks/session-end/codex   (kills watcher, drains transcript first)
  PermissionRequest   → POST /hooks/permission-request/codex (returns local {behavior: "allow"})
  UserPromptSubmit    → swallowed (informational; no server route in v1)
  PreToolUse          → swallowed (informational; no server route in v1)
  PostToolUse         → swallowed (informational; no server route in v1)

This command is not intended to be invoked directly. Use `kapacitor plugin
install --codex` to register it via Codex's hooks framework.
```

- [ ] **Step 5: Verify the help text loads**

```bash
dotnet build src/kapacitor/kapacitor.csproj
dotnet run --project src/kapacitor/kapacitor.csproj --no-build -- --help | grep -E "(codex-hook|--codex)"
```

Expected: lines mentioning `--codex` (in plugin section) and `codex-hook` (in hooks section).

```bash
dotnet run --project src/kapacitor/kapacitor.csproj --no-build -- plugin --help | grep "\-\-codex"
```

Expected: at least three lines mentioning `--codex`.

- [ ] **Step 6: Commit**

```bash
git add src/Kapacitor.Core/Resources/help-usage.txt src/Kapacitor.Core/Resources/help-plugin.txt src/Kapacitor.Core/Resources/help-status.txt src/Kapacitor.Core/Resources/help-codex-hook.txt
git commit -m "[AI-70] docs: surface --codex flags in help output"
```

---

## Task 9: Open the CLI PR

The CLI side is feature-complete and can land independently — `plugin install --codex` is opt-in, so the CLI doesn't change behavior for existing Claude users. Server work (Tasks 10-12) lands in a separate PR in the kapacitor-server repo.

**Repo:** CLI

- [ ] **Step 1: Run the full unit test suite**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: all tests pass. Per the project's CI policy, do NOT assume a failing test is pre-existing — investigate.

- [ ] **Step 2: Run the integration test suite**

```bash
dotnet run --project test/kapacitor.Tests.Integration/kapacitor.Tests.Integration.csproj
```

Expected: all tests pass.

- [ ] **Step 3: Verify AOT publish has no IL warnings**

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

Expected: no output.

- [ ] **Step 4: Smoke test `plugin install --codex`**

```bash
export ORIG_HOME=$HOME
export HOME=$(mktemp -d)
trap "rm -rf $HOME; export HOME=$ORIG_HOME" EXIT

dotnet run --project src/kapacitor/kapacitor.csproj --no-build -- plugin install --codex
test -f $HOME/.codex/hooks.json && echo OK || echo FAIL
cat $HOME/.codex/hooks.json | head -20
```

Expected: `OK` followed by JSON containing all six events.

- [ ] **Step 5: Push and open the CLI PR**

```bash
git push -u origin capacitor/agent-05e74395770b4a
gh pr create --title "[AI-70] CLI: Codex hook surface (codex-hook command + plugin install --codex)" --body "$(cat <<'EOF'
## Summary
- Adds `kapacitor codex-hook` — single dispatcher for Codex hooks. Maps Codex's hook vocabulary onto Capacitor's canonical hook routes:
  - `SessionStart` → POST `/hooks/session-start/codex` (spawns watcher with `--vendor codex`)
  - `Stop` → POST `/hooks/session-end/codex` (Codex has no separate session-end hook per AI-67 spike)
  - `PermissionRequest` → POST `/hooks/permission-request/codex` (returns local `{behavior: "allow"}` stub for v1)
  - `UserPromptSubmit`/`PreToolUse`/`PostToolUse` → swallowed (informational; no server route in v1)
- Adds `kapacitor plugin install --codex` (and `remove --codex`) writing `~/.codex/hooks.json` (or `<repo>/.codex/hooks.json` with `--project`).
- Extends `kapacitor status` to report installation state for both Claude and Codex hook surfaces.
- Threads a real `--vendor` argument through `kapacitor watch` → `WatcherManager.SpawnWatcher` → `SendTranscriptBatch`, replacing the hardcoded `"claude"` left in place when AI-75 added the wire-protocol slot. Codex sessions now tag `TranscriptBatch.vendor = "codex"` end-to-end (live watcher path and inline drain).

Server-side route changes (path-param vendor segment + DTO loosening) land separately in the kapacitor-server repo (companion PR).

## Test plan
- [x] Unit tests cover hook dispatch + URL mapping, plugin install/remove, status detection, watcher arg builder, inline drain vendor tagging.
- [x] AOT publish reports no IL3050/IL2026 warnings.
- [x] Smoke: `kapacitor plugin install --codex` writes a valid hooks.json.
- [ ] Live integration smoke (start Codex, see session in dashboard) — gated on companion server PR.
EOF
)"
```

Note the PR URL — referenced in the server PR body in Task 12.

---

# Part 2 — Server repo

> Switch to the server repo: create a worktree at `/Users/alexey/dev/eventstore/kapacitor-server/.capacitor/worktrees/ai-70` (`git worktree add .capacitor/worktrees/ai-70 -b ai-70-codex-hook-surface main` from the server repo root). All Part 2 paths are **relative to that worktree**.

## Task 10: Loosen `SessionStartHook.Source` and `SessionEndHook.Reason` to nullable

Codex's wire payload doesn't include Claude-specific enum fields (`source`, `reason`). To use the same DTO for both vendors, make those two fields nullable. The handlers in Task 11 will validate (`vendor=="claude"` ⇒ field must be non-null) and default for Codex.

**Repo:** Server

**Files:**
- Modify: `src/Kurrent.Capacitor/Models.cs:44-79`

- [ ] **Step 1: Loosen `SessionStartHook.Source`**

In `src/Kurrent.Capacitor/Models.cs`, change line 45-46:

```csharp
public record SessionStartHook : HookBase {
    [JsonPropertyName("source")]
    public SessionStartSource? Source { get; init; }   // ← was: required SessionStartSource

    // ... rest unchanged ...
}
```

- [ ] **Step 2: Loosen `SessionEndHook.Reason`**

In the same file, change line 73-74:

```csharp
public record SessionEndHook : HookBase {
    [JsonPropertyName("reason")]
    public SessionEndReason? Reason { get; init; }   // ← was: required SessionEndReason

    // ... rest unchanged ...
}
```

- [ ] **Step 3: Add optional `TurnId` to `HookBase` for Codex turn-scoped hooks**

In the same file, after line 39 (the `AgentHostId` field):

```csharp
    /// <summary>
    /// Codex turn-scoped hooks (PreToolUse, PostToolUse, PermissionRequest)
    /// carry a per-turn UUID. Null on Claude payloads and on Codex
    /// session-scoped hooks (SessionStart, Stop). See
    /// https://developers.openai.com/codex/hooks for the spec.
    /// </summary>
    [JsonPropertyName("turn_id")]
    public string? TurnId { get; init; }
```

- [ ] **Step 4: Verify build still passes**

```bash
dotnet build src/Kurrent.Capacitor/Kurrent.Capacitor.csproj
```

Expected: success. Existing handlers may surface CS8629 ("nullable value type may be null") warnings on `hook.Source.Value` / `hook.Reason.Value` accesses — those will be addressed in Task 11. If any are hard errors (not warnings), note them and fix in Task 11.

- [ ] **Step 5: Commit**

```bash
git add src/Kurrent.Capacitor/Models.cs
git commit -m "[AI-70] Loosen SessionStartHook.Source and SessionEndHook.Reason to nullable; add HookBase.TurnId"
```

---

## Task 11: Add vendor route param to existing routes; vendor-aware handlers

Change three existing route templates to include `{vendor=claude}` so a CLI client can specify vendor in the URL while existing Claude clients (which omit the segment) continue to work. Add a `string vendor` parameter to the affected handlers, validate Claude-required fields when `vendor=="claude"`, and default them when `vendor=="codex"`.

**Repo:** Server

**Files:**
- Modify: `src/Kurrent.Capacitor/Sessions/RouteGroups.cs`
- Modify: `src/Kurrent.Capacitor/Sessions/SessionHookHandlers.cs`

- [ ] **Step 1: Update route templates**

In `src/Kurrent.Capacitor/Sessions/RouteGroups.cs:21-24`, change the `/session-start` mapping to:

```csharp
            hooks.MapPost(
                "/session-start/{vendor=claude}",
                (SessionStartHook payload, [FromRoute] string vendor, HttpContext context, SessionHookHandlers h, CancellationToken ct)
                    => h.HandleSessionStart(payload, vendor, context, ct)
            );
```

In the same file at lines 26-29, change `/session-end`:

```csharp
            hooks.MapPost(
                "/session-end/{vendor=claude}",
                (SessionEndHook payload, [FromRoute] string vendor, SessionHookHandlers h, CancellationToken ct)
                    => h.HandleSessionEnd(payload, vendor, ct)
            );
```

At lines 76-79, change `/permission-request`:

```csharp
            hooks.MapPost(
                "/permission-request/{vendor=claude}",
                (PermissionRequestHook payload, [FromRoute] string vendor, SessionHookHandlers h, CancellationToken ct)
                    => h.HandlePermissionRequest(payload, vendor, ct)
            );
```

Add `using Microsoft.AspNetCore.Mvc;` at the top of the file if it's not already imported (for `[FromRoute]`).

- [ ] **Step 2: Update `HandleSessionStart` signature and add validation/defaults**

In `src/Kurrent.Capacitor/Sessions/SessionHookHandlers.cs`, change the signature of `HandleSessionStart` (around line 38):

```csharp
public async Task<IResult> HandleSessionStart(
        SessionStartHook  hook,
        string            vendor,
        HttpContext       httpContext,
        CancellationToken ct
    ) {
    // Vendor validation: Claude payloads MUST include source. Codex payloads
    // legitimately omit it (Codex has no SessionStartSource concept), so we
    // default to Startup. This keeps Claude's contract strict while letting
    // the path-param-driven Codex case relax cleanly.
    SessionStartSource source;

    if (hook.Source is { } src) {
        source = src;
    } else if (vendor == "claude") {
        return Results.BadRequest(new { error = "source is required for Claude session-start payloads" });
    } else {
        source = SessionStartSource.Startup;
    }

    // ... existing body, but use `source` instead of `hook.Source.Value` everywhere ...
}
```

You'll need to scan the existing body for every `hook.Source` access (or `hook.Source.Value`) and change it to use the local `source` variable. The `RecordSessionStartedAsync` call's `source` parameter receives `source` instead of `hook.Source`.

- [ ] **Step 3: Update `HandleSessionEnd` signature and add validation/defaults**

In the same file, change `HandleSessionEnd` (around line 170):

```csharp
public async Task<IResult> HandleSessionEnd(SessionEndHook hook, string vendor, CancellationToken ct) {
    SessionEndReason reason;

    if (hook.Reason is { } r) {
        reason = r;
    } else if (vendor == "claude") {
        return Results.BadRequest(new { error = "reason is required for Claude session-end payloads" });
    } else {
        reason = SessionEndReason.UserExit;
    }

    // ... existing body, using `reason` instead of `hook.Reason` ...
}
```

- [ ] **Step 4: Update `HandlePermissionRequest` signature**

In the same file, change `HandlePermissionRequest` (around line 955) to accept `vendor`:

```csharp
public Task<IResult> HandlePermissionRequest(PermissionRequestHook hook, string vendor, CancellationToken ct) {
    // The PermissionRequest payload shape is already vendor-neutral
    // (session_id + tool_name + tool_input + permission_suggestions). Vendor
    // doesn't change behaviour today; this parameter exists for routing
    // consistency and forward compatibility with AI-68.
    return RunPermissionFlow(hook, ct);
}
```

(If `RunPermissionFlow` doesn't currently exist as a private method, the inline body of `HandlePermissionRequest` stays as-is — just add the `vendor` parameter to the public signature without otherwise changing the implementation.)

- [ ] **Step 5: Verify build**

```bash
dotnet build src/Kurrent.Capacitor/Kurrent.Capacitor.csproj
```

Expected: success. Resolve any remaining nullable-warning sites flagged in Task 10 step 4.

- [ ] **Step 6: Run existing server unit tests**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: PASS. Existing tests use `HandleSessionStart(hook, ctx, ct)` (positional). They'll fail to compile after this signature change. Update each call site to pass the new `vendor` arg — the only legitimate value in existing tests is `"claude"`. (This is a chore but mechanical; grep for `HandleSessionStart(`/`HandleSessionEnd(`/`HandlePermissionRequest(` in `test/` and add `"claude"` as the new positional arg.)

- [ ] **Step 7: Commit**

```bash
git add src/Kurrent.Capacitor/Sessions/RouteGroups.cs src/Kurrent.Capacitor/Sessions/SessionHookHandlers.cs test/
git commit -m "[AI-70] Server: vendor route segment + handler validation/defaults for Codex"
```

---

## Task 12: Codex round-trip integration tests + server PR

Add an integration test that posts a Codex SessionStart to `/hooks/session-start/codex`, asserts the canonical `SessionStarted` event lands; same for Stop → `/hooks/session-end/codex`; and a guard test that posting to `/hooks/session-start` (no vendor) without a `source` field 400s.

**Repo:** Server

**Files:**
- Create: `test/kapacitor.Tests.Integration/CodexHookRoundTripTests.cs`

- [ ] **Step 1: Write the integration tests**

Create `test/kapacitor.Tests.Integration/CodexHookRoundTripTests.cs`. Mirror patterns from the existing `HookRoundTripTests`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TUnit.Core;

namespace kapacitor.Tests.Integration;

[ClassDataSource<KurrentDbFixture>(Shared = SharedType.PerTestSession)]
public class CodexHookRoundTripTests(KurrentDbFixture fixture) {
    [Test]
    public async Task CodexSessionStart_creates_session_via_vendor_route_segment() {
        var client    = fixture.Factory.CreateClient();
        var sessionId = Guid.NewGuid().ToString("N");

        // Codex payload — note: no `source` field (Codex doesn't have one).
        var payload = new {
            hook_event_name = "SessionStart",
            session_id      = sessionId,
            transcript_path = "/tmp/rollout.jsonl",
            cwd             = "/tmp",
            model           = "gpt-5",
            home_dir        = "/Users/test"
        };

        var response = await client.PostAsJsonAsync("/hooks/session-start/codex", payload);
        await Assert.That(response.IsSuccessStatusCode).IsTrue();

        // Async projector — poll the detail endpoint up to 5s for it to land.
        for (var i = 0; i < 50; i++) {
            var detail = await client.GetAsync($"/api/sessions/{sessionId}/detail");
            if (detail.IsSuccessStatusCode) {
                var json = await detail.Content.ReadAsStringAsync();
                var doc  = JsonDocument.Parse(json);
                await Assert.That(doc.RootElement.GetProperty("session_id").GetString()).IsEqualTo(sessionId);
                return;
            }
            await Task.Delay(100);
        }

        await Assert.Fail($"Session {sessionId} did not appear in /detail within 5s");
    }

    [Test]
    public async Task ClaudeSessionStart_without_vendor_segment_still_works() {
        // Backward compatibility: existing Claude clients POST to
        // /hooks/session-start (no vendor segment). The {vendor=claude}
        // route default catches them.
        var client    = fixture.Factory.CreateClient();
        var sessionId = Guid.NewGuid().ToString("N");

        var payload = new {
            hook_event_name = "SessionStart",
            session_id      = sessionId,
            transcript_path = "/tmp/transcript.jsonl",
            cwd             = "/tmp",
            source          = "Startup",   // Claude requires this
            model           = "claude-opus-4-7"
        };

        var response = await client.PostAsJsonAsync("/hooks/session-start", payload);
        await Assert.That(response.IsSuccessStatusCode).IsTrue();
    }

    [Test]
    public async Task ClaudeSessionStart_without_source_returns_400() {
        // Claude contract is preserved: missing `source` on a claude-vendored
        // request is a client error, not silently defaulted.
        var client = fixture.Factory.CreateClient();

        var payload = new {
            hook_event_name = "SessionStart",
            session_id      = Guid.NewGuid().ToString("N"),
            transcript_path = "/tmp/t.jsonl",
            cwd             = "/tmp"
            // intentionally no `source`
        };

        var response = await client.PostAsJsonAsync("/hooks/session-start", payload);
        await Assert.That((int)response.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task CodexStop_routes_to_session_end_codex_and_records_SessionEnded() {
        var client    = fixture.Factory.CreateClient();
        var sessionId = Guid.NewGuid().ToString("N");

        await client.PostAsJsonAsync("/hooks/session-start/codex", new {
            hook_event_name = "SessionStart",
            session_id      = sessionId,
            transcript_path = "/tmp/r.jsonl",
            cwd             = "/tmp",
            model           = "gpt-5"
        });

        // CLI sends Codex Stop to /hooks/session-end/codex (not /hooks/stop)
        // because Codex has no separate session-end hook.
        var stopResponse = await client.PostAsJsonAsync("/hooks/session-end/codex", new {
            hook_event_name = "Stop",
            session_id      = sessionId,
            transcript_path = "/tmp/r.jsonl",
            cwd             = "/tmp"
            // intentionally no `reason` — server should default to UserExit for codex
        });

        await Assert.That(stopResponse.IsSuccessStatusCode).IsTrue();

        for (var i = 0; i < 50; i++) {
            var events = await client.GetAsync($"/api/sessions/{sessionId}/events");
            if (events.IsSuccessStatusCode) {
                var json = await events.Content.ReadAsStringAsync();
                if (json.Contains("SessionEnded", StringComparison.OrdinalIgnoreCase)) return;
            }
            await Task.Delay(100);
        }

        await Assert.Fail($"SessionEnded did not appear for {sessionId} within 5s");
    }

    [Test]
    public async Task ClaudeSessionEnd_without_reason_returns_400() {
        var client    = fixture.Factory.CreateClient();
        var sessionId = Guid.NewGuid().ToString("N");

        // Seed a Claude session first.
        await client.PostAsJsonAsync("/hooks/session-start", new {
            hook_event_name = "SessionStart",
            session_id      = sessionId,
            transcript_path = "/tmp/t.jsonl",
            cwd             = "/tmp",
            source          = "Startup"
        });

        var response = await client.PostAsJsonAsync("/hooks/session-end", new {
            hook_event_name = "SessionEnd",
            session_id      = sessionId,
            transcript_path = "/tmp/t.jsonl",
            cwd             = "/tmp"
            // intentionally no `reason`
        });

        await Assert.That((int)response.StatusCode).IsEqualTo(400);
    }
}
```

(Adapt `KurrentDbFixture` setup to whatever the existing `HookRoundTripTests` uses — `factory.CreateClient()` vs `factory.CreateClientWithAuth()` — when porting.)

- [ ] **Step 2: Run the integration tests**

```bash
dotnet run --project test/kapacitor.Tests.Integration/kapacitor.Tests.Integration.csproj \
  --treenode-filter "/*/*/CodexHookRoundTripTests/*"
```

Expected: PASS (all five).

- [ ] **Step 3: Run the full server test suite to catch regressions**

```bash
dotnet run --project test/kapacitor.Tests.Integration/kapacitor.Tests.Integration.csproj
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: PASS — Claude paths untouched aside from the `vendor` parameter additions resolved in Task 11.

- [ ] **Step 4: Commit**

```bash
git add test/kapacitor.Tests.Integration/CodexHookRoundTripTests.cs
git commit -m "[AI-70] Server: Codex hook round-trip integration tests"
```

- [ ] **Step 5: Push and open the server PR**

```bash
git push -u origin ai-70-codex-hook-surface
gh pr create --title "[AI-70] Server: vendor path-param routing + Codex hook DTO loosening" --body "$(cat <<'EOF'
## Summary
- Adds a `{vendor=claude}` segment to three existing hook routes (`/hooks/session-start`, `/hooks/session-end`, `/hooks/permission-request`) so a CLI can specify vendor in the URL. Existing Claude clients (which omit the segment) continue to work because the route template defaults to `claude`.
- Loosens `SessionStartHook.Source` and `SessionEndHook.Reason` to nullable. Handlers now validate (`vendor == "claude"` ⇒ field required, 400 otherwise) and default for Codex (`SessionStartSource.Startup`, `SessionEndReason.UserExit`). Claude's contract is preserved; Codex's payload doesn't need to fib.
- Adds optional `HookBase.TurnId` for Codex turn-scoped hooks (PreToolUse, PostToolUse, PermissionRequest).
- Handlers gain `string vendor` parameter; behaviour is identical for Claude and currently identical for Codex aside from the field defaults. Future vendor-specific branches (skipping slug-resolution / top-clusters for Codex) can land as follow-ups.

CLI companion PR: <link to CLI PR from Task 9 step 5>.

## Why path-param over a separate /hooks/codex/* group?
- DRY: one route per event, vendor as data on the URL.
- Backward compat: existing Claude clients keep their URLs.
- Easy to extend: future vendors (Cursor, Aider) follow the same pattern — just a new vendor value, no new routes.

## Test plan
- [x] `CodexHookRoundTripTests` covers vendor-segmented Codex SessionStart, Codex Stop → session-end mapping, backward-compat Claude (no segment), and Claude validation (missing source/reason → 400).
- [x] Existing `HookRoundTripTests` still pass (Claude path semantically unchanged).
EOF
)"
```

Note the PR URL — paste back into the CLI PR description (Task 9 step 5) to cross-link.

---

# Self-review checklist

Verified before saving:

1. **Spec coverage:** every acceptance criterion in AI-70 maps to a task.
   - `codex-hook` handles all six events → Task 5 (three with server routes, three swallowed).
   - `plugin install --codex` writes hooks.json → Task 6.
   - `status` reports Codex hook installation → Task 7.
   - Server-side Codex hook surface → Tasks 10-12 (vendor route param + handler vendor-awareness).
   - Live Codex session in dashboard → Task 12 integration test asserts canonical `SessionStarted` lands; full live-Codex smoke test gated on both PRs landing (out of scope for the plan, in AI-67's parent acceptance).
   - PermissionRequest round-trip → Task 5 (CLI v1 stub) + Task 11 (server signature update); full daemon-bridge translation deferred to AI-68 per the issue's own scope.
   - Watcher terminates on Stop and parent-PID exit → Task 5 (calls `KillWatcher`); parent-PID logic already exists in WatchCommand.

2. **No placeholders:** every step has concrete code or an exact command. No "TBD", no "similar to above", no "add error handling". Where the exact server signature might differ (e.g., `RecordSessionEndedAsync`, `RunPermissionFlow`), the step explicitly notes "adjust based on the actual signature when the build surfaces an error" — that's a planned signal-driven correction, not a placeholder.

3. **Type consistency:**
   - `CodexHookCommand.Handle(string baseUrl, TextReader stdin)` — used identically in tests and Program.cs registration.
   - `WatcherManager.BuildSpawnArgs(...)` — same parameter list in tests and the implementation.
   - `WatcherManager.SpawnWatcher(... string vendor = "claude")` and `EnsureWatcherRunning(... string vendor = "claude")` — same default everywhere.
   - `WatcherManager.InlineDrainAsync(... string vendor = "claude")` — same default.
   - `PluginCommand.InstallCodexHooks(string)` / `RemoveCodexHooks(string)` — same names used in tests, implementation, and the wrapper methods.
   - `StatusCommand.IsClaudePluginInstalled(string)` / `IsCodexHooksInstalled(string)` — same names used in tests and implementation.
   - `CodexPaths.UserHooksJson` — used in PluginCommand and StatusCommand; defined in Task 1.
   - Server: `HandleSessionStart(SessionStartHook, string vendor, HttpContext, CancellationToken)`, `HandleSessionEnd(SessionEndHook, string vendor, CancellationToken)`, `HandlePermissionRequest(PermissionRequestHook, string vendor, CancellationToken)` — same signatures in route mappings and handlers.

4. **TDD discipline:** every CLI task starts with a failing test, then minimal implementation, then verification. Server Task 10 (DTO loosening) is tested transitively via Task 11's existing-test-pass requirement and Task 12's new integration tests — pure type-shape changes don't need their own failing test.

5. **CLAUDE.md compliance:**
   - JSON arrays use `new JsonArray(...)` constructors, not `[item1, item2]` collection expressions.
   - AOT publish verification step included (Task 9 step 3).
   - TUnit tests use `--treenode-filter` glob syntax, not `--filter`.
   - Tests run as executables via `dotnet run --project ...`.
   - PR titles prefixed with `[AI-70]` per the project convention.

6. **Cross-repo workflow:** Part 1 (CLI) and Part 2 (Server) are sequenced so the CLI PR can land first (it's opt-in via `plugin install --codex`, so no Claude users are affected). The server PR can land in parallel or just after — they cross-reference via PR URLs in their bodies. Each repo gets its own working directory, branch, and PR title.

7. **Vendor-as-path-param decision:** chosen over `/hooks/codex/*` (denormalized routing) and `vendor` body field (would require more invasive DTO changes or normalization on the CLI side). Path-param keeps URLs explicit, preserves backward compat via `{vendor=claude}` default, and keeps the DTO/handler count flat.
