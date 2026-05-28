using Kapacitor.Cli.Core.Cursor;
using Microsoft.Data.Sqlite;

namespace Kapacitor.Cli.Tests.Unit.Cursor;

static class CursorTestFixtures {
    /// <summary>
    /// Creates a temporary directory containing:
    /// <list type="bullet">
    ///   <item>A workspace storage subdir with <c>workspace.json</c> (folder=file://&lt;tempDir&gt;)
    ///         and a <c>state.vscdb</c> with <c>composer.composerData</c> referencing the composer.</item>
    ///   <item>A global <c>state.vscdb</c> with the composer header (unifiedMode=agent) and
    ///         a minimal <c>composerData:&lt;id&gt;</c> blob so payload assembly can read it.</item>
    /// </list>
    /// Returns <c>(workspaceFolder, CursorPaths)</c> pointing to the temp tree.
    /// The <c>CursorPaths.WorkspaceStorageDir</c> is set to the fixture's workspace storage dir so
    /// <c>CursorImportSource</c> finds the fixture workspace.
    /// </summary>
    public static (string WorkspaceFolder, CursorPaths Paths) WorkspaceWithOneComposer(
            string    composerId,
            string[]? generatingBubbleIds = null
        ) {
        var root = Path.Combine(Path.GetTempPath(), $"cursor-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        // ── global storage ──────────────────────────────────────────────────
        var globalDir = Path.Combine(root, "globalStorage");
        Directory.CreateDirectory(globalDir);
        var globalDb = Path.Combine(globalDir, "state.vscdb");

        CreateDb(
            globalDb,
            conn => {
                Exec(conn, "CREATE TABLE ItemTable    (key TEXT PRIMARY KEY, value TEXT);");
                Exec(conn, "CREATE TABLE cursorDiskKV (key TEXT PRIMARY KEY, value TEXT);");

                // Composer header — unifiedMode = "agent", lastUpdatedAt = 1
                var headersJson = $$"""
                                    {"allComposers":[
                                      {"composerId":"{{composerId}}","unifiedMode":"agent","name":"Test Session",
                                       "createdAt":1,"lastUpdatedAt":1}
                                    ]}
                                    """;
                ExecParam(conn, "INSERT INTO ItemTable VALUES ('composer.composerHeaders', @v)", headersJson);

                // Minimal composerData blob — include any in-flight generating bubble IDs
                var ids     = generatingBubbleIds ?? [];
                var idsJson = "[" + string.Join(",", ids.Select(id => $"\"{id}\"")) + "]";

                var dataJson = $$"""
                                 {"modelConfig":{"modelName":"claude-4"},"fullConversationHeadersOnly":[],
                                  "generatingBubbleIds":{{idsJson}},"status":"idle"}
                                 """;

                ExecParam(
                    conn,
                    "INSERT INTO cursorDiskKV VALUES (@k, @v)",
                    key: $"composerData:{composerId}",
                    value: dataJson
                );
            }
        );

        // ── workspace storage ────────────────────────────────────────────────
        var wsStorageDir = Path.Combine(root, "workspaceStorage");
        var wsSubdir     = Path.Combine(wsStorageDir, "ws-fixture");
        Directory.CreateDirectory(wsSubdir);

        // The workspace folder is just the temp root itself
        var workspaceFolder = root;
        var folderUri       = $"file://{workspaceFolder}";

        File.WriteAllText(
            Path.Combine(wsSubdir, "workspace.json"),
            $$"""{"folder":"{{folderUri}}"}"""
        );

        var wsDb = Path.Combine(wsSubdir, "state.vscdb");

        CreateDb(
            wsDb,
            conn => {
                Exec(conn, "CREATE TABLE ItemTable (key TEXT PRIMARY KEY, value TEXT);");

                ExecParam(
                    conn,
                    "INSERT INTO ItemTable VALUES ('composer.composerData', @v)",
                    value: $$"""{"selectedComposerId":"{{composerId}}","selectedComposerIds":["{{composerId}}"]}"""
                );
            }
        );

        // Build paths pointing at our fixture tree
        var paths = new CursorPaths(
            UserDir: root,
            WorkspaceStorageDir: wsStorageDir,
            GlobalStateDb: globalDb
        );

        return (workspaceFolder, paths);
    }

    /// <summary>
    /// Creates a fixture with three user bubbles (A, B, C) stored in SQLite in shuffled
    /// order (B, C, A) but declared in conversation order (A, B, C) via
    /// <c>fullConversationHeadersOnly</c>. Used to verify bubble-ordering fix.
    /// </summary>
    public static (string WorkspaceFolder, CursorPaths Paths) WorkspaceWithBubblesInShuffledOrder(
            string composerId
        ) {
        var root = Path.Combine(Path.GetTempPath(), $"cursor-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var globalDir = Path.Combine(root, "globalStorage");
        Directory.CreateDirectory(globalDir);
        var globalDb = Path.Combine(globalDir, "state.vscdb");

        CreateDb(
            globalDb,
            conn => {
                Exec(conn, "CREATE TABLE ItemTable    (key TEXT PRIMARY KEY, value TEXT);");
                Exec(conn, "CREATE TABLE cursorDiskKV (key TEXT PRIMARY KEY, value TEXT);");

                var headersJson = $$"""
                                    {"allComposers":[
                                      {"composerId":"{{composerId}}","unifiedMode":"agent","name":"Ordered Test",
                                       "createdAt":1,"lastUpdatedAt":1}
                                    ]}
                                    """;
                ExecParam(conn, "INSERT INTO ItemTable VALUES ('composer.composerHeaders', @v)", headersJson);

                // fullConversationHeadersOnly lists A, B, C
                // SQLite storage order (insert order) is B, C, A — purposely shuffled
                var bubIdA = $"{composerId}:bub-A";
                var bubIdB = $"{composerId}:bub-B";
                var bubIdC = $"{composerId}:bub-C";

                var dataJson = $$"""
                                 {
                                   "modelConfig":{"modelName":"claude-4"},
                                   "fullConversationHeadersOnly":[
                                     {"bubbleId":"{{bubIdA}}","type":1},
                                     {"bubbleId":"{{bubIdB}}","type":1},
                                     {"bubbleId":"{{bubIdC}}","type":1}
                                   ],
                                   "generatingBubbleIds":[],
                                   "status":"completed"
                                 }
                                 """;

                ExecParam(
                    conn,
                    "INSERT INTO cursorDiskKV VALUES (@k, @v)",
                    key: $"composerData:{composerId}",
                    value: dataJson
                );

                // Insert bubbles in shuffled order: B, C, A
                foreach (var (bid, text) in new[] { (bubIdB, "msg B"), (bubIdC, "msg C"), (bubIdA, "msg A") }) {
                    var bJson = $$"""{"bubbleId":"{{bid}}","type":1,"createdAt":"2024-01-01T00:00:00Z","text":"{{text}}"}""";

                    ExecParam(
                        conn,
                        "INSERT INTO cursorDiskKV VALUES (@k, @v)",
                        key: $"bubbleId:{composerId}:{bid}",
                        value: bJson
                    );
                }
            }
        );

        var wsStorageDir = Path.Combine(root, "workspaceStorage");
        var wsSubdir     = Path.Combine(wsStorageDir, "ws-fixture");
        Directory.CreateDirectory(wsSubdir);

        var workspaceFolder = root;
        var folderUri       = $"file://{workspaceFolder}";

        File.WriteAllText(
            Path.Combine(wsSubdir, "workspace.json"),
            $$"""{"folder":"{{folderUri}}"}"""
        );

        var wsDb = Path.Combine(wsSubdir, "state.vscdb");

        CreateDb(
            wsDb,
            conn => {
                Exec(conn, "CREATE TABLE ItemTable (key TEXT PRIMARY KEY, value TEXT);");

                ExecParam(
                    conn,
                    "INSERT INTO ItemTable VALUES ('composer.composerData', @v)",
                    value: $$"""{"selectedComposerId":"{{composerId}}","selectedComposerIds":["{{composerId}}"]}"""
                );
            }
        );

        var paths = new CursorPaths(
            UserDir: root,
            WorkspaceStorageDir: wsStorageDir,
            GlobalStateDb: globalDb
        );

        return (workspaceFolder, paths);
    }

    /// <summary>
    /// Creates a fixture with two edit_file_v2 bubbles:
    /// <list type="bullet">
    ///   <item><c>bub-in</c> — listed in <c>fullConversationHeadersOnly</c>; references <c>composer.content.blob-in</c>.</item>
    ///   <item><c>bub-orphan</c> — NOT listed in headers (orphan); references <c>composer.content.blob-orphan</c>.</item>
    /// </list>
    /// After the orphan-blob fix, only <c>blob-in</c> must appear in the posted <c>contentBlobs</c>.
    /// </summary>
    public static (string WorkspaceFolder, CursorPaths Paths) WorkspaceWithOrphanBubbleBlobs(string composerId) {
        var root = Path.Combine(Path.GetTempPath(), $"cursor-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var globalDir = Path.Combine(root, "globalStorage");
        Directory.CreateDirectory(globalDir);
        var globalDb = Path.Combine(globalDir, "state.vscdb");

        var bubIdIn     = $"{composerId}:bub-in";
        var bubIdOrphan = $"{composerId}:bub-orphan";

        CreateDb(
            globalDb,
            conn => {
                Exec(conn, "CREATE TABLE ItemTable    (key TEXT PRIMARY KEY, value TEXT);");
                Exec(conn, "CREATE TABLE cursorDiskKV (key TEXT PRIMARY KEY, value TEXT);");

                // Composer header
                var headersJson = $$"""
                                    {"allComposers":[
                                      {"composerId":"{{composerId}}","unifiedMode":"agent","name":"Blob Test",
                                       "createdAt":1,"lastUpdatedAt":1}
                                    ]}
                                    """;
                ExecParam(conn, "INSERT INTO ItemTable VALUES ('composer.composerHeaders', @v)", headersJson);

                // fullConversationHeadersOnly: only bub-in is listed; bub-orphan is NOT listed
                var dataJson = $$"""
                                 {
                                   "modelConfig":{"modelName":"claude-4"},
                                   "fullConversationHeadersOnly":[
                                     {"bubbleId":"{{bubIdIn}}","type":2}
                                   ],
                                   "generatingBubbleIds":[],
                                   "status":"completed"
                                 }
                                 """;

                ExecParam(
                    conn,
                    "INSERT INTO cursorDiskKV VALUES (@k, @v)",
                    key: $"composerData:{composerId}",
                    value: dataJson
                );

                // bub-in: edit_file_v2 referencing blob-in
                var resultIn = @"{""beforeContentId"":""composer.content.blob-in"",""afterContentId"":null}";

                var bubInJson =
                    $@"{{""bubbleId"":""{bubIdIn}"",""type"":2,""createdAt"":""2024-01-01T00:00:00Z"",""toolFormerData"":{{""toolCallId"":""t1"",""name"":""edit_file_v2"",""result"":{resultIn}}}}}";

                ExecParam(
                    conn,
                    "INSERT INTO cursorDiskKV VALUES (@k, @v)",
                    key: $"bubbleId:{composerId}:{bubIdIn}",
                    value: bubInJson
                );

                // bub-orphan: edit_file_v2 referencing blob-orphan (NOT in headers)
                var resultOrphan = @"{""beforeContentId"":""composer.content.blob-orphan"",""afterContentId"":null}";

                var bubOrphanJson =
                    $@"{{""bubbleId"":""{bubIdOrphan}"",""type"":2,""createdAt"":""2024-01-01T00:00:00Z"",""toolFormerData"":{{""toolCallId"":""t2"",""name"":""edit_file_v2"",""result"":{resultOrphan}}}}}";

                ExecParam(
                    conn,
                    "INSERT INTO cursorDiskKV VALUES (@k, @v)",
                    key: $"bubbleId:{composerId}:{bubIdOrphan}",
                    value: bubOrphanJson
                );

                // Content blobs referenced by both bubbles
                ExecParam(
                    conn,
                    "INSERT INTO cursorDiskKV VALUES (@k, @v)",
                    key: "composer.content.blob-in",
                    value: "// content for in-headers bubble"
                );

                ExecParam(
                    conn,
                    "INSERT INTO cursorDiskKV VALUES (@k, @v)",
                    key: "composer.content.blob-orphan",
                    value: "// content for orphan bubble"
                );
            }
        );

        var wsStorageDir = Path.Combine(root, "workspaceStorage");
        var wsSubdir     = Path.Combine(wsStorageDir, "ws-fixture");
        Directory.CreateDirectory(wsSubdir);

        var workspaceFolder = root;
        var folderUri       = $"file://{workspaceFolder}";

        File.WriteAllText(
            Path.Combine(wsSubdir, "workspace.json"),
            $$"""{"folder":"{{folderUri}}"}"""
        );

        var wsDb = Path.Combine(wsSubdir, "state.vscdb");

        CreateDb(
            wsDb,
            conn => {
                Exec(conn, "CREATE TABLE ItemTable (key TEXT PRIMARY KEY, value TEXT);");

                ExecParam(
                    conn,
                    "INSERT INTO ItemTable VALUES ('composer.composerData', @v)",
                    value: $$"""{"selectedComposerId":"{{composerId}}","selectedComposerIds":["{{composerId}}"]}"""
                );
            }
        );

        var paths = new CursorPaths(
            UserDir: root,
            WorkspaceStorageDir: wsStorageDir,
            GlobalStateDb: globalDb
        );

        return (workspaceFolder, paths);
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
