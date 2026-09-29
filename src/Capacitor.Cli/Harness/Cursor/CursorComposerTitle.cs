using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Core.Http;
using Microsoft.Data.Sqlite;

namespace Capacitor.Cli.Harness.Cursor;

/// <summary>The Cursor IDE's own name for an agent chat: <c>name</c> in the <c>composerData:&lt;session-id&gt;</c>
/// record of <c>globalStorage/state.vscdb</c>'s <c>cursorDiskKV</c> table, keyed by the same dashed id as the chat's
/// agent transcript. Blank until named; <c>"New Agent"</c> is the unnamed default and reads as no title.
///
/// <para>Lives in the CLI project, not Core, so the SQLite native bundle never reaches the AOT-published daemon.
/// Opened read-only and unpooled, so no handle outlives a read and holds the IDE's own database open.</para></summary>
public sealed class CursorComposerTitle(string stateDbPath, string dashedSessionId) : IHarnessTitleStore {
    static CursorComposerTitle() => SqliteNativeResolver.Register();

    public bool RecordsChangeTime => false;

    public StoreTitle? Read() {
        if (!File.Exists(stateDbPath)) return null;

        try {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = stateDbPath,
                Mode       = SqliteOpenMode.ReadOnly,
                Cache      = SqliteCacheMode.Private,
                Pooling    = false,
            }.ToString());
            conn.Open();

            using var cmd = conn.CreateCommand();
            // The column is declared BLOB; the cast reads a text or a blob value alike.
            cmd.CommandText = "SELECT CAST(value AS TEXT) FROM cursorDiskKV WHERE key = $key";
            cmd.Parameters.AddWithValue("$key", $"composerData:{dashedSessionId}");

            if (cmd.ExecuteScalar() is not string json) return null;

            using var doc  = JsonDocument.Parse(json);
            var       name = doc.RootElement.Str("name");

            return string.IsNullOrWhiteSpace(name) || name.Trim() == "New Agent"
                ? null
                : new StoreTitle(name.Trim(), HarnessTitleKind.Rename, null);
        } catch {
            return null; // locked / not-a-db / schema drift / malformed record — best-effort
        }
    }
}
