using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Capacitor.Cli.Commands;

/// <summary>
/// One OpenCode session row (subset used by import). <c>TimeCreated</c>/<c>TimeUpdated</c>
/// are <c>null</c> when the column is NULL or 0 (both observed in the wild) — "unknown",
/// never epoch (AI-1358).
/// </summary>
internal sealed record OpenCodeSessionRow(
    string  Id,
    string? ParentId,
    string? Directory,
    string  Title,
    long?   TimeCreated,
    long?   TimeUpdated);

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

    /// <summary>Import-side recursion depth cap (AI-1383 D3) — a descendant beyond this depth
    /// is neither imported nor promoted; the caller surfaces the count.</summary>
    public const int MaxDescendantDepth = 8;

    /// <summary>
    /// Hard ceiling on the total number of nodes visited by the below-cap COUNTING walk
    /// (AI-1383 D3 review fix #2/#3). The visited-id set already prevents an infinite loop on a
    /// reachable cycle; this bounds the total work a pathologically huge or wide capped subtree
    /// could otherwise force onto a single classify/import pass. A real OpenCode session graph
    /// tops out at a few dozen nodes, so this is a generous safety valve, not a realistic limit —
    /// if it's ever hit, the walk simply stops early (an undercounted omission is safer than an
    /// unbounded scan).
    /// </summary>
    public const int MaxCountingNodes = 10_000;

    /// <summary>One recursively-discovered descendant row at its depth below the root (a direct
    /// child is depth 1, a grandchild depth 2, etc.).</summary>
    public readonly record struct DescendantRow(OpenCodeSessionRow Row, int Depth);

    /// <summary>
    /// Recursive descendant discovery result. <see cref="Descendants"/> holds every descendant
    /// WITHIN <see cref="MaxDescendantDepth"/> — the import set (BFS, deterministic, each level
    /// ordered like <see cref="QueryChildren"/>). <see cref="DescendantsOmitted"/> is the
    /// ACCURATE total count of every descendant BELOW the cap, not merely the boundary children
    /// immediately past it — a chain through depths 9 and 10 reports 2, not 1 (AI-1383 D3 review
    /// fix #3). <see cref="OmittedDescendantIds"/> carries those same omitted ids (sorted,
    /// order-independent) so a caller can fold them into a completeness fingerprint: a
    /// newly-reachable capped descendant then changes the fingerprint even though its content is
    /// never imported (AI-1383 D3 review fix #2). Never silent — the caller surfaces the count in
    /// the import summary.
    /// </summary>
    public readonly record struct DescendantDiscoveryResult(
        IReadOnlyList<DescendantRow> Descendants,
        int                          DescendantsOmitted,
        IReadOnlyList<string>        OmittedDescendantIds);

    /// <summary>
    /// Recursively walks <c>parent_id</c> edges from <paramref name="rootId"/> — per-level
    /// <see cref="QueryChildren"/>, a visited-id set to guard a reachable cycle (traversal
    /// simply stops re-descending, terminating the walk), IMPORT depth capped at
    /// <see cref="MaxDescendantDepth"/> (AI-1383 D3 — previously <see cref="QueryChildren"/> was
    /// called directly, a single-level query that silently dropped grandchildren). A descendant
    /// beyond the cap is never imported, but the walk still recurses into it (same visited-set
    /// guard, bounded by <see cref="MaxCountingNodes"/>) purely to COUNT the whole omitted
    /// subtree accurately and surface its identities (AI-1383 D3 review fix #2/#3) —
    /// <see cref="DescendantDiscoveryResult.DescendantsOmitted"/> and
    /// <see cref="DescendantDiscoveryResult.OmittedDescendantIds"/>.
    /// </summary>
    public DescendantDiscoveryResult QueryDescendants(string rootId) {
        var result     = new List<DescendantRow>();
        var visited    = new HashSet<string>(StringComparer.Ordinal) { rootId };
        var omittedIds = new List<string>();

        var frontier = new Queue<(string Id, int Depth)>();
        frontier.Enqueue((rootId, 0));

        while (frontier.Count > 0) {
            if (visited.Count >= MaxCountingNodes) break; // hard ceiling — see MaxCountingNodes
            var (id, depth) = frontier.Dequeue();

            foreach (var child in QueryChildren(id)) {
                if (visited.Count >= MaxCountingNodes) break;
                if (!visited.Add(child.Id)) continue; // cycle guard — already reached

                var childDepth = depth + 1;
                if (childDepth > MaxDescendantDepth) {
                    // Below the import cap: never imported, but keep walking (same visited-set
                    // guard) so the count/signature reflect the WHOLE omitted subtree, not just
                    // its boundary (AI-1383 D3 review fix #2/#3).
                    omittedIds.Add(child.Id);
                    frontier.Enqueue((child.Id, childDepth));
                    continue;
                }

                result.Add(new DescendantRow(child, childDepth));
                frontier.Enqueue((child.Id, childDepth));
            }
        }

        omittedIds.Sort(StringComparer.Ordinal);
        return new DescendantDiscoveryResult(result, omittedIds.Count, omittedIds);
    }

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
                    TimeCreated: r.IsDBNull(4) ? null : NonZero(r.GetInt64(4)),
                    TimeUpdated: r.IsDBNull(5) ? null : NonZero(r.GetInt64(5)));
            } catch {
                continue; // skip a malformed / schema-drifted row rather than abort the scan
            }
            rows.Add(row);
        }
        return rows;
    }

    // A 0 epoch is observed alongside NULL for an unset time_created/time_updated
    // column — both mean "unknown", not the 1970 epoch (AI-1358).
    static long? NonZero(long v) => v == 0 ? null : v;

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
