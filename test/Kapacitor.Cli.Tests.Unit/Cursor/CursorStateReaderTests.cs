using Kapacitor.Cli.Core.Cursor;
using Microsoft.Data.Sqlite;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Kapacitor.Cli.Tests.Unit.Cursor;

public class CursorStateReaderTests {
    static string CreateFixtureDb(Action<SqliteConnection> seed) {
        var path = Path.Combine(Path.GetTempPath(), $"cursor-{Guid.NewGuid():N}.vscdb");
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using (var cmd = conn.CreateCommand()) {
            cmd.CommandText = """
                              CREATE TABLE ItemTable     (key TEXT PRIMARY KEY, value TEXT);
                              CREATE TABLE cursorDiskKV  (key TEXT PRIMARY KEY, value TEXT);
                              """;
            cmd.ExecuteNonQuery();
        }
        seed(conn);
        return path;
    }

    [Test]
    public async Task Reads_selectedComposerIds_from_workspace_db() {
        var path = CreateFixtureDb(conn => {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO ItemTable VALUES ('composer.composerData', @v)";
            cmd.Parameters.AddWithValue("@v", """{"selectedComposerIds":["c1","c2"]}""");
            cmd.ExecuteNonQuery();
        });
        var ids = await CursorStateReader.ListWorkspaceComposerIdsAsync(path);
        await Assert.That(ids).IsEquivalentTo(new[] { "c1", "c2" });
    }

    [Test]
    public async Task Accepts_workspace_db_with_allComposers_populated() {
        var path = CreateFixtureDb(conn => {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO ItemTable VALUES ('composer.composerData', @v)";
            cmd.Parameters.AddWithValue("@v",
                """{"selectedComposerIds":["c1"],"allComposers":[{"composerId":"c1","name":"T"},{"composerId":"c2","name":"U"}]}""");
            cmd.ExecuteNonQuery();
        });
        var ids = await CursorStateReader.ListWorkspaceComposerIdsAsync(path);
        await Assert.That(ids).IsEquivalentTo(new[] { "c1", "c2" });
    }

    [Test]
    public async Task Reads_composer_header_from_global_db() {
        var path = CreateFixtureDb(conn => {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO ItemTable VALUES ('composer.composerHeaders', @v)";
            cmd.Parameters.AddWithValue("@v",
                """{"allComposers":[{"composerId":"c1","unifiedMode":"agent","name":"Test","createdAt":1,"lastUpdatedAt":2}]}""");
            cmd.ExecuteNonQuery();
        });
        var hdr = await CursorStateReader.GetComposerHeaderAsync(path, "c1");
        await Assert.That(hdr).IsNotNull();
        await Assert.That(hdr!.UnifiedMode).IsEqualTo("agent");
    }

    [Test]
    public async Task Lists_bubbles_for_composer() {
        var path = CreateFixtureDb(conn => {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                              INSERT INTO cursorDiskKV VALUES ('bubbleId:c1:b1', 'v1');
                              INSERT INTO cursorDiskKV VALUES ('bubbleId:c1:b2', 'v2');
                              INSERT INTO cursorDiskKV VALUES ('bubbleId:c2:other', 'x');
                              """;
            cmd.ExecuteNonQuery();
        });
        var bubbles = await CursorStateReader.ListBubblesAsync(path, "c1");
        await Assert.That(bubbles.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Gets_content_blob_by_key() {
        var path = CreateFixtureDb(conn => {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO cursorDiskKV VALUES ('composer.content.abc', '# README')";
            cmd.ExecuteNonQuery();
        });
        var content = await CursorStateReader.GetContentBlobAsync(path, "composer.content.abc");
        await Assert.That(content).IsEqualTo("# README");
    }

    [Test]
    public async Task Reads_extended_composer_header_fields() {
        var path = CreateFixtureDb(conn => {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO ItemTable VALUES ('composer.composerHeaders', @v)";
            cmd.Parameters.AddWithValue("@v", """
                {
                  "allComposers": [{
                    "composerId": "c1",
                    "unifiedMode": "agent",
                    "name": "My Session",
                    "createdAt": 1000,
                    "lastUpdatedAt": 2000,
                    "subtitle": "Fix the bug",
                    "totalLinesAdded": 42,
                    "totalLinesRemoved": 7,
                    "filesChangedCount": 3,
                    "trackedGitRepos": [
                      {"repoPath": "/home/user/repo", "branchNames": ["main", "feature/x"]}
                    ]
                  }]
                }
                """);
            cmd.ExecuteNonQuery();
        });
        var hdr = await CursorStateReader.GetComposerHeaderAsync(path, "c1");
        await Assert.That(hdr).IsNotNull();
        await Assert.That(hdr!.Subtitle).IsEqualTo("Fix the bug");
        await Assert.That(hdr.TotalLinesAdded).IsEqualTo(42);
        await Assert.That(hdr.TotalLinesRemoved).IsEqualTo(7);
        await Assert.That(hdr.FilesChangedCount).IsEqualTo(3);
        await Assert.That(hdr.TrackedGitRepos).IsNotNull();
        await Assert.That(hdr.TrackedGitRepos!.Count).IsEqualTo(1);
        await Assert.That(hdr.TrackedGitRepos[0].RepoPath).IsEqualTo("/home/user/repo");
        await Assert.That(hdr.TrackedGitRepos[0].BranchNames).IsNotNull();
        await Assert.That(hdr.TrackedGitRepos[0].BranchNames!.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Reads_composer_header_with_missing_optional_fields() {
        var path = CreateFixtureDb(conn => {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO ItemTable VALUES ('composer.composerHeaders', @v)";
            // Minimal header — no subtitle, line counts, or tracked repos
            cmd.Parameters.AddWithValue("@v",
                """{"allComposers":[{"composerId":"c1","unifiedMode":"agent","createdAt":1,"lastUpdatedAt":2}]}""");
            cmd.ExecuteNonQuery();
        });
        var hdr = await CursorStateReader.GetComposerHeaderAsync(path, "c1");
        await Assert.That(hdr).IsNotNull();
        await Assert.That(hdr!.Subtitle).IsNull();
        await Assert.That(hdr.TotalLinesAdded).IsEqualTo(0);
        await Assert.That(hdr.TotalLinesRemoved).IsEqualTo(0);
        await Assert.That(hdr.FilesChangedCount).IsEqualTo(0);
        await Assert.That(hdr.TrackedGitRepos).IsNull();
    }
}
