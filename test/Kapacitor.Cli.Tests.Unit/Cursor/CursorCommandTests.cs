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
            token: "fake",
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
            token: "fake",
            pathsOverride: paths
        );

        await Assert.That(rc).IsEqualTo(0);
        var posts = server.FindLogEntries(Request.Create().UsingPost().WithPath("/hooks/cursor-import"));
        await Assert.That(posts.Count).IsEqualTo(0);
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
    public static (string WorkspaceFolder, CursorPaths Paths) WorkspaceWithOneComposer(string composerId) {
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

            // Minimal composerData blob
            var dataJson = $$"""
                {"modelConfig":{"modelName":"claude-4"},"fullConversationHeadersOnly":[],
                 "generatingBubbleIds":[],"status":"idle"}
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
