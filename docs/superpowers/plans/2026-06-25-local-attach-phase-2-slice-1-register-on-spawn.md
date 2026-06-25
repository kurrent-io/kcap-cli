# Local Attach Phase 2 / Slice 1 — Register-on-Spawn Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a locally-launched `kcap run-agent` agent register exactly like a UI-launched hosted agent by default — visible and drivable from the owner's web UI — with a `--private` opt-out that preserves the Phase 1 unregistered behavior.

**Architecture:** A locally-spawned agent flips `IsPrivate = false` and runs the same server-registration sequence as the hosted launch path (extracted into a shared `RegisterAgentAsync` helper). Eager terminal streaming and bridge-mode permissions then fall out of the existing `!IsPrivate` gates. The `--private` flag rides a new trailing byte on the Spawn IPC frame (appended for wire-compat with a version-skewed daemon). Per-agent terminal dimensions are tracked on `AgentInstance` so registration, resize, and reconnect all report the real PTY size.

**Tech Stack:** .NET 10 (NativeAOT), TUnit on Microsoft Testing Platform, hand-rolled binary IPC over a Unix domain socket, SignalR client to the server.

## Global Constraints

- **AOT-safe:** no reflection-based serialization; IPC framing stays hand-rolled binary. After changes, `dotnet publish -c Release` must show no IL3050/IL2026 warnings.
- **JsonArray:** never use collection-expression `[a, b]` for `JsonArray` (needs dynamic code) — use `new JsonArray(...)`. (Not expected to arise here.)
- **README sync:** any user-facing CLI change updates `README.md` in the same PR (Getting started + the `run-agent` command section); updating `help-*.txt` alone is insufficient.
- **Top safety invariant (unchanged from Phase 1):** never `Directory.Delete` / `git worktree remove` / `git branch -D` a borrowed cwd; this plan must not weaken the `Work = BorrowedCwd` cleanup guard.
- **Tests:** TUnit. Run a single test file as an executable with `dotnet run`; filter with `--treenode-filter` (glob), never `--filter`.

---

## File Structure

- `src/Capacitor.Cli.Core/RunAgentArgs.cs` — add the `--private` flag (Task 1).
- `src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs` — append the `private` byte to the Spawn frame (Task 2).
- `src/Capacitor.Cli/Commands/RunAgentCommand.cs` — pass `parsed.Private` into the Spawn frame (Task 2).
- `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` — `CurrentCols/Rows` field, `RegisterAgentAsync` helper, reconnect-dims fix, web-resize-dims fix, `GetLiveAgentIds` filter (Tasks 3, 5, 6).
- `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs` — register on the local spawn path, clamp-dims update + announce (Tasks 4, 5).
- Tests: `test/Capacitor.Cli.Tests.Unit/{RunAgentArgsTests,FrameCodecTests,AgentOrchestratorLocalAttachTests}.cs`.
- Docs: `README.md`, `src/Capacitor.Cli.Core/Resources/help-*.txt`.

Task order: 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8. Each task leaves the build green and tests passing.

---

## Task 1: `--private` CLI flag parsing

**Files:**
- Modify: `src/Capacitor.Cli.Core/RunAgentArgs.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/RunAgentArgsTests.cs`

**Interfaces:**
- Produces: `RunAgentArgs.Private` (`bool`), set by a `--private` kcap flag (before `--`).

- [ ] **Step 1: Write the failing test**

Add to `test/Capacitor.Cli.Tests.Unit/RunAgentArgsTests.cs`:

```csharp
    [Test]
    public async Task Private_flag_is_parsed_and_defaults_false() {
        var on  = RunAgentArgs.Parse(["claude", "--private", "--", "fix"]);
        await Assert.That(on.Private).IsTrue();
        await Assert.That(on.Passthrough).IsEquivalentTo(new[] { "fix" });
        await Assert.That(on.Error).IsNull();

        var off = RunAgentArgs.Parse(["claude"]);
        await Assert.That(off.Private).IsFalse();
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/RunAgentArgsTests/Private_flag_is_parsed_and_defaults_false"`
Expected: FAIL (compile error — `RunAgentArgs` has no `Private`).

- [ ] **Step 3: Implement the flag**

In `src/Capacitor.Cli.Core/RunAgentArgs.cs`, add the property after `Detached`:

```csharp
    public bool     Detached    { get; private set; }
    public bool     Private     { get; private set; }
```

Add a case to the flag `switch` (alongside `--worktree` / `--detached`):

```csharp
                case "--worktree": r.Worktree = true; break;
                case "--detached": r.Detached = true; break;
                case "--private":  r.Private  = true; break;
```

Update the usage string to mention it:

```csharp
            r.Error = "usage: kcap run-agent <vendor> [--worktree] [--private] [--name <id>] [--detached] [-- <agent args>]";
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/RunAgentArgsTests/Private_flag_is_parsed_and_defaults_false"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/RunAgentArgs.cs test/Capacitor.Cli.Tests.Unit/RunAgentArgsTests.cs
git commit -m "feat(run-agent): parse --private flag"
```

---

## Task 2: Append the `private` flag to the Spawn IPC frame

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs`
- Modify: `src/Capacitor.Cli/Commands/RunAgentCommand.cs:30`
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs:20` (keep it compiling; the value is wired in Task 4)
- Test: `test/Capacitor.Cli.Tests.Unit/FrameCodecTests.cs`

**Interfaces:**
- Consumes: `RunAgentArgs.Private` (Task 1).
- Produces:
  - `FrameCodec.Spawn(string vendor, WorkLocation work, bool isPrivate, string cwd, IReadOnlyList<string> args, ushort cols, ushort rows) : LocalFrame`
  - `FrameCodec.Spawn(LocalFrame) : (string vendor, WorkLocation work, bool isPrivate, string cwd, string[] args, ushort cols, ushort rows)`
  - Wire layout: the `private` flag is a single trailing byte **after** the args; absent ⇒ `isPrivate = true`.

- [ ] **Step 1: Update the codec round-trip test (and add the missing-byte default test)**

In `test/Capacitor.Cli.Tests.Unit/FrameCodecTests.cs`, replace `Spawn_round_trips_vendor_cwd_args_and_worklocation` with:

```csharp
    [Test]
    public async Task Spawn_round_trips_vendor_cwd_args_worklocation_and_private() {
        foreach (var priv in new[] { false, true }) {
            var built = FrameCodec.Spawn("codex", WorkLocation.OwnedWorktree, priv, "/repo", ["--model", "opus", "fix it"], 100, 30);
            var r = await RoundTrip(built);
            await Assert.That(r.Type).IsEqualTo(FrameType.Spawn);
            var (vendor, work, isPrivate, cwd, args, cols, rows) = FrameCodec.Spawn(r);
            await Assert.That(vendor).IsEqualTo("codex");
            await Assert.That(work).IsEqualTo(WorkLocation.OwnedWorktree);
            await Assert.That(isPrivate).IsEqualTo(priv);
            await Assert.That(cwd).IsEqualTo("/repo");
            await Assert.That(args).IsEquivalentTo(new[] { "--model", "opus", "fix it" });
            await Assert.That(cols).IsEqualTo((ushort)100);
            await Assert.That(rows).IsEqualTo((ushort)30);
        }
    }

    [Test]
    public async Task Spawn_without_trailing_flag_defaults_to_private() {
        // An older CLI's Spawn frame carries no trailing private byte; ParseSpawn must default to private.
        using var ms = new MemoryStream();
        ms.WriteByte((byte)WorkLocation.BorrowedCwd);
        ms.Write([0, 80]); ms.Write([0, 24]);            // cols=80, rows=24 (BE)
        ms.Write([0, 0, 0, 6]); ms.Write("claude"u8);    // vendorLen=6, "claude"
        ms.Write([0, 0, 0, 0]);                          // cwdLen=0
        ms.Write([0, 0, 0, 0]);                          // argCount=0  (no trailing private byte)
        var frame = new LocalFrame(FrameType.Spawn) { Bytes = ms.ToArray() };

        var (vendor, _, isPrivate, _, _, _, _) = FrameCodec.Spawn(frame);
        await Assert.That(vendor).IsEqualTo("claude");
        await Assert.That(isPrivate).IsTrue();
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/FrameCodecTests/*"`
Expected: FAIL (compile — `Spawn` has no `isPrivate` overload / 7-tuple).

- [ ] **Step 3: Encode the trailing byte**

In `src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs`, change the `Spawn(...)` builder signature and append the byte after the arg loop:

```csharp
    public static LocalFrame Spawn(string vendor, WorkLocation work, bool isPrivate, string cwd, IReadOnlyList<string> args, ushort cols, ushort rows) {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)work);
        WriteBe16(ms, cols); WriteBe16(ms, rows);
        WriteLp(ms, vendor); WriteLp(ms, cwd);
        WriteBe32(ms, args.Count);
        foreach (var a in args) WriteLp(ms, a);
        ms.WriteByte((byte)(isPrivate ? 1 : 0)); // APPENDED after args: older parsers ignore trailing bytes
        return new(FrameType.Spawn) { Bytes = ms.ToArray(), Text = vendor, Work = work, Cols = cols, Rows = rows };
    }
```

- [ ] **Step 4: Decode the trailing byte**

In the same file, change the public tuple accessor return type:

```csharp
    public static (string vendor, WorkLocation work, bool isPrivate, string cwd, string[] args, ushort cols, ushort rows) Spawn(LocalFrame f)
        => ParseSpawn(f.Bytes);
```

Change `ParseSpawn`'s signature and its `return` (read the optional trailing byte after the arg loop):

```csharp
    static (string vendor, WorkLocation work, bool isPrivate, string cwd, string[] args, ushort cols, ushort rows) ParseSpawn(byte[] p) {
```

```csharp
        var args = new string[n];
        for (var i = 0; i < n; i++) args[i] = ReadLp(p, ref o);
        // Trailing private flag (appended for wire-compat): absent (older CLI) => private=true,
        // the conservative default that preserves Phase-1 unregistered behaviour.
        var isPrivate = o >= p.Length || p[o] != 0;
        return (vendor, work, isPrivate, cwd, args, cols, rows);
    }
```

(`SpawnCwd`/`SpawnArgs` use named tuple members `.cwd`/`.args`, so they keep compiling.)

- [ ] **Step 5: Keep the two call sites compiling**

In `src/Capacitor.Cli/Commands/RunAgentCommand.cs:30`, pass the parsed flag:

```csharp
        var spawn = FrameCodec.Spawn(parsed.Vendor, work, parsed.Private, Environment.CurrentDirectory, parsed.Passthrough, cols, rows);
```

In `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs:20`, discard the new element for now (wired up in Task 4):

```csharp
        var (vendor, work, _, cwd, args, cols, rows) = FrameCodec.Spawn(spawn);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/FrameCodecTests/*"`
Expected: PASS (incl. the two existing protocol-error tests, which throw before the trailing read).

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs src/Capacitor.Cli/Commands/RunAgentCommand.cs src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs test/Capacitor.Cli.Tests.Unit/FrameCodecTests.cs
git commit -m "feat(ipc): append private flag to Spawn frame (wire-compatible)"
```

---

## Task 3: `AgentInstance.CurrentCols/Rows` + extract `RegisterAgentAsync`

This is a behavior-preserving refactor of the hosted path plus the shared registration helper the local path will call in Task 4.

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (AgentInstance record ~17-71; hosted spawn ~375; registration block 380-408)
- Test: `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs`

**Interfaces:**
- Produces:
  - `AgentInstance.CurrentCols` / `AgentInstance.CurrentRows` (`ushort`, get/set) — current PTY dims, the source of truth for every dims send.
  - `Task AgentOrchestrator.RegisterAgentAsync(AgentInstance agent)` — registers like a UI launch (AgentRegistered + dims + AgentRunStarted + repo-path announce); **no-ops when `agent.IsPrivate`**.
  - Test hook: `internal Task RegisterAgentForTestAsync(AgentInstance a)`.

- [ ] **Step 1: Add the dims fields to `AgentInstance`**

In `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs`, in the `AgentInstance` record body (after `Work`):

```csharp
    /// <summary>Current PTY dimensions — the single source of truth for every dims send
    /// (registration, reconnect). Updated by every resize path (local clamp + web resize).
    /// Hosted agents initialise these to the fixed HostedPtyCols/Rows; ushort read/write is
    /// atomic, and stale-by-one-resize is harmless for best-effort dims.</summary>
    public ushort CurrentCols { get; set; }
    public ushort CurrentRows { get; set; }
```

- [ ] **Step 2: Initialise them for hosted agents**

In `HandleLaunchAgent`, where the hosted `AgentInstance` is created (~line 375), add to the initializer:

```csharp
            var agent = new AgentInstance(agentId, prompt, model, effort, repoPath, cmd.Vendor, process, worktree, cts) {
                McpConfigPath = mcpConfigPath,
                CurrentCols   = HostedPtyCols,
                CurrentRows   = HostedPtyRows
            };
```

- [ ] **Step 3: Extract `RegisterAgentAsync` and call it from the hosted path**

Add the helper method to the `AgentOrchestrator` partial class (e.g. just below `HandleLaunchAgent`):

```csharp
    /// <summary>
    /// Registers an agent with the server exactly as a UI-launched agent: AgentRegistered +
    /// terminal dims + AgentRunStarted, then persists/announces the repo path. No-ops for a
    /// PrivateLocal agent. Shared by the hosted launch and the registered local launch so the
    /// two cannot drift. Dims come from <see cref="AgentInstance.CurrentCols"/>/<c>CurrentRows</c>
    /// (hosted = HostedPtyCols/Rows; local = the client's terminal size).
    /// </summary>
    async Task RegisterAgentAsync(AgentInstance agent) {
        if (agent.IsPrivate) return;

        await _server.AgentRegisteredAsync(agent.Id, agent.Prompt, agent.Model, agent.Effort, agent.RepoPath);

        // Report the PTY size so read-only viewers lock their xterm to it. Best-effort.
        try {
            await _server.SendTerminalDimensionsAsync(agent.Id, agent.CurrentCols, agent.CurrentRows);
        } catch (Exception ex) {
            LogTerminalDimsSendFailed(ex, agent.Id);
        }

        _ = _server.AppendAgentRunEventAsync(
            agent.Id,
            new AgentRunStarted(agent.Prompt, agent.Model, agent.Effort, agent.RepoPath, agent.Worktree.Path, agent.Vendor)
        );

        // Persist repo path and notify server so the launch dialog updates.
        _ = Task.Run(async () => {
                try {
                    await RepoPathStore.AddAsync(agent.RepoPath);
                    await _server.UpdateRepoPathsAsync();
                } catch (Exception ex) {
                    LogRepoPathPersistFailed(ex, agent.Id);
                }
            }
        );
    }
```

In `HandleLaunchAgent`, **replace** the inline registration block (from the `// Notify server` line through the repo-path `Task.Run(...)`, currently ~lines 380-408) with a single call, leaving `_ = ReadAgentOutputAsync(agent);` in place:

```csharp
            await RegisterAgentAsync(agent);

            // Start reading output
            _ = ReadAgentOutputAsync(agent);
```

- [ ] **Step 4: Add the test hook**

Near the other test hooks (~line 1201, `RegisterAgentForTest` / `CleanupAgentForTest`):

```csharp
    internal Task RegisterAgentForTestAsync(AgentInstance agent) => RegisterAgentAsync(agent);
```

- [ ] **Step 5: Write the helper test**

Add to `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs`:

```csharp
    [Test]
    public async Task RegisterAgentAsync_registers_public_agent_and_skips_private() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var pub = new AgentInstance("pub-1", null, "", null, "/r", "claude",
            new StubPtyProcess(), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) { IsPrivate = false };
        await orch.RegisterAgentForTestAsync(pub);
        await Assert.That(server.Calls).Contains(nameof(ServerConnection.AgentRegisteredAsync));

        var priv = new AgentInstance("priv-1", null, "", null, "/r", "claude",
            new StubPtyProcess(), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) { IsPrivate = true };
        server.Calls.Clear();
        await orch.RegisterAgentForTestAsync(priv);
        await Assert.That(server.Calls.Count).IsEqualTo(0);
    }
```

(`server.Calls` is a `ConcurrentBag<string>`; `.Clear()` exists on it. `TripwireServerConnection.SendTerminalDimensionsAsync` is not overridden, so the base call throws on the unconnected hub and is swallowed by the helper's try/catch — harmless.)

- [ ] **Step 6: Run the new test + the full local-attach file**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentOrchestratorVendorTests/*"`
Expected: PASS (new test green; all pre-existing hosted/local tests still green — the refactor preserves behavior).

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs
git commit -m "refactor(daemon): extract RegisterAgentAsync + track per-agent PTY dims"
```

---

## Task 4: Register on the local spawn path (the core)

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs` (`HandleLocalSpawnAsync`, ~19-84)
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (add one `[LoggerMessage]`)
- Test: `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs`

**Interfaces:**
- Consumes: `FrameCodec.Spawn(LocalFrame)` 7-tuple (Task 2); `RegisterAgentAsync` + `CurrentCols/Rows` (Task 3).
- Produces: a non-`--private` local spawn sets `IsPrivate = false`, the hosted env (`KCAP_RENDERED_AGENT`/`KCAP_AGENT_ID`/`KCAP_DAEMON_URL`) while keeping `ANTHROPIC_API_KEY`, and registers via `RegisterAgentAsync`.

- [ ] **Step 1: Re-scope the existing privacy test to `--private`**

In `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs`, rename and update `PrivateLocal_spawn_makes_no_server_calls_and_omits_hosted_agent_env` — change only the `FrameCodec.Spawn(...)` call to pass `isPrivate: true`:

```csharp
    [Test]
    public async Task Private_spawn_makes_no_server_calls_and_omits_hosted_agent_env() {
        // ...unchanged setup...
            var spawn = FrameCodec.Spawn("claude", WorkLocation.BorrowedCwd, isPrivate: true, dir.FullName, ["--model", "opus"], 80, 24);
            await orch.HandleLocalSpawnAsync(spawn, client, default);
        // ...unchanged assertions: server.Calls.Count == 0; KCAP_URL present;
        //    KCAP_AGENT_ID / KCAP_RENDERED_AGENT / KCAP_DAEMON_URL absent...
    }
```

- [ ] **Step 2: Add the registered-spawn test**

Add alongside it:

```csharp
    [Test]
    public async Task Registered_spawn_calls_server_and_sets_hosted_env() {
        var dir = Directory.CreateTempSubdirectory("kcap-reg-");

        try {
            var server    = new TripwireServerConnection();
            var pty       = new EnvCapturingPtyFactory();
            var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = new SpyHostedAgentLauncher("claude", "spy-claude") };

            await using var orch = BuildOrchestrator(server, pty, launchers);

            var readBuf = new MemoryStream();
            await FrameCodec.WriteAsync(readBuf, LocalFrame.Detach(), default);
            readBuf.Position = 0;
            using var client = new DuplexTestStream(readBuf, new MemoryStream());

            var spawn = FrameCodec.Spawn("claude", WorkLocation.BorrowedCwd, isPrivate: false, dir.FullName, ["--model", "opus"], 80, 24);
            await orch.HandleLocalSpawnAsync(spawn, client, default);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (orch.ActiveAgentCountForTest > 0 && DateTime.UtcNow < deadline) await Task.Delay(20);

            await Assert.That(server.Calls).Contains(nameof(ServerConnection.AgentRegisteredAsync));
            await Assert.That(pty.LastEnv!.ContainsKey("KCAP_URL")).IsTrue();
            await Assert.That(pty.LastEnv!.ContainsKey("KCAP_AGENT_ID")).IsTrue();
            await Assert.That(pty.LastEnv!.ContainsKey("KCAP_RENDERED_AGENT")).IsTrue();
        } finally {
            Directory.Delete(dir.FullName, true);
        }
    }
```

- [ ] **Step 3: Run both tests to verify the new one fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentOrchestratorVendorTests/Registered_spawn_calls_server_and_sets_hosted_env"`
Expected: FAIL (`HandleLocalSpawnAsync` still forces `IsPrivate = true` and omits hosted env).

- [ ] **Step 4: Add the log message for a non-fatal local registration failure**

In `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs`, near the other `[LoggerMessage]` declarations (~line 1149):

```csharp
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to register local agent {AgentId} with the server (continuing; terminal stays usable)")]
    partial void LogLocalRegisterFailed(Exception ex, string agentId);
```

- [ ] **Step 5: Wire the registered path in `HandleLocalSpawnAsync`**

In `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs`:

Use the decoded flag (replace the Task-2 discard at line 20):

```csharp
        var (vendor, work, isPrivate, cwd, args, cols, rows) = FrameCodec.Spawn(spawn);
```

Replace the env block (currently lines 58-61) with a conditional hosted env:

```csharp
            var env = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(_config.ServerUrl)) env["KCAP_URL"] = _config.ServerUrl;
            var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (!string.IsNullOrEmpty(apiKey)) env["ANTHROPIC_API_KEY"] = apiKey;

            if (!isPrivate) {
                // Register like a UI-launched agent: hosted env so it's visible/drivable from the
                // owner's web UI and permissions route through the daemon bridge (Slice 1, AI-972).
                env["KCAP_RENDERED_AGENT"] = "1";
                env["KCAP_AGENT_ID"]       = agentId;
                if (_permissionBridge.BaseUrl is { } bridgeUrl) env["KCAP_DAEMON_URL"] = bridgeUrl;
            }
```

Set `IsPrivate`/dims on the instance (replace the initializer at lines 65-69):

```csharp
            agent = new AgentInstance(agentId, null, "", null, cwd, vendor, proc, worktree, new CancellationTokenSource()) {
                IsPrivate     = isPrivate,
                Work          = work,
                McpConfigPath = built.McpConfigPath,
                CurrentCols   = cols,
                CurrentRows   = rows
            };
            _agents[agentId] = agent;
```

Register before starting the read loop (replace the tail at lines 82-83):

```csharp
        // Register like a UI launch (no-op for --private). Best-effort: a registration hiccup
        // must not break the local terminal session.
        try { await RegisterAgentAsync(agent); }
        catch (Exception ex) { LogLocalRegisterFailed(ex, agentId); }

        _ = ReadAgentOutputAsync(agent);
        await AttachClientLoopAsync(agent, stream, ct);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentOrchestratorVendorTests/*"`
Expected: PASS (`Registered_spawn...` green; `Private_spawn...` still green).

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs
git commit -m "feat(daemon): register local agents on spawn (default); --private opts out"
```

---

## Task 5: Per-agent dims on reconnect + every resize path

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (`ReRegisterAgentsAsync` line 941; `HandleResizeTerminal` 900-906; add test hooks)
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs` (`ClampPtyLocked` 189-195; `ApplyResizeClamp` 176-181)
- Test: `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs`

**Interfaces:**
- Consumes: `AgentInstance.CurrentCols/Rows` (Task 3).
- Produces: test hooks `internal Task ReRegisterAgentsForTestAsync()` and `internal void HandleResizeTerminalForTest(ResizeTerminalCommand cmd)`.

- [ ] **Step 1: Write the failing tests**

Add to `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs`:

```csharp
    [Test]
    public async Task Reconnect_resends_stored_dims_not_the_hosted_constant() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        orch.RegisterAgentForTest(new AgentInstance("reg-1", null, "", null, "/r", "claude",
            new StubPtyProcess(), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) {
            IsPrivate = false, Status = "Running", CurrentCols = 73, CurrentRows = 19
        });

        await orch.ReRegisterAgentsForTestAsync();

        await Assert.That(server.LastDims).IsEqualTo(((ushort)73, (ushort)19));
    }

    [Test]
    public async Task Web_resize_updates_stored_dims_then_reconnect_resends_them() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var agent = new AgentInstance("reg-2", null, "", null, "/r", "claude",
            new StubPtyProcess(), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) {
            IsPrivate = false, Status = "Running", CurrentCols = 80, CurrentRows = 24
        };
        orch.RegisterAgentForTest(agent);

        orch.HandleResizeTerminalForTest(new ResizeTerminalCommand("reg-2", 51, 200));
        await Assert.That(agent.CurrentCols).IsEqualTo((ushort)51);
        await Assert.That(agent.CurrentRows).IsEqualTo((ushort)200);

        await orch.ReRegisterAgentsForTestAsync();
        await Assert.That(server.LastDims).IsEqualTo(((ushort)51, (ushort)200));
    }
```

Add a dims-capturing override to `TripwireServerConnection` (in the same file):

```csharp
        public (ushort Cols, ushort Rows)? LastDims { get; private set; }
        public override Task SendTerminalDimensionsAsync(string agentId, ushort cols, ushort rows) {
            LastDims = (cols, rows); Calls.Add(nameof(SendTerminalDimensionsAsync)); return Task.CompletedTask;
        }
```

(If `SendTerminalDimensionsAsync` is not already `virtual` on `ServerConnection`, make it `virtual` — the other overridden members establish that pattern. Confirm the `ResizeTerminalCommand` constructor arg order is `(AgentId, Cols, Rows)`; adjust the test literal if the record differs.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentOrchestratorVendorTests/Reconnect_resends_stored_dims_not_the_hosted_constant"`
Expected: FAIL (compile — no `ReRegisterAgentsForTestAsync` / `HandleResizeTerminalForTest` / `LastDims`).

- [ ] **Step 3: Reconnect resends stored dims**

In `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs`, in `ReRegisterAgentsAsync`, change the dims resend (line 941):

```csharp
                    try {
                        await _server.SendTerminalDimensionsAsync(agent.Id, agent.CurrentCols, agent.CurrentRows);
                    } catch (Exception ex) {
                        LogTerminalDimsSendFailed(ex, agent.Id);
                    }
```

- [ ] **Step 4: Web resize updates stored dims**

In the same file, update `HandleResizeTerminal` (900-906):

```csharp
    Task HandleResizeTerminal(ResizeTerminalCommand cmd) {
        if (_agents.TryGetValue(cmd.AgentId, out var agent)) {
            agent.Process.Resize((ushort)cmd.Cols, (ushort)cmd.Rows);
            agent.CurrentCols = (ushort)cmd.Cols;
            agent.CurrentRows = (ushort)cmd.Rows;
        }

        return Task.CompletedTask;
    }
```

- [ ] **Step 5: Local clamp updates stored dims + announces**

In `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs`, update `ClampPtyLocked`:

```csharp
    void ClampPtyLocked(AgentInstance agent) {
        if (agent.ClientDims.Count == 0) return;

        var c = agent.ClientDims.Values.Min(d => d.Cols);
        var r = agent.ClientDims.Values.Min(d => d.Rows);
        if (c > 0 && r > 0) {
            agent.Process.Resize(c, r);
            agent.CurrentCols = c;
            agent.CurrentRows = r;
        }
    }
```

Announce after a live resize (so web viewers re-lock) — update `ApplyResizeClamp`:

```csharp
    void ApplyResizeClamp(AgentInstance agent, ITerminalSink sink, ushort cols, ushort rows) {
        lock (agent.SinksLock) {
            agent.ClientDims[sink] = new AgentInstance.Dim(cols, rows);
            ClampPtyLocked(agent);
        }

        if (!agent.IsPrivate) _ = SafeSendDimsAsync(agent);
    }

    async Task SafeSendDimsAsync(AgentInstance agent) {
        try { await _server.SendTerminalDimensionsAsync(agent.Id, agent.CurrentCols, agent.CurrentRows); }
        catch (Exception ex) { LogTerminalDimsSendFailed(ex, agent.Id); }
    }
```

- [ ] **Step 6: Add the test hooks**

In `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs`, near the other test hooks:

```csharp
    internal Task ReRegisterAgentsForTestAsync() => ReRegisterAgentsAsync();
    internal void HandleResizeTerminalForTest(ResizeTerminalCommand cmd) => _ = HandleResizeTerminal(cmd);
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentOrchestratorVendorTests/*"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs
git commit -m "fix(daemon): report real per-agent PTY dims on reconnect and every resize"
```

---

## Task 6: Exclude `--private` agents from `LiveAgentIds`

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs:187-191`
- Test: `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs`

**Interfaces:**
- Consumes: `_server.GetLiveAgentIds` (already wired in the orchestrator ctor).

- [ ] **Step 1: Write the failing test**

Add to `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs`:

```csharp
    [Test]
    public async Task Private_agents_are_excluded_from_live_agent_ids() {
        var server = new CaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        orch.RegisterAgentForTest(new AgentInstance("pub-1", null, "", null, "/r", "claude",
            new StubPtyProcess(), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) { IsPrivate = false, Status = "Running" });
        orch.RegisterAgentForTest(new AgentInstance("priv-1", null, "", null, "/r", "claude",
            new StubPtyProcess(), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) { IsPrivate = true, Status = "Running" });

        var ids = server.GetLiveAgentIds!();

        await Assert.That(ids).Contains("pub-1");
        await Assert.That(ids).DoesNotContain("priv-1");
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentOrchestratorVendorTests/Private_agents_are_excluded_from_live_agent_ids"`
Expected: FAIL (`priv-1` present — filter not applied yet).

- [ ] **Step 3: Add the filter**

In `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs:187-191`:

```csharp
        _server.GetLiveAgentIds = () => [
            .. _agents
                .Where(kvp => (kvp.Value.Status is "Starting" or "Running") && !kvp.Value.IsPrivate)
                .Select(kvp => kvp.Key)
        ];
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentOrchestratorVendorTests/Private_agents_are_excluded_from_live_agent_ids"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs
git commit -m "fix(daemon): keep --private agent ids out of DaemonConnect LiveAgentIds"
```

---

## Task 7: Docs — README + help text

**Files:**
- Modify: `README.md` (Getting started + the `run-agent` command section)
- Modify: `src/Capacitor.Cli.Core/Resources/help-*.txt` (the run-agent help blocks)

- [ ] **Step 1: Update the README `run-agent` section**

In `README.md`, under the `run-agent` command documentation, add the `--private` flag and state the new default. Insert this (adapt wording to the surrounding style):

```markdown
By default `kcap run-agent` registers the agent with the server, so it appears in your
web UI immediately and you can drive it from there (continue-from-anywhere). It is
visible only to you until you share it. Add `--private` to keep the agent purely local —
unregistered, not streamed to the server, with permission prompts answered natively in
the terminal.
```

- [ ] **Step 2: Update the Getting started quick-start**

In the `## Getting started` section, where `run-agent` is introduced, add one line:

```markdown
A locally-launched agent now shows up in your web UI by default — close the terminal and
keep driving it from the browser. Use `kcap run-agent --private` to opt out.
```

- [ ] **Step 3: Update the help text**

In `src/Capacitor.Cli.Core/Resources/help-*.txt`, find the `run-agent` usage/flags block and add the `--private` line next to `--worktree`/`--detached`:

```
  --private     keep the agent local-only (not registered/visible in the web UI)
```

- [ ] **Step 4: Commit**

```bash
git add README.md src/Capacitor.Cli.Core/Resources/help-*.txt
git commit -m "docs: document run-agent default registration and --private"
```

---

## Task 8: Full verification (tests + AOT publish)

**Files:** none (verification only).

- [ ] **Step 1: Run the full unit-test suite**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
Expected: all tests PASS.

- [ ] **Step 2: AOT publish and check for trimming warnings**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: **no output** (no IL3050/IL2026 warnings). If the publish copied an AOT binary on macOS, re-sign with `codesign --force --sign - <binary>`.

- [ ] **Step 3: Commit (if anything changed) / done**

No code changes expected here; this task is a gate. If the publish surfaced a warning, fix it in the offending task and re-run.

---

## Self-Review

**Spec coverage:**
- Registration like a UI launch → Tasks 3 (helper) + 4 (local path). ✓
- `KCAP_AGENT_ID` + hosted env / bridge-mode permissions → Task 4 env block. ✓
- Eager streaming / item 4 → falls out of `IsPrivate = false` (Task 4); the existing read-loop `SendTerminalOutputAsync` gate; first-subscribe replay is server-side (verification in manual testing). ✓
- `--private` opt-out → Tasks 1 (parse) + 2 (frame) + 4 (daemon honors it). ✓
- Wire-compat (append, default private) → Task 2. ✓
- Per-agent dims at registration / clamp / web-resize / reconnect → Tasks 3 + 5. ✓
- `LiveAgentIds` leak → Task 6. ✓
- Privacy test re-scope + registration/dims tests → Tasks 4, 5. ✓
- In-place safety unchanged → not modified; existing `Borrowed_cwd_cleanup...` test still asserts it. ✓
- Docs → Task 7. ✓
- AOT → Task 8. ✓
- **Bridge-mode local-keypress permission resolution** is a *manual* validation item (per spec), not automatable here — call it out during manual testing; not a coded task.

**Placeholder scan:** none — every code step shows the code.

**Type consistency:** `RegisterAgentAsync(AgentInstance)`, `CurrentCols`/`CurrentRows` (ushort), `FrameCodec.Spawn` 7-tuple `(vendor, work, isPrivate, cwd, args, cols, rows)`, `RunAgentArgs.Private`, test hooks `RegisterAgentForTestAsync` / `ReRegisterAgentsForTestAsync` / `HandleResizeTerminalForTest` — used consistently across tasks.

**Implementation-time confirmations (cheap to verify in-code, noted so the implementer isn't surprised):** `ServerConnection.SendTerminalDimensionsAsync` is `virtual` (make it so if not, for the test double); the `ResizeTerminalCommand` constructor is `(AgentId, Cols, Rows)`; `CaptureServerConnection` exists in the test project (used by existing local-attach tests).
