using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Core.Http;
using Microsoft.Data.Sqlite;

namespace Capacitor.Cli.Harness.Antigravity;

/// <summary>The product root's single <c>conversation_summaries.db</c> (beside <c>brain/</c> and
/// <c>conversations/</c>, keyed by the dashed <c>conversation_id</c>) carries one auto-generated <c>title</c> per
/// conversation. Antigravity records no rename signal, so a title here always reads as
/// <see cref="HarnessTitleKind.Auto"/>. The per-conversation <c>conversations/&lt;id&gt;.db</c> has no summary table.
///
/// <para>Lives in the CLI project, not Core, so the SQLite native bundle never reaches the AOT-published daemon.
/// Opened read-only and unpooled, so no handle outlives a read and blocks Antigravity replacing its own file.</para></summary>
public sealed class AntigravitySummaryTitle(string dbPath, string dashedConversationId) : IHarnessTitleStore {
    static AntigravitySummaryTitle() => SqliteNativeResolver.Register();

    public static AntigravitySummaryTitle? ForTranscript(string transcriptPath) =>
        AntigravityPaths.SummaryDbFromTranscript(transcriptPath) is (var dbPath, var conversationId)
            ? new AntigravitySummaryTitle(dbPath, conversationId)
            : null;

    public bool RecordsChangeTime => false;

    public StoreTitle? Read() {
        if (!File.Exists(dbPath)) return null;

        try {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = dbPath,
                Mode       = SqliteOpenMode.ReadOnly,
                Cache      = SqliteCacheMode.Private,
                Pooling    = false,
            }.ToString());
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT title FROM conversation_summaries WHERE conversation_id = $id";
            cmd.Parameters.AddWithValue("$id", dashedConversationId);

            var title = cmd.ExecuteScalar() as string;

            return string.IsNullOrWhiteSpace(title) ? null : new StoreTitle(title.Trim(), HarnessTitleKind.Auto, null);
        } catch {
            return null; // locked / not-a-db / schema drift — cost is best-effort
        }
    }
}
