using Kapacitor.Cli.Core.Cursor;
using Microsoft.Data.Sqlite;

namespace Kapacitor.Cli.Tests.Unit.Cursor;

/// <summary>
/// Extra fixtures for tests exercising discovery-time filters (<c>--cwd</c> /
/// <c>--since</c>) that the original <see cref="CursorTestFixtures"/> doesn't
/// cover (it builds a single workspace with a single composer).
/// </summary>
static class CursorTestFixturesExtra {
    /// <summary>
    /// Builds a Cursor user dir with two distinct workspaces (each with one
    /// composer). Returns the constructed <see cref="CursorPaths"/> and emits
    /// the workspaces' folder paths via the out parameters.
    /// </summary>
    public static CursorPaths TwoWorkspaces(out string folderA, out string folderB) {
        var root = Path.Combine(Path.GetTempPath(), $"cursor-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        // ── global storage ──────────────────────────────────────────────────
        var globalDir = Path.Combine(root, "globalStorage");
        Directory.CreateDirectory(globalDir);
        var globalDb = Path.Combine(globalDir, "state.vscdb");

        CreateDb(globalDb, conn => {
            Exec(conn, "CREATE TABLE ItemTable    (key TEXT PRIMARY KEY, value TEXT);");
            Exec(conn, "CREATE TABLE cursorDiskKV (key TEXT PRIMARY KEY, value TEXT);");

            var headersJson = """
                              {"allComposers":[
                                {"composerId":"comp-A","unifiedMode":"agent","name":"Workspace A","createdAt":1,"lastUpdatedAt":1},
                                {"composerId":"comp-B","unifiedMode":"agent","name":"Workspace B","createdAt":1,"lastUpdatedAt":1}
                              ]}
                              """;
            ExecParam(conn, "INSERT INTO ItemTable VALUES ('composer.composerHeaders', @v)", headersJson);

            ExecParam(
                conn,
                "INSERT INTO cursorDiskKV VALUES (@k, @v)",
                key:   "composerData:comp-A",
                value: """{"modelConfig":{"modelName":"claude-4"},"fullConversationHeadersOnly":[],"generatingBubbleIds":[],"status":"idle"}"""
            );

            ExecParam(
                conn,
                "INSERT INTO cursorDiskKV VALUES (@k, @v)",
                key:   "composerData:comp-B",
                value: """{"modelConfig":{"modelName":"claude-4"},"fullConversationHeadersOnly":[],"generatingBubbleIds":[],"status":"idle"}"""
            );
        });

        // ── workspace storage (two subdirs, each pointing at a distinct folder) ──
        var wsStorageDir = Path.Combine(root, "workspaceStorage");

        folderA = Path.Combine(root, "folderA");
        Directory.CreateDirectory(folderA);
        folderB = Path.Combine(root, "folderB");
        Directory.CreateDirectory(folderB);

        SeedWorkspace(wsStorageDir, "ws-A", folderA, "comp-A");
        SeedWorkspace(wsStorageDir, "ws-B", folderB, "comp-B");

        return new CursorPaths(
            UserDir:             root,
            WorkspaceStorageDir: wsStorageDir,
            GlobalStateDb:       globalDb);
    }

    /// <summary>
    /// Builds a Cursor user dir with one workspace containing two composers
    /// whose header <c>createdAt</c> straddle the caller-supplied <paramref name="cutoffMs"/>.
    /// Used to pin --since pre-pruning behaviour at discovery.
    /// </summary>
    public static CursorPaths WorkspaceWithTwoComposersByTimestamp(
            string oldComposerId, long oldCreatedAtMs,
            string newComposerId, long newCreatedAtMs
        ) {
        var root = Path.Combine(Path.GetTempPath(), $"cursor-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var globalDir = Path.Combine(root, "globalStorage");
        Directory.CreateDirectory(globalDir);
        var globalDb = Path.Combine(globalDir, "state.vscdb");

        CreateDb(globalDb, conn => {
            Exec(conn, "CREATE TABLE ItemTable    (key TEXT PRIMARY KEY, value TEXT);");
            Exec(conn, "CREATE TABLE cursorDiskKV (key TEXT PRIMARY KEY, value TEXT);");

            var headersJson = $$"""
                                {"allComposers":[
                                  {"composerId":"{{oldComposerId}}","unifiedMode":"agent","name":"old","createdAt":{{oldCreatedAtMs}},"lastUpdatedAt":{{oldCreatedAtMs}}},
                                  {"composerId":"{{newComposerId}}","unifiedMode":"agent","name":"new","createdAt":{{newCreatedAtMs}},"lastUpdatedAt":{{newCreatedAtMs}}}
                                ]}
                                """;
            ExecParam(conn, "INSERT INTO ItemTable VALUES ('composer.composerHeaders', @v)", headersJson);

            foreach (var cid in new[] { oldComposerId, newComposerId }) {
                ExecParam(
                    conn,
                    "INSERT INTO cursorDiskKV VALUES (@k, @v)",
                    key:   $"composerData:{cid}",
                    value: """{"modelConfig":{"modelName":"claude-4"},"fullConversationHeadersOnly":[],"generatingBubbleIds":[],"status":"idle"}"""
                );
            }
        });

        var wsStorageDir = Path.Combine(root, "workspaceStorage");
        SeedWorkspace(wsStorageDir, "ws-fixture", root, oldComposerId, newComposerId);

        return new CursorPaths(
            UserDir:             root,
            WorkspaceStorageDir: wsStorageDir,
            GlobalStateDb:       globalDb);
    }

    static void SeedWorkspace(string wsStorageDir, string subdirName, string folderPath, params string[] composerIds) {
        var wsSubdir = Path.Combine(wsStorageDir, subdirName);
        Directory.CreateDirectory(wsSubdir);

        var folderUri = $"file://{folderPath}";

        File.WriteAllText(
            Path.Combine(wsSubdir, "workspace.json"),
            $$"""{"folder":"{{folderUri}}"}"""
        );

        var wsDb = Path.Combine(wsSubdir, "state.vscdb");

        CreateDb(wsDb, conn => {
            Exec(conn, "CREATE TABLE ItemTable (key TEXT PRIMARY KEY, value TEXT);");

            var idsJson = "[" + string.Join(",", composerIds.Select(id => $"\"{id}\"")) + "]";
            ExecParam(
                conn,
                "INSERT INTO ItemTable VALUES ('composer.composerData', @v)",
                value: $$"""{"selectedComposerId":"{{composerIds[0]}}","selectedComposerIds":{{idsJson}}}"""
            );
        });
    }

    static void CreateDb(string path, Action<SqliteConnection> seed) {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        seed(conn);
    }

    static void Exec(SqliteConnection conn, string sql) {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static void ExecParam(SqliteConnection conn, string sql, string value, string? key = null) {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (key is not null) cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }
}
