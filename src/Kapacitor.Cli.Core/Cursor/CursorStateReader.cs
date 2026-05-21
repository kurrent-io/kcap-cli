using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kapacitor.Cli.Core.Cursor;

public sealed record RawComposerHeader(string ComposerId, string UnifiedMode, string? Name, long CreatedAtMs, long LastUpdatedAtMs);

public static class CursorStateReader {
    static string ConnString(string path) => new SqliteConnectionStringBuilder {
        DataSource = path,
        Mode       = SqliteOpenMode.ReadOnly,
        Cache      = SqliteCacheMode.Shared
    }.ToString();

    public static async Task<IReadOnlyList<string>> ListWorkspaceComposerIdsAsync(string workspaceDbPath, CancellationToken ct = default) {
        await using var conn = new SqliteConnection(ConnString(workspaceDbPath));
        await conn.OpenAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM ItemTable WHERE key = 'composer.composerData'";
        var raw = (string?)await cmd.ExecuteScalarAsync(ct);
        if (raw is null) return [];

        using var doc = JsonDocument.Parse(raw);
        var root      = doc.RootElement;
        var ids       = new List<string>();

        // Prefer allComposers when populated, else selectedComposerIds.
        if (root.TryGetProperty("allComposers", out var all) && all.GetArrayLength() > 0) {
            foreach (var c in all.EnumerateArray())
                if (c.TryGetProperty("composerId", out var cid))
                    ids.Add(cid.GetString()!);
        } else if (root.TryGetProperty("selectedComposerIds", out var sel)) {
            foreach (var id in sel.EnumerateArray())
                ids.Add(id.GetString()!);
        }
        return ids;
    }

    public static async Task<RawComposerHeader?> GetComposerHeaderAsync(string globalDbPath, string composerId, CancellationToken ct = default) {
        await using var conn = new SqliteConnection(ConnString(globalDbPath));
        await conn.OpenAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM ItemTable WHERE key = 'composer.composerHeaders'";
        var raw = (string?)await cmd.ExecuteScalarAsync(ct);
        if (raw is null) return null;

        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("allComposers", out var all)) return null;
        foreach (var c in all.EnumerateArray()) {
            if (c.GetProperty("composerId").GetString() != composerId) continue;
            return new RawComposerHeader(
                ComposerId:      composerId,
                UnifiedMode:     c.GetProperty("unifiedMode").GetString() ?? "",
                Name:            c.TryGetProperty("name", out var n) ? n.GetString() : null,
                CreatedAtMs:     c.GetProperty("createdAt").GetInt64(),
                LastUpdatedAtMs: c.GetProperty("lastUpdatedAt").GetInt64()
            );
        }
        return null;
    }

    public static async Task<string?> GetComposerDataAsync(string globalDbPath, string composerId, CancellationToken ct = default) {
        await using var conn = new SqliteConnection(ConnString(globalDbPath));
        await conn.OpenAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM cursorDiskKV WHERE key = @k";
        cmd.Parameters.AddWithValue("@k", $"composerData:{composerId}");
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    public static async Task<IReadOnlyList<(string Key, string Value)>> ListBubblesAsync(string globalDbPath, string composerId, CancellationToken ct = default) {
        await using var conn = new SqliteConnection(ConnString(globalDbPath));
        await conn.OpenAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM cursorDiskKV WHERE key LIKE @prefix";
        cmd.Parameters.AddWithValue("@prefix", $"bubbleId:{composerId}:%");
        var list = new List<(string, string)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    public static async Task<string?> GetContentBlobAsync(string globalDbPath, string contentKey, CancellationToken ct = default) {
        await using var conn = new SqliteConnection(ConnString(globalDbPath));
        await conn.OpenAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM cursorDiskKV WHERE key = @k";
        cmd.Parameters.AddWithValue("@k", contentKey);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }
}
