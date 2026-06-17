using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Capacitor.Cli.Core.Kiro;

/// <summary>
/// Reads AWS Kiro CLI conversations out of the SQLite <c>data.sqlite3</c> DB and
/// flattens them into the per-turn JSONL the server's
/// <c>KiroTranscriptNormalizer</c> consumes. Kiro (the rebranded Amazon Q
/// Developer CLI) stores each conversation as a single JSON
/// <c>ConversationState</c> blob in the <c>conversations_v2.value</c> column
/// (legacy fallback <c>conversations.value</c>), NOT an append-only JSONL — so
/// there is nothing to tail. This reader is the bridge: the live watcher polls
/// the DB and materializes the flattened lines to a kcap-owned file (which the
/// shared file-tail drain loop then streams), and <c>kcap import --kiro</c>
/// flattens historical rows through the very same path.
///
/// <para>The flattened envelope (one <c>session</c> header + one <c>turn</c> per
/// <c>history[]</c> entry) is the contract with the server normalizer — keep it
/// in sync with <c>KiroTranscriptNormalizer</c>:</para>
/// <code>
/// {"type":"session","conversation_id":…,"cwd":…,"model":…,"started_at":…}
/// {"type":"turn","conversation_id":…,"index":0,"user":{…},"assistant":{…},"request_metadata":{…}}
/// </code>
///
/// <para>The DB is opened <b>copy-first, read-only</b> (WAL/SHM copied alongside)
/// so polling never contends with the live <c>kiro-cli</c> writer.</para>
/// </summary>
public static class KiroTranscriptReader {
    static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>One conversation row, vendor-agnostic of how it was located.</summary>
    public sealed record ConversationRow(string ConversationId, string? Cwd, string ValueJson, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt);

    // ── flattening (pure — no SQLite; unit-tested directly) ────────────────

    /// <summary>
    /// Flattens a single <c>ConversationState</c> <paramref name="valueJson"/>
    /// blob into ordered JSONL lines: a <c>session</c> header followed by one
    /// <c>turn</c> per <c>history[]</c> entry (each carrying the entry's verbatim
    /// <c>user</c> / <c>assistant</c> / <c>request_metadata</c> objects). Returns
    /// an empty list on a malformed blob — fail-open, matching the server
    /// normalizer (Kiro's schema is undocumented and ships frequently).
    /// </summary>
    public static List<string> FlattenValue(string valueJson, string? conversationIdHint = null, string? cwdHint = null) {
        var lines = new List<string>();

        JsonNode? root;
        try { root = JsonNode.Parse(valueJson); }
        catch { return lines; }

        if (root is not JsonObject obj) return lines;

        var conversationId = (obj["conversation_id"]?.GetValue<string>()) ?? conversationIdHint;
        var model          = obj["model"]?.GetValue<string>();

        var history = obj["history"] as JsonArray;

        // started_at proxy: the first turn's user timestamp, else null (the
        // caller may inject the DB created_at via cwdHint/header below).
        string? startedAt = null;
        if (history is { Count: > 0 } && history[0] is JsonObject first0)
            startedAt = first0["user"]?["timestamp"]?.GetValue<string>();

        var header = new JsonObject { ["type"] = "session" };
        if (conversationId is not null) header["conversation_id"] = conversationId;
        if (cwdHint is not null) header["cwd"] = cwdHint;
        if (model is not null) header["model"] = model;
        if (startedAt is not null) header["started_at"] = startedAt;
        lines.Add(header.ToJsonString(Compact));

        if (history is null) return lines;

        for (var i = 0; i < history.Count; i++) {
            if (history[i] is not JsonObject entry) continue;

            var turn = new JsonObject { ["type"] = "turn" };
            if (conversationId is not null) turn["conversation_id"] = conversationId;
            turn["index"] = i;

            // DeepClone: a JsonNode can only live in one parent tree.
            if (entry["user"] is { } user) turn["user"] = user.DeepClone();
            if (entry["assistant"] is { } assistant) turn["assistant"] = assistant.DeepClone();
            if (entry["request_metadata"] is { } meta) turn["request_metadata"] = meta.DeepClone();

            lines.Add(turn.ToJsonString(Compact));
        }

        return lines;
    }

    /// <summary>Flattens a discovered row, injecting the DB-derived cwd + a
    /// created_at fallback for <c>started_at</c> when the conversation carries no
    /// first-turn timestamp.</summary>
    public static List<string> FlattenRow(ConversationRow row) {
        var lines = FlattenValue(row.ValueJson, row.ConversationId, row.Cwd);

        // Backfill started_at on the header from the DB created_at when the
        // conversation had no first-turn timestamp to derive it from.
        if (lines.Count > 0 && row.CreatedAt is { } created) {
            try {
                if (JsonNode.Parse(lines[0]) is JsonObject header && header["started_at"] is null) {
                    header["started_at"] = created.ToString("O");
                    lines[0] = header.ToJsonString(Compact);
                }
            } catch { /* leave header as-is */ }
        }

        return lines;
    }

    // ── SQLite access (copy-first, read-only, WAL-aware) ───────────────────

    /// <summary>
    /// Loads the conversation whose <c>conversation_id</c> matches
    /// <paramref name="conversationId"/> (dashed UUID). Tries
    /// <c>conversations_v2</c> first, then the legacy <c>conversations</c> table
    /// (keyed by cwd, no id column — matched by parsing the blob). Returns null
    /// when the DB / row is absent. Never throws on a locked or partial DB.
    /// </summary>
    public static ConversationRow? ReadByConversationId(string dbPath, string conversationId, string? cwd = null) {
        return WithReadOnlyCopy(dbPath, conn => {
            // Preferred: conversations_v2 has a dedicated conversation_id column.
            if (TableExists(conn, "conversations_v2")) {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT conversation_id, key, value, created_at, updated_at FROM conversations_v2 WHERE conversation_id = $cid LIMIT 1;";
                cmd.Parameters.AddWithValue("$cid", conversationId);
                using var r = cmd.ExecuteReader();
                if (r.Read()) return RowFromV2(r);
            }

            // Legacy: conversations(key=cwd PK, value=JSON). No id column —
            // locate by cwd when known, else scan and match the embedded id.
            if (TableExists(conn, "conversations")) {
                if (cwd is not null) {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT key, value FROM conversations WHERE key = $key LIMIT 1;";
                    cmd.Parameters.AddWithValue("$key", cwd);
                    using var r = cmd.ExecuteReader();
                    if (r.Read() && RowFromLegacy(r) is { } row && row.ConversationId == conversationId) return row;
                }

                using var scan = conn.CreateCommand();
                scan.CommandText = "SELECT key, value FROM conversations;";
                using var sr = scan.ExecuteReader();
                while (sr.Read()) {
                    if (RowFromLegacy(sr) is { } row && row.ConversationId == conversationId) return row;
                }
            }

            return null;
        });
    }

    /// <summary>
    /// Enumerates every conversation in the DB (newest <c>updated_at</c> first),
    /// for historical import discovery. Empty on absent/locked DB.
    /// </summary>
    public static IReadOnlyList<ConversationRow> DiscoverAll(string dbPath) {
        var rows = WithReadOnlyCopy(dbPath, conn => {
            var acc = new List<ConversationRow>();

            if (TableExists(conn, "conversations_v2")) {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT conversation_id, key, value, created_at, updated_at FROM conversations_v2;";
                using var r = cmd.ExecuteReader();
                while (r.Read()) {
                    if (RowFromV2(r) is { } row) acc.Add(row);
                }
            } else if (TableExists(conn, "conversations")) {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT key, value FROM conversations;";
                using var r = cmd.ExecuteReader();
                while (r.Read()) {
                    if (RowFromLegacy(r) is { } row) acc.Add(row);
                }
            }

            return acc;
        }) ?? new List<ConversationRow>();

        return rows
            .OrderByDescending(r => r.UpdatedAt ?? r.CreatedAt ?? DateTimeOffset.MinValue)
            .ToList();
    }

    static ConversationRow? RowFromV2(SqliteDataReader r) {
        var convId = r.IsDBNull(0) ? null : r.GetString(0);
        var key    = r.IsDBNull(1) ? null : r.GetString(1);
        var value  = r.IsDBNull(2) ? null : r.GetString(2);
        if (value is null) return null;

        // conversation_id column can be absent/null on some rows — fall back to
        // the id embedded in the blob.
        convId ??= TryExtractConversationId(value);
        if (convId is null) return null;

        return new ConversationRow(convId, key, value, EpochMsColumn(r, 3), EpochMsColumn(r, 4));
    }

    static ConversationRow? RowFromLegacy(SqliteDataReader r) {
        var key   = r.IsDBNull(0) ? null : r.GetString(0);
        var value = r.IsDBNull(1) ? null : r.GetString(1);
        if (value is null) return null;

        var convId = TryExtractConversationId(value);
        if (convId is null) return null;

        return new ConversationRow(convId, key, value, null, null);
    }

    static string? TryExtractConversationId(string valueJson) {
        try {
            using var doc = JsonDocument.Parse(valueJson);
            return doc.RootElement.TryGetProperty("conversation_id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        } catch {
            return null;
        }
    }

    static DateTimeOffset? EpochMsColumn(SqliteDataReader r, int ordinal) {
        if (ordinal >= r.FieldCount || r.IsDBNull(ordinal)) return null;
        try {
            // created_at / updated_at are epoch-ms (stored as INTEGER, but be
            // lenient about TEXT too).
            var ms = r.GetFieldType(ordinal) == typeof(string)
                ? (long.TryParse(r.GetString(ordinal), out var p) ? p : (long?)null)
                : r.GetInt64(ordinal);
            return ms is { } v ? DateTimeOffset.FromUnixTimeMilliseconds(v) : null;
        } catch {
            return null;
        }
    }

    static bool TableExists(SqliteConnection conn, string table) {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n LIMIT 1;";
        cmd.Parameters.AddWithValue("$n", table);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Copies the DB (plus any <c>-wal</c> / <c>-shm</c> sidecars) to a temp
    /// location and opens THAT read-only, so a live <c>kiro-cli</c> holding a
    /// write lock never blocks us and our read sees a consistent snapshot.
    /// Returns <paramref name="work"/>'s result, or default(T) on any failure.
    /// </summary>
    static T? WithReadOnlyCopy<T>(string dbPath, Func<SqliteConnection, T> work) {
        if (!File.Exists(dbPath)) return default;

        string? tempDir = null;
        try {
            tempDir = Path.Combine(Path.GetTempPath(), "kcap-kiro-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var tempDb = Path.Combine(tempDir, "data.sqlite3");
            File.Copy(dbPath, tempDb, overwrite: true);
            foreach (var ext in new[] { "-wal", "-shm" }) {
                var side = dbPath + ext;
                if (File.Exists(side)) File.Copy(side, tempDb + ext, overwrite: true);
            }

            var cs = new SqliteConnectionStringBuilder {
                DataSource = tempDb,
                Mode       = SqliteOpenMode.ReadOnly,
                Pooling    = false
            }.ToString();

            using var conn = new SqliteConnection(cs);
            conn.Open();
            return work(conn);
        } catch {
            return default;
        } finally {
            try { if (tempDir is not null && Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    // ── materialization (live watcher) ─────────────────────────────────────

    /// <summary>
    /// Flattens the conversation once and APPENDS any lines beyond what the
    /// output file already holds, keeping the file append-only so the watcher's
    /// line-number tracking stays stable. Returns the new total line count.
    /// Safe to call repeatedly (idempotent append).
    /// </summary>
    public static int MaterializeOnce(string dbPath, string conversationId, string? cwd, string outputPath) {
        var row = ReadByConversationId(dbPath, conversationId, cwd);
        if (row is null) return CountLines(outputPath);

        var lines = FlattenRow(row);

        var existing = CountLines(outputPath);
        if (lines.Count <= existing) return existing;

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using (var w = new StreamWriter(outputPath, append: true)) {
            for (var i = existing; i < lines.Count; i++) w.WriteLine(lines[i]);
        }

        return lines.Count;
    }

    /// <summary>
    /// Polls the DB and materializes new flattened lines to
    /// <paramref name="outputPath"/> until cancelled, then does one final
    /// materialize so the watcher's shutdown drain sees the full conversation.
    /// </summary>
    public static async Task RunMaterializerLoopAsync(
        string dbPath, string conversationId, string? cwd, string outputPath, TimeSpan interval, CancellationToken ct
    ) {
        try {
            while (!ct.IsCancellationRequested) {
                try { MaterializeOnce(dbPath, conversationId, cwd, outputPath); } catch { /* keep polling */ }

                try { await Task.Delay(interval, ct); }
                catch (OperationCanceledException) { break; }
            }
        } finally {
            try { MaterializeOnce(dbPath, conversationId, cwd, outputPath); } catch { /* best effort final flush */ }
        }
    }

    static int CountLines(string path) {
        if (!File.Exists(path)) return 0;
        try {
            var n = 0;
            using var reader = new StreamReader(path);
            while (reader.ReadLine() is not null) n++;
            return n;
        } catch {
            return 0;
        }
    }
}
