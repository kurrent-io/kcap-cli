using System.Collections.Frozen;
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

    /// <summary>Every session's current index title, keyed by the dashless session id, from one pass over the
    /// index. Empty when the index is missing or unreadable.</summary>
    public static FrozenDictionary<string, StoreTitle> ReadAll(string codexHome) {
        var path = Path.Combine(codexHome, "session_index.jsonl");

        try {
            if (!File.Exists(path)) return FrozenDictionary<string, StoreTitle>.Empty;

            var latest = new Dictionary<string, StoreTitle>(StringComparer.Ordinal);

            foreach (var line in File.ReadLinesShared(path)) {
                if (Parse(line) is ({ } id, { } title)) latest[id.ToString("N")] = title;
            }

            return latest.ToFrozenDictionary(StringComparer.Ordinal);
        } catch (IOException) {
            return FrozenDictionary<string, StoreTitle>.Empty;
        } catch (UnauthorizedAccessException) {
            return FrozenDictionary<string, StoreTitle>.Empty;
        }
    }

    static StoreTitle? Scan(string path, Guid id) {
        var         prefix = id.ToString("D")[..8];
        StoreTitle? last   = null;

        foreach (var line in File.ReadLinesShared(path)) {
            if (!line.Contains(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            if (Parse(line) is ({ } lineId, { } title) && lineId == id) last = title;
        }

        return last;
    }

    // A line naming no session or no title parses to nulls.
    static (Guid? Id, StoreTitle? Title) Parse(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return (null, null);
            if (!Guid.TryParse(root.Str("id"), out var id)) return (null, null);
            if (root.Str("thread_name") is not { } name || string.IsNullOrWhiteSpace(name)) return (null, null);

            DateTimeOffset? at = DateTimeOffset.TryParse(root.Str("updated_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)
                ? ts.ToUniversalTime()
                : null;

            return (id, new StoreTitle(name.Trim(), HarnessTitleKind.Rename, at));
        } catch (JsonException) {
            return (null, null);
        }
    }
}
