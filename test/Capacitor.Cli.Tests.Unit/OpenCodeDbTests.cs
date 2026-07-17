using Capacitor.Cli.Commands;
using Microsoft.Data.Sqlite;

namespace Capacitor.Cli.Tests.Unit;

public class OpenCodeDbTests {
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
    public async Task QueryRoots_returns_only_parentless_sessions() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);
        InsertSession(db, "ses_root1", null, "/work/a", "Root one", 1782241513759);
        InsertSession(db, "ses_root2", "",   "/work/b", "Root two", 1782241513760);
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

    // ── QueryDescendants (AI-1383 D3: recursive grandchild discovery) ──────────────────────

    [Test]
    public async Task QueryDescendants_walks_a_multi_level_parent_chain() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);
        InsertSession(db, "ses_root", null, "/w", "Root", 100);
        InsertSession(db, "ses_child", "ses_root", "/w", "Child", 200);
        InsertSession(db, "ses_grandchild", "ses_child", "/w", "Grandchild", 300);

        using var ocdb = new OpenCodeDb(db);
        var (descendants, omitted, omittedIds) = ocdb.QueryDescendants("ses_root");

        await Assert.That(omitted).IsEqualTo(0);
        await Assert.That(omittedIds.Count).IsEqualTo(0);
        await Assert.That(descendants.Select(d => (d.Row.Id, d.Depth)))
            .IsEquivalentTo(new[] { ("ses_child", 1), ("ses_grandchild", 2) });
    }

    [Test]
    public async Task QueryDescendants_depth_8_imports_depth_9_is_omitted_with_diagnostic() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);
        InsertSession(db, "ses_root", null, "/w", "Root", 100);

        // A 9-level chain below the root: n1 (depth 1) ... n9 (depth 9).
        var prev = "ses_root";
        for (var depth = 1; depth <= 9; depth++) {
            var id = $"ses_n{depth}";
            InsertSession(db, id, prev, "/w", $"N{depth}", 100 + depth);
            prev = id;
        }

        using var ocdb = new OpenCodeDb(db);
        var (descendants, omitted, omittedIds) = ocdb.QueryDescendants("ses_root");

        // Depths 1..8 import; depth 9 is omitted, not imported, not promoted.
        await Assert.That(descendants.Select(d => d.Row.Id).OrderBy(x => x))
            .IsEquivalentTo(Enumerable.Range(1, 8).Select(i => $"ses_n{i}"));
        await Assert.That(descendants.Any(d => d.Row.Id == "ses_n9")).IsFalse();
        await Assert.That(descendants.Max(d => d.Depth)).IsEqualTo(8);
        await Assert.That(omitted).IsEqualTo(1);
        await Assert.That(omittedIds).IsEquivalentTo(new[] { "ses_n9" });
    }

    // AI-1383 D3 review fix #3: the walker used to stop AT the boundary child (depth 9) and
    // never look below it, so a chain continuing to depth 10 was still counted as ONE omitted
    // descendant — undercounting the true size of the omitted subtree. The walk must now
    // continue (never importing) below the cap to count the WHOLE subtree.
    [Test]
    public async Task QueryDescendants_depth_9_and_10_chain_reports_omitted_two_not_one() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);
        InsertSession(db, "ses_root", null, "/w", "Root", 100);

        // A 10-level chain below the root: n1 (depth 1) ... n10 (depth 10).
        var prev = "ses_root";
        for (var depth = 1; depth <= 10; depth++) {
            var id = $"ses_n{depth}";
            InsertSession(db, id, prev, "/w", $"N{depth}", 100 + depth);
            prev = id;
        }

        using var ocdb = new OpenCodeDb(db);
        var (descendants, omitted, omittedIds) = ocdb.QueryDescendants("ses_root");

        // Depths 1..8 import; depths 9 AND 10 are omitted — TWO, not one.
        await Assert.That(descendants.Select(d => d.Row.Id).OrderBy(x => x))
            .IsEquivalentTo(Enumerable.Range(1, 8).Select(i => $"ses_n{i}"));
        await Assert.That(omitted).IsEqualTo(2);
        await Assert.That(omittedIds).IsEquivalentTo(new[] { "ses_n9", "ses_n10" });
    }

    [Test]
    public async Task QueryDescendants_reachable_cycle_terminates_via_visited_set() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);
        // A mutual-parent cycle: X's parent is Y, and Y's parent is X (corrupt data —
        // no acyclic parent_id tree would ever be written this way, but the walker must
        // not spin forever if it's found).
        InsertSession(db, "ses_x", "ses_y", "/w", "X", 100);
        InsertSession(db, "ses_y", "ses_x", "/w", "Y", 200);

        using var ocdb = new OpenCodeDb(db);
        var (descendants, omitted, omittedIds) = ocdb.QueryDescendants("ses_x");

        // Terminates (the awaited assertion below is reachable at all — a hang would time
        // out the test) and does not re-discover ses_x as its own descendant.
        await Assert.That(descendants.Select(d => d.Row.Id)).IsEquivalentTo(new[] { "ses_y" });
        await Assert.That(omitted).IsEqualTo(0);
        await Assert.That(omittedIds.Count).IsEqualTo(0);
    }

    [Test]
    public async Task reads_while_a_wal_writer_holds_uncheckpointed_data() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);

        using var writer = new SqliteConnection($"Data Source={db}");
        writer.Open();
        Exec(writer, "PRAGMA journal_mode=WAL;");
        InsertSession(db, "ses_live", null, "/w", "Live", 100);

        await Assert.That(File.Exists(db + "-wal")).IsTrue();
        await Assert.That(File.Exists(db + "-shm")).IsTrue();

        using var ocdb = new OpenCodeDb(db);
        var roots = ocdb.QueryRoots();

        await Assert.That(roots.Select(r => r.Id)).Contains("ses_live");
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
        InsertMessage(db, "msg_b", "ses_x", 100, """{"role":"user"}""");
        InsertMessage(db, "msg_a", "ses_x", 200, """{"role":"assistant"}""");
        InsertPart(db, "prt_b", "msg_b", "ses_x", 100, """{"type":"text","text":"first"}""");
        InsertPart(db, "prt_a", "msg_a", "ses_x", 200, """{"type":"text","text":"second"}""");

        using var ocdb = new OpenCodeDb(db);
        var roles = ocdb.SynthesizeLines("ses_x")
            .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.GetProperty("info").GetProperty("role").GetString())
            .ToList();

        await Assert.That(string.Join(",", roles)).IsEqualTo("user,assistant");
    }

    [Test]
    public async Task SynthesizeLines_includes_message_with_no_parts() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);
        InsertSession(db, "ses_x", null, "/w", "T", 100);
        InsertMessage(db, "msg_1", "ses_x", 100, """{"role":"assistant"}""");

        using var ocdb = new OpenCodeDb(db);
        var lines = ocdb.SynthesizeLines("ses_x").ToList();

        await Assert.That(lines.Count).IsEqualTo(1);
        var parts = System.Text.Json.JsonDocument.Parse(lines[0]).RootElement.GetProperty("parts");
        await Assert.That(parts.GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task IsImportRelevantLine_matches_server_normalizer_rules() {
        // Importable:
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"user"},"parts":[{"id":"p","type":"text","text":"hi"}]}""")).IsTrue();
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"assistant"},"parts":[{"id":"p","type":"reasoning","text":"thinking"}]}""")).IsTrue();
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"assistant"},"parts":[{"id":"p","type":"tool","callID":"c","tool":"bash","state":{"status":"completed"}}]}""")).IsTrue();

        // NOT importable:
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"assistant"},"parts":[{"type":"step-start"},{"type":"step-finish"}]}""")).IsFalse();
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"user"},"parts":[{"id":"p","type":"text","text":""}]}""")).IsFalse();
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"user"},"parts":[{"id":"p","type":"reasoning","text":"x"}]}""")).IsFalse();
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"user"},"parts":[{"id":"p","type":"tool","callID":"c","tool":"bash","state":{"status":"completed"}}]}""")).IsFalse();
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"assistant"},"parts":[{"id":"p","type":"text","text":"x","synthetic":true}]}""")).IsFalse();
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"assistant"},"parts":[{"id":"p","type":"text","text":"x","ignored":true}]}""")).IsFalse();
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"assistant"},"parts":[{"id":"p","type":"tool","state":{"status":"completed"}}]}""")).IsFalse();
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"assistant"},"parts":[{"id":"p","type":"tool","callID":"c","tool":"bash","state":{"status":"running"}}]}""")).IsFalse();
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"assistant"},"parts":[{"type":"text","text":"no id"}]}""")).IsFalse();
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"assistant"},"parts":[{"id":"p","type":"text","text":" "}]}""")).IsTrue();
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = Directory.CreateTempSubdirectory("kcap-ocdb").FullName;
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
