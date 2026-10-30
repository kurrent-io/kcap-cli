using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Per-server record of how far each Claude and Codex transcript was imported. The server's last-line
/// watermark only counts lines that produced an event, so a transcript ending in lines it ignores reads
/// as unfinished forever; a mark taken at that same watermark says those lines were already sent.
/// A mark applies only while the server still answers the watermark it recorded, so a server that lost
/// or gained lines since falls back to its own watermark.
/// </summary>
internal sealed class TranscriptImportLedger {
    internal const string FileName = "transcript-imports.json";

    readonly ConfigRoot                                                          _config;
    readonly Dictionary<string, Dictionary<string, TranscriptImportMark>>        _marks;
    readonly Dictionary<(string Server, string SessionId), TranscriptImportMark> _pending = [];
    readonly Lock                                                                _gate    = new();

    TranscriptImportLedger(ConfigRoot config, Dictionary<string, Dictionary<string, TranscriptImportMark>> marks) {
        _config = config;
        _marks  = marks;
    }

    public static TranscriptImportLedger Load(ConfigRoot config) => new(config, Read(config, out _) ?? Empty());

    /// <summary>A ledger that only records; <see cref="Save"/> merges into whatever the file holds then.</summary>
    public static TranscriptImportLedger ForRecording(ConfigRoot config) => new(config, Empty());

    /// <summary>The line to resume from when the server's watermark is still the one the mark recorded.</summary>
    public int? ResumeFrom(string server, string sessionId, int watermark) {
        lock (_gate) {
            return _marks.TryGetValue(server, out var sessions)
                && sessions.TryGetValue(sessionId, out var mark)
                && mark.Watermark == watermark
                && mark.Lines > watermark
                    ? mark.Lines
                    : null;
        }
    }

    public void Record(string server, string sessionId, TranscriptImportMark mark) {
        lock (_gate) _pending[(server, sessionId)] = mark;
    }

    /// <summary>Merges this run's marks into the file. Best effort: a mark that never lands costs only a
    /// resend on the next import.</summary>
    public void Save() {
        lock (_gate) {
            if (_pending.Count == 0) return;

            try {
                using (_config.AcquireLock(FileName)) {
                    // A file that exists but cannot be read is left alone rather than replaced by this run's marks.
                    var onDisk = Read(_config, out var unreadable) ?? (unreadable ? null : Empty());
                    if (onDisk is null) return;

                    foreach (var ((server, sessionId), mark) in _pending) {
                        if (!onDisk.TryGetValue(server, out var sessions)) onDisk[server] = sessions = new(StringComparer.Ordinal);
                        sessions[sessionId] = mark;
                    }

                    Directory.CreateDirectory(_config.Directory);
                    AtomicFile.Replace(
                        _config.Path(FileName),
                        JsonSerializer.Serialize(onDisk, TranscriptImportLedgerJsonContext.Default.DictionaryStringDictionaryStringTranscriptImportMark));
                }

                _pending.Clear();
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException or AbandonedMutexException) { }
        }
    }

    static Dictionary<string, Dictionary<string, TranscriptImportMark>> Empty() => new(StringComparer.Ordinal);

    static Dictionary<string, Dictionary<string, TranscriptImportMark>>? Read(ConfigRoot config, out bool unreadable) {
        unreadable = false;

        try {
            var json  = File.ReadAllText(config.Path(FileName));
            var marks = JsonSerializer.Deserialize(json, TranscriptImportLedgerJsonContext.Default.DictionaryStringDictionaryStringTranscriptImportMark);

            return marks?
                .Where(s => s.Value is not null)
                .ToDictionary(
                    s => s.Key,
                    s => s.Value.Where(m => m.Value is not null).ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal),
                    StringComparer.Ordinal);
        } catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException) {
            return null;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            unreadable = true;

            return null;
        }
    }
}
