using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Core.Http;
using Microsoft.Data.Sqlite;

namespace Capacitor.Cli.Harness.Antigravity;

/// <summary>Antigravity's single <c>conversation_summaries.db</c> (under the CLI's
/// <c>antigravity-cli</c> config root, keyed by <c>conversation_id</c> — confirmed against the installed
/// schema) carries one auto-generated <c>title</c> row per conversation; there is no separate rename
/// signal, so a title here always reads as <see cref="HarnessTitleKind.Auto"/>.
///
/// <para>Lives in the CLI project (not Core) so the Microsoft.Data.Sqlite native bundle never reaches
/// the AOT-published daemon — same rationale as <see cref="OpenCode.OpenCodeDb"/>. Opened read-only,
/// every step best-effort: a missing db / table / row is skipped, never thrown.</para></summary>
public sealed class AntigravitySummaryTitle(string dbPath, string dashedConversationId) : IHarnessTitleStore {
    static AntigravitySummaryTitle() => SqliteNativeResolver.Register();

    /// <summary>The conversation id is the db's own file name — <see cref="AntigravityPaths.ConversationDb"/>
    /// names it that way, so no separate parse of the transcript path is needed.</summary>
    public static AntigravitySummaryTitle? ForTranscript(string transcriptPath) {
        var dbPath = AntigravityPaths.ConversationDbFromTranscript(transcriptPath);

        return dbPath is null ? null : new AntigravitySummaryTitle(dbPath, Path.GetFileNameWithoutExtension(dbPath));
    }

    public bool RecordsChangeTime => false;

    public StoreTitle? Read() {
        if (!File.Exists(dbPath)) return null;

        try {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = dbPath,
                Mode       = SqliteOpenMode.ReadOnly,
                Cache      = SqliteCacheMode.Private,
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
