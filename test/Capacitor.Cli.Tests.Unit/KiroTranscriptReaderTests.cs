using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Kiro;
using Microsoft.Data.Sqlite;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Covers <see cref="KiroTranscriptReader"/>: the pure flatten contract (the
/// JSONL envelope the server's KiroTranscriptNormalizer consumes) and the
/// copy-first/read-only SQLite read + materialize path.
/// </summary>
public class KiroTranscriptReaderTests {
    const string Cid = "5f6c2b1a-9e3d-4a7c-bb12-1c2d3e4f5a6b";

    static string SampleValue => $$"""
        {
          "conversation_id": "{{Cid}}",
          "model": "claude-sonnet-4-6",
          "history": [
            {
              "user": { "content": { "Prompt": { "prompt": "hello" } }, "timestamp": "2026-06-17T10:30:00-07:00" },
              "assistant": { "Response": { "message_id": "m1", "content": "hi there" } },
              "request_metadata": { "request_id": "r1", "model_id": "claude-sonnet-4-6" }
            },
            {
              "user": { "content": { "ToolUseResults": { "tool_use_results": [ { "tool_use_id": "t1", "content": [ { "Text": "42\n" } ], "status": "Success" } ] } }, "timestamp": "2026-06-17T10:31:00-07:00" },
              "assistant": { "ToolUse": { "message_id": "m2", "content": "counting", "tool_uses": [ { "id": "t1", "name": "execute_bash", "args": { "command": "echo 42" } } ] } },
              "request_metadata": { "request_id": "r2", "model_id": "claude-sonnet-4-6" }
            }
          ],
          "valid_history_range": [0, 2]
        }
        """;

    [Test]
    public async Task flatten_emits_session_header_then_one_turn_per_history_entry() {
        var lines = KiroTranscriptReader.FlattenValue(SampleValue, cwdHint: "/work/widgets");

        await Assert.That(lines.Count).IsEqualTo(3); // 1 session header + 2 turns

        var session = JsonNode.Parse(lines[0])!.AsObject();
        await Assert.That(session["type"]!.GetValue<string>()).IsEqualTo("session");
        await Assert.That(session["conversation_id"]!.GetValue<string>()).IsEqualTo(Cid);
        await Assert.That(session["model"]!.GetValue<string>()).IsEqualTo("claude-sonnet-4-6");
        await Assert.That(session["cwd"]!.GetValue<string>()).IsEqualTo("/work/widgets");
        // started_at derives from the first turn's user timestamp.
        await Assert.That(session["started_at"]!.GetValue<string>()).IsEqualTo("2026-06-17T10:30:00-07:00");

        var turn0 = JsonNode.Parse(lines[1])!.AsObject();
        await Assert.That(turn0["type"]!.GetValue<string>()).IsEqualTo("turn");
        await Assert.That(turn0["index"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(turn0["conversation_id"]!.GetValue<string>()).IsEqualTo(Cid);
        await Assert.That(turn0["user"]!["content"]!["Prompt"]!["prompt"]!.GetValue<string>()).IsEqualTo("hello");
        await Assert.That(turn0["assistant"]!["Response"]!["content"]!.GetValue<string>()).IsEqualTo("hi there");
        await Assert.That(turn0["request_metadata"]!["request_id"]!.GetValue<string>()).IsEqualTo("r1");

        var turn1 = JsonNode.Parse(lines[2])!.AsObject();
        await Assert.That(turn1["index"]!.GetValue<int>()).IsEqualTo(1);
        // Kiro carries a turn's tool results on the NEXT turn's user message.
        await Assert.That(turn1["user"]!["content"]!["ToolUseResults"]!["tool_use_results"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(turn1["assistant"]!["ToolUse"]!["tool_uses"]![0]!["id"]!.GetValue<string>()).IsEqualTo("t1");
    }

    [Test]
    public async Task flatten_malformed_blob_returns_empty() {
        await Assert.That(KiroTranscriptReader.FlattenValue("{not json").Count).IsEqualTo(0);
        await Assert.That(KiroTranscriptReader.FlattenValue("[]").Count).IsEqualTo(0);
    }

    [Test]
    public async Task flatten_no_history_emits_only_session_header() {
        var lines = KiroTranscriptReader.FlattenValue("""{"conversation_id":"x","model":"m"}""");
        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(JsonNode.Parse(lines[0])!["type"]!.GetValue<string>()).IsEqualTo("session");
    }

    [Test]
    public async Task reads_and_materializes_conversations_v2_round_trip() {
        using var tmp = new TempDir();
        var dbPath = Path.Combine(tmp.Path, "data.sqlite3");

        CreateV2Db(dbPath, key: "/work/widgets", conversationId: Cid, value: SampleValue,
            createdAt: 1781026200000, updatedAt: 1781026280000);

        var row = KiroTranscriptReader.ReadByConversationId(dbPath, Cid);
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.ConversationId).IsEqualTo(Cid);
        await Assert.That(row.Cwd).IsEqualTo("/work/widgets");

        var all = KiroTranscriptReader.DiscoverAll(dbPath);
        await Assert.That(all.Count).IsEqualTo(1);

        var outPath = Path.Combine(tmp.Path, "out.jsonl");
        var n = KiroTranscriptReader.MaterializeOnce(dbPath, Cid, cwd: "/work/widgets", outPath);

        await Assert.That(n).IsEqualTo(3);
        await Assert.That((await File.ReadAllLinesAsync(outPath)).Length).IsEqualTo(3);

        // Append-only + idempotent: re-materializing the unchanged conversation
        // neither grows the file nor rewrites it.
        var n2 = KiroTranscriptReader.MaterializeOnce(dbPath, Cid, cwd: "/work/widgets", outPath);
        await Assert.That(n2).IsEqualTo(3);
        await Assert.That((await File.ReadAllLinesAsync(outPath)).Length).IsEqualTo(3);
    }

    [Test]
    public async Task reads_from_legacy_conversations_table_by_embedded_id() {
        using var tmp = new TempDir();
        var dbPath = Path.Combine(tmp.Path, "data.sqlite3");

        CreateLegacyDb(dbPath, key: "/work/widgets", value: SampleValue);

        var row = KiroTranscriptReader.ReadByConversationId(dbPath, Cid, cwd: "/work/widgets");
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.ConversationId).IsEqualTo(Cid);
    }

    [Test]
    public async Task missing_db_yields_no_rows_and_no_throw() {
        var missing = Path.Combine(Path.GetTempPath(), $"kcap-kiro-missing-{Guid.NewGuid():N}.sqlite3");
        await Assert.That(KiroTranscriptReader.ReadByConversationId(missing, Cid)).IsNull();
        await Assert.That(KiroTranscriptReader.DiscoverAll(missing).Count).IsEqualTo(0);
    }

    static void CreateV2Db(string dbPath, string key, string conversationId, string value, long createdAt, long updatedAt) {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        Exec(conn, "CREATE TABLE conversations_v2 (key TEXT, conversation_id TEXT, value TEXT, created_at INTEGER, updated_at INTEGER);");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO conversations_v2 (key, conversation_id, value, created_at, updated_at) VALUES ($k,$c,$v,$ca,$ua);";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$c", conversationId);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.Parameters.AddWithValue("$ca", createdAt);
        cmd.Parameters.AddWithValue("$ua", updatedAt);
        cmd.ExecuteNonQuery();
    }

    static void CreateLegacyDb(string dbPath, string key, string value) {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        Exec(conn, "CREATE TABLE conversations (key TEXT PRIMARY KEY, value TEXT);");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO conversations (key, value) VALUES ($k,$v);";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    static void Exec(SqliteConnection conn, string sql) {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kcap-kiro-reader-test-{Guid.NewGuid().ToString("N")[..8]}"
        );
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
