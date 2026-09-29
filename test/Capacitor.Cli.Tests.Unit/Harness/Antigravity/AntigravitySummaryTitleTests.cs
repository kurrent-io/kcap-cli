using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Harness.Antigravity;
using Microsoft.Data.Sqlite;

namespace Capacitor.Cli.Tests.Unit.Harness.Antigravity;

public class AntigravitySummaryTitleTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string ConversationId = "46744763-380c-4d48-b6c9-d89e6feefeb1";

    string BuildDb(string? title) {
        var path = Tmp.PathTo("conversation_summaries.db");

        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();

        using (var create = conn.CreateCommand()) {
            create.CommandText = "CREATE TABLE conversation_summaries (conversation_id TEXT PRIMARY KEY, title TEXT NOT NULL DEFAULT '')";
            create.ExecuteNonQuery();
        }

        using var insert = conn.CreateCommand();
        insert.CommandText = "INSERT INTO conversation_summaries(conversation_id, title) VALUES ($id, $title)";
        insert.Parameters.AddWithValue("$id", ConversationId);
        insert.Parameters.AddWithValue("$title", (object?)title ?? "");
        insert.ExecuteNonQuery();

        return path;
    }

    [Test]
    public async Task Title_reads_as_auto() {
        var title = new AntigravitySummaryTitle(BuildDb("Fix the flaky test"), ConversationId).Read();

        await Assert.That(title!.Title).IsEqualTo("Fix the flaky test");
        await Assert.That(title.Kind).IsEqualTo(HarnessTitleKind.Auto);
    }

    [Test]
    public async Task Empty_title_reads_null() {
        await Assert.That(new AntigravitySummaryTitle(BuildDb(null), ConversationId).Read()).IsNull();
    }

    [Test]
    public async Task Unknown_conversation_reads_null() {
        await Assert.That(new AntigravitySummaryTitle(BuildDb("Something"), "ffffffff-ffff-ffff-ffff-ffffffffffff").Read()).IsNull();
    }

    [Test]
    public async Task Missing_db_reads_null() {
        await Assert.That(new AntigravitySummaryTitle(Tmp.PathTo("absent.db"), ConversationId).Read()).IsNull();
    }

    [Test]
    public async Task Does_not_record_change_time() {
        await Assert.That(new AntigravitySummaryTitle(Tmp.PathTo("absent.db"), ConversationId).RecordsChangeTime).IsFalse();
    }
}
