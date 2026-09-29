using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Harness.Cursor;
using Microsoft.Data.Sqlite;

namespace Capacitor.Cli.Tests.Unit.Harness.Cursor;

public class CursorComposerTitleTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string SessionId = "5cdc4fcc-ff9e-4fdc-8d93-cf47ff5a140a";

    /// <summary>A <c>state.vscdb</c> shaped as the IDE writes it: a BLOB-declared <c>cursorDiskKV</c> holding JSON
    /// text, one row per composer.</summary>
    internal static string BuildStateDb(string path, string sessionId, string? name) {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();

        using (var create = conn.CreateCommand()) {
            create.CommandText = "CREATE TABLE cursorDiskKV (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)";
            create.ExecuteNonQuery();
        }

        using var insert = conn.CreateCommand();
        insert.CommandText = "INSERT INTO cursorDiskKV(key, value) VALUES ($key, $value)";
        insert.Parameters.AddWithValue("$key", $"composerData:{sessionId}");
        insert.Parameters.AddWithValue("$value",
            name is null ? $$"""{"_v":17,"composerId":"{{sessionId}}"}""" : $$"""{"_v":17,"composerId":"{{sessionId}}","name":"{{name}}"}""");
        insert.ExecuteNonQuery();

        return path;
    }

    [Test]
    public async Task Name_reads_as_a_rename() {
        var title = new CursorComposerTitle(BuildStateDb(Tmp.PathTo("state.vscdb"), SessionId, "Repository project overview"), SessionId).Read();

        await Assert.That(title!.Title).IsEqualTo("Repository project overview");
        await Assert.That(title.Kind).IsEqualTo(HarnessTitleKind.Rename);
    }

    [Test]
    [Arguments("New Agent")]
    [Arguments("")]
    [Arguments(null)]
    public async Task Default_or_blank_name_reads_null(string? name) {
        await Assert.That(new CursorComposerTitle(BuildStateDb(Tmp.PathTo("state.vscdb"), SessionId, name), SessionId).Read()).IsNull();
    }

    [Test]
    public async Task Missing_key_reads_null() {
        var db = BuildStateDb(Tmp.PathTo("state.vscdb"), "ffffffff-ffff-ffff-ffff-ffffffffffff", "Other");

        await Assert.That(new CursorComposerTitle(db, SessionId).Read()).IsNull();
    }

    [Test]
    public async Task Missing_db_reads_null() {
        await Assert.That(new CursorComposerTitle(Tmp.PathTo("absent.vscdb"), SessionId).Read()).IsNull();
    }

    [Test]
    public async Task Does_not_record_change_time() {
        await Assert.That(new CursorComposerTitle(Tmp.PathTo("absent.vscdb"), SessionId).RecordsChangeTime).IsFalse();
    }

    [Test]
    public async Task Reads_while_a_writer_holds_the_file() {
        var db = BuildStateDb(Tmp.PathTo("state.vscdb"), SessionId, "Live");

        using var writer = new FileStream(db, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        await Assert.That(new CursorComposerTitle(db, SessionId).Read()!.Title).IsEqualTo("Live");
    }
}
