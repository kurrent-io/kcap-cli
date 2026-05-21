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
    public async Task GetContentBlobs_returns_requested_keys_in_one_call() {
        var path = CreateFixtureDb(conn => {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                              INSERT INTO cursorDiskKV VALUES ('composer.content.a', 'one');
                              INSERT INTO cursorDiskKV VALUES ('composer.content.b', 'two');
                              INSERT INTO cursorDiskKV VALUES ('composer.content.c', 'three');
                              INSERT INTO cursorDiskKV VALUES ('bubbleId:other',     'noise');
                              """;
            cmd.ExecuteNonQuery();
        });
        var blobs = await CursorStateReader.GetContentBlobsAsync(path, new[] {
            "composer.content.a",
            "composer.content.b",
            "composer.content.c"
        });
        await Assert.That(blobs.Count).IsEqualTo(3);
        await Assert.That(blobs["composer.content.a"]).IsEqualTo("one");
        await Assert.That(blobs["composer.content.b"]).IsEqualTo("two");
        await Assert.That(blobs["composer.content.c"]).IsEqualTo("three");
    }

    [Test]
    public async Task GetContentBlobs_omits_missing_keys_and_returns_empty_dict_for_empty_input() {
        var path = CreateFixtureDb(conn => {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO cursorDiskKV VALUES ('composer.content.a', 'one')";
            cmd.ExecuteNonQuery();
        });

        var empty = await CursorStateReader.GetContentBlobsAsync(path, []);
        await Assert.That(empty.Count).IsEqualTo(0);

        var partial = await CursorStateReader.GetContentBlobsAsync(path, new[] {
            "composer.content.a",
            "composer.content.missing"
        });
        await Assert.That(partial.Count).IsEqualTo(1);
        await Assert.That(partial.ContainsKey("composer.content.missing")).IsFalse();
        await Assert.That(partial["composer.content.a"]).IsEqualTo("one");
    }

    [Test]
    public async Task GetContentBlobs_deduplicates_input_keys() {
        var path = CreateFixtureDb(conn => {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO cursorDiskKV VALUES ('composer.content.a', 'one')";
            cmd.ExecuteNonQuery();
        });
        var blobs = await CursorStateReader.GetContentBlobsAsync(path, new[] {
            "composer.content.a",
            "composer.content.a",
            "composer.content.a"
        });
        await Assert.That(blobs.Count).IsEqualTo(1);
        await Assert.That(blobs["composer.content.a"]).IsEqualTo("one");
    }

    [Test]
    public async Task Reads_extended_composer_header_fields() {
        // Uses the real Cursor wire shape: branches is an array of objects with branchName field.
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
                      {"repoPath": "/home/user/repo", "branches": [
                        {"branchName": "main", "lastInteractionAt": 1779302743015},
                        {"branchName": "feature/x", "lastInteractionAt": 1779302700000}
                      ]}
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
        await Assert.That(hdr.TrackedGitRepos[0].BranchNames[0]).IsEqualTo("main");
        await Assert.That(hdr.TrackedGitRepos[0].BranchNames[1]).IsEqualTo("feature/x");
    }

    [Test]
    public async Task Reads_branches_from_legacy_string_array_shape() {
        // Defensive backward compat: older Cursor schemas stored branchNames as a flat
        // string array ("branchNames": ["main", "feature/x"]) rather than the current
        // object-array form.  The parser must handle both shapes.
        var path = CreateFixtureDb(conn => {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO ItemTable VALUES ('composer.composerHeaders', @v)";
            cmd.Parameters.AddWithValue("@v", """
                {
                  "allComposers": [{
                    "composerId": "c1",
                    "unifiedMode": "agent",
                    "createdAt": 1000,
                    "lastUpdatedAt": 2000,
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
        await Assert.That(hdr!.TrackedGitRepos).IsNotNull();
        await Assert.That(hdr.TrackedGitRepos!.Count).IsEqualTo(1);
        await Assert.That(hdr.TrackedGitRepos[0].BranchNames).IsNotNull();
        await Assert.That(hdr.TrackedGitRepos[0].BranchNames!.Count).IsEqualTo(2);
        await Assert.That(hdr.TrackedGitRepos[0].BranchNames[0]).IsEqualTo("main");
        await Assert.That(hdr.TrackedGitRepos[0].BranchNames[1]).IsEqualTo("feature/x");
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
