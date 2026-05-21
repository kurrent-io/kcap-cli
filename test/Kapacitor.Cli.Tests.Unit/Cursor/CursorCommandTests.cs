using System.Text.Json;
using Kapacitor.Cli.Commands;
using Kapacitor.Cli.Core.Cursor;
using Microsoft.Data.Sqlite;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Kapacitor.Cli.Tests.Unit.Cursor;

public class CursorCommandTests {
    [Test]
    public async Task Import_posts_payload_when_watermark_absent() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().UsingGet().WithPath("/api/cursor/comp-A/watermark"))
              .RespondWith(Response.Create().WithStatusCode(404));
        server.Given(Request.Create().UsingPost().WithPath("/hooks/cursor-import"))
              .RespondWith(Response.Create().WithStatusCode(202).WithBody("{}"));

        var (workspace, paths) = CursorCommandTestFixtures.WorkspaceWithOneComposer("comp-A");

        var rc = await CursorCommand.RunAsync(
            args: ["import", "--workspace", workspace],
            baseUrl: server.Url!,
            pathsOverride: paths
        );

        await Assert.That(rc).IsEqualTo(0);
        var posts = server.FindLogEntries(Request.Create().UsingPost().WithPath("/hooks/cursor-import"));
        await Assert.That(posts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Import_skips_when_watermark_current() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().UsingGet().WithPath("/api/cursor/comp-A/watermark"))
              .RespondWith(Response.Create()
                  .WithStatusCode(200)
                  .WithBody("""{"last_bubble_id":"b1","last_updated_at_ms":9999999999999}"""));
        server.Given(Request.Create().UsingPost().WithPath("/hooks/cursor-import"))
              .RespondWith(Response.Create().WithStatusCode(202).WithBody("{}"));

        var (workspace, paths) = CursorCommandTestFixtures.WorkspaceWithOneComposer("comp-A");

        var rc = await CursorCommand.RunAsync(
            args: ["import", "--workspace", workspace],
            baseUrl: server.Url!,
            pathsOverride: paths
        );

        await Assert.That(rc).IsEqualTo(0);
        var posts = server.FindLogEntries(Request.Create().UsingPost().WithPath("/hooks/cursor-import"));
        await Assert.That(posts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Import_skips_composer_with_generating_bubbles_in_flight() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().UsingGet().WithPath("/api/cursor/comp-A/watermark"))
              .RespondWith(Response.Create().WithStatusCode(404));
        server.Given(Request.Create().UsingPost().WithPath("/hooks/cursor-import"))
              .RespondWith(Response.Create().WithStatusCode(202).WithBody("{}"));

        var (workspace, paths) = CursorCommandTestFixtures.WorkspaceWithOneComposer("comp-A",
            generatingBubbleIds: ["b1"]);

        var rc = await CursorCommand.RunAsync(
            args: ["import", "--workspace", workspace],
            baseUrl: server.Url!,
            pathsOverride: paths
        );

        await Assert.That(rc).IsEqualTo(0);
        var posts = server.FindLogEntries(Request.Create().UsingPost().WithPath("/hooks/cursor-import"));
        await Assert.That(posts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Import_orders_bubbles_by_fullConversationHeadersOnly() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().UsingGet().WithPath("/api/cursor/comp-B/watermark"))
              .RespondWith(Response.Create().WithStatusCode(404));
        server.Given(Request.Create().UsingPost().WithPath("/hooks/cursor-import"))
              .RespondWith(Response.Create().WithStatusCode(202).WithBody("{}"));

        var (workspace, paths) = CursorCommandTestFixtures.WorkspaceWithBubblesInShuffledOrder("comp-B");

        var rc = await CursorCommand.RunAsync(
            args: ["import", "--workspace", workspace],
            baseUrl: server.Url!,
            pathsOverride: paths
        );

        await Assert.That(rc).IsEqualTo(0);
        var posts = server.FindLogEntries(Request.Create().UsingPost().WithPath("/hooks/cursor-import"));
        await Assert.That(posts.Count).IsEqualTo(1);

        // Deserialise the posted body and verify bubble order matches fullConversationHeadersOnly
        var body = posts[0].RequestMessage.Body!;
        using var doc = JsonDocument.Parse(body);
        var bubbles = doc.RootElement.GetProperty("bubbles").EnumerateArray().ToList();
        await Assert.That(bubbles.Count).IsEqualTo(3);
        // Expected order: A, B, C  (as declared in fullConversationHeadersOnly)
        await Assert.That(bubbles[0].GetProperty("bubbleId").GetString()).IsEqualTo("comp-B:bub-A");
        await Assert.That(bubbles[1].GetProperty("bubbleId").GetString()).IsEqualTo("comp-B:bub-B");
        await Assert.That(bubbles[2].GetProperty("bubbleId").GetString()).IsEqualTo("comp-B:bub-C");
    }

    [Test]
    public async Task Import_returns_nonzero_when_post_fails() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().UsingGet().WithPath("/api/cursor/comp-A/watermark"))
              .RespondWith(Response.Create().WithStatusCode(404));
        server.Given(Request.Create().UsingPost().WithPath("/hooks/cursor-import"))
              .RespondWith(Response.Create().WithStatusCode(500));

        var (workspace, paths) = CursorCommandTestFixtures.WorkspaceWithOneComposer("comp-A");

        var rc = await CursorCommand.RunAsync(
            args: ["import", "--workspace", workspace],
            baseUrl: server.Url!,
            pathsOverride: paths
        );

        await Assert.That(rc).IsEqualTo(1);
    }
}

static class CursorCommandTestFixtures {
    /// <summary>
    /// Creates a temporary directory containing:
    /// <list type="bullet">
    ///   <item>A workspace storage subdir with <c>workspace.json</c> (folder=file://&lt;tempDir&gt;)
    ///         and a <c>state.vscdb</c> with <c>composer.composerData</c> referencing the composer.</item>
    ///   <item>A global <c>state.vscdb</c> with the composer header (unifiedMode=agent) and
    ///         a minimal <c>composerData:&lt;id&gt;</c> blob so <c>BuildPayload</c> can read it.</item>
    /// </list>
    /// Returns <c>(workspaceFolder, CursorPaths)</c> pointing to the temp tree.
    /// The <c>CursorPaths.WorkspaceStorageDir</c> is set to the fixture's workspace storage dir so
    /// <see cref="CursorCommand.ResolveWorkspaces"/> finds the fixture workspace.
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
        CreateDb(globalDb, conn => {
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
            var ids      = generatingBubbleIds ?? [];
            var idsJson  = "[" + string.Join(",", ids.Select(id => $"\"{id}\"")) + "]";
            var dataJson = $$"""
                {"modelConfig":{"modelName":"claude-4"},"fullConversationHeadersOnly":[],
                 "generatingBubbleIds":{{idsJson}},"status":"idle"}
                """;
            ExecParam(conn, "INSERT INTO cursorDiskKV VALUES (@k, @v)",
                key: $"composerData:{composerId}", value: dataJson);
        });

        // ── workspace storage ────────────────────────────────────────────────
        var wsStorageDir = Path.Combine(root, "workspaceStorage");
        var wsSubdir     = Path.Combine(wsStorageDir, "ws-fixture");
        Directory.CreateDirectory(wsSubdir);

        // The workspace folder is just the temp root itself
        var workspaceFolder = root;
        var folderUri       = $"file://{workspaceFolder}";
        File.WriteAllText(Path.Combine(wsSubdir, "workspace.json"),
            $$"""{"folder":"{{folderUri}}"}""");

        var wsDb = Path.Combine(wsSubdir, "state.vscdb");
        CreateDb(wsDb, conn => {
            Exec(conn, "CREATE TABLE ItemTable (key TEXT PRIMARY KEY, value TEXT);");
            ExecParam(conn, "INSERT INTO ItemTable VALUES ('composer.composerData', @v)",
                value: $$"""{"selectedComposerId":"{{composerId}}","selectedComposerIds":["{{composerId}}"]}""");
        });

        // Build paths pointing at our fixture tree
        var paths = new CursorPaths(
            UserDir:             root,
            WorkspaceStorageDir: wsStorageDir,
            GlobalStateDb:       globalDb
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
        CreateDb(globalDb, conn => {
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
            ExecParam(conn, "INSERT INTO cursorDiskKV VALUES (@k, @v)",
                key: $"composerData:{composerId}", value: dataJson);

            // Insert bubbles in shuffled order: B, C, A
            foreach (var (bid, text) in new[] { (bubIdB, "msg B"), (bubIdC, "msg C"), (bubIdA, "msg A") }) {
                var bJson = $$"""{"bubbleId":"{{bid}}","type":1,"createdAt":"2024-01-01T00:00:00Z","text":"{{text}}"}""";
                ExecParam(conn, "INSERT INTO cursorDiskKV VALUES (@k, @v)",
                    key: $"bubbleId:{composerId}:{bid}", value: bJson);
            }
        });

        var wsStorageDir = Path.Combine(root, "workspaceStorage");
        var wsSubdir     = Path.Combine(wsStorageDir, "ws-fixture");
        Directory.CreateDirectory(wsSubdir);

        var workspaceFolder = root;
        var folderUri       = $"file://{workspaceFolder}";
        File.WriteAllText(Path.Combine(wsSubdir, "workspace.json"),
            $$"""{"folder":"{{folderUri}}"}""");

        var wsDb = Path.Combine(wsSubdir, "state.vscdb");
        CreateDb(wsDb, conn => {
            Exec(conn, "CREATE TABLE ItemTable (key TEXT PRIMARY KEY, value TEXT);");
            ExecParam(conn, "INSERT INTO ItemTable VALUES ('composer.composerData', @v)",
                value: $$"""{"selectedComposerId":"{{composerId}}","selectedComposerIds":["{{composerId}}"]}""");
        });

        var paths = new CursorPaths(
            UserDir:             root,
            WorkspaceStorageDir: wsStorageDir,
            GlobalStateDb:       globalDb
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
