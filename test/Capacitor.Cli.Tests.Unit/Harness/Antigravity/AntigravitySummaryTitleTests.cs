using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Harness.Antigravity;
using Microsoft.Data.Sqlite;

namespace Capacitor.Cli.Tests.Unit.Harness.Antigravity;

public class AntigravitySummaryTitleTests {
    [TempDir] public required TempDir Tmp { get; init; }

    internal const string ConversationId = "46744763-380c-4d48-b6c9-d89e6feefeb1";

    string BuildDb(string? title) => BuildSummaryDb(Tmp.PathTo("conversation_summaries.db"), title);

    internal static string BuildSummaryDb(string path, string? title) {
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString())) {
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
        }

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

    /// <summary>Pins the real layout: the title lives in the root's single <c>conversation_summaries.db</c>, and the
    /// per-conversation <c>conversations/&lt;id&gt;.db</c> beside it has no summary table — a reader opening that one
    /// reads null, so this fails for it.</summary>
    [Test]
    public async Task ForTranscript_reads_the_roots_summary_db_not_the_conversation_db() {
        string root = Tmp.CreateDir("antigravity-cli");
        BuildSummaryDb(Path.Combine(root, "conversation_summaries.db"), "From the summary db");

        string conversations  = Tmp.CreateDir("antigravity-cli", "conversations");
        var    conversationDb = Path.Combine(conversations, $"{ConversationId}.db");
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = conversationDb, Pooling = false }.ToString())) {
            conn.Open();
            using var create = conn.CreateCommand();
            create.CommandText = "CREATE TABLE steps (id INTEGER PRIMARY KEY)";
            create.ExecuteNonQuery();
        }

        var transcriptPath = Path.Combine(root, "brain", ConversationId, ".system_generated", "logs", "transcript_full.jsonl");

        var title = AntigravitySummaryTitle.ForTranscript(transcriptPath)!.Read();

        await Assert.That(title!.Title).IsEqualTo("From the summary db");
    }

    [Test]
    public async Task Read_releases_the_db_file() {
        var path = BuildDb("Released");

        await Assert.That(new AntigravitySummaryTitle(path, ConversationId).Read()!.Title).IsEqualTo("Released");

        File.Move(path, Tmp.PathTo("moved.db"));

        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task ForTranscript_returns_null_for_an_unrecognized_shape() {
        await Assert.That(AntigravitySummaryTitle.ForTranscript(Tmp.PathTo("not-a-transcript.jsonl"))).IsNull();
    }
}
