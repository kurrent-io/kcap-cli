# OpenCode Historical Import (`kcap import --opencode`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `kcap import --opencode`, importing historical OpenCode sessions (including subagents) from OpenCode's SQLite db into Kurrent Capacitor, with no server changes.

**Architecture:** A routed `OpenCodeImportSource : IImportSource` (modeled on `PiImportSource`/`GeminiImportSource`) reads `~/.local/share/opencode/opencode.db` via a CLI-only `OpenCodeDb` helper, reconstructing the live plugin's `{info,parts}` JSONL per message from the `message`/`part` rows. Completeness is tracked **client-side** in a per-server import ledger (the server exposes no completeness signal): a ledger hit → `AlreadyLoaded` (skip); otherwise `New` (no server watermark) or `Partial`-repair (watermark present → replay above the server HWM, idempotent by `prt_` id, since the live/import line spaces are incompatible). A strict transcript send returns `Failed` before any terminal lifecycle POST on a batch failure, so a partial import is never recorded in the ledger and is repaired on re-run. Child sessions are routed as subagents before `session-end`. The ledger is written only after `session-end` succeeds.

**Tech Stack:** .NET 10, NativeAOT, Microsoft.Data.Sqlite + SQLitePCLRaw.bundle_e_sqlite3, TUnit, WireMock.Net.

**Spec:** `docs/superpowers/specs/2026-06-26-opencode-import-design.md` (rev3).

---

## File Structure

| File | Responsibility | Action |
|---|---|---|
| `Directory.Packages.props` | Pin SQLite package versions | Modify |
| `src/Capacitor.Cli/Capacitor.Cli.csproj` | Reference SQLite packages (CLI only, not Core) | Modify |
| `src/Capacitor.Cli/Commands/OpenCodeDb.cs` | Read-only db open; query roots/children; stream-synthesize `{info,parts}` lines; importable-line predicate | Create |
| `src/Capacitor.Cli/Commands/OpenCodeImportLedger.cs` | Per-machine, per-server record of fully-imported sessions (client-side completeness signal) | Create |
| `src/Capacitor.Cli/Commands/OpenCodeImportSource.cs` | `IImportSource`: discover roots, ledger-gated classify, import roots + subagent children + set-title, record ledger | Create |
| `src/Capacitor.Cli/Commands/VendorSelection.cs` | Recognize `--opencode` | Modify |
| `src/Capacitor.Cli/Program.cs` | Register `OpenCodeImportSource` in import sources | Modify |
| `src/Capacitor.Cli.Core/Resources/help-import.txt` | Add `--opencode` filter line | Modify |
| `src/Capacitor.Cli.Core/Resources/help-usage.txt` | Add OpenCode to import one-liner | Modify |
| `README.md` | Quick-start + `kcap import` section | Modify |
| `test/Capacitor.Cli.Tests.Unit/OpenCodeDbTests.cs` | DB reconstruction, ordering, importable predicate | Create |
| `test/Capacitor.Cli.Tests.Unit/OpenCodeImportLedgerTests.cs` | Ledger record/report, server keying, corrupt-file tolerance | Create |
| `test/Capacitor.Cli.Tests.Unit/OpenCodeImportSourceTests.cs` | Discovery (roots vs children), classification | Create |
| `test/Capacitor.Cli.Tests.Unit/VendorSelectionTests.cs` | `--opencode` parsing (create if absent; else extend) | Modify/Create |
| `test/Capacitor.Cli.Tests.Integration/OpenCodeImportSourceImportTests.cs` | Full discover→classify→import wire contract incl. subagents + set-title | Create |

**Reference files to read before starting:** `src/Capacitor.Cli/Commands/PiImportSource.cs` (routed-source shape), `src/Capacitor.Cli/Commands/GeminiImportSource.cs` (subagent import ordering), `src/Capacitor.Cli.Core/OpenCode/OpenCodeSubagentDiscovery.cs` (subagent payload builders), `test/Capacitor.Cli.Tests.Integration/PiImportSourceImportTests.cs` (integration harness).

**Build/test commands (this repo uses `~/.dotnet/dotnet` for .NET 10):**
- Build: `~/.dotnet/dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj`
- Unit tests: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/OpenCodeDbTests/*"`
- AOT publish: `~/.dotnet/dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release`

---

## Task 0: Confirm the server repair contract (prerequisite — no code)

Completeness is tracked **client-side** (the import ledger, Task 4b) — confirmed:
the current server does **not** expose an `ended`/`completed` field on
`/api/sessions/{id}/last-line` (it returns only `{ last_line_number }`), so we do
not depend on one. What the repair path *does* depend on is the server's transcript
ingest semantics. Confirm these against the kcap-server repo (or a dev server) and
record the answers in the PR **before** implementing Task 5–7.

- [ ] **Step 1: Transcript ingest dedupes by canonical id, independent of line number.**

Confirm the `opencode` normalizer + transcript pipeline keep-first per canonical
message/`prt_` id (the live plugin relies on this: "the server keeps the FIRST
append per `prt_` id"). This is what makes offset-above-HWM replay idempotent —
re-sending already-ingested content under new line numbers must NOT duplicate it.
  - **If false:** STOP — offset replay would duplicate content; revisit with the
    user.

- [ ] **Step 2: The HWM filter drops `line_number <= currentHwm` before normalization; higher numbers simply advance the HWM (no contiguity requirement).**

This is why repair offsets line numbers above the probed HWM.

- [ ] **Step 3: `GET /api/sessions/{id}/last-line` returns a watermark usable for repair.**

Per server source, this endpoint scans the last ~50 stream events for the max
`$lineNumber` rather than reading the in-memory HWM. Confirm it returns a
**sufficiently high durable max line** even after trailing lifecycle / summary /
subagent events — i.e. that replaying above it actually lands new content. If it
can under-report the true max line, repair may re-ingest a suffix (still idempotent
by canonical id, just slightly wasteful) — note this in the PR.

- [ ] **Step 4: Subsession watermark via `?agentId=`.**

Confirm child subsessions are watermarked by `(parentSessionId, agentId)` and that
`GET /api/sessions/{parentId}/last-line?agentId={childAgentId}` returns the child's
HWM. The live watcher already uses this query; confirm the response shape.

**Record** the four answers in the PR. None of them gate on a server `ended`
signal — completeness lives in the ledger.

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

        // Order-sensitive: c1 (t=210) must precede c2 (t=220).
        await Assert.That(string.Join(",", kids.Select(k => k.Id))).IsEqualTo("ses_c1,ses_c2");
    }

    [Test]
    public async Task reads_while_a_wal_writer_holds_uncheckpointed_data() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);

        // Open a writer in WAL mode and leave an uncommitted-to-main (uncheckpointed) write.
        using var writer = new SqliteConnection($"Data Source={db}");
        writer.Open();
        Exec(writer, "PRAGMA journal_mode=WAL;");
        InsertSession(db, "ses_live", null, "/w", "Live", 100);
        // Do NOT checkpoint — the row lives in the -wal file, mimicking a running OpenCode.

        // Prove the intended condition actually holds: the WAL sidecars exist.
        await Assert.That(File.Exists(db + "-wal")).IsTrue();
        await Assert.That(File.Exists(db + "-shm")).IsTrue();

        using var ocdb = new OpenCodeDb(db); // read-only open must still see committed WAL data
        var roots = ocdb.QueryRoots();

        await Assert.That(roots.Select(r => r.Id)).Contains("ses_live");
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = Directory.CreateTempSubdirectory("kcap-ocdb").FullName;
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
```

**Settled (not "confirm later"):** `OpenCodeDb` uses `Mode=ReadOnly; Cache=Private`. `Cache=Shared` is **not** required and must not be added — it is unrelated to reading a WAL db's sidecars, and a read-only connection reads committed WAL data fine as long as the writer connection stays open (as it does above). If this test ever fails to open the db, that's a real bug to fix, not a reason to flip to shared cache.

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

    // Order-sensitive: chronological (user@100 then assistant@200), NOT lexical msg_a-first.
    await Assert.That(string.Join(",", roles)).IsEqualTo("user,assistant");
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
public async Task IsImportRelevantLine_matches_server_normalizer_rules() {
    // Importable:
    //   user → non-hidden text only
    await Assert.That(OpenCodeDb.IsImportRelevantLine(
        """{"info":{"role":"user"},"parts":[{"id":"p","type":"text","text":"hi"}]}""")).IsTrue();
    //   assistant → non-hidden reasoning or text
    await Assert.That(OpenCodeDb.IsImportRelevantLine(
        """{"info":{"role":"assistant"},"parts":[{"id":"p","type":"reasoning","text":"thinking"}]}""")).IsTrue();
    //   assistant tool → terminal state AND id + callID + tool present
    await Assert.That(OpenCodeDb.IsImportRelevantLine(
        """{"info":{"role":"assistant"},"parts":[{"id":"p","type":"tool","callID":"c","tool":"bash","state":{"status":"completed"}}]}""")).IsTrue();

    // NOT importable:
    //   structural-only assistant
    await Assert.That(OpenCodeDb.IsImportRelevantLine(
        """{"info":{"role":"assistant"},"parts":[{"type":"step-start"},{"type":"step-finish"}]}""")).IsFalse();
    //   empty user text
    await Assert.That(OpenCodeDb.IsImportRelevantLine(
        """{"info":{"role":"user"},"parts":[{"id":"p","type":"text","text":""}]}""")).IsFalse();
    //   user reasoning (only assistant reasoning emits)
    await Assert.That(OpenCodeDb.IsImportRelevantLine(
        """{"info":{"role":"user"},"parts":[{"id":"p","type":"reasoning","text":"x"}]}""")).IsFalse();
    //   user tool (tools require assistant role)
    await Assert.That(OpenCodeDb.IsImportRelevantLine(
        """{"info":{"role":"user"},"parts":[{"id":"p","type":"tool","callID":"c","tool":"bash","state":{"status":"completed"}}]}""")).IsFalse();
    //   hidden text — synthetic:true
    await Assert.That(OpenCodeDb.IsImportRelevantLine(
        """{"info":{"role":"assistant"},"parts":[{"id":"p","type":"text","text":"x","synthetic":true}]}""")).IsFalse();
    //   hidden text — ignored:true
    await Assert.That(OpenCodeDb.IsImportRelevantLine(
        """{"info":{"role":"assistant"},"parts":[{"id":"p","type":"text","text":"x","ignored":true}]}""")).IsFalse();
    //   terminal tool missing callID/tool
    await Assert.That(OpenCodeDb.IsImportRelevantLine(
        """{"info":{"role":"assistant"},"parts":[{"id":"p","type":"tool","state":{"status":"completed"}}]}""")).IsFalse();
    //   non-terminal tool
    await Assert.That(OpenCodeDb.IsImportRelevantLine(
        """{"info":{"role":"assistant"},"parts":[{"id":"p","type":"tool","callID":"c","tool":"bash","state":{"status":"running"}}]}""")).IsFalse();
    //   assistant text/reasoning with MISSING id → skipped by server
    await Assert.That(OpenCodeDb.IsImportRelevantLine(
        """{"info":{"role":"assistant"},"parts":[{"type":"text","text":"no id"}]}""")).IsFalse();
    //   whitespace-only text counts (server uses Length > 0, not IsNullOrWhiteSpace)
    await Assert.That(OpenCodeDb.IsImportRelevantLine(
        """{"info":{"role":"assistant"},"parts":[{"id":"p","type":"text","text":" "}]}""")).IsTrue();
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
/// under the <c>opencode</c> normalizer. The rules are ROLE-AWARE and
/// HIDDEN-AWARE — they mirror
/// <c>Capacitor.Server/Sessions/Canonical/OpenCodeTranscriptNormalizer.cs</c>:
/// <list type="bullet">
///   <item>user → a non-hidden <c>text</c> part with non-empty text;</item>
///   <item>assistant → a non-hidden <c>reasoning</c> or <c>text</c> part with non-empty text;</item>
///   <item>assistant → a <c>tool</c> part with <c>id</c>, <c>callID</c>, <c>tool</c>, and
///         terminal <c>state.status</c> (completed/error).</item>
/// </list>
/// A part is "hidden" when it carries <c>synthetic: true</c> or <c>ignored: true</c>.
/// IMPLEMENTER: re-read the server normalizer before finalizing and add any rule
/// it has that this misses — this predicate must not over-count (it gates TooShort).
/// </summary>
public static bool IsImportRelevantLine(string line) {
    try {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var role = root.TryGetProperty("info", out var info) && info.TryGetProperty("role", out var rl)
            ? rl.GetString() : null;
        if (root.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array) {
            foreach (var p in parts.EnumerateArray()) {
                if (IsHidden(p)) continue;
                var type = p.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (role, type) {
                    // user text: non-empty (server uses Length > 0, so whitespace counts as content).
                    case ("user", "text"):
                        if (HasText(p)) return true;
                        break;
                    // assistant text/reasoning: server skips the part when id is null, then requires Length > 0.
                    case ("assistant", "text"):
                    case ("assistant", "reasoning"):
                        if (HasField(p, "id") && HasText(p)) return true;
                        break;
                    // assistant tool: id + callID + tool present (null-checked, not non-empty) + terminal state.
                    case ("assistant", "tool"):
                        var status = p.TryGetProperty("state", out var st) && st.TryGetProperty("status", out var ss)
                            ? ss.GetString() : null;
                        if (status is "completed" or "error"
                         && HasField(p, "id") && HasField(p, "callID") && HasField(p, "tool"))
                            return true;
                        break;
                }
            }
        }
        return false;
    } catch {
        return false;
    }
}

static bool IsHidden(JsonElement p) =>
    (p.TryGetProperty("synthetic", out var s) && s.ValueKind == JsonValueKind.True) ||
    (p.TryGetProperty("ignored",   out var i) && i.ValueKind == JsonValueKind.True);

// Present as a JSON string — mirrors the server's `part.Str(name)`, which treats only
// string values as present (a numeric/object would be ignored). Empty string counts,
// for exact parity with the server's null-check on the parsed string.
static bool HasField(JsonElement p, string name) =>
    p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String;

// text present, string, length > 0 (matches server's Length > 0 — NOT IsNullOrWhiteSpace).
static bool HasText(JsonElement p) =>
    p.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String && tx.GetString()!.Length > 0;
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

        var source   = new OpenCodeImportSource(tmp.DbPath, tmp.LedgerPath);
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

        var source = new OpenCodeImportSource(tmp.DbPath, tmp.LedgerPath);

        var byCwd = await source.DiscoverAsync(new DiscoveryFilters("/work/b", null, null, 0), CancellationToken.None);
        await Assert.That(byCwd.Select(s => s.SessionId)).IsEquivalentTo(new[] { "ses_b" });

        var bySession = await source.DiscoverAsync(new DiscoveryFilters(null, "ses_a", null, 0), CancellationToken.None);
        await Assert.That(bySession.Select(s => s.SessionId)).IsEquivalentTo(new[] { "ses_a" });
    }

    [Test]
    public async Task IsAvailable_false_when_db_missing() {
        var source = new OpenCodeImportSource(Path.Combine(Path.GetTempPath(), "no-such-kcap.db"),
            ledgerPathOverride: Path.Combine(Path.GetTempPath(), $"kcap-ledger-{Guid.NewGuid():N}.json"));
        await Assert.That(source.IsAvailable).IsFalse();
    }

    [Test]
    public async Task discovery_treats_ms_as_ms_and_seconds_as_seconds() {
        using var tmp = new OpenCodeDbFixture();
        tmp.AddSession("ses_ms",  null, "/w", "ms",  1782241513759); // milliseconds (real OpenCode)
        tmp.AddSession("ses_sec", null, "/w", "sec", 1782241513);    // seconds (hypothetical future column)
        tmp.AddMessageWithText("ses_ms",  "m1", "x", 1782241513759);
        tmp.AddMessageWithText("ses_sec", "m2", "x", 1782241513);

        var source = new OpenCodeImportSource(tmp.DbPath, tmp.LedgerPath);
        var byId = (await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None))
            .ToDictionary(s => s.SessionId, s => s.FirstTimestamp);

        await Assert.That(byId["ses_ms"]).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1782241513759));
        await Assert.That(byId["ses_sec"]).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(1782241513));
    }

    [Test]
    public async Task discovery_handles_null_directory_and_excludes_it_under_cwd_filter() {
        using var tmp = new OpenCodeDbFixture();
        tmp.AddSession("ses_nodir", null, dir: null, "No dir", 100);
        tmp.AddMessageWithText("ses_nodir", "m1", "x", 100);

        var source = new OpenCodeImportSource(tmp.DbPath, tmp.LedgerPath);

        var all = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        await Assert.That(all.Single().Cwd).IsNull();

        // A --cwd filter cannot match a null directory → excluded.
        var filtered = await source.DiscoverAsync(new DiscoveryFilters("/work/a", null, null, 0), CancellationToken.None);
        await Assert.That(filtered.Count).IsEqualTo(0);
    }
}
```

Also create the shared fixture `test/Capacitor.Cli.Tests.Unit/OpenCodeDbFixture.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>Builds a throwaway OpenCode-shaped SQLite db for import tests.</summary>
internal sealed class OpenCodeDbFixture : IDisposable {
    public string Dir        { get; } = Directory.CreateTempSubdirectory("kcap-ocfix").FullName;
    public string DbPath     => Path.Combine(Dir, "opencode.db");
    public string LedgerPath => Path.Combine(Dir, "ledger.json"); // isolates each test from the real ~/.cache ledger

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

    // dir is nullable so tests can cover a session with no directory (DBNull).
    public void AddSession(string id, string? parent, string? dir, string title, long t) {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO session(id,parent_id,directory,title,version,time_created,time_updated) VALUES($i,$p,$d,$t,'1.17',$tc,$tc)";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$p", (object?)parent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", (object?)dir ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$tc", t);
        cmd.ExecuteNonQuery();
    }

    // Build JSON with JsonObject so quoting/escaping is correct and readable.
    public void AddMessageWithText(string sid, string msgId, string text, long t, string? agent = null) {
        var info = new System.Text.Json.Nodes.JsonObject {
            ["role"] = "user",
            ["time"] = new System.Text.Json.Nodes.JsonObject { ["created"] = t },
        };
        if (agent is not null) info["agent"] = agent;
        var part = new System.Text.Json.Nodes.JsonObject {
            ["id"] = "prt_" + msgId, ["type"] = "text", ["text"] = text,
        };
        InsertRaw(sid, msgId, t, info.ToJsonString(), "prt_" + msgId, part.ToJsonString());
    }

    // Convenience used by subagent tests: message carries info.agent.
    public void AddMessageWithTextAndAgent(string sid, string msgId, string text, long t, string agent) =>
        AddMessageWithText(sid, msgId, text, t, agent);

    void InsertRaw(string sid, string msgId, long t, string msgData, string partId, string partData) {
        using var c = Open();
        using (var m = c.CreateCommand()) {
            m.CommandText = "INSERT INTO message(id,session_id,time_created,data) VALUES($i,$s,$t,$d)";
            m.Parameters.AddWithValue("$i", msgId); m.Parameters.AddWithValue("$s", sid);
            m.Parameters.AddWithValue("$t", t); m.Parameters.AddWithValue("$d", msgData);
            m.ExecuteNonQuery();
        }
        using (var p = c.CreateCommand()) {
            p.CommandText = "INSERT INTO part(id,message_id,session_id,time_created,data) VALUES($i,$m,$s,$t,$d)";
            p.Parameters.AddWithValue("$i", partId); p.Parameters.AddWithValue("$m", msgId);
            p.Parameters.AddWithValue("$s", sid); p.Parameters.AddWithValue("$t", t);
            p.Parameters.AddWithValue("$d", partData);
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
    readonly string                 _dbPath;
    readonly OpenCodeImportLedger   _ledger;
    readonly object                 _ledgerLock = new(); // routed imports may run concurrently

    public OpenCodeImportSource(string? dbPathOverride = null, string? ledgerPathOverride = null) {
        _dbPath = dbPathOverride ?? Path.Combine(OpenCodePaths.DataDir(), "opencode.db");
        _ledger = OpenCodeImportLedger.Load(ledgerPathOverride ?? OpenCodeImportLedger.DefaultPath());
    }

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
                FirstTimestamp: FromEpoch(row.TimeCreated),
                SourceMeta:     new Dictionary<string, object?> {
                    ["Title"]       = row.Title,
                    ["TimeUpdated"] = row.TimeUpdated,
                }));
        }
        return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);
    }

    // OpenCode stores epoch MILLISECONDS (observed ~1.78e12). Guard against a future
    // seconds-based column: a value too small to be plausible ms is read as seconds.
    static DateTimeOffset FromEpoch(long v) =>
        v < 100_000_000_000L   // < ~1973-03 expressed in ms ⇒ the value must be seconds
            ? DateTimeOffset.FromUnixTimeSeconds(v)
            : DateTimeOffset.FromUnixTimeMilliseconds(v);

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

## Task 4b: OpenCode import ledger (client-side completeness record)

**Why:** the server exposes no completeness signal, so without a ledger every
re-run would re-send every already-imported session (correct via dedup, but
wasteful). The ledger records a session as fully imported **only after its
`session-end` posts** (which, by the strict ordering, means the parent transcript
**and** all children succeeded). On re-run, a ledger hit → `AlreadyLoaded` (skip).
A partial/failed import never reaches `session-end`, so it is never recorded and is
repaired normally. **Caveat (documented):** the ledger is per-machine and trusts
local state — if a recorded session is later deleted server-side, a re-run will
wrongly skip it. The ledger is keyed by **server URL** so it never claims a session
is loaded on a server that doesn't have it.

**Files:**
- Create: `src/Capacitor.Cli/Commands/OpenCodeImportLedger.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/OpenCodeImportLedgerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class OpenCodeImportLedgerTests {
    [Test]
    public async Task records_and_reports_complete_only_for_matching_line_count_and_server() {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "ledger.json");
        const string url = "https://srv.example";

        var ledger = OpenCodeImportLedger.Load(path);
        await Assert.That(ledger.IsComplete(url, "ses_x", lineCount: 10)).IsFalse();

        ledger.MarkComplete(url, "ses_x", lineCount: 10);
        ledger.Save();

        var reloaded = OpenCodeImportLedger.Load(path);
        // Same id + same line count + same server → complete.
        await Assert.That(reloaded.IsComplete(url, "ses_x", 10)).IsTrue();
        // Grew since import (session continued) → not complete, re-import.
        await Assert.That(reloaded.IsComplete(url, "ses_x", 11)).IsFalse();
        // Different server → not complete.
        await Assert.That(reloaded.IsComplete("https://other", "ses_x", 10)).IsFalse();
        // Unknown id → not complete.
        await Assert.That(reloaded.IsComplete(url, "ses_y", 10)).IsFalse();
    }

    [Test]
    public async Task load_tolerates_missing_or_corrupt_file() {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "nope.json");
        await Assert.That(OpenCodeImportLedger.Load(path).IsComplete("u", "s", 1)).IsFalse(); // missing
        await File.WriteAllTextAsync(path, "{ not json");
        await Assert.That(OpenCodeImportLedger.Load(path).IsComplete("u", "s", 1)).IsFalse(); // corrupt → empty
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = Directory.CreateTempSubdirectory("kcap-ledger").FullName;
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/OpenCodeImportLedgerTests/*"`
Expected: FAIL — `OpenCodeImportLedger` not defined.

- [ ] **Step 3: Implement the ledger (AOT-safe JSON via source-generated context)**

This repo is NativeAOT — use a `JsonSerializerContext`, never reflection-based
`JsonSerializer`. Create `src/Capacitor.Cli/Commands/OpenCodeImportLedger.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Per-machine record of OpenCode sessions fully imported to a given server, used
/// to skip already-loaded sessions on re-run (the server exposes no completeness
/// signal). Shape: { "<serverUrl>": { "<sessionId>": <reconstructedLineCount> } }.
/// Keyed by server URL so it never claims a session is loaded on the wrong server.
/// </summary>
internal sealed class OpenCodeImportLedger {
    readonly string _path;
    readonly Dictionary<string, Dictionary<string, int>> _byServer;

    OpenCodeImportLedger(string path, Dictionary<string, Dictionary<string, int>> data) {
        _path = path; _byServer = data;
    }

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     ".cache", "kcap", "opencode-imported.json");

    public static OpenCodeImportLedger Load(string path) {
        try {
            if (File.Exists(path)) {
                var json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize(json, OpenCodeLedgerJsonContext.Default.DictionaryStringDictionaryStringInt32);
                if (data is not null) return new(path, data);
            }
        } catch { /* missing/corrupt → empty ledger */ }
        return new(path, new(StringComparer.Ordinal));
    }

    public bool IsComplete(string serverUrl, string sessionId, int lineCount) =>
        _byServer.TryGetValue(serverUrl, out var sessions)
        && sessions.TryGetValue(sessionId, out var recorded)
        && recorded == lineCount;

    public void MarkComplete(string serverUrl, string sessionId, int lineCount) {
        if (!_byServer.TryGetValue(serverUrl, out var sessions)) {
            sessions = new(StringComparer.Ordinal);
            _byServer[serverUrl] = sessions;
        }
        sessions[sessionId] = lineCount;
    }

    public void Save() {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(_byServer, OpenCodeLedgerJsonContext.Default.DictionaryStringDictionaryStringInt32);
            File.WriteAllText(_path, json);
        } catch { /* best effort — a ledger write failure must not fail the import */ }
    }
}

[JsonSerializable(typeof(Dictionary<string, Dictionary<string, int>>))]
internal partial class OpenCodeLedgerJsonContext : JsonSerializerContext { }
```

**Implementer note:** confirm the generated property name for the nested dictionary
type on `OpenCodeLedgerJsonContext.Default` (it may differ from
`DictionaryStringDictionaryStringInt32`); use whatever the source generator emits.

- [ ] **Step 4: Run to verify it passes**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/OpenCodeImportLedgerTests/*"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/OpenCodeImportLedger.cs test/Capacitor.Cli.Tests.Unit/OpenCodeImportLedgerTests.cs
git commit -m "feat: OpenCode import ledger (client-side completeness record)"
```

---

## Task 5: `OpenCodeImportSource` — ledger-gated classification

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

    var source     = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath);
    var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
    var classified = await source.ClassifyAsync(discovered,
        new ClassifyContext(client, server.Url!, MinLines: 1, ExcludedRepos: null, ExcludedPaths: null),
        CancellationToken.None);

    await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
    await Assert.That(classified[0].Vendor).IsEqualTo("opencode");
    await Assert.That(classified[0].FilePath).IsEqualTo(""); // routed
}

[Test]
public async Task classify_already_loaded_when_ledger_has_matching_count_for_this_server() {
    using var fix = new OpenCodeDbFixture();
    fix.AddSession("ses_x", null, "/w", "T", 100);
    fix.AddMessageWithText("ses_x", "m1", "hello", 100); // → 1 reconstructed {info,parts} line

    using var server = WireMockServer.Start();
    // Pre-seed the ledger: ses_x fully imported to THIS server, total reconstructed lines = 1.
    var seed = OpenCodeImportLedger.Load(fix.LedgerPath);
    seed.MarkComplete(server.Url!, "ses_x", lineCount: 1);
    seed.Save();
    // Even if probed, the watermark is irrelevant once the ledger hits.
    server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
          .RespondWith(Response.Create().WithStatusCode(404));
    using var client = new HttpClient();

    var source     = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath);
    var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
    var classified = await source.ClassifyAsync(discovered,
        new ClassifyContext(client, server.Url!, MinLines: 1, ExcludedRepos: null, ExcludedPaths: null),
        CancellationToken.None);

    await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);
}

[Test]
public async Task classify_repair_when_not_in_ledger_and_watermark_present() {
    using var fix = new OpenCodeDbFixture();
    fix.AddSession("ses_x", null, "/w", "T", 100);
    fix.AddMessageWithText("ses_x", "m1", "hello", 100);

    using var server = WireMockServer.Start(); // ledger empty (fresh fix)
    server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
          .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":42}"""));
    using var client = new HttpClient();

    var source     = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath);
    var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
    var classified = await source.ClassifyAsync(discovered,
        new ClassifyContext(client, server.Url!, MinLines: 1, ExcludedRepos: null, ExcludedPaths: null),
        CancellationToken.None);

    // Not in ledger + watermark present → Partial repair, carrying the HWM (42).
    await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.Partial);
    await Assert.That(classified[0].ResumeFromLine).IsEqualTo(42);
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

    var source     = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath);
    var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
    var classified = await source.ClassifyAsync(discovered,
        new ClassifyContext(client, server.Url!, MinLines: 1, ExcludedRepos: null, ExcludedPaths: null),
        CancellationToken.None);

    await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.TooShort);
}

[Test]
public async Task classify_too_short_for_zero_message_session() {
    using var fix = new OpenCodeDbFixture();
    fix.AddSession("ses_empty", null, "/w", "Empty", 100); // no messages at all

    using var server = WireMockServer.Start();
    server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
          .RespondWith(Response.Create().WithStatusCode(404));
    using var client = new HttpClient();

    var source     = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath);
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

- [ ] **Step 3: Implement ledger-gated classification**

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
                ? FromEpoch(tums) : null,
        };

        int total, importable;
        try {
            (total, importable) = CountLines(db, s.SessionId);
        } catch {
            results.Add(Make(s, meta, ImportCommand.ClassificationStatus.ProbeError, 0, "transcript read failed"));
            continue;
        }

        if (importable < ctx.MinLines) {
            results.Add(Make(s, meta, ImportCommand.ClassificationStatus.TooShort, total));
            continue;
        }

        // Completeness is tracked client-side (the ledger): a ledger hit on this server
        // with the same reconstructed line count means we already fully imported it.
        lock (_ledgerLock) {
            if (_ledger.IsComplete(ctx.BaseUrl, s.SessionId, total)) {
                results.Add(Make(s, meta, ImportCommand.ClassificationStatus.AlreadyLoaded, total));
                continue;
            }
        }

        // Not in the ledger → New (no server watermark) or Partial-repair (watermark
        // present → replay ABOVE the HWM, idempotent via canonical prt_ ids; the live
        // and import line spaces are incompatible, so we never resume line-by-line).
        int? hwm;
        try {
            hwm = await FetchServerLastLineAsync(ctx.HttpClient, ctx.BaseUrl, s.SessionId, agentId: null, ct);
        } catch {
            results.Add(Make(s, meta, ImportCommand.ClassificationStatus.ProbeError, total, "watermark probe failed"));
            continue;
        }

        var c = hwm is { } h
            ? Make(s, meta, ImportCommand.ClassificationStatus.Partial, total) with { ResumeFromLine = h }
            : Make(s, meta, ImportCommand.ClassificationStatus.New, total);
        results.Add(c);
    }
    return results;
}

/// <summary>Single pass: (total reconstructed lines, importable lines). Total is the
/// ledger key (drift detector); importable gates MinLines.</summary>
static (int Total, int Importable) CountLines(OpenCodeDb db, string sessionId) {
    int total = 0, importable = 0;
    foreach (var line in db.SynthesizeLines(sessionId)) {
        total++;
        if (OpenCodeDb.IsImportRelevantLine(line)) importable++;
    }
    return (total, importable);
}

static async Task<int?> FetchServerLastLineAsync(
    HttpClient http, string baseUrl, string sessionId, string? agentId, CancellationToken ct) {
    var url = $"{baseUrl}/api/sessions/{sessionId}/last-line" + (agentId is not null ? $"?agentId={agentId}" : "");
    using var resp = await http.GetWithRetryAsync(url, ct: ct);
    if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
    if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"watermark probe returned {(int)resp.StatusCode}");
    var body = await resp.Content.ReadAsStringAsync(ct);
    using var doc = JsonDocument.Parse(body);
    return doc.RootElement.TryGetProperty("last_line_number", out var ln) && ln.ValueKind == JsonValueKind.Number
        ? ln.GetInt32() : null;
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

```

**Implementer note:** confirm `SessionMetadata`'s property names (`SessionId`, `Cwd`, `FirstTimestamp`, `LastTimestamp`) against `PiImportSource`'s usage — copy exactly what compiles there.

- [ ] **Step 4: Run to verify it passes**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/OpenCodeImportSourceTests/*"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/OpenCodeImportSource.cs test/Capacitor.Cli.Tests.Unit/OpenCodeImportSourceTests.cs test/Capacitor.Cli.Tests.Unit/OpenCodeDbFixture.cs
git commit -m "feat: OpenCodeImportSource ledger-gated classification (New/AlreadyLoaded/Partial/TooShort)"
```

---

## Task 6: Strict transcript sender + parent lifecycle + transcript + set-title

**Why a strict sender (works with the ledger):** the shared `SessionImporter.PostTranscriptBatch` swallows HTTP failures and counts the batch as sent. A partial multi-batch send may already have advanced the server HWM; returning early does not undo that. The strict sender's job is therefore **not** "leave no watermark" — it's "**don't record the session as complete on failure**": by aborting before any terminal lifecycle POST (`session-end`/`subagent-stop`) — which is also where the ledger is written — the session is **never recorded in the ledger**, so the ledger-gated classifier (Task 5) reclassifies it `Partial` on re-run and **repairs** it (replay above HWM) instead of skipping it as `AlreadyLoaded`. Strict-send + ledger-gated repair are the two halves of one guarantee. We add an opt-in `failOnError` flag (default `false` → peers unchanged; OpenCode passes `true`).

**Files:**
- Modify: `src/Capacitor.Cli/Commands/SessionImporter.cs` (add `failOnError` to `SendTranscriptBatches` + `PostTranscriptBatch`)
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
        var source     = new OpenCodeImportSource(_fix.DbPath, _fix.LedgerPath);
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

    [Test]
    public async Task ImportSession_fails_and_withholds_session_end_when_transcript_rejected() {
        _fix.AddSession("ses_root", null, "/work/a", "T", 100);
        _fix.AddMessageWithText("ses_root", "msg_1", "hello", 110);

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(404));
        _server.Given(Request.Create().WithPath("/hooks/session-start/opencode").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200));
        // Transcript POST is rejected.
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(500));
        _server.Given(Request.Create().WithPath("/hooks/session-end/opencode").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();
        var source     = new OpenCodeImportSource(_fix.DbPath, _fix.LedgerPath);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered,
            new ClassifyContext(client, _server.Url!, 0, null, null), CancellationToken.None);

        var outcome = await source.ImportSessionAsync(classified[0],
            new ImportContext(client, _server.Url!, false), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(ImportOutcome.Failed);
        // session-end must NOT have been posted — so the session is never recorded in
        // the ledger and the next run repairs it instead of skipping it.
        var posted = _server.LogEntries.Select(e => e.RequestMessage.Path).ToList();
        await Assert.That(posted).DoesNotContain("/hooks/session-end/opencode");
    }

    [Test]
    public async Task batch2_failure_then_rerun_repairs_above_hwm() {
        _fix.AddSession("ses_root", null, "/work/a", "T", 100);
        // 150 importable messages → 2 transcript batches (batchSize 100).
        for (var i = 0; i < 150; i++)
            _fix.AddMessageWithText("ses_root", $"msg_{i:D3}", $"line {i}", 100 + i);

        using var client = new HttpClient();
        var ctx       = new ClassifyContext(client, _server.Url!, 0, null, null);
        var importCtx = new ImportContext(client, _server.Url!, false);
        var source    = new OpenCodeImportSource(_fix.DbPath, _fix.LedgerPath);

        // ── Run 1: batch 1 → 200, batch 2 → 500 (WireMock scenario state machine). ──
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(404));
        _server.Given(Request.Create().WithPath("/hooks/session-start/opencode").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost().InScenario("b")
                   .WillSetStateTo("after1")) // first transcript POST
               .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost().InScenario("b")
                   .WhenStateIs("after1"))
               .RespondWith(Response.Create().WithStatusCode(500)); // second batch fails

        var c1 = await source.ClassifyAsync(
            await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None), ctx, CancellationToken.None);
        await Assert.That(c1[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
        await Assert.That(await source.ImportSessionAsync(c1[0], importCtx, CancellationToken.None))
            .IsEqualTo(ImportOutcome.Failed);
        await Assert.That(_server.LogEntries.Select(e => e.RequestMessage.Path))
            .DoesNotContain("/hooks/session-end/opencode"); // not recorded → repairable

        // ── Run 2: server now reports the HWM left by batch 1 (line 99); all POSTs OK. ──
        _server.Reset();
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":99}"""));
        foreach (var p in new[] { "/hooks/session-start/opencode", "/hooks/transcript",
                                  "/hooks/set-title", "/hooks/session-end/opencode" })
            _server.Given(Request.Create().WithPath(p).UsingPost())
                   .RespondWith(Response.Create().WithStatusCode(200));

        var source2 = new OpenCodeImportSource(_fix.DbPath, _fix.LedgerPath);
        var c2 = await source2.ClassifyAsync(
            await source2.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None), ctx, CancellationToken.None);
        await Assert.That(c2[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.Partial); // repair
        await Assert.That(await source2.ImportSessionAsync(c2[0], importCtx, CancellationToken.None))
            .IsEqualTo(ImportOutcome.Resumed);

        // Repaired: session-end posted, and every replayed line number is above the HWM (99).
        await Assert.That(_server.LogEntries.Select(e => e.RequestMessage.Path)).Contains("/hooks/session-end/opencode");
        foreach (var e in _server.LogEntries.Where(e => e.RequestMessage.Path == "/hooks/transcript")) {
            using var doc = System.Text.Json.JsonDocument.Parse(e.RequestMessage.Body!);
            foreach (var n in doc.RootElement.GetProperty("line_numbers").EnumerateArray())
                await Assert.That(n.GetInt32() > 99).IsTrue();
        }
    }
}
```

(Define `OpenCodeDbFixtureIt` in the integration project with the same members as
`OpenCodeDbFixture` from Task 4 — including `Dir`, `DbPath`, **`LedgerPath`**, and
the `Add*` builders — so integration tests are isolated from the real `~/.cache`
ledger too.)

- [ ] **Step 2: Run to verify it fails**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj --treenode-filter "/*/*/OpenCodeImportSourceImportTests/*"`
Expected: FAIL — `ImportSessionAsync` throws `NotImplementedException`.

- [ ] **Step 3: Add opt-in `failOnError` + `lineNumberOffset` to `SessionImporter`**

In `src/Capacitor.Cli/Commands/SessionImporter.cs`, on the **routed** overload
(`SendTranscriptBatches(httpClient, baseUrl, sessionId, filePath, agentId, startLine, progress, vendor)` —
the one Pi/Gemini use), add two optional parameters, both defaulting to keep peer behavior identical:

```csharp
int  lineNumberOffset = 0,   // added to every emitted line number (repair: offset above server HWM)
bool failOnError      = false // strict callers abort the import on a rejected batch
```

- Where it builds line numbers (`batchLineNumbers.Add(lineIndex)`), change to
  `batchLineNumbers.Add(checked(lineIndex + lineNumberOffset))`. The `checked`
  arithmetic guards the (low-risk) case where many repeated repairs push the offset
  toward `int.MaxValue` — it throws `OverflowException` (caught by the strict
  caller → `Failed`) instead of silently wrapping to a negative line number.
- Pass `failOnError` to each `PostTranscriptBatch` call.

Update `PostTranscriptBatch` to accept `bool failOnError = false` and, when `true`, throw instead of swallowing:

```csharp
// in PostTranscriptBatch, replace the existing try/catch:
try {
    using var resp = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/transcript", content);
    if (failOnError && !resp.IsSuccessStatusCode)
        throw new HttpRequestException($"transcript batch rejected: HTTP {(int)resp.StatusCode}");
} catch (HttpRequestException) {
    if (failOnError) throw;   // strict callers (OpenCode) abort the import
    // Default (Pi/Gemini/Kiro/Copilot): log but continue — unchanged behavior.
}
```

Notes:
- `PostWithRetryAsync` currently discards the response (`using var _`). Capture it as `resp` so the status can be checked.
- Both new params are **defaulted**, so every existing caller (including the Claude/Codex overload's internal `PostTranscriptBatch` calls) compiles and behaves exactly as before. Only OpenCode passes non-defaults.

Verify peers still build: `~/.dotnet/dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj`.

- [ ] **Step 4: Implement parent import (no children yet)**

Replace the `ImportSessionAsync` stub. Add `using System.Text;`, `using System.Text.Json.Nodes;`:

```csharp
public async Task<ImportOutcome> ImportSessionAsync(
    ImportCommand.SessionClassification c, ImportContext ctx, CancellationToken ct) {
    if (c.Status == ImportCommand.ClassificationStatus.AlreadyLoaded) return ImportOutcome.Skipped;

    var title  = c.SourceMeta!.TryGetValue("Title", out var t) ? t as string : null;
    var repair = c.Status == ImportCommand.ClassificationStatus.Partial;
    // Repair replays the FULL transcript with line numbers offset above the server
    // HWM (ResumeFromLine), so previously-accepted content dedupes by prt_ id and the
    // gap lands. New imports send from 0. (Line spaces are incompatible, so we never
    // "resume from line N" — we re-send everything, just renumbered above the HWM.)
    var lineOffset = repair ? c.ResumeFromLine + 1 : 0;

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
            filePath: tmpFile, agentId: null, startLine: 0, vendor: Vendor,
            lineNumberOffset: lineOffset, failOnError: true);
    } catch {
        // Strict: abort before session-end. A partial send may have advanced the HWM,
        // but the ledger is written only after session-end — which we never reach — so
        // the session is NOT recorded, and a re-run classifies it Partial and REPAIRS
        // it (replay above HWM) instead of skipping as AlreadyLoaded.
        return ImportOutcome.Failed;
    } finally {
        try { File.Delete(tmpFile); } catch { }
    }

    // 3. (children go here in Task 7 — before session-end)

    // 4. native title (best-effort, like Copilot/Kiro).
    if (!string.IsNullOrWhiteSpace(title))
        await PostSetTitleAsync(ctx.HttpClient, ctx.BaseUrl, c.SessionId, title!, ct);

    // 5. session-end (posted only after parent transcript + all children succeeded).
    if (!await PostHookAsync(ctx.HttpClient, ctx.BaseUrl, "session-end/opencode",
            BuildSessionEndPayload(c.SessionId, c.Meta.Cwd, c.Meta.LastTimestamp), ct))
        return ImportOutcome.Failed;

    // 6. Record completeness in the ledger — ONLY now, after session-end succeeded
    //    (so a partial/failed import is never recorded and is repaired on re-run).
    //    Keyed by server URL + the reconstructed line count (TotalLines) for drift.
    lock (_ledgerLock) {
        _ledger.MarkComplete(ctx.BaseUrl, c.SessionId, c.TotalLines);
        _ledger.Save();
    }

    return repair ? ImportOutcome.Resumed : (sent == 0 ? ImportOutcome.Skipped : ImportOutcome.Loaded);
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

- [ ] **Step 5: Run to verify both tests pass**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj --treenode-filter "/*/*/OpenCodeImportSourceImportTests/*"`
Expected: PASS (both the happy-path and the strict-failure test).

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli/Commands/SessionImporter.cs src/Capacitor.Cli/Commands/OpenCodeImportSource.cs test/Capacitor.Cli.Tests.Integration/OpenCodeImportSourceImportTests.cs
git commit -m "feat: OpenCode parent import + opt-in strict transcript sender"
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
    var source     = new OpenCodeImportSource(_fix.DbPath, _fix.LedgerPath);
    var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
    await Assert.That(discovered.Select(d => d.SessionId)).IsEquivalentTo(new[] { "ses_root" }); // child excluded
    var classified = await source.ClassifyAsync(discovered,
        new ClassifyContext(client, _server.Url!, 0, null, null), CancellationToken.None);

    var outcome = await source.ImportSessionAsync(classified[0],
        new ImportContext(client, _server.Url!, false), CancellationToken.None);
    await Assert.That(outcome).IsEqualTo(ImportOutcome.Loaded);

    var entries = _server.LogEntries.OrderBy(e => e.RequestMessage.DateTime).ToList();
    var paths   = entries.Select(e => e.RequestMessage.Path).ToList();

    // (a) Full POST order: parent start → (parent transcript) → subagent-start →
    //     (child transcript) → subagent-stop → session-end.
    var startIdx   = paths.IndexOf("/hooks/session-start/opencode");
    var subStart   = paths.IndexOf("/hooks/subagent-start");
    var subStop    = paths.LastIndexOf("/hooks/subagent-stop");
    var endIdx     = paths.IndexOf("/hooks/session-end/opencode");
    await Assert.That(startIdx >= 0 && subStart > startIdx && subStop > subStart && endIdx > subStop).IsTrue();

    // (b) subagent-start carries agent_id (canonical child id) + agent_type from info.agent.
    var startBody = entries.First(e => e.RequestMessage.Path == "/hooks/subagent-start").RequestMessage.Body!;
    await Assert.That(startBody).Contains("\"agent_id\":\"ses_kid\"");
    await Assert.That(startBody).Contains("\"agent_type\":\"general\"");

    // (c) the child transcript batch is tagged vendor=opencode, routed under the child
    //     agentId, AND posted between subagent-start and subagent-stop.
    var childTranscriptIdx = -1;
    for (var i = 0; i < entries.Count; i++) {
        var e = entries[i];
        if (e.RequestMessage.Path == "/hooks/transcript"
         && e.RequestMessage.Body!.Contains("\"opencode\"")
         && e.RequestMessage.Body!.Contains("ses_kid")) { childTranscriptIdx = i; break; }
    }
    await Assert.That(childTranscriptIdx).IsGreaterThan(subStart);
    await Assert.That(childTranscriptIdx).IsLessThan(subStop);
}
```

Add `AddMessageWithTextAndAgent` to the integration fixture — same as `AddMessageWithText` but the message `data` includes `"agent":"general"` so `info.agent` resolves the subagent type. (`CanonicalAgentId("ses_kid")` returns `"ses_kid"` since `ses_…` ids contain no dashes.)

- [ ] **Step 1b: Write the failing repair test (watermark present, not in ledger → re-run repairs above HWM)**

This is the scenario Codex flagged: a prior run left a watermark (HWM=42) but the
session was never recorded in the ledger (the import failed before `session-end`).
The re-run must classify `Partial`, replay the transcript with line numbers
**above 42**, and post `session-end`.

```csharp
[Test]
public async Task rerun_repairs_unrecorded_session_by_replaying_above_hwm() {
    _fix.AddSession("ses_root", null, "/work/a", "T", 100);
    _fix.AddMessageWithText("ses_root", "msg_1", "hello", 110);

    // Watermark present (42), ledger empty → repair.
    _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
           .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":42}"""));
    foreach (var p in new[] { "/hooks/session-start/opencode", "/hooks/transcript",
                              "/hooks/set-title", "/hooks/session-end/opencode" })
        _server.Given(Request.Create().WithPath(p).UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200));

    using var client = new HttpClient();
    var source     = new OpenCodeImportSource(_fix.DbPath, _fix.LedgerPath);
    var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
    var classified = await source.ClassifyAsync(discovered,
        new ClassifyContext(client, _server.Url!, 0, null, null), CancellationToken.None);
    await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.Partial);

    var outcome = await source.ImportSessionAsync(classified[0],
        new ImportContext(client, _server.Url!, false), CancellationToken.None);
    await Assert.That(outcome).IsEqualTo(ImportOutcome.Resumed);

    // (a) every transcript line number is > 42 (replayed above the HWM).
    var body = _server.LogEntries.First(e => e.RequestMessage.Path == "/hooks/transcript").RequestMessage.Body!;
    using var doc = System.Text.Json.JsonDocument.Parse(body);
    foreach (var n in doc.RootElement.GetProperty("line_numbers").EnumerateArray())
        await Assert.That(n.GetInt32() > 42).IsTrue();

    // (b) session-end IS posted on a successful repair (so a later run can skip it).
    await Assert.That(_server.LogEntries.Select(e => e.RequestMessage.Path)).Contains("/hooks/session-end/opencode");
}
```

(Confirm the `TranscriptBatch` JSON property name is `line_numbers` against
`src/Capacitor.Cli.Core/Models.cs` — adjust the assertion if the wire name differs.)

- [ ] **Step 1c: Write the failing ledger test (second run skips without re-sending)**

The whole point of the ledger: a re-run of a fully-imported session must classify
`AlreadyLoaded` and skip — no transcript re-send.

```csharp
[Test]
public async Task second_run_skips_via_ledger_and_does_not_resend() {
    _fix.AddSession("ses_root", null, "/work/a", "T", 100);
    _fix.AddMessageWithText("ses_root", "msg_1", "hello", 110);

    _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
           .RespondWith(Response.Create().WithStatusCode(404)); // run 1: New
    foreach (var p in new[] { "/hooks/session-start/opencode", "/hooks/transcript",
                              "/hooks/set-title", "/hooks/session-end/opencode" })
        _server.Given(Request.Create().WithPath(p).UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200));

    using var client = new HttpClient();
    var ctx       = new ClassifyContext(client, _server.Url!, 0, null, null);
    var importCtx = new ImportContext(client, _server.Url!, false);

    // Run 1 — imports (New → Loaded) and records the ledger on session-end.
    var s1 = new OpenCodeImportSource(_fix.DbPath, _fix.LedgerPath);
    var c1 = await s1.ClassifyAsync(
        await s1.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None), ctx, CancellationToken.None);
    await Assert.That(c1[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
    await Assert.That(await s1.ImportSessionAsync(c1[0], importCtx, CancellationToken.None))
        .IsEqualTo(ImportOutcome.Loaded);

    var transcriptCountAfterRun1 = _server.LogEntries.Count(e => e.RequestMessage.Path == "/hooks/transcript");

    // Run 2 — a FRESH source reloads the ledger from disk → AlreadyLoaded → skip.
    var s2 = new OpenCodeImportSource(_fix.DbPath, _fix.LedgerPath);
    var c2 = await s2.ClassifyAsync(
        await s2.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None), ctx, CancellationToken.None);
    await Assert.That(c2[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);
    await Assert.That(await s2.ImportSessionAsync(c2[0], importCtx, CancellationToken.None))
        .IsEqualTo(ImportOutcome.Skipped);

    // No new transcript POSTs on run 2.
    await Assert.That(_server.LogEntries.Count(e => e.RequestMessage.Path == "/hooks/transcript"))
        .IsEqualTo(transcriptCountAfterRun1);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `~/.dotnet/dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj --treenode-filter "/*/*/OpenCodeImportSourceImportTests/*"`
Expected: FAIL — no subagent POSTs (children not yet imported).

- [ ] **Step 3: Implement subagent import**

In `OpenCodeImportSource.cs`, replace the `// 3. (children go here...)` comment with a guarded call (a child failure must abort BEFORE the parent's `session-end`, same strict rationale as the parent transcript — otherwise the parent watermark would make a re-run skip the unrepaired children):

```csharp
    // 3. children as subagents — BEFORE session-end so SubagentCompleted precedes SessionEnded.
    //    Strict: any child failure aborts the import before session-end, so the parent
    //    is never recorded in the ledger and a re-run repairs the child (replay above
    //    its HWM). A partial child send may advance the child HWM — harmless (deduped).
    try {
        await ImportChildrenAsync(ctx.HttpClient, ctx.BaseUrl, c.SessionId, ct);
    } catch {
        return ImportOutcome.Failed;
    }
```

```csharp
async Task ImportChildrenAsync(HttpClient client, string baseUrl, string rootId, CancellationToken ct) {
    using var db = new OpenCodeDb(_dbPath);
    var children = db.QueryChildren(rootId); // ordered (time_created, id)

    foreach (var child in children) {
        ct.ThrowIfCancellationRequested();
        var agentId   = OpenCodeSubagentDiscovery.CanonicalAgentId(child.Id);
        var agentType = ResolveAgentType(db, child.Id); // info.agent, fallback "subagent"

        // No per-child completeness gate: a fully-imported parent is skipped wholesale
        // via the ledger (parent in ledger ⟹ session-end posted ⟹ all children done), so
        // children are only reached during a (re)import of an incomplete parent. We just
        // offset above the child's HWM so a repair replays above already-ingested content
        // (idempotent by prt_ id); re-sending a complete child within a repair run is
        // harmless. Child watermark is keyed by (rootId, agentId).
        var chwm = await FetchServerLastLineAsync(client, baseUrl, rootId, agentId, ct);
        var childOffset = chwm is { } v ? v + 1 : 0;

        // Synthesize the child transcript to a temp file FIRST — the subagent payload
        // builders require a transcript_path, and we pass this same path to start + stop.
        var tmp = Path.Combine(Path.GetTempPath(), $"kcap-oc-{child.Id}-{Guid.NewGuid():N}.jsonl");
        try {
            await using (var w = new StreamWriter(tmp)) {
                foreach (var line in db.SynthesizeLines(child.Id)) await w.WriteLineAsync(line);
            }

            // fail-closed: no content unless the subagent registered first.
            var startOk = await PostHookAsync(client, baseUrl, "subagent-start",
                OpenCodeSubagentDiscovery.BuildStartPayload(rootId, agentId, agentType, tmp), ct);
            if (!startOk) throw new HttpRequestException($"subagent-start failed for {child.Id}");

            await SessionImporter.SendTranscriptBatches(
                httpClient: client, baseUrl: baseUrl, sessionId: rootId,
                filePath: tmp, agentId: agentId, startLine: 0, vendor: Vendor,
                lineNumberOffset: childOffset, failOnError: true);

            if (!await PostHookAsync(client, baseUrl, "subagent-stop",
                    OpenCodeSubagentDiscovery.BuildStopPayload(rootId, agentId, agentType, tmp), ct))
                throw new HttpRequestException($"subagent-stop failed for {child.Id}");
        } finally {
            try { File.Delete(tmp); } catch { }
        }
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

**Implementer note:** `OpenCodeSubagentDiscovery.BuildStartPayload`/`BuildStopPayload` take `(parentSessionId, agentId, agentType, childTranscriptPath)` and place the path in `transcript_path`/`agent_transcript_path`. We pass the **temp transcript file path** (created above) to both start and stop, and delete it in `finally` only after `subagent-stop` has been posted — so the path is valid for the whole subagent lifecycle, matching the live watcher's contract.

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

- [ ] **Step 1: AOT-publish the CLI for each target RID, capturing logs (no masking)**

The naive `publish | grep || echo "OK"` masks a publish *failure* (non-warning error) as success. Use `pipefail` and check the publish exit code separately, then grep the saved log:

```bash
set -o pipefail
for RID in osx-arm64 linux-x64 win-x64; do
  echo "=== CLI $RID ==="
  ~/.dotnet/dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release -r "$RID" \
    2>&1 | tee "/tmp/kcap-aot-cli-$RID.log"
  test "${PIPESTATUS[0]}" -eq 0 || { echo "PUBLISH FAILED for $RID"; exit 1; }
  if grep -E 'IL[23][01][0-9]{2}' "/tmp/kcap-aot-cli-$RID.log"; then echo "AOT WARNINGS for $RID"; exit 1; fi
  echo "NO AOT WARNINGS for $RID"
done
```

Expected: each RID prints `NO AOT WARNINGS` and the loop exits 0. A publish failure or any `IL3050`/`IL2026` from Microsoft.Data.Sqlite exits non-zero — STOP and report; revisit access strategy per the spec. (Run only the RIDs you can build on this host; note any skipped in the PR.)

- [ ] **Step 2: AOT-publish the daemon (confirm no SQLite leakage into Core's consumer)**

```bash
set -o pipefail
~/.dotnet/dotnet publish src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj -c Release \
  2>&1 | tee /tmp/kcap-aot-daemon.log
test "${PIPESTATUS[0]}" -eq 0 || { echo "DAEMON PUBLISH FAILED"; exit 1; }
if grep -E 'IL[23][01][0-9]{2}' /tmp/kcap-aot-daemon.log; then echo "DAEMON AOT WARNINGS"; exit 1; fi
echo "NO AOT WARNINGS"
```

Expected: `NO AOT WARNINGS` and exit 0 — confirms the SQLite package did not reach the daemon via Core. (A failed publish exits non-zero *before* the grep, so it can never be masked as success.)

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

- **`SendTranscriptBatches` strict path:** the `failOnError`/`lineNumberOffset` params are **opt-in and defaulted** — peers (Pi/Gemini/Kiro/Copilot) keep their exact prior behavior; only OpenCode passes non-defaults. Do not change the shared sender's default path or "fix" the swallowing for other importers (out of scope).
- **Raw `ses_…` ids** are used verbatim as session ids — no GUID normalization. `CanonicalAgentId` only strips dashes (none in `ses_…`).
- **Grandchildren:** `QueryChildren(rootId)` returns direct children only. Current OpenCode data nests one level; deeper nesting is out of scope (YAGNI) — if encountered, a grandchild simply won't be imported under its grandparent. Do not add handling without evidence OpenCode nests deeper.
- **Confirm against `PiImportSource`** for exact `SessionMetadata` property names and `GetWithRetryAsync`/`PostWithRetryAsync` namespaces before finalizing.
- **Import ledger** (`~/.cache/kcap/opencode-imported.json`) is the client-side completeness signal — the server exposes none. It's keyed by server URL and written only after `session-end` succeeds. Caveat: a session deleted server-side after being recorded is wrongly skipped on re-run; a `--reimport`/`--force` bypass is a possible future flag (out of scope). A live-captured session (ingested by the watcher, not import) isn't in the ledger, so its first import is a `Partial` repair (re-sent once, deduped) — `--since` bounds this.
- **All tests must pass a temp `ledgerPathOverride`** (via the fixture's `LedgerPath`) so they never read/write the real `~/.cache` ledger.
