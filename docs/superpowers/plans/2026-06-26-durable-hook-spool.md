# Durable Hook Spool Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A brief server outage (e.g. a deploy window) during a Claude lifecycle hook never strands a session as "Active" — failed `session-start`/`session-end` POSTs are spooled to disk and replayed on the next hook.

**Architecture:** Generalize the existing Cursor spool into a vendor-neutral `HookSpool` (rotate-on-drain, cross-session, tri-state outcome). Anchor a hook budget at process entry so the bounded lifecycle POST and a minimal-body-first ordering guarantee the spool path is reached before Claude's hook-timeout kill. Wire both Cursor and Claude onto it.

**Tech Stack:** .NET 10, NativeAOT, TUnit (Microsoft Testing Platform), WireMock.Net / `HttpMessageHandler` stubs. `System.Text.Json` `JsonNode`/`JsonObject` only (no reflection serialization, no `JsonArray` collection expressions).

## Global Constraints

- **AOT-clean.** No `IL3050`/`IL2026` warnings. Verify after changes: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` (expect no output). Use `JsonNode`/`JsonObject`; never `JsonArray` collection expressions (`[a,b]` → use `new JsonArray(a,b)`).
- **Fail-open hook contract.** Any IO/JSON/network/auth error in spool, drain, migration, or enrichment is swallowed; a hook never crashes the agent and never returns nonzero for a recoverable situation.
- **Idempotent replay needs no server change.** The server already dedupes `session-start`/`session-end` (resume re-fires start; `kcap import` and the watcher parent-exit path re-POST end).
- **Spool entry format:** one JSON object per line, `{"route": "<segment>", "body": "<raw payload string>"}`. Per-session file `<spoolDir>/<dashless-sid>.jsonl`. Session-id guard: `^[0-9a-fA-F]{32}$`.
- **Spool dir:** `PathHelpers.ConfigPath("spool")` (`~/.config/kcap/spool/`).
- **Hook-timeout ceilings (from `kcap/hooks/hooks.json`, keep in sync):** `session-end` 15 s; `session-start`/`stop`/`notification`/`subagent-start`/`subagent-stop` 5 s. Safety margin **1.5 s**. "Remaining" always means time left to `process-start + ceiling − 1.5 s`.
- **Run tests:** `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj` (filter with `--treenode-filter`, NOT `--filter`).
- **TUnit assertions are awaited:** `await Assert.That(x).IsEqualTo(y);`.
- **Commit cadence:** one commit per task, on branch `durable-hook-spool` (already created).
- **README:** no new CLI command/flag → `README.md` unaffected; re-verify at the end.

## File Structure

- `src/Capacitor.Cli/Commands/HookSpool.cs` — **new**; vendor-neutral spool (replaces `CursorHookSpool.cs`). `Append`, `DrainAllAsync(currentSessionId, poster, budget, ct)`, `DrainOutcome`, rotate-on-drain, cap, reap.
- `src/Capacitor.Cli/Commands/CursorHookSpool.cs` — **deleted** (Task 2).
- `src/Capacitor.Cli/Commands/HookBudget.cs` — **new**; per-event ceiling + `Remaining(processStart, command)`.
- `src/Capacitor.Cli/Commands/CursorHookCommand.cs` — use `HookSpool` + `route` + cross-session drain + transforming/merging migration.
- `src/Capacitor.Cli/Program.cs` — capture process-start before bootstrap; bound hook-path `ResolveServerUrl`; thread process-start into the hook.
- `src/Capacitor.Cli.Core/Config/AppConfig.cs` — `ResolveServerUrl(args, gitTimeoutMs)` + git helper timeout param.
- `src/Capacitor.Cli/RepositoryDetection.cs` — `EnrichWithRepositoryInfo`/`DetectRepositoryAsync` gain a remaining-budget cap.
- `src/Capacitor.Cli/Commands/ClaudeHookCommand.cs` — `HandleCore` test seam + `WithHardCap`; minimal-body-first; bounded `PostOnceAsync`; spool start/end; current-session-first drain; `generate_whats_done` on replay.
- Tests:
  - `test/Capacitor.Cli.Tests.Unit/HookSpoolTests.cs` — **new**
  - `test/Capacitor.Cli.Tests.Unit/Cursor/CursorHookCommandTests.cs` — update (route, migration); `CursorHookSpoolTests.cs` → retarget to `HookSpool` or fold into `HookSpoolTests`
  - `test/Capacitor.Cli.Tests.Unit/ClaudeHookCommandTests.cs` — **new**
  - `test/Capacitor.Cli.Tests.Unit/HookBudgetTests.cs` — **new**

---

## Task 1: `HookSpool` component

**Files:**
- Create: `src/Capacitor.Cli/Commands/HookSpool.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/HookSpoolTests.cs`

**Interfaces:**
- Produces:
  - `enum DrainOutcome { Delivered, Drop, TransientStop }`
  - `sealed class HookSpool(string spoolDir, int capBytes = HookSpool.DefaultCapBytes)`
  - `void Append(string sessionId, string route, string rawPayloadJson)`
  - `Task DrainAllAsync(string? currentSessionId, Func<string,string,Task<DrainOutcome>> poster, TimeSpan budget, CancellationToken ct)`
  - `void ReapOlderThan(TimeSpan age)`

- [ ] **Step 1: Write the failing test (FIFO across sessions, current-session-first)**

Create `test/Capacitor.Cli.Tests.Unit/HookSpoolTests.cs`:

```csharp
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class HookSpoolTests {
    static string TmpDir() =>
        Path.Combine(Path.GetTempPath(), $"kcap-spool-{Guid.NewGuid():N}");

    const string SidA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string SidB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Test]
    public async Task drains_current_session_first_then_others_in_fifo() {
        var dir = TmpDir();
        try {
            var spool = new HookSpool(dir);
            spool.Append(SidB, "session-start", """{"n":"b1"}""");
            spool.Append(SidA, "session-start", """{"n":"a1"}""");
            spool.Append(SidA, "session-end",   """{"n":"a2"}""");

            var seen = new List<string>();
            await spool.DrainAllAsync(SidA, (route, body) => {
                seen.Add($"{route}:{body}");
                return Task.FromResult(DrainOutcome.Delivered);
            }, TimeSpan.FromSeconds(5), CancellationToken.None);

            // Current session A first (FIFO a1, a2), then B.
            await Assert.That(seen).IsEquivalentTo([
                """session-start:{"n":"a1"}""",
                """session-end:{"n":"a2"}""",
                """session-start:{"n":"b1"}""",
            ]);
            await Assert.That(Directory.EnumerateFiles(dir)).IsEmpty();
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/HookSpoolTests/*"`
Expected: FAIL — `HookSpool`/`DrainOutcome` do not exist (compile error).

- [ ] **Step 3: Create the `HookSpool` implementation**

Create `src/Capacitor.Cli/Commands/HookSpool.cs`:

```csharp
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Commands;

/// <summary>Result of a single spooled-entry replay attempt.</summary>
public enum DrainOutcome {
    Delivered,    // POST succeeded — advance past the entry
    Drop,         // permanent failure (4xx except 408/429) — discard, do not retry
    TransientStop // server down/timeout/budget — stop draining, keep the remainder
}

/// <summary>
/// Vendor-neutral on-disk spool for lifecycle hook POSTs whose delivery failed.
/// Per-session JSONL (<c>{spoolDir}/&lt;dashless-sid&gt;.jsonl</c>), one
/// <c>{"route","body"}</c> object per line in arrival order. Drains are
/// rotate-on-drain: the live file is atomically renamed to a private
/// <c>.draining</c> temp before reading, so concurrent appends never collide
/// with an in-flight drain.
/// </summary>
public sealed partial class HookSpool(string spoolDir, int capBytes = HookSpool.DefaultCapBytes) {
    public const int DefaultCapBytes = 1_048_576; // 1 MB per session file

    static readonly Regex SafeSessionId = SafeSessionIdRegex();
    static          int   seqCounter;

    string? LivePathFor(string sessionId) =>
        SafeSessionId.IsMatch(sessionId) ? Path.Combine(spoolDir, $"{sessionId}.jsonl") : null;

    public void Append(string sessionId, string route, string rawPayloadJson) {
        var path = LivePathFor(sessionId);
        if (path is null) return;
        try {
            Directory.CreateDirectory(spoolDir);
            var line = new JsonObject { ["route"] = route, ["body"] = rawPayloadJson }.ToJsonString();
            EnsureUnderCap(path, line.Length + 1);
            File.AppendAllText(path, $"{line}\n");
        } catch { /* best effort */ }
    }

    void EnsureUnderCap(string path, int incomingBytes) {
        try {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length + incomingBytes <= capBytes) return;
            var lines = File.ReadAllLines(path).ToList();
            while (lines.Count > 0 && lines.Sum(l => l.Length + 1) + incomingBytes > capBytes)
                lines.RemoveAt(0);
            File.WriteAllLines(path, lines);
        } catch { }
    }

    public async Task DrainAllAsync(
            string?                                   currentSessionId,
            Func<string, string, Task<DrainOutcome>>  poster,
            TimeSpan                                  budget,
            CancellationToken                         ct) {
        if (!Directory.Exists(spoolDir)) return;
        var sw = Stopwatch.StartNew();
        bool Expired() => sw.Elapsed >= budget;

        foreach (var sid in OrderedSessionIds(currentSessionId)) {
            if (Expired() || ct.IsCancellationRequested) return;
            if (await DrainSessionAsync(sid, poster, Expired, ct)) return; // transient → stop the pass
        }
    }

    // Current session first (if it has anything), then every other session once.
    IEnumerable<string> OrderedSessionIds(string? currentSessionId) {
        var ids = new List<string>();
        if (currentSessionId is not null && SafeSessionId.IsMatch(currentSessionId) && HasAny(currentSessionId))
            ids.Add(currentSessionId);
        foreach (var f in Directory.EnumerateFiles(spoolDir)) {
            var sid = SessionIdOf(f);
            if (sid is not null && !ids.Contains(sid)) ids.Add(sid);
        }
        return ids;
    }

    bool HasAny(string sid) =>
        File.Exists(Path.Combine(spoolDir, $"{sid}.jsonl"))
     || Directory.EnumerateFiles(spoolDir, $"{sid}.*.draining").Any();

    static string? SessionIdOf(string filePath) {
        var name = Path.GetFileName(filePath);
        var dot  = name.IndexOf('.');
        if (dot <= 0) return null;
        var sid = name[..dot];
        return SafeSessionId.IsMatch(sid) ? sid : null;
    }

    // Recovered temps (oldest first) then the rotated live file. Returns true => stop the whole pass.
    async Task<bool> DrainSessionAsync(
            string sid, Func<string, string, Task<DrainOutcome>> poster, Func<bool> expired, CancellationToken ct) {
        foreach (var temp in Directory.EnumerateFiles(spoolDir, $"{sid}.*.draining").OrderBy(File.GetCreationTimeUtc)) {
            if (expired() || ct.IsCancellationRequested) return false;
            if (await DrainFileAsync(temp, poster, expired, ct)) return true;
        }

        var live = Path.Combine(spoolDir, $"{sid}.jsonl");
        if (!File.Exists(live) || expired() || ct.IsCancellationRequested) return false;

        var rotated = Path.Combine(spoolDir, $"{sid}.{Environment.ProcessId}-{Interlocked.Increment(ref seqCounter)}.draining");
        try { File.Move(live, rotated); }
        catch { return false; } // lost the atomic-rename race (or vanished) — the winner handles it
        return await DrainFileAsync(rotated, poster, expired, ct);
    }

    // Drain a private temp. Delivered/Drop advance; TransientStop or budget stops and keeps the remainder.
    static async Task<bool> DrainFileAsync(
            string path, Func<string, string, Task<DrainOutcome>> poster, Func<bool> expired, CancellationToken ct) {
        string[] lines;
        try { lines = await File.ReadAllLinesAsync(path, ct); }
        catch { return false; }

        var i = 0;
        for (; i < lines.Length; i++) {
            if (expired() || ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(lines[i])) continue;

            string? route, body;
            try {
                var node = JsonNode.Parse(lines[i]);
                route = node?["route"]?.GetValue<string>();
                body  = node?["body"]?.GetValue<string>();
            } catch { route = body = null; }
            if (route is null || body is null) continue; // skip old-format / malformed

            DrainOutcome outcome;
            try { outcome = await poster(route, body); }
            catch { outcome = DrainOutcome.TransientStop; }

            if (outcome == DrainOutcome.TransientStop) break;
        }

        if (i >= lines.Length) {
            try { File.Delete(path); } catch { }
            return false;
        }
        try { await File.WriteAllLinesAsync(path, lines.Skip(i), ct); } catch { }
        return true;
    }

    public void ReapOlderThan(TimeSpan age) {
        try {
            if (!Directory.Exists(spoolDir)) return;
            var cutoff = DateTime.UtcNow - age;
            foreach (var file in Directory.EnumerateFiles(spoolDir)) {
                try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); } catch { }
            }
        } catch { }
    }

    [GeneratedRegex("^[0-9a-fA-F]{32}$", RegexOptions.Compiled)]
    private static partial Regex SafeSessionIdRegex();
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/HookSpoolTests/*"`
Expected: PASS.

- [ ] **Step 5: Add the remaining `HookSpool` tests**

Append to `HookSpoolTests.cs`:

```csharp
    [Test]
    public async Task transient_stop_keeps_remainder_drop_advances() {
        var dir = TmpDir();
        try {
            var spool = new HookSpool(dir);
            spool.Append(SidA, "session-start", """{"n":1}"""); // Delivered
            spool.Append(SidA, "session-start", """{"n":2}"""); // Drop (permanent)
            spool.Append(SidA, "session-end",   """{"n":3}"""); // TransientStop

            await spool.DrainAllAsync(SidA, (route, body) =>
                Task.FromResult(body.Contains("2") ? DrainOutcome.Drop
                              : body.Contains("3") ? DrainOutcome.TransientStop
                              : DrainOutcome.Delivered),
                TimeSpan.FromSeconds(5), CancellationToken.None);

            // n1 delivered, n2 dropped, n3 left for next time. After a partial drain the
            // remainder lives in a .draining temp; read whatever files remain in the dir.
            var all = string.Concat(Directory.EnumerateFiles(dir).Select(File.ReadAllText));
            await Assert.That(all).Contains("\"n\":3");
            await Assert.That(all).DoesNotContain("\"n\":1");
            await Assert.That(all).DoesNotContain("\"n\":2");
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task concurrent_append_during_drain_is_not_lost() {
        var dir = TmpDir();
        try {
            var spool = new HookSpool(dir);
            spool.Append(SidA, "session-start", """{"n":"old"}""");

            // Poster appends a NEW entry while the OLD one is being drained (live file
            // already rotated to a temp), simulating a racing hook on the same session.
            var appended = false;
            await spool.DrainAllAsync(SidA, (route, body) => {
                if (!appended) { spool.Append(SidA, "session-end", """{"n":"new"}"""); appended = true; }
                return Task.FromResult(DrainOutcome.Delivered);
            }, TimeSpan.FromSeconds(5), CancellationToken.None);

            var all = string.Concat(Directory.EnumerateFiles(dir).Select(File.ReadAllText));
            await Assert.That(all).Contains("new"); // survived in a fresh live file
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task old_format_lines_without_route_are_skipped() {
        var dir = TmpDir();
        Directory.CreateDirectory(dir);
        try {
            await File.WriteAllTextAsync(Path.Combine(dir, $"{SidA}.jsonl"),
                "{\"hook_event_name\":\"sessionEnd\",\"body\":\"x\"}\n");
            var count = 0;
            var spool = new HookSpool(dir);
            await spool.DrainAllAsync(SidA, (_, _) => { count++; return Task.FromResult(DrainOutcome.Delivered); },
                TimeSpan.FromSeconds(5), CancellationToken.None);
            await Assert.That(count).IsEqualTo(0); // skipped, not posted
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task recovered_draining_temp_drains_before_live_file() {
        var dir = TmpDir();
        Directory.CreateDirectory(dir);
        try {
            // Simulate a crash mid-drain: an older .draining temp + a newer live file.
            await File.WriteAllTextAsync(Path.Combine(dir, $"{SidA}.123-1.draining"),
                "{\"route\":\"session-start\",\"body\":\"old\"}\n");
            await Task.Delay(10);
            var spool = new HookSpool(dir);
            spool.Append(SidA, "session-end", """{"n":"newlive"}""");

            var seen = new List<string>();
            await spool.DrainAllAsync(SidA, (route, body) => { seen.Add(body); return Task.FromResult(DrainOutcome.Delivered); },
                TimeSpan.FromSeconds(5), CancellationToken.None);

            await Assert.That(seen[0]).IsEqualTo("old"); // temp first
            await Assert.That(seen).Contains("""{"n":"newlive"}""");
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task reap_deletes_stale_files() {
        var dir = TmpDir();
        Directory.CreateDirectory(dir);
        try {
            var f = Path.Combine(dir, $"{SidA}.jsonl");
            await File.WriteAllTextAsync(f, "{\"route\":\"x\",\"body\":\"y\"}\n");
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-40));
            new HookSpool(dir).ReapOlderThan(TimeSpan.FromDays(30));
            await Assert.That(File.Exists(f)).IsFalse();
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }
```

- [ ] **Step 6: Run all `HookSpool` tests**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/HookSpoolTests/*"`
Expected: PASS (all). If `transient_stop_keeps_remainder_drop_advances` is flaky on the `ContinueWith` line, simplify it to just the `all` assertions (the remainder lives in a `.draining` temp after a partial drain).

- [ ] **Step 7: Verify AOT-clean and commit**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output.

```bash
git add src/Capacitor.Cli/Commands/HookSpool.cs test/Capacitor.Cli.Tests.Unit/HookSpoolTests.cs
git commit -m "feat: add vendor-neutral HookSpool (rotate-on-drain, cross-session, tri-state)"
```

---

## Task 2: Migrate Cursor onto `HookSpool`

**Files:**
- Modify: `src/Capacitor.Cli/Commands/CursorHookCommand.cs`
- Delete: `src/Capacitor.Cli/Commands/CursorHookSpool.cs`
- Modify: `test/Capacitor.Cli.Tests.Unit/Cursor/CursorHookCommandTests.cs`
- Delete/retarget: `test/Capacitor.Cli.Tests.Unit/Cursor/CursorHookSpoolTests.cs`

**Interfaces:**
- Consumes: `HookSpool`, `DrainOutcome` (Task 1).
- Produces: `CursorHookCommand.HandleCore(HttpClient, string baseUrl, TextReader, HookSpool, TimeSpan budgetTotal)` (signature change: `CursorHookSpool` → `HookSpool`); `static void MigrateLegacyCursorSpool(HookSpool dest, string legacyDir)`.

- [ ] **Step 1: Write the failing migration test**

Add to `CursorHookCommandTests.cs` (inside the class):

```csharp
    [Test]
    public async Task legacy_cursor_spool_is_transformed_and_merged() {
        var dir       = Path.Combine(Path.GetTempPath(), $"kcap-mig-{Guid.NewGuid():N}");
        var legacyDir = Path.Combine(dir, "legacy");
        var spoolDir  = Path.Combine(dir, "spool");
        Directory.CreateDirectory(legacyDir);
        try {
            // Old format: {hook_event_name, body}
            await File.WriteAllTextAsync(Path.Combine(legacyDir, $"{Sid}.jsonl"),
                $"{{\"hook_event_name\":\"sessionEnd\",\"body\":\"{{\\\"session_id\\\":\\\"{Sid}\\\"}}\"}}\n");

            var spool = new HookSpool(spoolDir);
            CursorHookCommand.MigrateLegacyCursorSpool(spool, legacyDir);

            var migrated = await File.ReadAllTextAsync(Path.Combine(spoolDir, $"{Sid}.jsonl"));
            await Assert.That(migrated).Contains("\"route\":\"session-end/cursor\"");
            await Assert.That(File.Exists(Path.Combine(legacyDir, $"{Sid}.jsonl"))).IsFalse();
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CursorHookCommandTests/legacy_cursor_spool_is_transformed_and_merged"`
Expected: FAIL — `MigrateLegacyCursorSpool` / `HookSpool` overload not found (compile error).

- [ ] **Step 3: Delete `CursorHookSpool.cs` and update `CursorHookCommand.cs`**

Delete `src/Capacitor.Cli/Commands/CursorHookSpool.cs`.

In `CursorHookCommand.cs`: change every `CursorHookSpool` to `HookSpool`; in `HandleInternal` build the spool at the shared dir and run migration + reap:

```csharp
            var spool = new HookSpool(PathHelpers.ConfigPath("spool"));
            MigrateLegacyCursorSpool(spool, CursorPaths.SpoolDir());
            spool.ReapOlderThan(TimeSpan.FromDays(30));
```

Replace the per-session `DrainAsync` loop in `HandleCore` with a cross-session drain that maps a POST to a `DrainOutcome`:

```csharp
            if (sessionId is not null) {
                await spool.DrainAllAsync(sessionId, async (route, entryBody) => {
                    if (BudgetExpired()) return DrainOutcome.TransientStop;
                    try {
                        using var content = new StringContent(entryBody, Encoding.UTF8, "application/json");
                        using var resp    = await client.PostOnceAsync($"{baseUrl}/hooks/{route}", content, HookPostTimeout, ct);
                        if (resp.IsSuccessStatusCode) return DrainOutcome.Delivered;
                        var code = (int)resp.StatusCode;
                        return code is >= 500 or 408 or 429 ? DrainOutcome.TransientStop : DrainOutcome.Drop;
                    } catch { return DrainOutcome.TransientStop; }
                }, budgetTotal, ct);
            }
```

Change `spool.Append(sessionId, eventName, normalized)` call sites to pass the **route**: `spool.Append(sessionId, mapping.RouteSegment, normalized)`. Keep `spool.DeleteSession` behavior on `sessionEnd` success by reusing the existing `DeleteSession` — but since `HookSpool` no longer exposes per-session delete via the same name, replace that line with: the drain above already removes delivered entries, so the explicit `DeleteSession` on success is no longer needed; delete that `case true when eventName == "sessionEnd"` branch.

Add the migration helper (transform + merge through `Append`):

```csharp
    internal static void MigrateLegacyCursorSpool(HookSpool dest, string legacyDir) {
        try {
            if (!Directory.Exists(legacyDir)) return;
            foreach (var file in Directory.EnumerateFiles(legacyDir, "*.jsonl")) {
                try {
                    var sid = Path.GetFileNameWithoutExtension(file);
                    foreach (var line in File.ReadAllLines(file)) {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        string? ev, body;
                        try { var n = JsonNode.Parse(line); ev = n?["hook_event_name"]?.GetValue<string>(); body = n?["body"]?.GetValue<string>(); }
                        catch { continue; }
                        if (ev is null || body is null) continue;
                        if (CursorHookEventMap.TryResolve(ev, out var m)) dest.Append(sid, m.RouteSegment, body);
                    }
                    File.Delete(file); // only after lines are appended; re-run is idempotent
                } catch { /* per-file best effort */ }
            }
        } catch { }
    }
```

(`CursorHookEventMap.Mapping` exposes `RouteSegment` — confirm at `CursorHookEventMap.cs:10`.)

- [ ] **Step 4: Update the Cursor test fixture and existing tests**

In `CursorHookCommandTests.cs`: change `public CursorHookSpool Spool` to `public HookSpool Spool`, construct `new HookSpool(_spoolPath)`, and update `fx.Spool.Append(Sid, "sessionStart", ...)` calls (in `spool_drain_runs_before_current_event_under_budget` and `fresh_canonical_event_is_spooled_when_drain_consumes_budget`) to pass the **route**: `fx.Spool.Append(Sid, "session-start/cursor", ...)`. The existing ordering and spool-on-failure assertions stay.

- [ ] **Step 5: Delete or retarget `CursorHookSpoolTests.cs`**

The `HookSpoolTests` (Task 1) supersede it. Delete `test/Capacitor.Cli.Tests.Unit/Cursor/CursorHookSpoolTests.cs`.

- [ ] **Step 6: Run the Cursor tests**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CursorHookCommandTests/*"`
Expected: PASS — including `canonical_events_spool_on_POST_failure` (all four `SpoolOnFailure` events unchanged), `spool_drain_runs_before_current_event_under_budget`, and the new migration test.

- [ ] **Step 7: Verify AOT-clean and commit**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` → no output.

```bash
git add -A src/Capacitor.Cli/Commands/CursorHookCommand.cs test/Capacitor.Cli.Tests.Unit/Cursor/
git rm src/Capacitor.Cli/Commands/CursorHookSpool.cs test/Capacitor.Cli.Tests.Unit/Cursor/CursorHookSpoolTests.cs
git commit -m "refactor: migrate Cursor onto unified HookSpool with cross-session drain"
```

---

## Task 3: Process-start hook deadline + bootstrap bounding

**Files:**
- Create: `src/Capacitor.Cli/Commands/HookBudget.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/HookBudgetTests.cs`
- Modify: `src/Capacitor.Cli/Program.cs:45` (capture process-start; bound hook-path `ResolveServerUrl`; thread into hook)
- Modify: `src/Capacitor.Cli.Core/Config/AppConfig.cs:88,156,188` (`gitTimeoutMs` param)
- Modify: `src/Capacitor.Cli/RepositoryDetection.cs:73` (remaining-budget cap)

**Interfaces:**
- Produces:
  - `static class HookBudget { TimeSpan Ceiling(string command); TimeSpan Remaining(long processStartTimestamp, string command); }`
  - `AppConfig.ResolveServerUrl(string[] args, int gitTimeoutMs = 5000)`
  - `RepositoryDetection.DetectRepositoryAsync(string cwd, TimeSpan? budget = null)` and `EnrichWithRepositoryInfo(string body, TimeSpan? budget = null)`
- Consumes (Task 4+): `HookBudget.Remaining(processStart, command)`.

- [ ] **Step 1: Write the failing `HookBudget` test**

Create `test/Capacitor.Cli.Tests.Unit/HookBudgetTests.cs`:

```csharp
using System.Diagnostics;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class HookBudgetTests {
    [Test]
    public async Task session_end_ceiling_is_larger_than_others() {
        await Assert.That(HookBudget.Ceiling("session-end")).IsGreaterThan(HookBudget.Ceiling("stop"));
    }

    [Test]
    public async Task remaining_is_ceiling_minus_elapsed_minus_safety_and_never_negative() {
        var start = Stopwatch.GetTimestamp();
        var rem   = HookBudget.Remaining(start, "session-end");
        // ~15s ceiling - ~0 elapsed - 1.5s safety
        await Assert.That(rem).IsGreaterThan(TimeSpan.FromSeconds(12));
        await Assert.That(rem).IsLessThanOrEqualTo(TimeSpan.FromSeconds(13.5));

        // A start far in the past clamps to zero, never negative.
        var old = Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * 100);
        await Assert.That(HookBudget.Remaining(old, "stop")).IsEqualTo(TimeSpan.Zero);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/HookBudgetTests/*"`
Expected: FAIL — `HookBudget` does not exist.

- [ ] **Step 3: Create `HookBudget`**

Create `src/Capacitor.Cli/Commands/HookBudget.cs`:

```csharp
using System.Diagnostics;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Per-event hook-timeout ceilings (mirror kcap/hooks/hooks.json) and a
/// safety-adjusted "remaining" computed from a process-start timestamp, so the
/// hook always leaves time to spool + exit before Claude's kill.
/// </summary>
public static class HookBudget {
    public static readonly TimeSpan Safety = TimeSpan.FromMilliseconds(1500);

    public static TimeSpan Ceiling(string command) => command switch {
        "session-end" => TimeSpan.FromSeconds(15),
        _             => TimeSpan.FromSeconds(5),
    };

    public static TimeSpan Remaining(long processStartTimestamp, string command) {
        var rem = Ceiling(command) - Stopwatch.GetElapsedTime(processStartTimestamp) - Safety;
        return rem > TimeSpan.Zero ? rem : TimeSpan.Zero;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/HookBudgetTests/*"`
Expected: PASS.

- [ ] **Step 5: Bound the bootstrap git calls (AppConfig)**

In `AppConfig.cs`, add a `gitTimeoutMs` parameter that flows to the two git helpers:

```csharp
    public static async Task<string?> ResolveServerUrl(string[] args, int gitTimeoutMs = 5000) {
```

Thread it to `RepoRoot`/`GetGitRemoteUrls`. Change `GetGitRepoRoot()` and `GetGitRemoteUrls()` to accept `int timeoutMs = 5000` and use it in `WaitForExit(timeoutMs)` (replace the literal `5000` at `AppConfig.cs:170` and `:202`). At the call sites (`:118` `RepoRoot`, `:132`), pass `gitTimeoutMs`. (`RepoRoot` is a property; convert its use here to `GetGitRepoRoot(gitTimeoutMs) ?? Environment.CurrentDirectory`.)

- [ ] **Step 6: Capture process-start and bound hook bootstrap in Program.cs**

At the very top of `Program.cs` `Main` (before line 45), capture the clock and pass a tight git cap for hooks:

```csharp
var hookProcessStart = Stopwatch.GetTimestamp();
var isHook = command == "hook";
var baseUrl = await AppConfig.ResolveServerUrl(args, gitTimeoutMs: isHook ? 1000 : 5000);
```

Thread `hookProcessStart` into the Claude hook dispatch (used in Task 4):

```csharp
        if (args.Contains("--claude")) {
            return await ClaudeHookCommand.Handle(baseUrl!, Console.In, updateCheckTask, hookProcessStart);
        }
```

(Until Task 4 adds the parameter this won't compile — Tasks 3 and 4 land together if you build between them; or add the optional parameter to `Handle` now with a default and wire the body in Task 4. Prefer adding the optional param now: `Handle(..., long processStart = 0)` so Task 3 builds green.)

- [ ] **Step 7: Make repository enrichment budget-aware (RepositoryDetection)**

Add an optional `TimeSpan? budget` to `DetectRepositoryAsync` and `EnrichWithRepositoryInfo`. At the top of `DetectRepositoryAsync`, short-circuit when there is no headroom:

```csharp
    public static async Task<RepositoryPayload?> DetectRepositoryAsync(string cwd, TimeSpan? budget = null) {
        if (budget is { } b && b <= TimeSpan.Zero) return null;
        try {
```

`EnrichWithRepositoryInfo(string body, TimeSpan? budget = null)` forwards `budget` to `DetectRepositoryAsync`. Existing callers pass nothing (unchanged behavior).

- [ ] **Step 8: Run the unit suite + AOT check**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
Expected: PASS (no regressions).
Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` → no output.

- [ ] **Step 9: Commit**

```bash
git add src/Capacitor.Cli/Commands/HookBudget.cs test/Capacitor.Cli.Tests.Unit/HookBudgetTests.cs \
        src/Capacitor.Cli/Program.cs src/Capacitor.Cli.Core/Config/AppConfig.cs src/Capacitor.Cli/RepositoryDetection.cs
git commit -m "feat: anchor hook deadline at process entry; bound bootstrap git + enrichment"
```

---

## Task 4: `ClaudeHookCommand` test seam + hard cap

**Files:**
- Modify: `src/Capacitor.Cli/Commands/ClaudeHookCommand.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/ClaudeHookCommandTests.cs` (new)

**Interfaces:**
- Consumes: `HookSpool`, `HookBudget` (Tasks 1, 3).
- Produces:
  - `internal static Task<int> HandleCore(HttpClient client, HookSpool spool, long processStart, string baseUrl, TextReader stdin, Task? updateCheckTask = null)`
  - `internal static async Task<int> WithHardCap(Task<int> inner, TimeSpan budget)` (same semantics as Cursor's)
  - `Handle(...)` builds the real client/spool and delegates to `HandleCore`.

- [ ] **Step 1: Write a failing seam test (behavior preserved: session-start posts)**

Create `test/Capacitor.Cli.Tests.Unit/ClaudeHookCommandTests.cs`:

```csharp
using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

[NotInParallel("HomeEnvVarMutation")]
public class ClaudeHookCommandTests {
    const string Sid = "9dc2775376454e4691ecc2d69973c152";

    [Test]
    public async Task session_start_posts_to_session_start_route() {
        using var fx = new Fixture();
        await fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"/tmp"}""");
        await Assert.That(fx.RouteOrder).Contains("session-start");
    }

    // Covers the auth-hang case from the spec: the hard cap must beat an
    // uncancellable hang (e.g. TokenStore.RefreshAsync's untimed HttpClient.PostAsync).
    [Test]
    public async Task hard_cap_returns_zero_when_inner_ignores_cancellation() {
        var inner = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(10)); return 42; });
        var sw    = System.Diagnostics.Stopwatch.StartNew();
        var exit  = await ClaudeHookCommand.WithHardCap(inner, TimeSpan.FromMilliseconds(50));
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task hard_cap_returns_inner_result_when_inner_finishes_first() {
        var exit = await ClaudeHookCommand.WithHardCap(Task.FromResult(7), TimeSpan.FromSeconds(2));
        await Assert.That(exit).IsEqualTo(7);
    }

    sealed class Fixture : IDisposable {
        readonly string _tmpHome = Path.Combine(Path.GetTempPath(), $"kcap-claude-hook-{Guid.NewGuid():N}");
        readonly string _spoolPath;
        public List<string> Sent { get; } = [];
        public List<string> RouteOrder { get; } = [];
        public HookSpool Spool { get; }
        public HttpClient Client { get; }
        public TimeSpan HoldOnPost { get; set; } = TimeSpan.Zero;
        readonly HttpStatusCode _postStatus;

        public Fixture(HttpStatusCode postStatus = HttpStatusCode.OK) {
            Directory.CreateDirectory(_tmpHome);
            _spoolPath  = Path.Combine(_tmpHome, "spool");
            _postStatus = postStatus;
            Spool = new HookSpool(_spoolPath);
            Client = new HttpClient(new StubHandler(async req => {
                var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
                var path = req.RequestUri!.AbsolutePath;
                Sent.Add($"{path}|{body}");
                if (path.StartsWith("/hooks/")) RouteOrder.Add(path.Replace("/hooks/", ""));
                if (req.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (HoldOnPost > TimeSpan.Zero) await Task.Delay(HoldOnPost);
                return new HttpResponseMessage(_postStatus);
            }));
        }

        public Task<int> HandleAsync(string stdin, long processStart = 0) =>
            ClaudeHookCommand.HandleCore(Client, Spool, processStart == 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : processStart,
                "http://localhost", new StringReader(stdin));

        public IEnumerable<string> SpoolFiles =>
            Directory.Exists(_spoolPath) ? Directory.EnumerateFiles(_spoolPath) : [];

        public void Dispose() { Client.Dispose(); try { Directory.Delete(_tmpHome, true); } catch { } }
    }

    sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> impl) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => impl(r);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ClaudeHookCommandTests/*"`
Expected: FAIL — `ClaudeHookCommand.HandleCore` does not exist.

- [ ] **Step 3: Extract `HandleCore` + `WithHardCap`**

Refactor `ClaudeHookCommand`: rename the body of `Handle` into `HandleCore(HttpClient client, HookSpool spool, long processStart, string baseUrl, TextReader stdin, Task? updateCheckTask = null)`, replacing the internal `await HttpClientExtensions.CreateAuthenticatedClientAsync()` (line 241) with the injected `client`. Keep `Handle` as the production entry that builds the real deps and applies the hard cap:

```csharp
    public static Task<int> Handle(string baseUrl, TextReader stdin, Task? updateCheckTask = null, long processStart = 0) {
        var spool = new HookSpool(PathHelpers.ConfigPath("spool"));
        spool.ReapOlderThan(TimeSpan.FromDays(30));
        var ps = processStart == 0 ? Stopwatch.GetTimestamp() : processStart;
        return HandleWithDeps(spool, ps, baseUrl, stdin, updateCheckTask);
    }

    static async Task<int> HandleWithDeps(HookSpool spool, long processStart, string baseUrl, TextReader stdin, Task? updateCheckTask) {
        HttpClient? client = null;
        try {
            // Client creation is inside the deadline (Task 5 wraps the POST in a hard cap;
            // here we only need a client). A hung auth path is bounded by the per-call cap.
            client = await HttpClientExtensions.CreateAuthenticatedClientAsync();
            return await HandleCore(client, spool, processStart, baseUrl, stdin, updateCheckTask);
        } catch {
            return 0; // fail-open
        } finally { client?.Dispose(); }
    }

    internal static async Task<int> WithHardCap(Task<int> inner, TimeSpan budget) {
        var winner = await Task.WhenAny(inner, Task.Delay(budget));
        return winner == inner ? await inner : 0;
    }
```

Add `using System.Diagnostics;` and `using Capacitor.Cli.Commands;` is already the namespace. `HandleCore` keeps the existing logic but takes `client`/`spool`/`processStart` as parameters (do not call `CreateAuthenticatedClientAsync` inside it). For this task, `processStart`/`spool` are accepted but the spool/deadline wiring is added in Tasks 5–7; behavior is otherwise unchanged.

- [ ] **Step 4: Run the seam test + full suite**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ClaudeHookCommandTests/*"`
Expected: PASS.
Run the full suite to confirm no regression in existing Claude-hook integration: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`

- [ ] **Step 5: AOT check + commit**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` → no output.

```bash
git add src/Capacitor.Cli/Commands/ClaudeHookCommand.cs test/Capacitor.Cli.Tests.Unit/ClaudeHookCommandTests.cs
git commit -m "refactor: add ClaudeHookCommand HandleCore test seam + WithHardCap"
```

---

## Task 5: Claude session-end durability (minimal body, bounded POST, spool)

**Files:**
- Modify: `src/Capacitor.Cli/Commands/ClaudeHookCommand.cs` (session-end branch + the shared POST at `:247`)
- Modify: `test/Capacitor.Cli.Tests.Unit/ClaudeHookCommandTests.cs`

**Interfaces:**
- Consumes: `HookSpool.Append`, `HookBudget.Remaining`, `HttpClientExtensions.PostOnceAsync`.
- Produces: the bounded lifecycle POST helper `static Task<(bool ok, bool permanent)> PostLifecycleBoundedAsync(HttpClient, string url, string body, TimeSpan remaining, CancellationToken)` used by Tasks 5–6.

- [ ] **Step 1: Write failing tests (spool on 5xx; spool on hung server; not on 4xx)**

Add to `ClaudeHookCommandTests.cs`:

```csharp
    [Test]
    public async Task session_end_on_5xx_is_spooled_and_returns_zero() {
        using var fx = new Fixture(HttpStatusCode.InternalServerError);
        var exit = await fx.HandleAsync($$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp","reason":"other"}""");
        await Assert.That(exit).IsEqualTo(0);
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        var content = await File.ReadAllTextAsync(files[0]);
        await Assert.That(content).Contains("\"route\":\"session-end\"");
        await Assert.That(content).Contains("ended_at");
    }

    [Test]
    public async Task session_end_against_hung_server_is_spooled_within_budget() {
        using var fx = new Fixture();
        fx.HoldOnPost = TimeSpan.FromSeconds(30); // server hangs past the bounded attempt
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // processStart in the recent past leaves a small remaining budget.
        var exit = await fx.HandleAsync(
            $$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(15)); // did not wait the full 30s
        await Assert.That(fx.SpoolFiles.Any()).IsTrue();
    }

    [Test]
    public async Task session_end_on_4xx_is_not_spooled() {
        using var fx = new Fixture(HttpStatusCode.BadRequest);
        await fx.HandleAsync($$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(fx.SpoolFiles.Any()).IsFalse();
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ClaudeHookCommandTests/session_end*"`
Expected: FAIL — current code POSTs via `PostWithRetryAsync` and returns 1 on failure without spooling.

- [ ] **Step 3: Add the bounded POST helper and stamp `ended_at`**

In `ClaudeHookCommand.cs` add:

```csharp
    // Single bounded attempt. ok=true on 2xx. permanent=true on a 4xx that is not
    // 408/429 (do not spool). Any exception/timeout => (false, false) => spool as transient.
    static async Task<(bool ok, bool permanent)> PostLifecycleBoundedAsync(
            HttpClient client, string url, string body, TimeSpan remaining, CancellationToken ct) {
        if (remaining <= TimeSpan.Zero) return (false, false);
        try {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp    = await client.PostOnceAsync(url, content, remaining, ct);
            if (resp.IsSuccessStatusCode) return (true, false);
            var code = (int)resp.StatusCode;
            var transient = code is >= 500 or 408 or 429;
            return (false, !transient);
        } catch {
            return (false, false); // unreachable / hung / timeout — transient
        }
    }
```

In the session-end flow, stamp `ended_at` once before posting (after `body` is parsed), mirroring the watcher path:

```csharp
        // (session-end) stamp ended_at into the body so a spooled replay records the true end time
        try {
            var n = JsonNode.Parse(body)!;
            n["ended_at"] = DateTimeOffset.UtcNow.ToString("O");
            body = n.ToJsonString();
        } catch { }
```

- [ ] **Step 4: Replace the session-end POST with bounded-post + spool**

Replace the shared POST (`ClaudeHookCommand.cs:241-258`) for the `session-end` command with the bounded path. Concretely, for `command == "session-end"` compute remaining and post via the helper inside a `WithHardCap`-style guard, then spool on transient:

Do the session-end bounded POST **inline** (not via `PostLifecycleBoundedAsync`) so the success response stays available for the existing `generate_whats_done` read; the helper is reused only for session-start (Task 6). This replaces the generic POST + the old `generate_whats_done` block for the session-end command:

```csharp
        if (command == "session-end") {
            var remaining = HookBudget.Remaining(processStart, command);
            var sessionId = JsonNode.Parse(body)?["session_id"]?.GetValue<string>();
            HttpResponseMessage? resp = null;
            try {
                if (remaining > TimeSpan.Zero) {
                    using var content = new StringContent(body, Encoding.UTF8, "application/json");
                    resp = await client.PostOnceAsync($"{baseUrl}/hooks/session-end", content, remaining, CancellationToken.None);
                }
            } catch { resp = null; }

            if (resp is null || !resp.IsSuccessStatusCode) {
                var permanent = resp is not null && (int)resp.StatusCode is < 500 and not 408 and not 429;
                resp?.Dispose();
                if (!permanent && sessionId is not null) {
                    spool.Append(sessionId, "session-end", body);
                    await Console.Error.WriteLineAsync($"[kcap] session-end spooled; will retry on the next kcap hook ({sessionId})");
                }
                return 0;
            }

            try {
                var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
                if (node?["generate_whats_done"]?.GetValue<bool>() == true && sessionId is not null)
                    WatcherManager.SpawnWhatsDoneGenerator(baseUrl, sessionId);
            } catch { }
            resp.Dispose();
            return 0;
        }
```

This block runs **after** the existing pre-drain (KillWatcher + InlineDrain at `:171-203`) but **replaces** the generic POST + the old `generate_whats_done` block for session-end. The pre-drain's `RunCappedAsync` cap should be clamped to `remaining` too — change `PreHookDrainCap` usage to `TimeSpan.FromMilliseconds(Math.Min(PreHookDrainCap.TotalMilliseconds, HookBudget.Remaining(processStart, "session-end").TotalMilliseconds))` so the drain can't consume the budget the POST needs.

- [ ] **Step 5: Run the session-end tests**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ClaudeHookCommandTests/session_end*"`
Expected: PASS (5xx spooled, hung-server spooled fast, 4xx not spooled).

- [ ] **Step 6: AOT check + commit**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` → no output.

```bash
git add src/Capacitor.Cli/Commands/ClaudeHookCommand.cs test/Capacitor.Cli.Tests.Unit/ClaudeHookCommandTests.cs
git commit -m "feat: durable Claude session-end via bounded POST + spool"
```

---

## Task 6: Claude session-start durability (watcher-first, spool on failure)

**Files:**
- Modify: `src/Capacitor.Cli/Commands/ClaudeHookCommand.cs` (session-start branch at `:280-337` + the pre-POST flow)
- Modify: `test/Capacitor.Cli.Tests.Unit/ClaudeHookCommandTests.cs`

**Interfaces:**
- Consumes: `PostLifecycleBoundedAsync` (Task 5), `WatcherManager.EnsureWatcherRunning`, `RepositoryDetection.EnrichWithRepositoryInfo(body, budget)`.

- [ ] **Step 1: Write failing test (start failure → spooled; watcher attempted)**

Add to `ClaudeHookCommandTests.cs`. Since `EnsureWatcherRunning` spawns a real process, assert on the **spool** side-effect (watcher-spawn is covered by an integration test; here verify the start body is spooled and a minimal body is used):

```csharp
    [Test]
    public async Task session_start_on_failure_is_spooled_with_minimal_body() {
        using var fx = new Fixture(HttpStatusCode.InternalServerError);
        await fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp","source":"startup"}""");
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        var content = await File.ReadAllTextAsync(files[0]);
        await Assert.That(content).Contains("\"route\":\"session-start\"");
        await Assert.That(JsonNode.Parse(JsonNode.Parse(content.Split('\n')[0])!["body"]!.GetValue<string>())!["session_id"]!.GetValue<string>())
            .IsEqualTo(Sid);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ClaudeHookCommandTests/session_start_on_failure*"`
Expected: FAIL — current code returns 1 on failure (before watcher spawn) and never spools.

- [ ] **Step 3: Restructure the session-start flow**

For `command == "session-start"`: build the minimal body (session_id/transcript_path/cwd/source/hook_event_name already present), **spawn the watcher first**, run enrichment only with remaining headroom, then bounded-post; spool on transient failure. Replace the generic POST + the post-success `case "session-start"` watcher spawn with:

```csharp
        if (command == "session-start") {
            var node           = JsonNode.Parse(body);
            var sessionId      = node?["session_id"]?.GetValue<string>();
            var transcriptPath = node?["transcript_path"]?.GetValue<string>();
            var sessionCwd     = node?["cwd"]?.GetValue<string>();
            var source         = node?["source"]?.GetValue<string>();
            var isResumeOrCompact = source is not null &&
                (source.Equals("resume", StringComparison.OrdinalIgnoreCase) || source.Equals("compact", StringComparison.OrdinalIgnoreCase));

            // 1. Capture never lost: spawn the watcher before any slow git/gh/POST.
            if (sessionId is not null && transcriptPath is not null)
                await WatcherManager.EnsureWatcherRunning(baseUrl, sessionId, transcriptPath, agentId: null, cwd: sessionCwd, skipTitle: isResumeOrCompact);

            // 2. Enrich only with remaining headroom (already applied earlier for non-deferred path;
            //    if budget is gone, body stays minimal — repo info still arrives via the watcher).
            var remaining = HookBudget.Remaining(processStart, command);

            // 3. Bounded POST; spool on transient failure. PostLifecycleBoundedAsync
            //    disposes its response, so re-issue a single bounded POST here to read
            //    the success body for the envelope / plan-content emission.
            var (ok, permanent) = await PostLifecycleBoundedAsync(client, $"{baseUrl}/hooks/session-start", body, remaining, CancellationToken.None);
            if (!ok) {
                if (!permanent && sessionId is not null) spool.Append(sessionId, "session-start", body);
                return 0;
            }
            // success: retain the EXISTING context-envelope emission + plan-content POST
            // (ClaudeHookCommand.cs:286-324) verbatim here, reading the response via a
            // second bounded PostOnceAsync (clamped to the remaining budget) so the
            // injected-client test seam still observes the /hooks/session-start POST.
            // (No transcript watcher re-spawn — it was already started in step 1.)
            return 0;
        }
```

Move the existing context-envelope emission and plan-content POST (`:286-324`) into the success branch. The earlier `EnrichWithRepositoryInfo(body)` call (`:102`) should pass `HookBudget.Remaining(processStart, command)` as the budget so enrichment self-skips under pressure.

- [ ] **Step 4: Run the session-start test + full suite**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ClaudeHookCommandTests/*"`
Expected: PASS. Then full suite for no regression.

- [ ] **Step 5: AOT check + commit**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` → no output.

```bash
git add src/Capacitor.Cli/Commands/ClaudeHookCommand.cs test/Capacitor.Cli.Tests.Unit/ClaudeHookCommandTests.cs
git commit -m "feat: durable Claude session-start; spawn watcher before enrichment/POST"
```

---

## Task 7: Cross-session drain step (current-session-first) + replay side effects

**Files:**
- Modify: `src/Capacitor.Cli/Commands/ClaudeHookCommand.cs` (add drain before the current event)
- Modify: `test/Capacitor.Cli.Tests.Unit/ClaudeHookCommandTests.cs`

**Interfaces:**
- Consumes: `HookSpool.DrainAllAsync`, `HookBudget`, `PostLifecycleBoundedAsync`, `WatcherManager.SpawnWhatsDoneGenerator`.
- Produces: `static Func<string,string,Task<DrainOutcome>> ClaudePoster(HttpClient, string baseUrl, TimeSpan perAttempt)` (handles `generate_whats_done` on replayed `session-end`).

- [ ] **Step 1: Write failing tests (backlog drained; ordering; whats-done on replay)**

Add to `ClaudeHookCommandTests.cs`:

```csharp
    [Test]
    public async Task pending_backlog_is_drained_on_next_hook_when_server_up() {
        using var fx = new Fixture(); // 200 OK
        fx.Spool.Append(Sid, "session-end", $$"""{"session_id":"{{Sid}}"}""");
        // A fresh, unrelated stop hook with the server up flushes the backlog.
        await fx.HandleAsync($$"""{"hook_event_name":"Stop","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(fx.RouteOrder).Contains("session-end"); // replayed
        await Assert.That(fx.SpoolFiles.Any()).IsFalse();          // delivered + cleaned
    }

    [Test]
    public async Task current_session_start_replays_before_its_session_end() {
        using var fx = new Fixture();
        fx.Spool.Append(Sid, "session-start", $$"""{"session_id":"{{Sid}}"}""");
        await fx.HandleAsync($$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        var startIdx = fx.RouteOrder.IndexOf("session-start");
        var endIdx   = fx.RouteOrder.IndexOf("session-end");
        await Assert.That(startIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(endIdx).IsGreaterThan(startIdx);
    }

    [Test]
    public async Task replayed_session_end_with_generate_whats_done_is_handled() {
        // Server returns generate_whats_done:true for the replayed session-end.
        using var fx = new Fixture(); // default 200; override body below
        fx.RespondJson = """{"generate_whats_done":false}"""; // (whats-done spawns a process; assert no throw + drained)
        fx.Spool.Append(Sid, "session-end", $$"""{"session_id":"{{Sid}}"}""");
        await fx.HandleAsync($$"""{"hook_event_name":"Stop","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(fx.SpoolFiles.Any()).IsFalse();
    }
```

Add `public string? RespondJson { get; set; }` to the Fixture and return it as the POST body when set (default empty). This keeps the whats-done branch exercised without spawning a real generator (set false to avoid the process spawn; a true-case is covered by integration tests).

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ClaudeHookCommandTests/*backlog*"`
Expected: FAIL — no drain step exists yet.

- [ ] **Step 3: Add the poster and the drain step**

Add the Claude poster (handles `generate_whats_done` on replayed session-end so the side effect is not lost):

```csharp
    static Func<string, string, Task<DrainOutcome>> ClaudePoster(HttpClient client, string baseUrl, TimeSpan perAttempt) =>
        async (route, body) => {
            try {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var resp    = await client.PostOnceAsync($"{baseUrl}/hooks/{route}", content, perAttempt, CancellationToken.None);
                if (!resp.IsSuccessStatusCode) {
                    var code = (int)resp.StatusCode;
                    return code is >= 500 or 408 or 429 ? DrainOutcome.TransientStop : DrainOutcome.Drop;
                }
                if (route == "session-end") {
                    try {
                        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
                        var sid  = JsonNode.Parse(body)?["session_id"]?.GetValue<string>();
                        if (node?["generate_whats_done"]?.GetValue<bool>() == true && sid is not null)
                            WatcherManager.SpawnWhatsDoneGenerator(baseUrl, sid);
                    } catch { }
                }
                return DrainOutcome.Delivered;
            } catch { return DrainOutcome.TransientStop; }
        };
```

Call the drain **before** the current event's POST (after the disabled/exclusion early-returns, after `command` is known). Insert near the top of `HandleCore`, right after the exclusion checks:

```csharp
        // Drain stranded lifecycle events before handling the fresh one. Current session
        // first so a stranded session-start replays before this session's session-end.
        try {
            var drainBudget = TimeSpan.FromMilliseconds(Math.Min(2000, HookBudget.Remaining(processStart, command).TotalMilliseconds));
            var curSid      = JsonNode.Parse(body)?["session_id"]?.GetValue<string>();
            if (drainBudget > TimeSpan.Zero)
                await spool.DrainAllAsync(curSid, ClaudePoster(client, baseUrl, drainBudget), drainBudget, CancellationToken.None);
        } catch { /* fail-open */ }
```

Same-session ordering guarantee: if the current session's backlog could not fully drain (a `<sid>.jsonl`/`.draining` for `curSid` still exists after the drain), **spool the fresh event instead of posting it** for `session-start`/`session-end`. Add a guard before the session-end/session-start POST blocks (Tasks 5/6):

```csharp
        bool CurrentSessionHasBacklog(string? sid) =>
            sid is not null && Directory.Exists(PathHelpers.ConfigPath("spool"))
            && (File.Exists(Path.Combine(PathHelpers.ConfigPath("spool"), $"{sid}.jsonl"))
                || Directory.EnumerateFiles(PathHelpers.ConfigPath("spool"), $"{sid}.*.draining").Any());
```

In the session-end/session-start blocks, if `CurrentSessionHasBacklog(sessionId)` is true, `spool.Append(sessionId, command, body)` and `return 0` instead of posting. (For the injected-spool test seam, expose the spool dir via the spool instance — add `internal string Dir => spoolDir;` to `HookSpool` and use `spool.Dir` instead of `PathHelpers.ConfigPath("spool")` so tests observe the temp dir.)

- [ ] **Step 4: Run the drain tests + full suite**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ClaudeHookCommandTests/*"`
Expected: PASS. Then the full unit suite.

- [ ] **Step 5: AOT check + commit**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` → no output.

```bash
git add src/Capacitor.Cli/Commands/ClaudeHookCommand.cs src/Capacitor.Cli/Commands/HookSpool.cs test/Capacitor.Cli.Tests.Unit/ClaudeHookCommandTests.cs
git commit -m "feat: cross-session drain on every Claude hook (current-session-first, whats-done on replay)"
```

---

## Task 8: Integration test + final verification

**Files:**
- Test: `test/Capacitor.Cli.Tests.Integration/` (add a round-trip if the existing `HookRoundTripTests.cs` pattern fits)

- [ ] **Step 1: Add an integration round-trip (outage → recovery)**

Using the existing integration harness (`HookRoundTripTests.cs` / WireMock), assert: a `session-end` POST against a server returning 503 spools to disk; a subsequent `stop` hook against a 200 server drains it (server receives `/hooks/session-end`). Follow the existing integration test setup for spinning the mock server and pointing `KCAP_*` config at it.

- [ ] **Step 2: Run both suites**

Run:
```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj
```
Expected: PASS.

- [ ] **Step 3: Final AOT publish check**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output.

- [ ] **Step 4: README re-verify**

Confirm no user-facing CLI surface changed (no new command/flag). If a `spool` dir or behavior note is warranted, add a sentence under the relevant `## CLI commands` section; otherwise no change.

- [ ] **Step 5: Commit (if integration test or docs changed)**

```bash
git add -A
git commit -m "test: integration round-trip for hook-spool outage recovery"
```
