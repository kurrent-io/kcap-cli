# OpenCode Historical Import (`kcap import --opencode`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `kcap import --opencode`, importing historical OpenCode sessions (including subagents) from OpenCode's SQLite db into Kurrent Capacitor, with no server changes.

**Architecture:** A routed `OpenCodeImportSource : IImportSource` (modeled on `PiImportSource`/`GeminiImportSource`) reads `~/.local/share/opencode/opencode.db` via a CLI-only `OpenCodeDb` helper. It reconstructs the live plugin's `{info,parts}` JSONL per message from the `message`/`part` rows, classifies each root session binary New/AlreadyLoaded (line spaces between live and import are incompatible — no resume), and imports roots with their child sessions routed as subagents before `session-end`.

**Tech Stack:** .NET 10, NativeAOT, Microsoft.Data.Sqlite + SQLitePCLRaw.bundle_e_sqlite3, TUnit, WireMock.Net.

**Spec:** `docs/superpowers/specs/2026-06-26-opencode-import-design.md` (rev3).

---

## File Structure

| File | Responsibility | Action |
|---|---|---|
| `Directory.Packages.props` | Pin SQLite package versions | Modify |
| `src/Capacitor.Cli/Capacitor.Cli.csproj` | Reference SQLite packages (CLI only, not Core) | Modify |
| `src/Capacitor.Cli/Commands/OpenCodeDb.cs` | Read-only db open; query roots/children; stream-synthesize `{info,parts}` lines; importable-line predicate | Create |
| `src/Capacitor.Cli/Commands/OpenCodeImportSource.cs` | `IImportSource`: discover roots, binary classify, import roots + subagent children + set-title | Create |
| `src/Capacitor.Cli/Commands/VendorSelection.cs` | Recognize `--opencode` | Modify |
| `src/Capacitor.Cli/Program.cs` | Register `OpenCodeImportSource` in import sources | Modify |
| `src/Capacitor.Cli.Core/Resources/help-import.txt` | Add `--opencode` filter line | Modify |
| `src/Capacitor.Cli.Core/Resources/help-usage.txt` | Add OpenCode to import one-liner | Modify |
| `README.md` | Quick-start + `kcap import` section | Modify |
| `test/Capacitor.Cli.Tests.Unit/OpenCodeDbTests.cs` | DB reconstruction, ordering, importable predicate | Create |
| `test/Capacitor.Cli.Tests.Unit/OpenCodeImportSourceTests.cs` | Discovery (roots vs children), classification | Create |
| `test/Capacitor.Cli.Tests.Unit/VendorSelectionTests.cs` | `--opencode` parsing (create if absent; else extend) | Modify/Create |
| `test/Capacitor.Cli.Tests.Integration/OpenCodeImportSourceImportTests.cs` | Full discover→classify→import wire contract incl. subagents + set-title | Create |

**Reference files to read before starting:** `src/Capacitor.Cli/Commands/PiImportSource.cs` (routed-source shape), `src/Capacitor.Cli/Commands/GeminiImportSource.cs` (subagent import ordering), `src/Capacitor.Cli.Core/OpenCode/OpenCodeSubagentDiscovery.cs` (subagent payload builders), `test/Capacitor.Cli.Tests.Integration/PiImportSourceImportTests.cs` (integration harness).

**Build/test commands (this repo uses `~/.dotnet/dotnet` for .NET 10):**
- Build: `~/.dotnet/dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj`
- Unit tests: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/OpenCodeDbTests/*"`
- AOT publish: `~/.dotnet/dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release`

---

## Task 1: Add SQLite packages (CLI only)

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/Capacitor.Cli/Capacitor.Cli.csproj:35-37`

- [ ] **Step 1: Pin versions centrally**

In `Directory.Packages.props`, add inside the `<ItemGroup>` with the other `PackageVersion` entries (keep alphabetical-ish ordering near the `Microsoft.*` block):

```xml
<PackageVersion Include="Microsoft.Data.Sqlite" Version="10.0.8" />
<PackageVersion Include="SQLitePCLRaw.bundle_e_sqlite3" Version="3.0.2" />
```

- [ ] **Step 2: Reference from the CLI project only**

In `src/Capacitor.Cli/Capacitor.Cli.csproj`, in the `<ItemGroup>` containing the existing `<PackageReference>` lines, add:

```xml
<PackageReference Include="Microsoft.Data.Sqlite" />
<PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3" />
```

Do **not** add these to `Capacitor.Cli.Core` — Core is `IsAotCompatible`/`IsTrimmable` and the daemon depends on it.

- [ ] **Step 3: Verify restore + build**

Run: `~/.dotnet/dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj`
Expected: build succeeds, packages restore.

- [ ] **Step 4: Commit**

```bash
git add Directory.Packages.props src/Capacitor.Cli/Capacitor.Cli.csproj
git commit -m "build: add Microsoft.Data.Sqlite to CLI for OpenCode import"
```

---

## Task 2: `OpenCodeDb` — connection + session queries

**Files:**
- Create: `src/Capacitor.Cli/Commands/OpenCodeDb.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/OpenCodeDbTests.cs`

- [ ] **Step 1: Write the failing test (roots vs children + fixture builder)**

Create `test/Capacitor.Cli.Tests.Unit/OpenCodeDbTests.cs`:

```csharp
using Capacitor.Cli.Commands;
using Microsoft.Data.Sqlite;

namespace Capacitor.Cli.Tests.Unit;

public class OpenCodeDbTests {
    // Minimal subset of OpenCode's schema needed by the importer.
    static string BuildDb(string dir) {
        var path = Path.Combine(dir, "opencode.db");
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        Exec(conn, """
            CREATE TABLE session (id TEXT PRIMARY KEY, parent_id TEXT, directory TEXT,
                title TEXT NOT NULL, version TEXT NOT NULL DEFAULT '', model TEXT,
                time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL);
            CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT NOT NULL,
                time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL DEFAULT 0, data TEXT NOT NULL);
            CREATE TABLE part (id TEXT PRIMARY KEY, message_id TEXT NOT NULL, session_id TEXT NOT NULL,
                time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL DEFAULT 0, data TEXT NOT NULL);
            """);
        return path;
    }

    static void Exec(SqliteConnection c, string sql) {
        using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }

    static void InsertSession(string dbPath, string id, string? parent, string dir, string title, long t) {
        using var c = new SqliteConnection($"Data Source={dbPath}"); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO session(id,parent_id,directory,title,version,time_created,time_updated) VALUES($i,$p,$d,$t,'1.17',$tc,$tc)";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$p", (object?)parent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", dir);
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$tc", t);
        cmd.ExecuteNonQuery();
    }

    [Test]
    public async Task QueryRoots_returns_only_parentless_sessions() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);
        InsertSession(db, "ses_root1", null, "/work/a", "Root one", 1782241513759);
        InsertSession(db, "ses_root2", "",   "/work/b", "Root two", 1782241513760); // empty parent = root
        InsertSession(db, "ses_child", "ses_root1", "/work/a", "Child", 1782241513761);

        using var ocdb = new OpenCodeDb(db);
        var roots = ocdb.QueryRoots();

        await Assert.That(roots.Select(r => r.Id).OrderBy(x => x))
            .IsEquivalentTo(new[] { "ses_root1", "ses_root2" });
    }

    [Test]
    public async Task QueryChildren_returns_children_in_time_order() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);
        InsertSession(db, "ses_root", null, "/work/a", "Root", 100);
        InsertSession(db, "ses_c2", "ses_root", "/work/a", "C2", 220);
        InsertSession(db, "ses_c1", "ses_root", "/work/a", "C1", 210);

        using var ocdb = new OpenCodeDb(db);
        var kids = ocdb.QueryChildren("ses_root");

        await Assert.That(kids.Select(k => k.Id)).IsEquivalentTo(new[] { "ses_c1", "ses_c2" });
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = Directory.CreateTempSubdirectory("kcap-ocdb").FullName;
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/OpenCodeDbTests/*"`
Expected: FAIL — `OpenCodeDb` does not exist.

- [ ] **Step 3: Implement `OpenCodeDb` connection + session queries**

Create `src/Capacitor.Cli/Commands/OpenCodeDb.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace Capacitor.Cli.Commands;

/// <summary>One OpenCode session row (subset used by import).</summary>
internal sealed record OpenCodeSessionRow(
    string  Id,
    string? ParentId,
    string? Directory,
    string  Title,
    long    TimeCreated,
    long    TimeUpdated);

/// <summary>
/// Read-only reader over OpenCode's SQLite db (<c>~/.local/share/opencode/opencode.db</c>).
/// Lives in the CLI project (not Core) so the Microsoft.Data.Sqlite native bundle
/// never reaches the AOT-published daemon. Opened read-only, WAL-tolerant.
/// </summary>
internal sealed class OpenCodeDb : IDisposable {
    readonly SqliteConnection _conn;

    public OpenCodeDb(string dbPath) {
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = dbPath,
            Mode       = SqliteOpenMode.ReadOnly,
            Cache      = SqliteCacheMode.Private,
        }.ToString());
        _conn.Open();
    }

    public IReadOnlyList<OpenCodeSessionRow> QueryRoots() =>
        QuerySessions("(parent_id IS NULL OR parent_id = '')", parent: null);

    public IReadOnlyList<OpenCodeSessionRow> QueryChildren(string parentId) =>
        QuerySessions("parent_id = $parent", parentId);

    IReadOnlyList<OpenCodeSessionRow> QuerySessions(string whereClause, string? parent) {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            $"SELECT id, parent_id, directory, title, time_created, time_updated " +
            $"FROM session WHERE {whereClause} ORDER BY time_created, id";
        if (parent is not null) cmd.Parameters.AddWithValue("$parent", parent);

        var rows = new List<OpenCodeSessionRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) {
            rows.Add(new OpenCodeSessionRow(
                Id:          r.GetString(0),
                ParentId:    r.IsDBNull(1) ? null : r.GetString(1),
                Directory:   r.IsDBNull(2) ? null : r.GetString(2),
                Title:       r.IsDBNull(3) ? "" : r.GetString(3),
                TimeCreated: r.GetInt64(4),
                TimeUpdated: r.GetInt64(5)));
        }
        return rows;
    }

    public void Dispose() => _conn.Dispose();
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/OpenCodeDbTests/*"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/OpenCodeDb.cs test/Capacitor.Cli.Tests.Unit/OpenCodeDbTests.cs
git commit -m "feat: OpenCodeDb read-only reader with root/child session queries"
```

---

## Task 3: `OpenCodeDb` — line reconstruction + importable predicate

**Files:**
- Modify: `src/Capacitor.Cli/Commands/OpenCodeDb.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/OpenCodeDbTests.cs`

- [ ] **Step 1: Write the failing tests (line shape + ordering + importable count)**

Add to `OpenCodeDbTests.cs` (and a helper to insert messages/parts):

```csharp
static void InsertMessage(string dbPath, string id, string sid, long t, string dataJson) {
    using var c = new SqliteConnection($"Data Source={dbPath}"); c.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = "INSERT INTO message(id,session_id,time_created,data) VALUES($i,$s,$t,$d)";
    cmd.Parameters.AddWithValue("$i", id);
    cmd.Parameters.AddWithValue("$s", sid);
    cmd.Parameters.AddWithValue("$t", t);
    cmd.Parameters.AddWithValue("$d", dataJson);
    cmd.ExecuteNonQuery();
}
static void InsertPart(string dbPath, string id, string mid, string sid, long t, string dataJson) {
    using var c = new SqliteConnection($"Data Source={dbPath}"); c.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = "INSERT INTO part(id,message_id,session_id,time_created,data) VALUES($i,$m,$s,$t,$d)";
    cmd.Parameters.AddWithValue("$i", id);
    cmd.Parameters.AddWithValue("$m", mid);
    cmd.Parameters.AddWithValue("$s", sid);
    cmd.Parameters.AddWithValue("$t", t);
    cmd.Parameters.AddWithValue("$d", dataJson);
    cmd.ExecuteNonQuery();
}

[Test]
public async Task SynthesizeLines_merges_ids_onto_info_and_parts() {
    using var tmp = new TempDir();
    var db = BuildDb(tmp.Path);
    InsertSession(db, "ses_x", null, "/w", "T", 100);
    InsertMessage(db, "msg_1", "ses_x", 100, """{"role":"user","time":{"created":100}}""");
    InsertPart(db, "prt_1", "msg_1", "ses_x", 100, """{"type":"text","text":"hello"}""");

    using var ocdb = new OpenCodeDb(db);
    var lines = ocdb.SynthesizeLines("ses_x").ToList();

    await Assert.That(lines.Count).IsEqualTo(1);
    using var doc = System.Text.Json.JsonDocument.Parse(lines[0]);
    var info  = doc.RootElement.GetProperty("info");
    var part0 = doc.RootElement.GetProperty("parts")[0];
    await Assert.That(info.GetProperty("id").GetString()).IsEqualTo("msg_1");
    await Assert.That(info.GetProperty("sessionID").GetString()).IsEqualTo("ses_x");
    await Assert.That(info.GetProperty("role").GetString()).IsEqualTo("user");
    await Assert.That(part0.GetProperty("id").GetString()).IsEqualTo("prt_1");
    await Assert.That(part0.GetProperty("messageID").GetString()).IsEqualTo("msg_1");
    await Assert.That(part0.GetProperty("sessionID").GetString()).IsEqualTo("ses_x");
    await Assert.That(part0.GetProperty("type").GetString()).IsEqualTo("text");
}

[Test]
public async Task SynthesizeLines_orders_by_message_chronology_not_lexical_id() {
    using var tmp = new TempDir();
    var db = BuildDb(tmp.Path);
    InsertSession(db, "ses_x", null, "/w", "T", 100);
    // msg_b is lexically after msg_a but chronologically FIRST.
    InsertMessage(db, "msg_b", "ses_x", 100, """{"role":"user"}""");
    InsertMessage(db, "msg_a", "ses_x", 200, """{"role":"assistant"}""");
    InsertPart(db, "prt_b", "msg_b", "ses_x", 100, """{"type":"text","text":"first"}""");
    InsertPart(db, "prt_a", "msg_a", "ses_x", 200, """{"type":"text","text":"second"}""");

    using var ocdb = new OpenCodeDb(db);
    var roles = ocdb.SynthesizeLines("ses_x")
        .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.GetProperty("info").GetProperty("role").GetString())
        .ToList();

    await Assert.That(roles).IsEquivalentTo(new[] { "user", "assistant" }); // chronological, not msg_a-first
}

[Test]
public async Task SynthesizeLines_includes_message_with_no_parts() {
    using var tmp = new TempDir();
    var db = BuildDb(tmp.Path);
    InsertSession(db, "ses_x", null, "/w", "T", 100);
    InsertMessage(db, "msg_1", "ses_x", 100, """{"role":"assistant"}"""); // no parts

    using var ocdb = new OpenCodeDb(db);
    var lines = ocdb.SynthesizeLines("ses_x").ToList();

    await Assert.That(lines.Count).IsEqualTo(1);
    var parts = System.Text.Json.JsonDocument.Parse(lines[0]).RootElement.GetProperty("parts");
    await Assert.That(parts.GetArrayLength()).IsEqualTo(0);
}

[Test]
public async Task IsImportRelevantLine_rejects_structural_only_and_empty() {
    // Structural-only assistant (step markers) and empty user text are not importable.
    await Assert.That(OpenCodeDb.IsImportRelevantLine(
        """{"info":{"role":"assistant"},"parts":[{"type":"step-start"},{"type":"step-finish"}]}""")).IsFalse();
    await Assert.That(OpenCodeDb.IsImportRelevantLine(
        """{"info":{"role":"user"},"parts":[{"type":"text","text":""}]}""")).IsFalse();
    // A real text part is importable.
    await Assert.That(OpenCodeDb.IsImportRelevantLine(
        """{"info":{"role":"user"},"parts":[{"type":"text","text":"hi"}]}""")).IsTrue();
    // A terminal tool part is importable.
    await Assert.That(OpenCodeDb.IsImportRelevantLine(
        """{"info":{"role":"assistant"},"parts":[{"type":"tool","state":{"status":"completed"}}]}""")).IsTrue();
    // A non-terminal tool alone is not.
    await Assert.That(OpenCodeDb.IsImportRelevantLine(
        """{"info":{"role":"assistant"},"parts":[{"type":"tool","state":{"status":"running"}}]}""")).IsFalse();
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/OpenCodeDbTests/*"`
Expected: FAIL — `SynthesizeLines` / `IsImportRelevantLine` not defined.

- [ ] **Step 3: Implement reconstruction + predicate**

Add to `OpenCodeDb.cs` (add `using System.Text.Json;` and `using System.Text.Json.Nodes;` at top):

```csharp
/// <summary>
/// Streams one reconstructed <c>{info,parts}</c> JSONL line per message, in
/// message chronological order, parts in (time_created,id) order. Reproduces the
/// live plugin's line shape by merging each row's key columns back onto its
/// <c>data</c> JSON: info gets {id, sessionID}; each part gets {id, messageID,
/// sessionID}. A LEFT JOIN keeps messages that have no parts. Bounded memory:
/// holds only the current message's parts at a time.
/// </summary>
public IEnumerable<string> SynthesizeLines(string sessionId) {
    using var cmd = _conn.CreateCommand();
    cmd.CommandText =
        "SELECT m.id, m.data, p.id, p.message_id, p.session_id, p.data " +
        "FROM message m LEFT JOIN part p ON p.message_id = m.id " +
        "WHERE m.session_id = $s " +
        "ORDER BY m.time_created, m.id, p.time_created, p.id";
    cmd.Parameters.AddWithValue("$s", sessionId);

    using var r = cmd.ExecuteReader();

    string?    curMsgId = null;
    string     curMsgData = "{}";
    JsonArray? parts = null;

    while (r.Read()) {
        var msgId = r.GetString(0);
        if (msgId != curMsgId) {
            if (curMsgId is not null) yield return BuildLine(curMsgId, sessionId, curMsgData, parts!);
            curMsgId   = msgId;
            curMsgData = r.GetString(1);
            parts      = new JsonArray();
        }
        // p.* columns are null for a message with no parts (LEFT JOIN).
        if (!r.IsDBNull(2)) {
            var partNode = JsonNode.Parse(r.GetString(5))!.AsObject();
            partNode["id"]        = r.GetString(2);
            partNode["messageID"] = r.GetString(3);
            partNode["sessionID"] = r.GetString(4);
            parts!.Add(partNode);
        }
    }
    if (curMsgId is not null) yield return BuildLine(curMsgId, sessionId, curMsgData, parts!);
}

// Merge the row's key columns back onto the message's data JSON to reproduce the
// live SDK's `info` object: {id (= message.id), sessionID (= the session row id)}.
static string BuildLine(string msgId, string sessionId, string msgData, JsonArray parts) {
    var info = JsonNode.Parse(msgData)!.AsObject();
    info["id"]        = msgId;
    info["sessionID"] = sessionId;
    return new JsonObject { ["info"] = info, ["parts"] = parts }.ToJsonString();
}

/// <summary>
/// True when a reconstructed line maps to at least one canonical server event
/// under the <c>opencode</c> normalizer: a user/assistant message with real text,
/// or a terminal (completed/error) tool part. Structural-only parts
/// (step-start/step-finish), empty text, and non-terminal tools emit nothing.
/// Keep in sync with the server normalizer (same coupling Pi documents).
/// </summary>
public static bool IsImportRelevantLine(string line) {
    try {
        using var doc = JsonDocument.Parse(line);
        if (!doc.RootElement.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var p in parts.EnumerateArray()) {
            var type = p.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type) {
                case "text":
                case "reasoning":
                    if (p.TryGetProperty("text", out var tx) && !string.IsNullOrWhiteSpace(tx.GetString()))
                        return true;
                    break;
                case "tool":
                    var status = p.TryGetProperty("state", out var st) && st.TryGetProperty("status", out var ss)
                        ? ss.GetString() : null;
                    if (status is "completed" or "error") return true;
                    break;
            }
        }
        return false;
    } catch {
        return false;
    }
}
```

**Note for implementer:** the `SynthesizeLines_merges_ids` test asserts `info.sessionID == "ses_x"` and each part's `sessionID`/`messageID`/`id` — it will catch any wrong key wiring.

- [ ] **Step 4: Run to verify it passes**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/OpenCodeDbTests/*"`
Expected: PASS (all OpenCodeDbTests).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/OpenCodeDb.cs test/Capacitor.Cli.Tests.Unit/OpenCodeDbTests.cs
git commit -m "feat: OpenCodeDb line reconstruction + importable-line predicate"
```

---

## Task 4: `OpenCodeImportSource` — discovery (roots only)

**Files:**
- Create: `src/Capacitor.Cli/Commands/OpenCodeImportSource.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/OpenCodeImportSourceTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/Capacitor.Cli.Tests.Unit/OpenCodeImportSourceTests.cs` (reuse the fixture builders from Task 2 — copy the `BuildDb`/`InsertSession`/`InsertMessage`/`InsertPart`/`TempDir` helpers, or refactor them into a shared `OpenCodeDbFixture` static in the test project and reference it from both test files):

```csharp
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class OpenCodeImportSourceTests {
    [Test]
    public async Task discovery_returns_roots_with_cwd_and_timestamp_excludes_children() {
        using var tmp = new OpenCodeDbFixture();
        tmp.AddSession("ses_root", null, "/work/a", "Root", 1782241513759);
        tmp.AddSession("ses_child", "ses_root", "/work/a", "Child", 1782241513761);
        tmp.AddMessageWithText("ses_root", "msg_1", "hello", 1782241513760);

        var source   = new OpenCodeImportSource(tmp.DbPath);
        var sessions = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);

        await Assert.That(sessions.Count).IsEqualTo(1);
        await Assert.That(sessions[0].SessionId).IsEqualTo("ses_root"); // raw id, not GUID-normalized
        await Assert.That(sessions[0].Vendor).IsEqualTo("opencode");
        await Assert.That(sessions[0].Cwd).IsEqualTo("/work/a");
        await Assert.That(sessions[0].FirstTimestamp)
            .IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1782241513759));
    }

    [Test]
    public async Task discovery_filters_by_cwd_and_session() {
        using var tmp = new OpenCodeDbFixture();
        tmp.AddSession("ses_a", null, "/work/a", "A", 100);
        tmp.AddSession("ses_b", null, "/work/b", "B", 200);
        tmp.AddMessageWithText("ses_a", "m1", "x", 100);
        tmp.AddMessageWithText("ses_b", "m2", "y", 200);

        var source = new OpenCodeImportSource(tmp.DbPath);

        var byCwd = await source.DiscoverAsync(new DiscoveryFilters("/work/b", null, null, 0), CancellationToken.None);
        await Assert.That(byCwd.Select(s => s.SessionId)).IsEquivalentTo(new[] { "ses_b" });

        var bySession = await source.DiscoverAsync(new DiscoveryFilters(null, "ses_a", null, 0), CancellationToken.None);
        await Assert.That(bySession.Select(s => s.SessionId)).IsEquivalentTo(new[] { "ses_a" });
    }

    [Test]
    public async Task IsAvailable_false_when_db_missing() {
        var source = new OpenCodeImportSource(Path.Combine(Path.GetTempPath(), "no-such-kcap.db"));
        await Assert.That(source.IsAvailable).IsFalse();
    }
}
```

Also create the shared fixture `test/Capacitor.Cli.Tests.Unit/OpenCodeDbFixture.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>Builds a throwaway OpenCode-shaped SQLite db for import tests.</summary>
internal sealed class OpenCodeDbFixture : IDisposable {
    public string Dir    { get; } = Directory.CreateTempSubdirectory("kcap-ocfix").FullName;
    public string DbPath => Path.Combine(Dir, "opencode.db");

    public OpenCodeDbFixture() {
        using var c = Open();
        Exec(c, """
            CREATE TABLE session (id TEXT PRIMARY KEY, parent_id TEXT, directory TEXT,
                title TEXT NOT NULL, version TEXT NOT NULL DEFAULT '', model TEXT,
                time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL);
            CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT NOT NULL,
                time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL DEFAULT 0, data TEXT NOT NULL);
            CREATE TABLE part (id TEXT PRIMARY KEY, message_id TEXT NOT NULL, session_id TEXT NOT NULL,
                time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL DEFAULT 0, data TEXT NOT NULL);
            """);
    }

    SqliteConnection Open() { var c = new SqliteConnection($"Data Source={DbPath}"); c.Open(); return c; }
    static void Exec(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    public void AddSession(string id, string? parent, string dir, string title, long t) {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO session(id,parent_id,directory,title,version,time_created,time_updated) VALUES($i,$p,$d,$t,'1.17',$tc,$tc)";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$p", (object?)parent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", dir);
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$tc", t);
        cmd.ExecuteNonQuery();
    }

    public void AddMessageWithText(string sid, string msgId, string text, long t) {
        using var c = Open();
        using (var m = c.CreateCommand()) {
            m.CommandText = "INSERT INTO message(id,session_id,time_created,data) VALUES($i,$s,$t,$d)";
            m.Parameters.AddWithValue("$i", msgId);
            m.Parameters.AddWithValue("$s", sid);
            m.Parameters.AddWithValue("$t", t);
            m.Parameters.AddWithValue("$d", """{"role":"user","time":{"created":""" + t + "}}");
            m.ExecuteNonQuery();
        }
        using (var p = c.CreateCommand()) {
            p.CommandText = "INSERT INTO part(id,message_id,session_id,time_created,data) VALUES($i,$m,$s,$t,$d)";
            p.Parameters.AddWithValue("$i", "prt_" + msgId);
            p.Parameters.AddWithValue("$m", msgId);
            p.Parameters.AddWithValue("$s", sid);
            p.Parameters.AddWithValue("$t", t);
            p.Parameters.AddWithValue("$d", """{"type":"text","text":"""" + text + """"}""");
            p.ExecuteNonQuery();
        }
    }

    public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/OpenCodeImportSourceTests/*"`
Expected: FAIL — `OpenCodeImportSource` not defined.

- [ ] **Step 3: Implement discovery**

Create `src/Capacitor.Cli/Commands/OpenCodeImportSource.cs`:

```csharp
using Capacitor.Cli.Core.OpenCode;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Historical import of OpenCode sessions from its SQLite db. Routed source
/// (FilePath="" → ImportSessionAsync), modeled on PiImportSource/GeminiImportSource.
/// Roots are main sessions; child sessions (parent_id set) are imported as
/// subagents of their parent. See docs/superpowers/specs/2026-06-26-opencode-import-design.md.
/// </summary>
internal sealed class OpenCodeImportSource : IImportSource {
    readonly string _dbPath;

    public OpenCodeImportSource(string? dbPathOverride = null) =>
        _dbPath = dbPathOverride ?? Path.Combine(OpenCodePaths.DataDir(), "opencode.db");

    public string Vendor => "opencode";
    public bool   IsAvailable => File.Exists(_dbPath);
    public bool   SupportsTitleGeneration => false; // routed; native title forwarded via /hooks/set-title

    static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    static string Norm(string p) {
        try { return Path.GetFullPath(p).TrimEnd('/', '\\'); } catch { return p.TrimEnd('/', '\\'); }
    }

    public Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) {
        if (!File.Exists(_dbPath)) return Task.FromResult<IReadOnlyList<DiscoveredSession>>([]);

        using var db = new OpenCodeDb(_dbPath);
        var normalizedCwd = filters.FilterCwd is { } cwd ? Norm(cwd) : null;
        var sinceMs = filters.Since is { } s
            ? new DateTimeOffset(s.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToUnixTimeMilliseconds()
            : (long?)null;

        var result = new List<DiscoveredSession>();
        foreach (var row in db.QueryRoots()) {
            ct.ThrowIfCancellationRequested();
            if (filters.FilterSession is { } fs && !string.Equals(row.Id, fs, StringComparison.Ordinal)) continue;
            if (normalizedCwd is not null &&
                (row.Directory is null || !Norm(row.Directory).Equals(normalizedCwd, PathComparison))) continue;
            if (sinceMs is { } cutoff && row.TimeCreated < cutoff) continue;

            result.Add(new DiscoveredSession(
                SessionId:      row.Id, // raw ses_… — no GUID normalization
                Vendor:         Vendor,
                Cwd:            row.Directory,
                FirstTimestamp: DateTimeOffset.FromUnixTimeMilliseconds(row.TimeCreated),
                SourceMeta:     new Dictionary<string, object?> {
                    ["Title"]       = row.Title,
                    ["TimeUpdated"] = row.TimeUpdated,
                }));
        }
        return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);
    }

    public Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
        IReadOnlyList<DiscoveredSession> sessions, ClassifyContext ctx, CancellationToken ct) =>
        throw new NotImplementedException(); // Task 5

    public Task<ImportOutcome> ImportSessionAsync(
        ImportCommand.SessionClassification classification, ImportContext ctx, CancellationToken ct) =>
        throw new NotImplementedException(); // Task 6/7
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/OpenCodeImportSourceTests/*"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/OpenCodeImportSource.cs test/Capacitor.Cli.Tests.Unit/OpenCodeImportSourceTests.cs test/Capacitor.Cli.Tests.Unit/OpenCodeDbFixture.cs
git commit -m "feat: OpenCodeImportSource discovery (roots, filters)"
```

---

## Task 5: `OpenCodeImportSource` — binary classification

**Files:**
- Modify: `src/Capacitor.Cli/Commands/OpenCodeImportSource.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/OpenCodeImportSourceTests.cs`

- [ ] **Step 1: Write the failing test (WireMock last-line stub)**

Add to `OpenCodeImportSourceTests.cs`:

```csharp
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
// ...

[Test]
public async Task classify_new_when_server_has_no_watermark() {
    using var fix = new OpenCodeDbFixture();
    fix.AddSession("ses_x", null, "/w", "T", 100);
    fix.AddMessageWithText("ses_x", "m1", "hello", 100);

    using var server = WireMockServer.Start();
    server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
          .RespondWith(Response.Create().WithStatusCode(404));
    using var client = new HttpClient();

    var source     = new OpenCodeImportSource(fix.DbPath);
    var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
    var classified = await source.ClassifyAsync(discovered,
        new ClassifyContext(client, server.Url!, MinLines: 1, ExcludedRepos: null, ExcludedPaths: null),
        CancellationToken.None);

    await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
    await Assert.That(classified[0].Vendor).IsEqualTo("opencode");
    await Assert.That(classified[0].FilePath).IsEqualTo(""); // routed
}

[Test]
public async Task classify_already_loaded_when_server_has_any_watermark() {
    using var fix = new OpenCodeDbFixture();
    fix.AddSession("ses_x", null, "/w", "T", 100);
    fix.AddMessageWithText("ses_x", "m1", "hello", 100);

    using var server = WireMockServer.Start();
    server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
          .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":5}"""));
    using var client = new HttpClient();

    var source     = new OpenCodeImportSource(fix.DbPath);
    var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
    var classified = await source.ClassifyAsync(discovered,
        new ClassifyContext(client, server.Url!, MinLines: 1, ExcludedRepos: null, ExcludedPaths: null),
        CancellationToken.None);

    await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);
}

[Test]
public async Task classify_too_short_for_structural_only_session() {
    using var fix = new OpenCodeDbFixture();
    fix.AddSession("ses_x", null, "/w", "T", 100);
    // A message whose only part is structural — not importable.
    fix.AddStructuralMessage("ses_x", "m1", 100); // add this helper: a step-finish-only assistant msg

    using var server = WireMockServer.Start();
    server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
          .RespondWith(Response.Create().WithStatusCode(404));
    using var client = new HttpClient();

    var source     = new OpenCodeImportSource(fix.DbPath);
    var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
    var classified = await source.ClassifyAsync(discovered,
        new ClassifyContext(client, server.Url!, MinLines: 1, ExcludedRepos: null, ExcludedPaths: null),
        CancellationToken.None);

    await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.TooShort);
}
```

Add `AddStructuralMessage` to `OpenCodeDbFixture`:

```csharp
public void AddStructuralMessage(string sid, string msgId, long t) {
    using var c = Open();
    using (var m = c.CreateCommand()) {
        m.CommandText = "INSERT INTO message(id,session_id,time_created,data) VALUES($i,$s,$t,$d)";
        m.Parameters.AddWithValue("$i", msgId); m.Parameters.AddWithValue("$s", sid);
        m.Parameters.AddWithValue("$t", t); m.Parameters.AddWithValue("$d", """{"role":"assistant"}""");
        m.ExecuteNonQuery();
    }
    using (var p = c.CreateCommand()) {
        p.CommandText = "INSERT INTO part(id,message_id,session_id,time_created,data) VALUES($i,$m,$s,$t,$d)";
        p.Parameters.AddWithValue("$i", "prt_" + msgId); p.Parameters.AddWithValue("$m", msgId);
        p.Parameters.AddWithValue("$s", sid); p.Parameters.AddWithValue("$t", t);
        p.Parameters.AddWithValue("$d", """{"type":"step-finish"}""");
        p.ExecuteNonQuery();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/OpenCodeImportSourceTests/*"`
Expected: FAIL — `ClassifyAsync` throws `NotImplementedException`.

- [ ] **Step 3: Implement binary classification**

Replace the `ClassifyAsync` stub in `OpenCodeImportSource.cs`. Add `using System.Net;`, `using System.Text.Json;`, `using Capacitor.Cli.Core;` (for `GetWithRetryAsync`):

```csharp
public async Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
    IReadOnlyList<DiscoveredSession> sessions, ClassifyContext ctx, CancellationToken ct) {
    using var db = new OpenCodeDb(_dbPath);
    var results = new List<ImportCommand.SessionClassification>(sessions.Count);

    foreach (var s in sessions) {
        ct.ThrowIfCancellationRequested();

        var meta = new SessionMetadata {
            SessionId      = s.SessionId,
            Cwd            = s.Cwd,
            FirstTimestamp = s.FirstTimestamp,
            LastTimestamp  = s.SourceMeta!.TryGetValue("TimeUpdated", out var tu) && tu is long tums
                ? DateTimeOffset.FromUnixTimeMilliseconds(tums) : null,
        };

        int importable;
        try {
            importable = db.SynthesizeLines(s.SessionId).Count(OpenCodeDb.IsImportRelevantLine);
        } catch {
            results.Add(Make(s, meta, ImportCommand.ClassificationStatus.ProbeError, 0, "transcript read failed"));
            continue;
        }

        if (importable < ctx.MinLines) {
            results.Add(Make(s, meta, ImportCommand.ClassificationStatus.TooShort, importable));
            continue;
        }

        int? serverLastLine;
        try {
            serverLastLine = await FetchServerLastLineAsync(ctx.HttpClient, ctx.BaseUrl, s.SessionId, ct);
        } catch {
            results.Add(Make(s, meta, ImportCommand.ClassificationStatus.ProbeError, importable, "watermark probe failed"));
            continue;
        }

        // Binary policy: any server watermark → AlreadyLoaded (skip). No line-number resume:
        // live snapshot line space ≠ import final-state line space. See design spec.
        var status = serverLastLine is not null
            ? ImportCommand.ClassificationStatus.AlreadyLoaded
            : ImportCommand.ClassificationStatus.New;

        results.Add(Make(s, meta, status, importable));
    }
    return results;
}

static ImportCommand.SessionClassification Make(
    DiscoveredSession s, SessionMetadata meta, ImportCommand.ClassificationStatus status,
    int totalLines, string? probeError = null) => new() {
    SessionId        = s.SessionId,
    FilePath         = "",   // routed phase
    EncodedCwd       = "",
    Meta             = meta,
    Status           = status,
    Vendor           = "opencode",
    ProbeErrorReason = probeError,
    TotalLines       = totalLines,
    SourceMeta       = s.SourceMeta,
};

static async Task<int?> FetchServerLastLineAsync(HttpClient http, string baseUrl, string sessionId, CancellationToken ct) {
    using var resp = await http.GetWithRetryAsync($"{baseUrl}/api/sessions/{sessionId}/last-line", ct: ct);
    if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
    if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"watermark probe returned {(int)resp.StatusCode}");
    var body = await resp.Content.ReadAsStringAsync(ct);
    using var doc = JsonDocument.Parse(body);
    return doc.RootElement.TryGetProperty("last_line_number", out var ln) && ln.ValueKind == JsonValueKind.Number
        ? ln.GetInt32() : null;
}
```

**Implementer note:** confirm `SessionMetadata`'s property names (`SessionId`, `Cwd`, `FirstTimestamp`, `LastTimestamp`) against `PiImportSource`'s usage — copy exactly what compiles there.

- [ ] **Step 4: Run to verify it passes**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/OpenCodeImportSourceTests/*"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/OpenCodeImportSource.cs test/Capacitor.Cli.Tests.Unit/OpenCodeImportSourceTests.cs test/Capacitor.Cli.Tests.Unit/OpenCodeDbFixture.cs
git commit -m "feat: OpenCodeImportSource binary classification (New/AlreadyLoaded/TooShort)"
```

---

## Task 6: `ImportSessionAsync` — parent lifecycle + transcript + set-title

**Files:**
- Modify: `src/Capacitor.Cli/Commands/OpenCodeImportSource.cs`
- Test: `test/Capacitor.Cli.Tests.Integration/OpenCodeImportSourceImportTests.cs`

- [ ] **Step 1: Write the failing integration test**

Create `test/Capacitor.Cli.Tests.Integration/OpenCodeImportSourceImportTests.cs` (model on `PiImportSourceImportTests.cs`; copy a local fixture builder analogous to `OpenCodeDbFixture` into the integration project, or move the fixture to a shared test helper project if one exists):

```csharp
using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

public class OpenCodeImportSourceImportTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();
    readonly OpenCodeDbFixtureIt _fix = new();

    public void Dispose() { _server.Stop(); _fix.Dispose(); }

    [Test]
    public async Task ImportSession_posts_parent_lifecycle_transcript_and_title() {
        _fix.AddSession("ses_root", null, "/work/a", "Repo overview", 1782241513759);
        _fix.AddMessageWithText("ses_root", "msg_1", "hello", 1782241513760);

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(404));
        foreach (var p in new[] { "/hooks/session-start/opencode", "/hooks/transcript",
                                  "/hooks/set-title", "/hooks/session-end/opencode" })
            _server.Given(Request.Create().WithPath(p).UsingPost())
                   .RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();
        var source     = new OpenCodeImportSource(_fix.DbPath);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered,
            new ClassifyContext(client, _server.Url!, 0, null, null), CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);

        var outcome = await source.ImportSessionAsync(classified[0],
            new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(ImportOutcome.Loaded);

        var logs = _server.LogEntries.Select(e => e.RequestMessage.Path).ToList();
        await Assert.That(logs).Contains("/hooks/session-start/opencode");
        await Assert.That(logs).Contains("/hooks/transcript");
        await Assert.That(logs).Contains("/hooks/set-title");
        await Assert.That(logs).Contains("/hooks/session-end/opencode");
    }
}
```

(Define `OpenCodeDbFixtureIt` in the integration project with the same builder methods as `OpenCodeDbFixture` from Task 4.)

- [ ] **Step 2: Run to verify it fails**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj --treenode-filter "/*/*/OpenCodeImportSourceImportTests/*"`
Expected: FAIL — `ImportSessionAsync` throws `NotImplementedException`.

- [ ] **Step 3: Implement parent import (no children yet)**

Replace the `ImportSessionAsync` stub. Add `using System.Text;`, `using System.Text.Json.Nodes;`:

```csharp
public async Task<ImportOutcome> ImportSessionAsync(
    ImportCommand.SessionClassification c, ImportContext ctx, CancellationToken ct) {
    if (c.Status == ImportCommand.ClassificationStatus.AlreadyLoaded) return ImportOutcome.Skipped;

    var title = c.SourceMeta!.TryGetValue("Title", out var t) ? t as string : null;

    // 1. session-start (lifecycle-before-transcript; idempotent server-side).
    if (!await PostHookAsync(ctx.HttpClient, ctx.BaseUrl, "session-start/opencode",
            BuildSessionStartPayload(c.SessionId, c.Meta.Cwd, c.Meta.FirstTimestamp, ctx.ForcePrivate), ct))
        return ImportOutcome.Failed;

    // 2. parent transcript (synthesize to a temp file; SendTranscriptBatches needs a path).
    int sent;
    var tmpFile = Path.Combine(Path.GetTempPath(), $"kcap-oc-{c.SessionId}-{Guid.NewGuid():N}.jsonl");
    try {
        using var db = new OpenCodeDb(_dbPath);
        await using (var w = new StreamWriter(tmpFile)) {
            foreach (var line in db.SynthesizeLines(c.SessionId)) await w.WriteLineAsync(line);
        }
        sent = await SessionImporter.SendTranscriptBatches(
            httpClient: ctx.HttpClient, baseUrl: ctx.BaseUrl, sessionId: c.SessionId,
            filePath: tmpFile, agentId: null, startLine: 0, vendor: Vendor);
    } catch {
        return ImportOutcome.Failed;
    } finally {
        try { File.Delete(tmpFile); } catch { }
    }

    // 3. (children go here in Task 7 — before session-end)

    // 4. native title (best-effort, like Copilot/Kiro).
    if (!string.IsNullOrWhiteSpace(title))
        await PostSetTitleAsync(ctx.HttpClient, ctx.BaseUrl, c.SessionId, title!, ct);

    // 5. session-end.
    if (!await PostHookAsync(ctx.HttpClient, ctx.BaseUrl, "session-end/opencode",
            BuildSessionEndPayload(c.SessionId, c.Meta.Cwd, c.Meta.LastTimestamp), ct))
        return ImportOutcome.Failed;

    return sent == 0 ? ImportOutcome.Skipped : ImportOutcome.Loaded;
}

static JsonObject BuildSessionStartPayload(string sid, string? cwd, DateTimeOffset? startedAt, bool forcePrivate) {
    var p = new JsonObject {
        ["hook_event_name"] = "sessionStart",
        ["session_id"]      = sid,
        ["source"]          = "startup",
    };
    if (cwd is not null) p["cwd"] = cwd;
    if (startedAt is { } ts) p["started_at"] = ts.ToString("O");
    if (forcePrivate) p["default_visibility"] = "private";
    return p;
}

static JsonObject BuildSessionEndPayload(string sid, string? cwd, DateTimeOffset? endedAt) {
    var p = new JsonObject {
        ["hook_event_name"] = "sessionEnd",
        ["session_id"]      = sid,
        ["reason"]          = "opencode-import",
    };
    if (cwd is not null) p["cwd"] = cwd;
    if (endedAt is { } ts) p["ended_at"] = ts.ToString("O");
    return p;
}

static async Task<bool> PostHookAsync(HttpClient client, string baseUrl, string route, JsonObject payload, CancellationToken ct) {
    try {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await client.PostWithRetryAsync($"{baseUrl}/hooks/{route}", content, ct: ct);
        return resp.IsSuccessStatusCode;
    } catch { return false; }
}

static async Task PostSetTitleAsync(HttpClient client, string baseUrl, string sid, string title, CancellationToken ct) {
    if (title.Length > 120) title = title[..120];
    var payload = new JsonObject { ["session_id"] = sid, ["title"] = title };
    try {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var _ = await client.PostWithRetryAsync($"{baseUrl}/hooks/set-title", content, ct: ct);
    } catch { /* best effort */ }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj --treenode-filter "/*/*/OpenCodeImportSourceImportTests/*"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/OpenCodeImportSource.cs test/Capacitor.Cli.Tests.Integration/OpenCodeImportSourceImportTests.cs
git commit -m "feat: OpenCodeImportSource parent import (lifecycle + transcript + title)"
```

---

## Task 7: `ImportSessionAsync` — subagent children (before session-end)

**Files:**
- Modify: `src/Capacitor.Cli/Commands/OpenCodeImportSource.cs`
- Test: `test/Capacitor.Cli.Tests.Integration/OpenCodeImportSourceImportTests.cs`

- [ ] **Step 1: Write the failing integration test**

Add to `OpenCodeImportSourceImportTests.cs`:

```csharp
[Test]
public async Task ImportSession_routes_children_as_subagents_before_session_end() {
    _fix.AddSession("ses_root", null, "/work/a", "Parent", 100);
    _fix.AddMessageWithText("ses_root", "msg_p", "parent says hi", 110);
    _fix.AddSession("ses_kid", "ses_root", "/work/a", "Child", 120);
    _fix.AddMessageWithTextAndAgent("ses_kid", "msg_c", "child work", 130, agent: "general");

    _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
           .RespondWith(Response.Create().WithStatusCode(404));
    foreach (var p in new[] { "/hooks/session-start/opencode", "/hooks/transcript", "/hooks/set-title",
                              "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-end/opencode" })
        _server.Given(Request.Create().WithPath(p).UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200));

    using var client = new HttpClient();
    var source     = new OpenCodeImportSource(_fix.DbPath);
    var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
    await Assert.That(discovered.Select(d => d.SessionId)).IsEquivalentTo(new[] { "ses_root" }); // child excluded
    var classified = await source.ClassifyAsync(discovered,
        new ClassifyContext(client, _server.Url!, 0, null, null), CancellationToken.None);

    var outcome = await source.ImportSessionAsync(classified[0],
        new ImportContext(client, _server.Url!, false), CancellationToken.None);
    await Assert.That(outcome).IsEqualTo(ImportOutcome.Loaded);

    // Order: subagent-start/stop must precede session-end.
    var paths = _server.LogEntries
        .OrderBy(e => e.RequestMessage.DateTime)
        .Select(e => e.RequestMessage.Path).ToList();
    await Assert.That(paths).Contains("/hooks/subagent-start");
    await Assert.That(paths).Contains("/hooks/subagent-stop");
    var lastSubagentStop = paths.LastIndexOf("/hooks/subagent-stop");
    var sessionEnd       = paths.IndexOf("/hooks/session-end/opencode");
    await Assert.That(lastSubagentStop < sessionEnd).IsTrue();
}
```

Add `AddMessageWithTextAndAgent` to the integration fixture — same as `AddMessageWithText` but the message `data` includes `"agent":"general"` so `info.agent` resolves the subagent type.

- [ ] **Step 2: Run to verify it fails**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj --treenode-filter "/*/*/OpenCodeImportSourceImportTests/*"`
Expected: FAIL — no subagent POSTs (children not yet imported).

- [ ] **Step 3: Implement subagent import**

In `OpenCodeImportSource.cs`, replace the `// 3. (children go here...)` comment with a call, and add the method. Add `using Capacitor.Cli.Core.OpenCode;` (already present) for `OpenCodeSubagentDiscovery`:

```csharp
    // 3. children as subagents — BEFORE session-end so SubagentCompleted precedes SessionEnded.
    await ImportChildrenAsync(ctx.HttpClient, ctx.BaseUrl, c.SessionId, ct);
```

```csharp
async Task ImportChildrenAsync(HttpClient client, string baseUrl, string rootId, CancellationToken ct) {
    using var db = new OpenCodeDb(_dbPath);
    var children = db.QueryChildren(rootId); // ordered (time_created, id)

    foreach (var child in children) {
        ct.ThrowIfCancellationRequested();
        var agentId   = OpenCodeSubagentDiscovery.CanonicalAgentId(child.Id);
        var agentType = ResolveAgentType(db, child.Id); // info.agent, fallback "subagent"

        // fail-closed: no content unless the subagent registered first.
        var startOk = await PostHookAsync(client, baseUrl, "subagent-start",
            OpenCodeSubagentDiscovery.BuildStartPayload(rootId, agentId, agentType, child.Id), ct);
        if (!startOk) continue;

        var tmp = Path.Combine(Path.GetTempPath(), $"kcap-oc-{child.Id}-{Guid.NewGuid():N}.jsonl");
        try {
            await using (var w = new StreamWriter(tmp)) {
                foreach (var line in db.SynthesizeLines(child.Id)) await w.WriteLineAsync(line);
            }
            await SessionImporter.SendTranscriptBatches(
                httpClient: client, baseUrl: baseUrl, sessionId: rootId,
                filePath: tmp, agentId: agentId, startLine: 0, vendor: Vendor);
        } catch {
            continue; // leave subagent-stop unsent; re-import retries (idempotent)
        } finally {
            try { File.Delete(tmp); } catch { }
        }

        await PostHookAsync(client, baseUrl, "subagent-stop",
            OpenCodeSubagentDiscovery.BuildStopPayload(rootId, agentId, agentType, child.Id), ct);
    }
}

static string ResolveAgentType(OpenCodeDb db, string childId) {
    foreach (var line in db.SynthesizeLines(childId)) {
        try {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("info", out var info) &&
                info.TryGetProperty("agent", out var a) && a.GetString() is { Length: > 0 } agent)
                return agent;
        } catch { }
    }
    return "subagent";
}
```

**Implementer note:** `OpenCodeSubagentDiscovery.BuildStartPayload`/`BuildStopPayload` take `(parentSessionId, agentId, agentType, childTranscriptPath)`. Here there is no child transcript *file* path (we stream from db); pass the child id (or `""`) for the `transcript_path` field — confirm the server only requires the field to be non-null (the live payload uses a real path, but the server's shared subagent handler keys on session_id + agent_id). If the server validates the path, pass the temp file path instead and delete after stop.

- [ ] **Step 4: Run to verify it passes**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj --treenode-filter "/*/*/OpenCodeImportSourceImportTests/*"`
Expected: PASS (both integration tests).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/OpenCodeImportSource.cs test/Capacitor.Cli.Tests.Integration/OpenCodeImportSourceImportTests.cs
git commit -m "feat: OpenCodeImportSource subagent children routed before session-end"
```

---

## Task 8: Wire `--opencode` into VendorSelection + Program.cs

**Files:**
- Modify: `src/Capacitor.Cli/Commands/VendorSelection.cs:15,25-29,40,49`
- Modify: `src/Capacitor.Cli/Program.cs:459-467`
- Test: `test/Capacitor.Cli.Tests.Unit/VendorSelectionTests.cs`

- [ ] **Step 1: Write the failing test**

Create (or extend) `test/Capacitor.Cli.Tests.Unit/VendorSelectionTests.cs`:

```csharp
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class VendorSelectionTests {
    [Test]
    public async Task parses_opencode_flag() {
        var r = VendorSelection.Parse(new[] { "import", "--opencode" });
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Vendors).Contains("opencode");
    }

    [Test]
    public async Task rejects_opencode_prefixed_unknown_flag() {
        var r = VendorSelection.Parse(new[] { "import", "--opencode-foo" });
        await Assert.That(r.HasError).IsTrue();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/VendorSelectionTests/*"`
Expected: FAIL — `--opencode` not recognized (vendors empty), and `--opencode-foo` not rejected.

- [ ] **Step 3: Implement the wiring**

In `VendorSelection.cs`:
- Line 15 `KnownVendorFlags`: append `"--opencode"`.
- In the `switch` (lines 25-29 area): add `case "--opencode": vendors.Add("opencode"); break;`
- In both `--..-` prefix guards (lines 40 and 49): append `|| a.StartsWith("--opencode-")`.

In `Program.cs`, in the `allSources` array (lines 459-467), add after `new PiImportSource(),`:

```csharp
new OpenCodeImportSource(),
```

- [ ] **Step 4: Run to verify it passes + full build**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/VendorSelectionTests/*"`
Expected: PASS.
Run: `~/.dotnet/dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj`
Expected: build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/VendorSelection.cs src/Capacitor.Cli/Program.cs test/Capacitor.Cli.Tests.Unit/VendorSelectionTests.cs
git commit -m "feat: wire --opencode import filter + register OpenCodeImportSource"
```

---

## Task 9: Docs (help text + README)

**Files:**
- Modify: `src/Capacitor.Cli.Core/Resources/help-import.txt:28` (after the `--pi` line)
- Modify: `src/Capacitor.Cli.Core/Resources/help-usage.txt:45`
- Modify: `README.md`

- [ ] **Step 1: help-import.txt**

After the `  --pi                    Import Pi (badlogic/pi-mono) session transcripts` line, add:

```
  --opencode              Import SST OpenCode sessions (~/.local/share/opencode/opencode.db)
```

- [ ] **Step 2: help-usage.txt**

On line 45, change the parenthesized vendor list to include OpenCode:

```
  import [vendor-filters] [options]   Import local sessions (Claude, Codex, Cursor, Copilot, Gemini, Kiro, Pi, OpenCode)
```

- [ ] **Step 3: README.md**

In `## Getting started`, wherever the importable agents are listed, add OpenCode. In the `## CLI commands` → `kcap import` section, add a bullet/line documenting `--opencode` (mirror the `--pi` wording, noting it reads OpenCode's SQLite db). Search the README for "Kiro" and "Pi" to find both spots:

Run to locate: `grep -n "Pi\|Kiro\|OpenCode" README.md`

Add `--opencode` alongside `--pi` in each list, and a sentence in the import section: "OpenCode history is read from its SQLite database (`~/.local/share/opencode/opencode.db`); child sessions are imported as subagents."

- [ ] **Step 4: Verify the binary prints the new help**

Run: `~/.dotnet/dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj && ~/.dotnet/dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- import --help`
Expected: output includes the `--opencode` line.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Resources/help-import.txt src/Capacitor.Cli.Core/Resources/help-usage.txt README.md
git commit -m "docs: document kcap import --opencode (help + README)"
```

---

## Task 10: AOT verification gate

**Files:** none (verification only)

- [ ] **Step 1: AOT-publish the CLI and grep for trimming warnings**

Run: `~/.dotnet/dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}' || echo "NO AOT WARNINGS"`
Expected: `NO AOT WARNINGS`. If any `IL3050`/`IL2026` appear from Microsoft.Data.Sqlite, STOP and report — revisit access strategy per the spec.

- [ ] **Step 2: AOT-publish the daemon (confirm no SQLite leakage)**

Run: `~/.dotnet/dotnet publish src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}' || echo "NO AOT WARNINGS"`
Expected: `NO AOT WARNINGS`.

- [ ] **Step 3: Record binary size delta**

Run: `ls -la src/Capacitor.Cli/bin/Release/net10.0/*/publish/kcap`
Note the size; compare against the pre-change size (from git history / a clean build) and record the delta in the PR description.

- [ ] **Step 4: Re-sign the macOS binary (per CLAUDE.md)**

Run (macOS only): `codesign --force --sign - src/Capacitor.Cli/bin/Release/net10.0/osx-arm64/publish/kcap`

- [ ] **Step 5: Run the full unit + integration suites**

Run:
```bash
~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj
```
Expected: all pass.

- [ ] **Step 6: Manual smoke test against the real db (if available)**

If OpenCode is installed locally with sessions: `~/.dotnet/dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- import --opencode --session <ses_id>` (against a dev/test server) and confirm the session + any subagents appear.

- [ ] **Step 7: Commit (if codesign or any fixups were needed)**

```bash
git commit -am "chore: AOT verification fixups for OpenCode import" --allow-empty
```

---

## Notes for the implementer

- **`SendTranscriptBatches` swallows failed batches** (counts them as sent) — this is shared behavior; we rely on server-side idempotency. Do not "fix" it here (out of scope per the spec).
- **Raw `ses_…` ids** are used verbatim as session ids — no GUID normalization. `CanonicalAgentId` only strips dashes (none in `ses_…`).
- **Grandchildren:** `QueryChildren(rootId)` returns direct children only. Current OpenCode data nests one level; deeper nesting is out of scope (YAGNI) — if encountered, a grandchild simply won't be imported under its grandparent. Do not add handling without evidence OpenCode nests deeper.
- **Confirm against `PiImportSource`** for exact `SessionMetadata` property names and `GetWithRetryAsync`/`PostWithRetryAsync` namespaces before finalizing.
