# Daemon Restart-After-Update Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A running kcap daemon detects that its on-disk binary was updated and restarts itself only when idle (no hosted agents and no in-flight eval run), via a clean supervisor-honored exit (service-managed) or a flock-handoff self-respawn (detached).

**Architecture:** A new daemon-side `RestartCoordinator` background service polls the running binary's size+mtime; a change queues a restart. On each tick, if a restart is queued and the daemon is idle, it invokes the `IRestartStrategy` chosen once at startup by `SupervisionDetector`. A name-specific `KCAP_DAEMON_SUPERVISED` marker plus non-inheritable runtime probes classify supervision. A `kcap daemon restart` command and a `<name>.restart-pending` marker file provide manual control and observability. Decision logic is extracted into pure functions for unit testing; OS-touching strategies sit behind an interface.

**Tech Stack:** .NET 10 NativeAOT, TUnit (Microsoft Testing Platform), Unix-domain-socket IPC (`FrameCodec`), `Microsoft.Extensions.Hosting` BackgroundService, Node.js npm launcher (`kcap.js`).

## Global Constraints

- **.NET 10, NativeAOT.** No reflection-based serialization. Use `JsonObject`/`JsonNode` (System.Text.Json.Nodes), never `JsonSerializer.Serialize<T>`. After any change run `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` and confirm no output.
- **JsonArray:** use `new JsonArray(a, b)` constructor, never collection-expression `[a, b]` (compiles to dynamic-code `Add<T>()`).
- **TUnit filtering:** use `--treenode-filter` (glob), never `--filter`.
- **Platform scope:** auto-restart (self-detection) is macOS + Linux only; the poll is a no-op on Windows. The `restart` command and marker work cross-platform.
- **README sync (mandatory, same PR):** any user-facing CLI change updates `README.md` — both `## Getting started` and the `daemon` section under `## CLI commands`. Updating only `help-*.txt` is insufficient.
- **Supervision label/prefix constants must match the CLI's service units:** launchd label prefix `io.kurrent.kcap.daemon.` (`LaunchdUnit.LabelPrefix`), systemd unit prefix `kcap-daemon-` (`SystemdUnit.Prefix`). The service id is `DaemonLockPaths.Sanitize(name)`.
- **Branch:** all work on `daemon-restart-on-update` (already checked out). Commit after every task.
- Run unit tests with: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`

---

## File Structure

**New — `Capacitor.Cli.Core` (shared by CLI + daemon):**
- `src/Capacitor.Cli.Core/ExitCodes.cs` — process exit-code constants.
- `src/Capacitor.Cli.Core/DaemonRestartMarker.cs` — `<name>.restart-pending` read/write/delete + JSON shape.

**New — `Capacitor.Cli.Daemon`:**
- `src/Capacitor.Cli.Daemon/Services/SupervisionDetector.cs` — `SupervisionMode` enum + pure `Detect(...)`.
- `src/Capacitor.Cli.Daemon/Services/RestartDecision.cs` — pure gate/binary-change helpers + `BinaryStat`.
- `src/Capacitor.Cli.Daemon/Services/IRestartStrategy.cs` + `SupervisedExitStrategy.cs`, `DetachedRespawnStrategy.cs`, `ForegroundNoopStrategy.cs`.
- `src/Capacitor.Cli.Daemon/Services/RestartState.cs` — singleton flag DaemonRunner reads to pick the exit code.
- `src/Capacitor.Cli.Daemon/Services/RestartCoordinator.cs` — the BackgroundService wiring it together.

**Modified:**
- `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs`, `LocalFrame.cs`, `FrameCodec.cs` — restart frames.
- `src/Capacitor.Cli.Core/DaemonLockPaths.cs` — `RestartPendingPath` + `EnumerateNames` unions markers.
- `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` — expose `ActiveCount` to the assembly.
- `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs` — route `Restart` frames.
- `src/Capacitor.Cli.Daemon/DaemonConfig.cs` — carry original argv.
- `src/Capacitor.Cli.Daemon/DaemonLock.cs` — `--await-lock` retry overload.
- `src/Capacitor.Cli.Daemon/DaemonRunner.cs` — register services, parse `--await-lock`, return restart exit code.
- `src/Capacitor.Cli.Daemon/Pty/Unix/UnixPtyProcess.cs` + `Pty/Windows/ConPtyProcess.cs` — scrub supervision env.
- `src/Capacitor.Cli/Commands/DaemonCommands.cs` — `restart` subcommand, status marker line, `ServiceInstall` marker, usage.
- `npm/kcap/bin/kcap.js` — Windows preflight.
- `src/Capacitor.Cli.Core/Resources/help-daemon.txt` (or the relevant help file) + `README.md`.

---

## Task 1: Restart-pending marker + lock-path enumeration (Core)

**Files:**
- Create: `src/Capacitor.Cli.Core/DaemonRestartMarker.cs`
- Modify: `src/Capacitor.Cli.Core/DaemonLockPaths.cs` (add `RestartPendingPath`; extend `EnumerateNames`)
- Test: `test/Capacitor.Cli.Tests.Unit/DaemonRestartMarkerTests.cs`

**Interfaces:**
- Produces: `DaemonLockPaths.RestartPendingPath(string name) : string`; `DaemonRestartMarker` record `(string RunningVersion, string Reason, DateTimeOffset QueuedAt)` with `static void Write(string name, DaemonRestartMarker m)`, `static DaemonRestartMarker? TryRead(string name)`, `static void Delete(string name)`, and instance `string Describe()` for status output.
- Consumes: `DaemonLockPaths.Directory`, `DaemonLockPaths.Sanitize`, `DaemonLockPaths.OverrideDirectoryForTesting` (test-only).

- [ ] **Step 1: Write the failing test**

```csharp
// test/Capacitor.Cli.Tests.Unit/DaemonRestartMarkerTests.cs
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class DaemonRestartMarkerTests {
    [Test]
    public async Task Write_then_read_round_trips() {
        var dir = Directory.CreateTempSubdirectory("kcap-marker-");
        DaemonLockPaths.OverrideDirectoryForTesting(dir.FullName);
        try {
            var when = new DateTimeOffset(2026, 6, 25, 12, 3, 0, TimeSpan.Zero);
            DaemonRestartMarker.Write("laptop", new DaemonRestartMarker("v0.4.11", "self-detected", when));

            var read = DaemonRestartMarker.TryRead("laptop");

            await Assert.That(read).IsNotNull();
            await Assert.That(read!.RunningVersion).IsEqualTo("v0.4.11");
            await Assert.That(read.Reason).IsEqualTo("self-detected");
            await Assert.That(read.QueuedAt).IsEqualTo(when);
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            dir.Delete(true);
        }
    }

    [Test]
    public async Task TryRead_returns_null_when_absent() {
        var dir = Directory.CreateTempSubdirectory("kcap-marker-");
        DaemonLockPaths.OverrideDirectoryForTesting(dir.FullName);
        try {
            await Assert.That(DaemonRestartMarker.TryRead("nope")).IsNull();
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            dir.Delete(true);
        }
    }

    [Test]
    public async Task EnumerateNames_includes_marker_only_entry() {
        var dir = Directory.CreateTempSubdirectory("kcap-marker-");
        DaemonLockPaths.OverrideDirectoryForTesting(dir.FullName);
        try {
            File.WriteAllText(DaemonLockPaths.RestartPendingPath("orphan"), "{}");
            await Assert.That(DaemonLockPaths.EnumerateNames()).Contains("orphan");
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            dir.Delete(true);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/DaemonRestartMarkerTests/*"`
Expected: compile error / FAIL — `DaemonRestartMarker` and `RestartPendingPath` don't exist.

- [ ] **Step 3: Add `RestartPendingPath` and extend `EnumerateNames` in `DaemonLockPaths.cs`**

Add this method next to `StartLockPath`:

```csharp
    /// <summary>Path to the daemon's "restart pending" marker (queued restart-after-update state).</summary>
    public static string RestartPendingPath(string daemonName) =>
        Path.Combine(Directory, $"{Sanitize(daemonName)}.restart-pending");
```

In `EnumerateNames`, add a third glob and include it in the union:

```csharp
        var fromMarkers = System.IO.Directory.EnumerateFiles(Directory, "*.restart-pending")
            .Select(Path.GetFileNameWithoutExtension);

        return [
            .. fromLocks.Concat(fromPids).Concat(fromMarkers)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .Distinct()
                .Order()
        ];
```

- [ ] **Step 4: Create `DaemonRestartMarker.cs`**

```csharp
// src/Capacitor.Cli.Core/DaemonRestartMarker.cs
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core;

/// <summary>
/// The <c>&lt;name&gt;.restart-pending</c> marker the daemon writes when a
/// restart-after-update is queued and clears on (successor) startup. Read by
/// <c>kcap daemon status</c> for observability — same on-disk pattern as the PID
/// file, so status needs no socket round-trip. JSON via <see cref="JsonObject"/>
/// to stay NativeAOT-safe (no reflection serializer).
/// </summary>
public sealed record DaemonRestartMarker(string RunningVersion, string Reason, DateTimeOffset QueuedAt) {
    public static void Write(string daemonName, DaemonRestartMarker m) {
        DaemonLockPaths.EnsureDirectory();
        var obj = new JsonObject {
            ["running_version"] = m.RunningVersion,
            ["reason"]          = m.Reason,
            ["queued_at"]       = m.QueuedAt,
        };
        var path = DaemonLockPaths.RestartPendingPath(daemonName);
        var tmp  = $"{path}.tmp";
        File.WriteAllText(tmp, obj.ToJsonString());
        File.Move(tmp, path, overwrite: true);
    }

    public static DaemonRestartMarker? TryRead(string daemonName) {
        var path = DaemonLockPaths.RestartPendingPath(daemonName);
        if (!File.Exists(path)) return null;
        try {
            var node = JsonNode.Parse(File.ReadAllText(path));
            var ver  = node?["running_version"]?.GetValue<string>();
            var why  = node?["reason"]?.GetValue<string>() ?? "requested";
            var when = node?["queued_at"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.UnixEpoch;
            return ver is null ? null : new DaemonRestartMarker(ver, why, when);
        } catch {
            return null; // corrupt marker — treat as absent
        }
    }

    public static void Delete(string daemonName) {
        try { File.Delete(DaemonLockPaths.RestartPendingPath(daemonName)); } catch { /* best-effort */ }
    }

    /// <summary>One-line status text, e.g. "restart pending: running v0.4.11, newer binary detected on disk (queued 2026-06-25 12:03, self-detected)".</summary>
    public string Describe() =>
        $"restart pending: running {RunningVersion}, newer binary detected on disk "
      + $"(queued {QueuedAt.LocalDateTime:yyyy-MM-dd HH:mm}, {Reason})";
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/DaemonRestartMarkerTests/*"`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Core/DaemonRestartMarker.cs src/Capacitor.Cli.Core/DaemonLockPaths.cs test/Capacitor.Cli.Tests.Unit/DaemonRestartMarkerTests.cs
git commit -m "feat(daemon): restart-pending marker + lock-path enumeration"
```

---

## Task 2: Restart IPC frames (Core)

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs`, `LocalFrame.cs`, `FrameCodec.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/RestartFrameCodecTests.cs`

**Interfaces:**
- Produces: `FrameType.Restart = 7`, `FrameType.RestartAck = 69`; `LocalFrame.Restart(string mode)`, `LocalFrame.RestartAck(string status)`. Mode/status travel in `LocalFrame.Text`.
- Consumes: existing `FrameCodec.WriteAsync`/`ReadAsync`.

- [ ] **Step 1: Write the failing test**

```csharp
// test/Capacitor.Cli.Tests.Unit/RestartFrameCodecTests.cs
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Tests.Unit;

public class RestartFrameCodecTests {
    [Test]
    public async Task Restart_frame_round_trips_mode() {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, LocalFrame.Restart("when-idle"), default);
        ms.Position = 0;
        var f = await FrameCodec.ReadAsync(ms, default);

        await Assert.That(f).IsNotNull();
        await Assert.That(f!.Type).IsEqualTo(FrameType.Restart);
        await Assert.That(f.Text).IsEqualTo("when-idle");
    }

    [Test]
    public async Task RestartAck_frame_round_trips_status() {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, LocalFrame.RestartAck("queued"), default);
        ms.Position = 0;
        var f = await FrameCodec.ReadAsync(ms, default);

        await Assert.That(f!.Type).IsEqualTo(FrameType.RestartAck);
        await Assert.That(f.Text).IsEqualTo("queued");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/RestartFrameCodecTests/*"`
Expected: compile error — `FrameType.Restart` / `LocalFrame.Restart` undefined.

- [ ] **Step 3: Add frame types** in `FrameType.cs`

Add to the client→daemon group and the daemon→client group:

```csharp
    Restart = 7,   // client → daemon: request restart-after-update (Text = "when-idle"|"now"|"force")
    // ... existing daemon → client entries ...
    RestartAck = 69, // daemon → client: restart acknowledgement (Text = short status)
```

- [ ] **Step 4: Add factory helpers** in `LocalFrame.cs` (after `Error`)

```csharp
    public static LocalFrame Restart(string mode)      => new(FrameType.Restart)    { Text = mode };
    public static LocalFrame RestartAck(string status) => new(FrameType.RestartAck) { Text = status };
```

- [ ] **Step 5: Add codec arms** in `FrameCodec.cs` — extend the two UTF-8 `Text` cases in `Encode` and `Decode`

In `Encode`:

```csharp
        FrameType.Error or FrameType.Attach or FrameType.AgentList
            or FrameType.Restart or FrameType.RestartAck => Encoding.UTF8.GetBytes(f.Text),
```

In `Decode`:

```csharp
        FrameType.Error or FrameType.Attach or FrameType.AgentList
            or FrameType.Restart or FrameType.RestartAck => new(t) { Text = Encoding.UTF8.GetString(p) },
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/RestartFrameCodecTests/*"`
Expected: PASS (2 tests).

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli.Core/LocalIpc/FrameType.cs src/Capacitor.Cli.Core/LocalIpc/LocalFrame.cs src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs test/Capacitor.Cli.Tests.Unit/RestartFrameCodecTests.cs
git commit -m "feat(ipc): Restart/RestartAck control frames"
```

---

## Task 3: Supervision detector (Daemon)

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/SupervisionDetector.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/SupervisionDetectorTests.cs`

**Interfaces:**
- Produces: `enum SupervisionMode { Supervised, Detached, Foreground }`; pure `SupervisionDetector.Detect(IReadOnlyDictionary<string,string> env, string sanitizedName, bool hasLogFile, string? cgroupContents, int processId) : SupervisionMode`. A production helper `SupervisionDetector.DetectCurrent(string sanitizedName, bool hasLogFile)` reads real env + `/proc/self/cgroup`.

- [ ] **Step 1: Write the failing test**

```csharp
// test/Capacitor.Cli.Tests.Unit/Daemon/SupervisionDetectorTests.cs
using System.Collections.Generic;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class SupervisionDetectorTests {
    static SupervisionMode Detect(Dictionary<string, string> env, string name = "laptop",
        bool hasLogFile = true, string? cgroup = null, int pid = 4242) =>
        SupervisionDetector.Detect(env, name, hasLogFile, cgroup, pid);

    [Test]
    public async Task Marker_matching_name_is_supervised() =>
        await Assert.That(Detect(new() { ["KCAP_DAEMON_SUPERVISED"] = "laptop" }))
            .IsEqualTo(SupervisionMode.Supervised);

    [Test]
    public async Task Marker_for_different_name_is_not_supervised() =>
        await Assert.That(Detect(new() { ["KCAP_DAEMON_SUPERVISED"] = "ci" }))
            .IsEqualTo(SupervisionMode.Detached);

    [Test]
    public async Task Systemd_cgroup_plus_exec_pid_match_is_supervised() =>
        await Assert.That(Detect(new() { ["SYSTEMD_EXEC_PID"] = "4242" },
            cgroup: "0::/user.slice/user-1000.slice/.../kcap-daemon-laptop.service"))
            .IsEqualTo(SupervisionMode.Supervised);

    [Test]
    public async Task Systemd_cgroup_with_inherited_exec_pid_is_not_supervised() =>
        await Assert.That(Detect(new() { ["SYSTEMD_EXEC_PID"] = "999" /* parent's pid */ },
            cgroup: "0::/user.slice/.../kcap-daemon-laptop.service"))
            .IsEqualTo(SupervisionMode.Detached);

    [Test]
    public async Task Launchd_exact_label_is_supervised() =>
        await Assert.That(Detect(new() { ["XPC_SERVICE_NAME"] = "io.kurrent.kcap.daemon.laptop" }))
            .IsEqualTo(SupervisionMode.Supervised);

    [Test]
    public async Task Launchd_different_label_is_not_supervised() =>
        await Assert.That(Detect(new() { ["XPC_SERVICE_NAME"] = "io.kurrent.kcap.daemon.other" }))
            .IsEqualTo(SupervisionMode.Detached);

    [Test]
    public async Task No_signals_with_logfile_is_detached() =>
        await Assert.That(Detect(new(), hasLogFile: true)).IsEqualTo(SupervisionMode.Detached);

    [Test]
    public async Task No_signals_no_logfile_is_foreground() =>
        await Assert.That(Detect(new(), hasLogFile: false)).IsEqualTo(SupervisionMode.Foreground);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/SupervisionDetectorTests/*"`
Expected: compile error — `SupervisionDetector` undefined.

- [ ] **Step 3: Create `SupervisionDetector.cs`**

```csharp
// src/Capacitor.Cli.Daemon/Services/SupervisionDetector.cs
namespace Capacitor.Cli.Daemon.Services;

public enum SupervisionMode { Supervised, Detached, Foreground }

/// <summary>
/// Classifies how this daemon process was launched, choosing the restart strategy.
/// All "supervised" signals are name-bound and non-inheritable so an inherited env
/// var (e.g. a marker leaking into a daemon-spawned agent) can't misclassify a
/// different-name daemon: the marker must equal our own sanitized name; systemd
/// requires our unit's cgroup AND SYSTEMD_EXEC_PID == our PID (a child has a
/// different PID); launchd requires XPC_SERVICE_NAME to equal our exact label.
/// (INVOCATION_ID is intentionally NOT used — it is inherited by children.)
/// </summary>
public static class SupervisionDetector {
    // Must match LaunchdUnit.LabelPrefix and SystemdUnit.Prefix in Capacitor.Cli.
    const string LaunchdLabelPrefix = "io.kurrent.kcap.daemon.";
    const string SystemdUnitPrefix  = "kcap-daemon-";

    public static SupervisionMode Detect(
            IReadOnlyDictionary<string, string> env,
            string                              sanitizedName,
            bool                                hasLogFile,
            string?                             cgroupContents,
            int                                 processId) {

        // 1. Authoritative, name-specific marker.
        if (env.TryGetValue("KCAP_DAEMON_SUPERVISED", out var marker) && marker == sanitizedName)
            return SupervisionMode.Supervised;

        // 2. systemd: our unit's cgroup AND direct-launch proof (non-inheritable).
        var inOurCgroup = cgroupContents is not null
                       && cgroupContents.Contains($"{SystemdUnitPrefix}{sanitizedName}.service", StringComparison.Ordinal);
        var execPidMatches = env.TryGetValue("SYSTEMD_EXEC_PID", out var execPid)
                          && execPid == processId.ToString();
        if (inOurCgroup && execPidMatches) return SupervisionMode.Supervised;

        // 3. launchd: exact label match.
        if (env.TryGetValue("XPC_SERVICE_NAME", out var label)
         && label == $"{LaunchdLabelPrefix}{sanitizedName}")
            return SupervisionMode.Supervised;

        // 4. Not supervised: detached if it logs to a file (CLI -d adds --log-file),
        //    otherwise an interactive foreground run.
        return hasLogFile ? SupervisionMode.Detached : SupervisionMode.Foreground;
    }

    /// <summary>Production entry: reads the real environment and /proc/self/cgroup.</summary>
    public static SupervisionMode DetectCurrent(string sanitizedName, bool hasLogFile) {
        var env = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(e => e.Value is not null)
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.Ordinal);

        string? cgroup = null;
        try { if (File.Exists("/proc/self/cgroup")) cgroup = File.ReadAllText("/proc/self/cgroup"); }
        catch { /* not Linux / unreadable — leave null */ }

        return Detect(env, sanitizedName, hasLogFile, cgroup, Environment.ProcessId);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/SupervisionDetectorTests/*"`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/SupervisionDetector.cs test/Capacitor.Cli.Tests.Unit/Daemon/SupervisionDetectorTests.cs
git commit -m "feat(daemon): supervision detector (name-bound, non-inheritable signals)"
```

---

## Task 4: Restart gate + binary-change decision helpers (Daemon)

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/RestartDecision.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/RestartDecisionTests.cs`

**Interfaces:**
- Produces: `readonly record struct BinaryStat(long Size, long MtimeTicks)`; `RestartDecision.BinaryChanged(BinaryStat? baseline, BinaryStat? current) : bool`; `RestartDecision.ShouldFire(bool pending, bool busy, bool force) : bool`.

- [ ] **Step 1: Write the failing test**

```csharp
// test/Capacitor.Cli.Tests.Unit/Daemon/RestartDecisionTests.cs
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class RestartDecisionTests {
    static readonly BinaryStat A = new(100, 1000);

    [Test]
    public async Task Fires_when_pending_and_idle() =>
        await Assert.That(RestartDecision.ShouldFire(pending: true, busy: false, force: false)).IsTrue();

    [Test]
    public async Task Does_not_fire_when_busy() =>
        await Assert.That(RestartDecision.ShouldFire(pending: true, busy: true, force: false)).IsFalse();

    [Test]
    public async Task Force_overrides_busy() =>
        await Assert.That(RestartDecision.ShouldFire(pending: true, busy: true, force: true)).IsTrue();

    [Test]
    public async Task Does_not_fire_when_not_pending() =>
        await Assert.That(RestartDecision.ShouldFire(pending: false, busy: false, force: true)).IsFalse();

    [Test]
    public async Task Size_change_is_a_change() =>
        await Assert.That(RestartDecision.BinaryChanged(A, A with { Size = 200 })).IsTrue();

    [Test]
    public async Task Mtime_change_is_a_change() =>
        await Assert.That(RestartDecision.BinaryChanged(A, A with { MtimeTicks = 2000 })).IsTrue();

    [Test]
    public async Task Identical_is_not_a_change() =>
        await Assert.That(RestartDecision.BinaryChanged(A, A)).IsFalse();

    [Test]
    public async Task Null_current_is_transient_not_a_change() =>
        await Assert.That(RestartDecision.BinaryChanged(A, null)).IsFalse();

    [Test]
    public async Task Null_baseline_is_not_a_change() =>
        await Assert.That(RestartDecision.BinaryChanged(null, A)).IsFalse();
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/RestartDecisionTests/*"`
Expected: compile error — `RestartDecision` undefined.

- [ ] **Step 3: Create `RestartDecision.cs`**

```csharp
// src/Capacitor.Cli.Daemon/Services/RestartDecision.cs
namespace Capacitor.Cli.Daemon.Services;

/// <summary>A cheap on-disk binary fingerprint: size + last-write-time ticks.</summary>
public readonly record struct BinaryStat(long Size, long MtimeTicks);

/// <summary>Pure decision helpers for the restart coordinator (unit-tested in isolation).</summary>
public static class RestartDecision {
    /// <summary>
    /// True when the on-disk binary differs from the startup baseline. A null
    /// <paramref name="current"/> means a transient stat failure (binary briefly
    /// missing mid-install) — treated as "no change" so we skip the tick. A null
    /// baseline (couldn't stat at startup) disables detection.
    /// </summary>
    public static bool BinaryChanged(BinaryStat? baseline, BinaryStat? current) =>
        baseline is { } b && current is { } c && (b.Size != c.Size || b.MtimeTicks != c.MtimeTicks);

    /// <summary>Restart fires only when one is queued AND (the daemon is idle OR the request forced it).</summary>
    public static bool ShouldFire(bool pending, bool busy, bool force) => pending && (force || !busy);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/RestartDecisionTests/*"`
Expected: PASS (9 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/RestartDecision.cs test/Capacitor.Cli.Tests.Unit/Daemon/RestartDecisionTests.cs
git commit -m "feat(daemon): pure restart gate + binary-change helpers"
```

---

## Task 5: Exit code, restart state, and restart strategies (Daemon)

**Files:**
- Create: `src/Capacitor.Cli.Core/ExitCodes.cs`
- Create: `src/Capacitor.Cli.Daemon/Services/RestartState.cs`
- Create: `src/Capacitor.Cli.Daemon/Services/IRestartStrategy.cs`, `SupervisedExitStrategy.cs`, `DetachedRespawnStrategy.cs`, `ForegroundNoopStrategy.cs`
- Modify: `src/Capacitor.Cli.Daemon/DaemonConfig.cs` (add `OriginalArgs` — first consumer is `DetachedRespawnStrategy` here)
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/DetachedRespawnArgsTests.cs`

**Interfaces:**
- Produces: `ExitCodes.RestartRequested = 10`; `RestartState { volatile bool SupervisedRestart }`; `interface IRestartStrategy { void Restart(); }`; `DetachedRespawnStrategy.BuildChildArgs(IReadOnlyList<string> originalArgs) : string[]` (pure, appends `--await-lock` once); `DaemonConfig.OriginalArgs : IReadOnlyList<string>` (default empty).
- Consumes: `IHostApplicationLifetime` (StopApplication), `Environment.ProcessPath`.

- [ ] **Step 1: Write the failing test** (the only purely-unit-testable piece — child-arg construction)

```csharp
// test/Capacitor.Cli.Tests.Unit/Daemon/DetachedRespawnArgsTests.cs
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class DetachedRespawnArgsTests {
    [Test]
    public async Task Appends_await_lock_once() {
        var args = DetachedRespawnStrategy.BuildChildArgs(["--name", "laptop", "--log-file", "/tmp/d.log"]);
        await Assert.That(args).IsEquivalentTo(new[] { "--name", "laptop", "--log-file", "/tmp/d.log", "--await-lock" });
    }

    [Test]
    public async Task Does_not_duplicate_existing_await_lock() {
        var args = DetachedRespawnStrategy.BuildChildArgs(["--name", "laptop", "--await-lock"]);
        await Assert.That(args.Count(a => a == "--await-lock")).IsEqualTo(1);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/DetachedRespawnArgsTests/*"`
Expected: compile error — `DetachedRespawnStrategy` undefined.

- [ ] **Step 3: Create `ExitCodes.cs`**

```csharp
// src/Capacitor.Cli.Core/ExitCodes.cs
namespace Capacitor.Cli.Core;

/// <summary>Daemon process exit codes wrappers/supervisors interpret.</summary>
public static class ExitCodes {
    /// <summary>
    /// Controlled restart-after-update for a supervised daemon. Non-zero so the
    /// failure-restart policy relaunches us (launchd KeepAlive/SuccessfulExit=false,
    /// systemd Restart=on-failure). Distinct from 1 (config error) and 2/3 (name-in-use).
    /// </summary>
    public const int RestartRequested = 10;
}
```

- [ ] **Step 4: Create `RestartState.cs`**

```csharp
// src/Capacitor.Cli.Daemon/Services/RestartState.cs
namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Shared flag the supervised strategy sets so <c>DaemonRunner.RunAsync</c> returns
/// <see cref="Capacitor.Cli.Core.ExitCodes.RestartRequested"/> instead of 0 after the
/// host shuts down. Registered as a singleton.
/// </summary>
public sealed class RestartState {
    public volatile bool SupervisedRestart;
}
```

- [ ] **Step 5: Create `IRestartStrategy.cs` + the three strategies**

```csharp
// src/Capacitor.Cli.Daemon/Services/IRestartStrategy.cs
namespace Capacitor.Cli.Daemon.Services;

/// <summary>The OS-specific action that applies a queued restart. Selected once at startup.</summary>
public interface IRestartStrategy {
    void Restart();
}
```

```csharp
// src/Capacitor.Cli.Daemon/Services/SupervisedExitStrategy.cs
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Service-managed: flag the restart exit code and shut down; the supervisor relaunches the new binary.</summary>
internal sealed partial class SupervisedExitStrategy(
        RestartState state, IHostApplicationLifetime lifetime, ILogger<SupervisedExitStrategy> logger
    ) : IRestartStrategy {
    public void Restart() {
        LogSupervisedRestart(logger);
        state.SupervisedRestart = true;
        lifetime.StopApplication();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Restart-after-update: exiting non-zero for supervisor relaunch")]
    static partial void LogSupervisedRestart(ILogger logger);
}
```

```csharp
// src/Capacitor.Cli.Daemon/Services/DetachedRespawnStrategy.cs
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Detached: spawn a fresh detached daemon from the (now-updated) on-disk binary with
/// the same argv plus --await-lock (so it waits out our flock), then shut ourselves down.
/// The successor's flock acquire succeeds once our cleanup releases it.
/// </summary>
internal sealed partial class DetachedRespawnStrategy(
        DaemonConfig config, IHostApplicationLifetime lifetime, ILogger<DetachedRespawnStrategy> logger
    ) : IRestartStrategy {

    /// <summary>Pure: original argv + "--await-lock" (idempotent).</summary>
    public static string[] BuildChildArgs(IReadOnlyList<string> originalArgs) =>
        originalArgs.Contains("--await-lock") ? [.. originalArgs] : [.. originalArgs, "--await-lock"];

    public void Restart() {
        var exe = Environment.ProcessPath;
        if (exe is null) { LogNoProcessPath(logger); return; }

        var psi = new ProcessStartInfo {
            FileName               = exe,
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        foreach (var a in BuildChildArgs(config.OriginalArgs)) psi.ArgumentList.Add(a);

        try {
            var child = Process.Start(psi)!;
            child.StandardInput.Close();
            child.StandardOutput.Close();
            child.StandardError.Close();
            LogRespawned(logger, child.Id);
        } catch (Exception ex) {
            // Spawn failed — do NOT shut down (that would leave no daemon at all).
            LogRespawnFailed(logger, ex);
            return;
        }

        lifetime.StopApplication();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Restart-after-update: Environment.ProcessPath is null; cannot self-respawn")]
    static partial void LogNoProcessPath(ILogger logger);
    [LoggerMessage(Level = LogLevel.Information, Message = "Restart-after-update: respawned detached successor (PID {Pid}); shutting down")]
    static partial void LogRespawned(ILogger logger, int pid);
    [LoggerMessage(Level = LogLevel.Error, Message = "Restart-after-update: self-respawn failed; staying up")]
    static partial void LogRespawnFailed(ILogger logger, Exception ex);
}
```

```csharp
// src/Capacitor.Cli.Daemon/Services/ForegroundNoopStrategy.cs
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Interactive foreground: never auto-restart; the queued marker + this log line tell the user to restart.</summary>
internal sealed partial class ForegroundNoopStrategy(ILogger<ForegroundNoopStrategy> logger) : IRestartStrategy {
    public void Restart() => LogForegroundPending(logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Restart-after-update pending: foreground daemon — exit and restart to apply the update")]
    static partial void LogForegroundPending(ILogger logger);
}
```

- [ ] **Step 6: Add `OriginalArgs` to `DaemonConfig.cs`** (so `DetachedRespawnStrategy` compiles; Task 6 only adds the `--await-lock` lock retry and assumes this property already exists)

```csharp
    /// <summary>The argv the daemon was launched with, captured for self-respawn (detached restart).</summary>
    public IReadOnlyList<string> OriginalArgs { get; set; } = [];
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/DetachedRespawnArgsTests/*"`
Expected: PASS (2 tests).

- [ ] **Step 8: Commit**

```bash
git add src/Capacitor.Cli.Core/ExitCodes.cs src/Capacitor.Cli.Daemon/Services/RestartState.cs src/Capacitor.Cli.Daemon/Services/IRestartStrategy.cs src/Capacitor.Cli.Daemon/Services/SupervisedExitStrategy.cs src/Capacitor.Cli.Daemon/Services/DetachedRespawnStrategy.cs src/Capacitor.Cli.Daemon/Services/ForegroundNoopStrategy.cs src/Capacitor.Cli.Daemon/DaemonConfig.cs test/Capacitor.Cli.Tests.Unit/Daemon/DetachedRespawnArgsTests.cs
git commit -m "feat(daemon): restart exit code, state flag, and strategies"
```

---

## Task 6: `--await-lock` retry in DaemonLock

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/DaemonLock.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/DaemonLockAwaitTests.cs`

**Interfaces:**
- Produces: `DaemonLock.TryAcquire(string daemonName, TimeSpan awaitTimeout) : DaemonLock?` (retries until the timeout elapses).
- Consumes: existing `DaemonLock.TryAcquire(string)`. (`DaemonConfig.OriginalArgs` was already added in Task 5.)

- [ ] **Step 1: Write the failing test**

```csharp
// test/Capacitor.Cli.Tests.Unit/Daemon/DaemonLockAwaitTests.cs
using System.Diagnostics;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class DaemonLockAwaitTests {
    [Test]
    public async Task Await_acquires_after_holder_releases() {
        var dir = Directory.CreateTempSubdirectory("kcap-lock-");
        DaemonLockPaths.OverrideDirectoryForTesting(dir.FullName);
        try {
            var first = DaemonLock.TryAcquire("await-test");
            await Assert.That(first).IsNotNull();

            // Release the holder after a short delay, on a background task.
            var releaser = Task.Run(async () => { await Task.Delay(300); first!.Dispose(); });

            var sw     = Stopwatch.StartNew();
            var second = DaemonLock.TryAcquire("await-test", TimeSpan.FromSeconds(5));
            sw.Stop();

            await Assert.That(second).IsNotNull();
            await Assert.That(sw.ElapsedMilliseconds).IsGreaterThanOrEqualTo(200);
            second!.Dispose();
            await releaser;
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            dir.Delete(true);
        }
    }

    [Test]
    public async Task Await_returns_null_if_never_released() {
        var dir = Directory.CreateTempSubdirectory("kcap-lock-");
        DaemonLockPaths.OverrideDirectoryForTesting(dir.FullName);
        try {
            var first = DaemonLock.TryAcquire("held");
            await Assert.That(first).IsNotNull();

            var second = DaemonLock.TryAcquire("held", TimeSpan.FromMilliseconds(500));
            await Assert.That(second).IsNull();

            first!.Dispose();
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            dir.Delete(true);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/DaemonLockAwaitTests/*"`
Expected: compile error — no `TryAcquire(string, TimeSpan)` overload. (`DaemonLock` is `internal`; the test project already has `InternalsVisibleTo` for `Capacitor.Cli.Daemon` — confirm by grepping the daemon csproj; existing daemon tests reference internal types.)

- [ ] **Step 3: Add the retry overload** in `DaemonLock.cs` (after the existing `TryAcquire`)

```csharp
    /// <summary>
    /// Like <see cref="TryAcquire(string)"/> but retries until <paramref name="awaitTimeout"/>
    /// elapses — used by a self-respawned successor (<c>--await-lock</c>) to wait out the
    /// outgoing daemon's flock instead of exiting with code 2 on the first contended attempt.
    /// </summary>
    public static DaemonLock? TryAcquire(string daemonName, TimeSpan awaitTimeout) {
        var deadline = DateTime.UtcNow + awaitTimeout;
        while (true) {
            if (TryAcquire(daemonName) is { } locked) return locked;
            if (DateTime.UtcNow >= deadline) return null;
            Thread.Sleep(100);
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/DaemonLockAwaitTests/*"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/DaemonLock.cs test/Capacitor.Cli.Tests.Unit/Daemon/DaemonLockAwaitTests.cs
git commit -m "feat(daemon): --await-lock retry on lock acquire"
```

---

## Task 7: RestartCoordinator background service + DaemonRunner wiring

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/RestartCoordinator.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (make `ActiveCount` `internal`)
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/RestartCoordinatorTests.cs`

**Interfaces:**
- Consumes: `RestartDecision`, `SupervisionDetector`, `IRestartStrategy`, `RestartState`, `DaemonRestartMarker`, `BinaryStat`, `AgentOrchestrator.ActiveCount`, `EvalContextCache.Count`, `DaemonRunner.ResolveDaemonVersion()`.
- Produces: `RestartCoordinator` (a `BackgroundService` singleton) with internal seams for testing: `internal Func<BinaryStat?> StatBinary`, `internal Func<bool> IsBusy`, `internal IRestartStrategy Strategy`, and `internal void Tick()` (one poll iteration), `internal void RequestRestart(bool force)` (from the control socket).

- [ ] **Step 1: Write the failing test** (drives the loop body `Tick()` and `RequestRestart` with fakes)

```csharp
// test/Capacitor.Cli.Tests.Unit/Daemon/RestartCoordinatorTests.cs
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class RestartCoordinatorTests {
    sealed class SpyStrategy : IRestartStrategy {
        public int Calls;
        public void Restart() => Calls++;
    }

    static RestartCoordinator NewCoordinator(SpyStrategy spy, Func<bool> isBusy, Func<BinaryStat?> stat) {
        var c = RestartCoordinator.ForTest("laptop", "v0.4.11", spy);
        c.IsBusy     = isBusy;
        c.StatBinary = stat;
        c.PrimeBaseline();   // capture initial stat as baseline
        return c;
    }

    [Test]
    public async Task Binary_change_while_idle_triggers_restart() {
        var spy   = new SpyStrategy();
        var size  = 100L;
        var c     = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(size, 1));
        size = 200; // simulate update landing
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Binary_change_while_busy_waits_then_fires_when_idle() {
        var spy  = new SpyStrategy();
        var size = 100L;
        var busy = true;
        var c    = NewCoordinator(spy, isBusy: () => busy, stat: () => new BinaryStat(size, 1));
        size = 200;
        c.Tick();                                   // busy → queued, no fire
        await Assert.That(spy.Calls).IsEqualTo(0);
        busy = false;
        c.Tick();                                   // idle → fire
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Transient_stat_failure_does_not_queue() {
        var spy = new SpyStrategy();
        var c   = NewCoordinator(spy, isBusy: () => false, stat: () => null);
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Explicit_force_request_fires_even_when_busy() {
        var spy = new SpyStrategy();
        var c   = NewCoordinator(spy, isBusy: () => true, stat: () => new BinaryStat(100, 1));
        c.RequestRestart(force: true);
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Restart_only_fires_once() {
        var spy  = new SpyStrategy();
        var size = 100L;
        var c    = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(size, 1));
        size = 200;
        c.Tick();
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/RestartCoordinatorTests/*"`
Expected: compile error — `RestartCoordinator` undefined.

- [ ] **Step 3: Make `AgentOrchestrator.ActiveCount` assembly-visible**

In `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs`, change the existing line:

```csharp
    int ActiveCount => _agents.Count(a => a.Value.Status is "Starting" or "Running");
```

to:

```csharp
    internal int ActiveCount => _agents.Count(a => a.Value.Status is "Starting" or "Running");
```

- [ ] **Step 4: Create `RestartCoordinator.cs`**

```csharp
// src/Capacitor.Cli.Daemon/Services/RestartCoordinator.cs
using Capacitor.Cli.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Polls the running binary; a size/mtime change queues a restart-after-update that
/// fires the chosen <see cref="IRestartStrategy"/> the moment the daemon is idle
/// (no hosted agents, no in-flight eval). Also handles explicit requests from the
/// control socket. Self-detection is macOS/Linux only (the poll is a no-op on Windows,
/// where a running binary can't be replaced); the explicit request path works anywhere.
/// </summary>
internal sealed partial class RestartCoordinator : BackgroundService {
    readonly string             _name;
    readonly string             _version;
    readonly ILogger            _logger;

    // Seams (assigned from DI in the production ctor; overridden directly in tests).
    internal Func<BinaryStat?>  StatBinary = static () => null;
    internal Func<bool>         IsBusy     = static () => false;
    internal IRestartStrategy   Strategy   = null!;

    BinaryStat? _baseline;
    bool        _pending;
    bool        _force;
    bool        _fired;

    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    // Production constructor (DI).
    public RestartCoordinator(
            DaemonConfig config, AgentOrchestrator orchestrator, EvalContextCache evalCache,
            IRestartStrategy strategy, ILogger<RestartCoordinator> logger) {
        _name    = DaemonLockPaths.Sanitize(config.Name);
        _version = DaemonRunner.ResolveDaemonVersion();
        _logger  = logger;
        Strategy = strategy;
        IsBusy   = () => orchestrator.ActiveCount > 0 || evalCache.Count > 0;
        StatBinary = StatProcessBinary;
    }

    RestartCoordinator(string name, string version, IRestartStrategy strategy, ILogger logger) {
        _name = name; _version = version; Strategy = strategy; _logger = logger;
    }

    /// <summary>Test factory — bypasses DI; caller sets the seams.</summary>
    internal static RestartCoordinator ForTest(string name, string version, IRestartStrategy strategy) =>
        new(name, version, strategy, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    internal void PrimeBaseline() => _baseline = StatBinary();

    static BinaryStat? StatProcessBinary() {
        try {
            var p = Environment.ProcessPath;
            if (p is null) return null;
            var fi = new FileInfo(p);
            return fi.Exists ? new BinaryStat(fi.Length, fi.LastWriteTimeUtc.Ticks) : null;
        } catch {
            return null; // transient (file being swapped mid-install)
        }
    }

    /// <summary>Explicit restart request from the control socket. <paramref name="force"/> bypasses the idle gate.</summary>
    internal void RequestRestart(bool force) {
        _pending = true;
        _force  |= force;
        if (!force) {
            DaemonRestartMarker.Write(_name, new DaemonRestartMarker(_version, "requested", DateTimeOffset.UtcNow));
            LogQueued(_logger, "requested");
        }
    }

    /// <summary>One poll iteration (extracted for unit testing).</summary>
    internal void Tick() {
        if (_fired) return;

        if (!_pending && !OperatingSystem.IsWindows()) {
            var current = StatBinary();
            if (RestartDecision.BinaryChanged(_baseline, current)) {
                _pending  = true;
                _baseline = current;
                DaemonRestartMarker.Write(_name, new DaemonRestartMarker(_version, "self-detected", DateTimeOffset.UtcNow));
                LogQueued(_logger, "self-detected");
            }
        }

        if (RestartDecision.ShouldFire(_pending, IsBusy(), _force)) {
            _fired = true;
            Strategy.Restart();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct) {
        PrimeBaseline();
        // The successor of a previous restart: clear any stale marker, since our
        // running version now matches the on-disk binary.
        DaemonRestartMarker.Delete(_name);

        using var timer = new PeriodicTimer(PollInterval);
        try {
            while (await timer.WaitForNextTickAsync(ct)) Tick();
        } catch (OperationCanceledException) { /* shutdown */ }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Restart-after-update queued ({Reason}); will apply when idle")]
    static partial void LogQueued(ILogger logger, string reason);
}
```

- [ ] **Step 5: Wire it in `DaemonRunner.cs`**

(a) Capture argv + parse `--await-lock`. Near the top of `RunAsync`, after `config` is constructed:

```csharp
        config.OriginalArgs = args;
        var awaitLock = args.Contains("--await-lock");
```

(b) Use the await variant when acquiring the lock. Replace:

```csharp
        var daemonLock = DaemonLock.TryAcquire(config.Name);
```

with:

```csharp
        var daemonLock = awaitLock
            ? DaemonLock.TryAcquire(config.Name, TimeSpan.FromSeconds(5))
            : DaemonLock.TryAcquire(config.Name);
```

(c) Register services. After `builder.Services.AddSingleton<AgentOrchestrator>();` (and the eval services), add:

```csharp
        builder.Services.AddSingleton<RestartState>();
        // Register all three strategies as concrete singletons (AOT-safe; same pattern as
        // AgentOrchestrator/LocalControlServer). The IRestartStrategy factory resolves the
        // one chosen by supervision detection — the others are never constructed.
        builder.Services.AddSingleton<SupervisedExitStrategy>();
        builder.Services.AddSingleton<DetachedRespawnStrategy>();
        builder.Services.AddSingleton<ForegroundNoopStrategy>();
        builder.Services.AddSingleton<IRestartStrategy>(sp => {
            var cfg        = sp.GetRequiredService<DaemonConfig>();
            var hasLogFile = cfg.OriginalArgs.Contains("--log-file");
            var mode       = SupervisionDetector.DetectCurrent(DaemonLockPaths.Sanitize(cfg.Name), hasLogFile);
            return mode switch {
                SupervisionMode.Supervised => sp.GetRequiredService<SupervisedExitStrategy>(),
                SupervisionMode.Detached   => sp.GetRequiredService<DetachedRespawnStrategy>(),
                _                          => sp.GetRequiredService<ForegroundNoopStrategy>(),
            };
        });
        builder.Services.AddSingleton<RestartCoordinator>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RestartCoordinator>());
```

(`using Microsoft.Extensions.DependencyInjection;` is already present. Avoid `ActivatorUtilities.CreateInstance` — it uses reflection and can emit IL2026/IL3050 under NativeAOT.)

(d) Honor the restart exit code. The method ends with `return nameInUse ? 3 : 0;`. Capture the state and change it:

After building the host (where other services are resolved), add:

```csharp
        var restartState = host.Services.GetRequiredService<RestartState>();
```

Change the final return to:

```csharp
        if (restartState.SupervisedRestart) return ExitCodes.RestartRequested;
        return nameInUse ? 3 : 0;
```

Add `using Capacitor.Cli.Core;` if not present (it is).

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/RestartCoordinatorTests/*"`
Expected: PASS (5 tests).

- [ ] **Step 7: Build the daemon + AOT check**

Run: `dotnet build src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj`
Expected: build succeeds.
Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output (no AOT warnings).

- [ ] **Step 8: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/RestartCoordinator.cs src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs src/Capacitor.Cli.Daemon/DaemonRunner.cs test/Capacitor.Cli.Tests.Unit/Daemon/RestartCoordinatorTests.cs
git commit -m "feat(daemon): RestartCoordinator + runner wiring (idle-gated restart-after-update)"
```

---

## Task 8: Route Restart frames over the control socket

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs`
- Test: `test/Capacitor.Cli.Tests.Integration/Daemon/RestartControlFrameTests.cs` (integration — real socket)

**Interfaces:**
- Consumes: `RestartCoordinator.RequestRestart(bool force)`, `RestartCoordinator.IsBusy`, `FrameType.Restart`, `LocalFrame.RestartAck`, `LocalFrame.Error`.
- Produces: on a `Restart` frame with `Text == "now"` while busy → `Error`; otherwise → `RestartAck("queued"|"restarting")`.

- [ ] **Step 1: Write the failing integration test**

```csharp
// test/Capacitor.Cli.Tests.Integration/Daemon/RestartControlFrameTests.cs
using System.Net.Sockets;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Tests.Integration.Daemon;

public class RestartControlFrameTests {
    // Helper: assumes a running daemon under name from KCAP_TEST_DAEMON (set by the harness
    // that boots a daemon for integration). If your integration suite uses a fixture, reuse it.
    [Test]
    [Skip("Requires a running daemon fixture — enable once the daemon boot fixture is wired")]
    public async Task When_idle_queue_returns_ack() {
        var socket = LocalSocketPaths.Socket(Environment.GetEnvironmentVariable("KCAP_TEST_DAEMON")!);
        using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await sock.ConnectAsync(new UnixDomainSocketEndPoint(socket));
        await using var stream = new NetworkStream(sock);

        await FrameCodec.WriteAsync(stream, LocalFrame.Restart("when-idle"), default);
        var reply = await FrameCodec.ReadAsync(stream, default);

        await Assert.That(reply!.Type).IsEqualTo(FrameType.RestartAck);
    }
}
```

(If the integration project has no daemon-boot fixture, keep the `[Skip]` and rely on Task 7's unit tests for the gate plus manual verification in Step 4. Do not invent a fixture that doesn't exist.)

- [ ] **Step 2: Add the route** in `LocalControlServer.cs`

Add `RestartCoordinator restart` to the primary constructor parameters:

```csharp
internal sealed partial class LocalControlServer(
        DaemonConfig config, AgentOrchestrator orchestrator, RestartCoordinator restart,
        ILogger<LocalControlServer> logger
    ) : BackgroundService {
```

In `HandleConnectionAsync`'s `switch`, add a case before `default`:

```csharp
                case FrameType.Restart: await HandleRestartAsync(first.Text, stream, ct); break;
```

Add the handler method:

```csharp
    async Task HandleRestartAsync(string mode, Stream stream, CancellationToken ct) {
        var force = mode is "force";
        // "now" requires idle; "when-idle"/"force" always accept.
        if (mode is "now" && restart.IsBusy()) {
            await FrameCodec.WriteAsync(stream, LocalFrame.Error("daemon busy — agents running or eval in progress; use --when-idle or --force"), ct);
            return;
        }
        restart.RequestRestart(force);
        var status = (force || (mode is "now")) ? "restarting" : "queued";
        await FrameCodec.WriteAsync(stream, LocalFrame.RestartAck(status), ct);
    }
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj`
Expected: build succeeds.

- [ ] **Step 4: Manual verification** (no automated daemon fixture)

```bash
# Terminal A: start a daemon
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- daemon start --name resttest
# Terminal B (after Task 9 lands the client): kcap daemon restart --name resttest --when-idle
# Expect: "Daemon 'resttest': restart queued" and the daemon log shows "Restart-after-update queued (requested)".
```

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs test/Capacitor.Cli.Tests.Integration/Daemon/RestartControlFrameTests.cs
git commit -m "feat(daemon): route Restart control frames to RestartCoordinator"
```

---

## Task 9: `kcap daemon restart` command + status marker + ServiceInstall marker

**Files:**
- Modify: `src/Capacitor.Cli/Commands/DaemonCommands.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/DaemonRestartCommandTests.cs`

**Interfaces:**
- Produces: `kcap daemon restart [--name N] [--when-idle] [--force]`; a pure `DaemonCommands.ParseRestartMode(string[] args) : string` returning `"now"|"when-idle"|"force"`; `Status` prints `DaemonRestartMarker.Describe()` when a marker exists; `ServiceInstall` injects `KCAP_DAEMON_SUPERVISED=<id>`.
- Consumes: `LocalSocketPaths.Socket`, `FrameCodec`, `LocalFrame.Restart`, `DaemonRestartMarker.TryRead`.

- [ ] **Step 1: Write the failing test**

```csharp
// test/Capacitor.Cli.Tests.Unit/Daemon/DaemonRestartCommandTests.cs
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class DaemonRestartCommandTests {
    [Test]
    public async Task Bare_is_now() =>
        await Assert.That(DaemonCommands.ParseRestartMode([])).IsEqualTo("now");

    [Test]
    public async Task When_idle_flag() =>
        await Assert.That(DaemonCommands.ParseRestartMode(["--when-idle"])).IsEqualTo("when-idle");

    [Test]
    public async Task Force_flag() =>
        await Assert.That(DaemonCommands.ParseRestartMode(["--force"])).IsEqualTo("force");

    [Test]
    public async Task Force_wins_over_when_idle() =>
        await Assert.That(DaemonCommands.ParseRestartMode(["--when-idle", "--force"])).IsEqualTo("force");
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/DaemonRestartCommandTests/*"`
Expected: compile error — `ParseRestartMode` undefined. (`DaemonCommands` is `public static`; `ParseRestartMode` must be `public` or `internal` with InternalsVisibleTo. Use `internal` and confirm the unit-test project sees `Capacitor.Cli` internals — existing tests like `ArgParsingTests` reference internal CLI helpers, so it does.)

- [ ] **Step 3: Implement the command, parser, status line, and marker injection** in `DaemonCommands.cs`

(a) Add `"restart"` to the subcommand switch in `HandleAsync`:

```csharp
            "restart" => await RestartAsync(remaining),
```

(b) Add the parser and command:

```csharp
    internal static string ParseRestartMode(string[] args) {
        if (args.Contains("--force"))     return "force";
        if (args.Contains("--when-idle")) return "when-idle";
        return "now";
    }

    static async Task<int> RestartAsync(string[] args) {
        string? name;
        try { name = ExtractFlagValue(args, "--name"); }
        catch (ArgumentException ex) { await Console.Error.WriteLineAsync(ex.Message); return 1; }

        var mode = ParseRestartMode(args);

        var targets = name is not null
            ? [name]
            : EnumerateRunningNames();

        if (targets.Count == 0) { await Console.Out.WriteLineAsync("No daemons are running."); return 0; }

        var failed = 0;
        foreach (var n in targets) if (await RestartOne(n, mode) != 0) failed++;
        return failed == 0 ? 0 : 1;
    }

    static async Task<int> RestartOne(string name, string mode) {
        var socketPath = LocalSocketPaths.Socket(name);
        using var sock = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Unspecified);
        try {
            await sock.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath));
        } catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException) {
            await Console.Error.WriteLineAsync($"Daemon '{name}': not reachable ({ex.Message}).");
            return 1;
        }

        await using var stream = new System.Net.Sockets.NetworkStream(sock, ownsSocket: false);
        await Core.LocalIpc.FrameCodec.WriteAsync(stream, Core.LocalIpc.LocalFrame.Restart(mode), default);
        var reply = await Core.LocalIpc.FrameCodec.ReadAsync(stream, default);

        switch (reply?.Type) {
            case Core.LocalIpc.FrameType.RestartAck:
                await Console.Out.WriteLineAsync($"Daemon '{name}': restart {reply.Text}.");
                return 0;
            case Core.LocalIpc.FrameType.Error:
                await Console.Error.WriteLineAsync($"Daemon '{name}': {reply.Text}");
                return 1;
            default:
                await Console.Error.WriteLineAsync($"Daemon '{name}': unexpected reply.");
                return 1;
        }
    }
```

(c) In `Status`, after the `running (PID …)` line for a live daemon, print a pending marker if present. Locate the `IsOurDaemon(...)` running branch and add after it:

```csharp
                if (DaemonRestartMarker.TryRead(name) is { } marker)
                    await Console.Out.WriteLineAsync($"  {marker.Describe()}");
```

(d) In `ServiceInstall`, inject the name-specific marker into the env. Replace:

```csharp
        var env         = ServiceEnvironment.Capture(profileName);
```

with:

```csharp
        var env = new Dictionary<string, string>(ServiceEnvironment.Capture(profileName)) {
            ["KCAP_DAEMON_SUPERVISED"] = id,   // name-specific; daemon honors only when == its sanitized --name
        };
```

(e) Add `restart` to `PrintUsage()`:

```csharp
        Console.Error.WriteLine("  restart [--name <n>] [--when-idle] [--force]  Restart daemon (now if idle; queue with --when-idle; --force overrides)");
```

(f) Make `doctor --clean` also delete a **stale** entry's restart marker (never a live daemon's — a HELD daemon may have a legitimately-pending marker). In `DoctorAsync`, the two stale branches are `case null:` (orphan, no lock) and the `default:` (had a lock but no holder). In **each** of those branches, inside the existing `if (clean) { … }` block, add:

```csharp
                        try { File.Delete(DaemonLockPaths.RestartPendingPath(name)); } catch { /* best-effort */ }
```

Do **not** add this to the `case null when hasLock:` (HELD) branch.

(Add `using Capacitor.Cli.Core.LocalIpc;` to `DaemonCommands.cs` for `FrameCodec`/`LocalFrame`/`FrameType`/`LocalSocketPaths`. `using Capacitor.Cli.Core;` is already present for `DaemonRestartMarker`/`DaemonLockPaths`.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/DaemonRestartCommandTests/*"`
Expected: PASS (4 tests).

- [ ] **Step 5: Build + AOT check**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli/Commands/DaemonCommands.cs test/Capacitor.Cli.Tests.Unit/Daemon/DaemonRestartCommandTests.cs
git commit -m "feat(cli): daemon restart command, status marker line, supervised env marker"
```

---

## Task 10: Scrub supervision env from spawned PTY children

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Pty/Unix/UnixPtyProcess.cs`
- Modify: `src/Capacitor.Cli.Daemon/Pty/Windows/ConPtyProcess.cs`
- Test: `test/Capacitor.Cli.Tests.Integration/Daemon/PtyEnvScrubTests.cs` (Unix only)

**Interfaces:**
- Consumes: nothing new. Adds `unsetenv` calls for `KCAP_DAEMON_SUPERVISED`, `XPC_SERVICE_NAME`, `INVOCATION_ID`, `SYSTEMD_EXEC_PID` in the child.

- [ ] **Step 1: Write the failing integration test (Unix)**

```csharp
// test/Capacitor.Cli.Tests.Integration/Daemon/PtyEnvScrubTests.cs
using System.Text;
using Capacitor.Cli.Daemon.Pty.Unix;

namespace Capacitor.Cli.Tests.Integration.Daemon;

public class PtyEnvScrubTests {
    [Test]
    [Skip("Run on macOS/Linux only")] // remove Skip when running on a Unix CI leg
    public async Task Spawned_child_does_not_see_supervision_marker() {
        Environment.SetEnvironmentVariable("KCAP_DAEMON_SUPERVISED", "laptop");
        try {
            using var pty = UnixPtyProcess.Spawn("/bin/sh", ["-c", "printf MARK=[$KCAP_DAEMON_SUPERVISED]"], "/tmp");
            var sb = new StringBuilder();
            await foreach (var chunk in pty.ReadOutputAsync()) {
                sb.Append(Encoding.UTF8.GetString(chunk));
                if (sb.ToString().Contains("MARK=")) break;
            }
            await Assert.That(sb.ToString()).Contains("MARK=[]");
        } finally {
            Environment.SetEnvironmentVariable("KCAP_DAEMON_SUPERVISED", null);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails** (on Unix, after removing `[Skip]`)

Run: `dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj --treenode-filter "/*/*/PtyEnvScrubTests/*"`
Expected: FAIL — output is `MARK=[laptop]` (child inherits the marker).

- [ ] **Step 3: Add the unset calls** in `UnixPtyProcess.cs` child branch (after the existing `unsetenv("KCAP_DAEMON_URL");`)

```csharp
                // Never leak daemon supervision state into hosted agents — otherwise a
                // `kcap daemon start` run from inside an agent could inherit a supervised
                // classification and later take the exit-for-relaunch path with no supervisor.
                UnixPtyInterop.unsetenv("KCAP_DAEMON_SUPERVISED");
                UnixPtyInterop.unsetenv("XPC_SERVICE_NAME");
                UnixPtyInterop.unsetenv("INVOCATION_ID");
                UnixPtyInterop.unsetenv("SYSTEMD_EXEC_PID");
```

- [ ] **Step 4: Mirror in `ConPtyProcess.cs`** (Windows). In `BuildEnvironmentBlock`, the child env is a mutable `Dictionary<string,string> env` that already does `env.Remove("CLAUDECODE")` / `CLAUDE_CODE_ENTRYPOINT` / `ANTHROPIC_API_KEY` (around lines 80-82). Add the supervision-var removals immediately after those three lines:

```csharp
        env.Remove("KCAP_DAEMON_SUPERVISED");
        env.Remove("XPC_SERVICE_NAME");
        env.Remove("INVOCATION_ID");
        env.Remove("SYSTEMD_EXEC_PID");
```

(Parity with `UnixPtyProcess`. Auto-restart is out of scope on Windows, so this is defense-in-depth, but keeping the two PTY paths in lockstep prevents drift.)

- [ ] **Step 5: Run test to verify it passes** (Unix)

Run: `dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj --treenode-filter "/*/*/PtyEnvScrubTests/*"`
Expected: PASS — output is `MARK=[]`.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Pty/Unix/UnixPtyProcess.cs src/Capacitor.Cli.Daemon/Pty/Windows/ConPtyProcess.cs test/Capacitor.Cli.Tests.Integration/Daemon/PtyEnvScrubTests.cs
git commit -m "fix(daemon): scrub supervision env from spawned PTY children"
```

---

## Task 11: Windows preflight in the npm launcher

**Files:**
- Modify: `npm/kcap/bin/kcap.js`

**Interfaces:**
- Consumes: the native binary's daemon-running probe. Reuse `kcap daemon status` — exit 0 always, but parse for a running line. Simpler and robust: add a tiny machine-readable check. For this task, parse `daemon status` text for "running (PID" since adding a new flag is more surface; if the team prefers a dedicated probe, add `daemon status --quiet` returning exit 3 when any daemon runs. **Chosen here:** parse `status` output (no new native surface).

- [ ] **Step 1: Add the preflight** in `runUpdate()` in `kcap.js`, immediately before the `spawnSync("npm", ["install", ...])` call

```js
  // Windows preflight: a running kcap-daemon.exe locks the binary, so `npm install`
  // would FAIL to overwrite it. Detect a running daemon and abort with instructions
  // BEFORE attempting the (doomed) install. macOS/Linux can replace the file in place,
  // so this guard is Windows-only.
  if (process.platform === "win32") {
    try {
      const status = execFileSync(binaryPath, ["daemon", "status"], { encoding: "utf8" });
      if (/running \(PID/i.test(status)) {
        console.error("A kcap daemon is running and locks the binary, so the update can't");
        console.error("replace it. Stop it first, then re-run `kcap update`:");
        console.error("  kcap daemon service stop   (if installed as a service)");
        console.error("  kcap daemon stop           (otherwise)");
        process.exit(1);
      }
    } catch {
      // status probe failed (no daemon / old binary) — fall through to the normal install.
    }
  }
```

- [ ] **Step 2: Verify by inspection + manual smoke (Windows only)**

```
# On Windows with a daemon running:
kcap daemon start -d --name wintest
kcap update
# Expect: the "A kcap daemon is running..." abort message and NO npm install attempt.
# Then: kcap daemon stop --name wintest && kcap update  → proceeds normally.
```

(On macOS/Linux this block is skipped entirely; no behavior change.)

- [ ] **Step 3: Commit**

```bash
git add npm/kcap/bin/kcap.js
git commit -m "feat(npm): Windows preflight aborts update while a daemon locks the binary"
```

---

## Task 12: Documentation (README + help text) + macOS/Linux update notice

**Files:**
- Modify: `README.md`
- Modify: the daemon help resource under `src/Capacitor.Cli.Core/Resources/` (e.g. `help-daemon.txt` — confirm the exact filename with `ls src/Capacitor.Cli.Core/Resources/help-*.txt`)
- Modify: `npm/kcap/bin/kcap.js` (post-install notice for macOS/Linux) and/or `refresh.js`

**Interfaces:** none (docs + user-facing strings).

- [ ] **Step 1: Add the macOS/Linux post-install notice** in `kcap.js`, after the successful `npm install` + `require("./refresh").runRefreshes(...)` block, before `console.log("kcap updated.")`

```js
  // macOS/Linux: the running daemon self-detects the new binary and restarts when idle.
  // Just inform the user (best-effort; never fail the update for this).
  if (process.platform !== "win32") {
    try {
      const status = execFileSync(binaryPath, ["daemon", "status"], { encoding: "utf8" });
      if (/running \(PID/i.test(status)) {
        console.log("A kcap daemon is running; it will restart automatically when idle to");
        console.log("pick up the new version. Check with `kcap daemon status`, or apply now");
        console.log("with `kcap daemon restart --force`.");
      }
    } catch { /* best-effort notice only */ }
  }
```

- [ ] **Step 2: Update `src/Capacitor.Cli.Core/Resources/help-daemon.txt`**

In the `Subcommands:` block, add a `restart` line after the `status` line (match the existing two-space indent + column alignment):

```
  restart [--name N]      Restart the daemon (now if idle; --when-idle queues;
                          --force overrides while busy)
```

Add a new options block after the `Options for stop:` block:

```
Options for restart:
  --when-idle             Queue the restart; it applies once the daemon is idle
                          (no running hosted agents or eval). Returns immediately.
  --force                 Restart now even if busy (running agents are torn down).

Updating: after `kcap update`, a running daemon on macOS/Linux detects the new
binary and restarts itself when idle. `kcap daemon status` shows a pending restart.
On Windows, stop the daemon before `kcap update` (a running daemon locks its binary).
```

- [ ] **Step 3: Update `README.md`** — in the daemon section under `## CLI commands`, document:
  - `kcap daemon restart [--name N] [--when-idle] [--force]` — restart now if idle; `--when-idle` queues; `--force` overrides.
  - A short "Updating" note: on macOS/Linux a running daemon restarts itself when idle after `kcap update`; on Windows, stop the daemon before updating (the running binary is locked).
  - In `## Getting started`, if it mentions `kcap update` / the daemon, add one sentence pointing at the auto-restart behavior.

Use prose consistent with the existing README voice. Concrete text to insert under the daemon command list:

```markdown
- `kcap daemon restart [--name <n>] [--when-idle] [--force]` — restart the daemon.
  Restarts immediately if idle; refuses while agents/evals are running unless you
  pass `--force`. `--when-idle` queues the restart and returns right away.

**Updating:** after `kcap update`, a running daemon on macOS/Linux detects the new
binary and restarts itself once it's idle (no running hosted agents or eval). Check
`kcap daemon status` for a pending restart, or run `kcap daemon restart --force` to
apply it now. On Windows, stop the daemon (`kcap daemon stop` / `kcap daemon service
stop`) before `kcap update` — a running daemon locks its binary and blocks the update.
```

- [ ] **Step 4: Build + AOT check (help text is embedded)**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output.

- [ ] **Step 5: Commit**

```bash
git add README.md src/Capacitor.Cli.Core/Resources/ npm/kcap/bin/kcap.js
git commit -m "docs(daemon): restart command + auto-restart-on-update behavior; update notice"
```

---

## Final verification

- [ ] **Run the full unit suite**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
Expected: all pass.

- [ ] **Run the integration suite** (Unix legs that aren't `[Skip]`)

Run: `dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj`
Expected: all pass (skipped daemon-fixture tests remain skipped).

- [ ] **AOT publish, no warnings**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output.

- [ ] **End-to-end smoke (macOS/Linux)**

```bash
# Build & publish, then with a detached daemon running, touch the on-disk binary
# to simulate an update and confirm it self-respawns when idle:
kcap daemon start -d --name e2e
#   ... note PID via: kcap daemon status --name e2e
touch "$(dirname "$(command -v kcap)")/../lib/node_modules/@kurrent/kcap-*/bin/kcap-daemon"  # or the real binary path
#   wait up to ~15s, then:
kcap daemon status --name e2e   # PID should have changed (respawned); no "restart pending" left
```

(If running from a dev build rather than an npm install, point `touch` at the actual `kcap-daemon` path reported by `kcap daemon doctor`.)
