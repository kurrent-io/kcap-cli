using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kapacitor.Cli.Core.Cursor;

public sealed record RawComposerHeader(
    string ComposerId,
    string UnifiedMode,
    string? Name,
    long CreatedAtMs,
    long LastUpdatedAtMs,
    string? Subtitle,
    int TotalLinesAdded,
    int TotalLinesRemoved,
    int FilesChangedCount,
    IReadOnlyList<RawTrackedRepo>? TrackedGitRepos);

public sealed record RawTrackedRepo(string RepoPath, IReadOnlyList<string>? BranchNames);

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

            // Parse optional tracked git repos (present in Cursor ≥ v0.x when a folder is open)
            IReadOnlyList<RawTrackedRepo>? trackedRepos = null;
            if (c.TryGetProperty("trackedGitRepos", out var tgrEl) && tgrEl.ValueKind == JsonValueKind.Array) {
                var repos = new List<RawTrackedRepo>();
                foreach (var r in tgrEl.EnumerateArray()) {
                    var repoPath = r.TryGetProperty("repoPath", out var rp) ? rp.GetString() ?? "" : "";
                    IReadOnlyList<string>? branches = null;
                    if (r.TryGetProperty("branchNames", out var bnEl) && bnEl.ValueKind == JsonValueKind.Array) {
                        branches = bnEl.EnumerateArray()
                            .Where(b => b.ValueKind == JsonValueKind.String)
                            .Select(b => b.GetString()!)
                            .ToList();
                    }
                    repos.Add(new RawTrackedRepo(repoPath, branches));
                }
                trackedRepos = repos;
            }

            return new RawComposerHeader(
                ComposerId:       composerId,
                UnifiedMode:      c.GetProperty("unifiedMode").GetString() ?? "",
                Name:             c.TryGetProperty("name",             out var n)    ? n.GetString()   : null,
                CreatedAtMs:      c.GetProperty("createdAt").GetInt64(),
                LastUpdatedAtMs:  c.GetProperty("lastUpdatedAt").GetInt64(),
                Subtitle:         c.TryGetProperty("subtitle",         out var sub)  ? sub.GetString() : null,
                TotalLinesAdded:  c.TryGetProperty("totalLinesAdded",   out var tla) && tla.ValueKind == JsonValueKind.Number ? tla.GetInt32() : 0,
                TotalLinesRemoved:c.TryGetProperty("totalLinesRemoved", out var tlr) && tlr.ValueKind == JsonValueKind.Number ? tlr.GetInt32() : 0,
                FilesChangedCount:c.TryGetProperty("filesChangedCount", out var fcc) && fcc.ValueKind == JsonValueKind.Number ? fcc.GetInt32() : 0,
                TrackedGitRepos:  trackedRepos
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
