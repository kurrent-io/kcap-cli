using System.Globalization;
using System.Text.Json;
using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>Codex appends <c>{id, thread_name, updated_at}</c> to <c>session_index.jsonl</c> whenever a thread is
/// named or renamed; the last line for an id is current, and <c>updated_at</c> is when that name was set.
/// <c>codex exec</c> sessions are never indexed. The session id may be dashed or dashless; one that is not a GUID
/// reads null.</summary>
public sealed class CodexSessionIndexTitle(string codexHome, string sessionId) : IHarnessTitleStore {
    readonly Guid? _id = Guid.TryParse(sessionId, out var id) ? id : null;

    // The index is shared by every session and only grows; an unchanged file is not re-read.
    long        _statLength = -1;
    DateTime    _statLastWriteUtc;
    StoreTitle? _statResult;

    public bool RecordsChangeTime => true;

    public StoreTitle? Read() {
        if (_id is not { } id) return null;

        var path = Path.Combine(codexHome, "session_index.jsonl");

        try {
            var info = new FileInfo(path);
            if (!info.Exists) return null;

            if (info.Length == _statLength && info.LastWriteTimeUtc == _statLastWriteUtc) return _statResult;

            var result = Scan(path, id);
            _statLength       = info.Length;
            _statLastWriteUtc = info.LastWriteTimeUtc;

            return _statResult = result;
        } catch (IOException) {
            return null;
        } catch (UnauthorizedAccessException) {
            return null;
        }
    }

    static StoreTitle? Scan(string path, Guid id) {
        var         prefix = id.ToString("D")[..8];
        StoreTitle? last   = null;

        foreach (var line in File.ReadLinesShared(path)) {
            if (!line.Contains(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            try {
                using var doc  = JsonDocument.Parse(line);
                var       root = doc.RootElement;

                if (!Guid.TryParse(root.Str("id"), out var lineId) || lineId != id) continue;
                if (root.Str("thread_name") is not { } name || string.IsNullOrWhiteSpace(name)) continue;

                DateTimeOffset? at = DateTimeOffset.TryParse(root.Str("updated_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)
                    ? ts.ToUniversalTime()
                    : null;

                last = new StoreTitle(name.Trim(), HarnessTitleKind.Rename, at);
            } catch (JsonException) { }
        }

        return last;
    }
}
