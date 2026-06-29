using System.Text.Json;
using System.Text.Json.Nodes;
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
///
/// <para>Reconstructs the live plugin's <c>{info,parts}</c> JSONL per message by merging
/// each row's key columns back onto its <c>data</c> JSON — info gets {id, sessionID};
/// each part gets {id, messageID, sessionID} — so the server's <c>opencode</c> normalizer
/// consumes import output identically to the live stream.</para>
/// </summary>
internal sealed class OpenCodeDb : IDisposable {
    readonly SqliteConnection _conn;

    // Install the on-demand native-SQLite resolver before SqliteConnection's static ctor
    // runs its first e_sqlite3 P/Invoke. A static ctor on this type is guaranteed to run
    // before the instance ctor body references SqliteConnection below.
    static OpenCodeDb() => SqliteNativeResolver.Register();

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
            OpenCodeSessionRow row;
            try {
                row = new OpenCodeSessionRow(
                    Id:          r.GetString(0),
                    ParentId:    r.IsDBNull(1) ? null : r.GetString(1),
                    Directory:   r.IsDBNull(2) ? null : r.GetString(2),
                    Title:       r.IsDBNull(3) ? "" : r.GetString(3),
                    TimeCreated: r.IsDBNull(4) ? 0 : r.GetInt64(4),
                    TimeUpdated: r.IsDBNull(5) ? 0 : r.GetInt64(5));
            } catch {
                continue; // skip a malformed / schema-drifted row rather than abort the scan
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Streams one reconstructed <c>{info,parts}</c> JSONL line per message, in
    /// message chronological order, parts in (time_created,id) order. A LEFT JOIN keeps
    /// messages that have no parts. Bounded memory: holds only the current message's
    /// parts at a time.
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

        string?    curMsgId   = null;
        string     curMsgData = "{}";
        JsonArray? parts      = null;

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
                // Cast to JsonNode so the non-generic Add(JsonNode?) overload is chosen —
                // the generic Add<T>(T) trips IL2026/IL3050 under AOT (see CLAUDE.md).
                parts!.Add((JsonNode)partNode);
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
    /// under the <c>opencode</c> normalizer. ROLE-AWARE and HIDDEN-AWARE, mirroring
    /// <c>Capacitor.Server/Sessions/Canonical/OpenCodeTranscriptNormalizer.cs</c>:
    /// <list type="bullet">
    ///   <item>user → a non-hidden <c>text</c> part with non-empty text;</item>
    ///   <item>assistant → a non-hidden <c>reasoning</c>/<c>text</c> part with <c>id</c> and non-empty text;</item>
    ///   <item>assistant → a <c>tool</c> part with <c>id</c>, <c>callID</c>, <c>tool</c>, and terminal <c>state.status</c>.</item>
    /// </list>
    /// A part is hidden when <c>synthetic:true</c> or <c>ignored:true</c>. Gates
    /// <c>TooShort</c>, so it must not over-count; keep in sync with the normalizer.
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
                        // user text: non-empty (server uses Length > 0, so whitespace counts).
                        case ("user", "text"):
                            if (HasText(p)) return true;
                            break;
                        // assistant text/reasoning: server skips when id is null, then requires Length > 0.
                        case ("assistant", "text"):
                        case ("assistant", "reasoning"):
                            if (HasField(p, "id") && HasText(p)) return true;
                            break;
                        // assistant tool: id + callID + tool present (null-checked) + terminal state.
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
    // string values as present. Empty string counts, for parity with the server's null-check.
    static bool HasField(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String;

    // text present, string, length > 0 (matches server's Length > 0 — NOT IsNullOrWhiteSpace).
    static bool HasText(JsonElement p) =>
        p.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String && tx.GetString()!.Length > 0;

    public void Dispose() => _conn.Dispose();
}
