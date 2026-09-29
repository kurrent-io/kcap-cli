using System.Globalization;
using System.Text.Json;
using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>Codex appends <c>{id, thread_name, updated_at}</c> to <c>session_index.jsonl</c> whenever a thread is
/// named or renamed; the last line for an id is current, and <c>updated_at</c> is when that name was set.
/// <c>codex exec</c> sessions are never indexed.</summary>
public sealed class CodexSessionIndexTitle(string codexHome, string dashlessSessionId) : IHarnessTitleStore {
    public bool RecordsChangeTime => true;

    public StoreTitle? Read() {
        var path = Path.Combine(codexHome, "session_index.jsonl");
        StoreTitle? last = null;

        try {
            if (!File.Exists(path)) return null;

            foreach (var line in File.ReadLinesShared(path)) {
                if (!line.Contains(dashlessSessionId[..8], StringComparison.OrdinalIgnoreCase)) continue;

                try {
                    using var doc  = JsonDocument.Parse(line);
                    var       root = doc.RootElement;

                    if (!Guid.TryParse(root.Str("id"), out var id) || id.ToString("N") != dashlessSessionId) continue;
                    if (string.IsNullOrWhiteSpace(root.Str("thread_name"))) continue;

                    DateTimeOffset? at = DateTimeOffset.TryParse(root.Str("updated_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)
                        ? ts.ToUniversalTime()
                        : null;

                    last = new StoreTitle(root.Str("thread_name")!.Trim(), HarnessTitleKind.Rename, at);
                } catch (JsonException) { }
            }
        } catch (IOException) {
            return null;
        } catch (UnauthorizedAccessException) {
            return null;
        }

        return last;
    }
}
