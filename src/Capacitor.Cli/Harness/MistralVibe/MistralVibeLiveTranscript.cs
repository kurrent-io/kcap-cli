using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Harness.MistralVibe;

/// <summary>
/// The append-only JSONL file the watcher tails for a unified Vibe session. Vibe hands hooks the
/// session's store directory as <c>transcript_path</c>, which no line tail can follow, so the hook
/// appends the entries finished so far and records which store the copy follows; the watcher then
/// keeps it current, since the turn's last message often completes after the last hook has run.
/// </summary>
/// <remarks>
/// Appends only, keyed on entry id, because the watcher resumes from a line offset: rewriting a
/// line it has already read would never reach the server. One writer at a time — a second hook that
/// finds the file locked skips its sync, and the next hook appends what it would have.
/// </remarks>
internal static class MistralVibeLiveTranscript {
    public static string PathFor(ConfigRoot config, string sessionId) =>
        config.Path("mistral-vibe", $"{sessionId}.jsonl");

    static string SourceFileFor(string transcriptPath) => transcriptPath + ".source";

    static readonly ConcurrentDictionary<string, (long Size, long Ticks)> LastSynced = new(StringComparer.Ordinal);

    /// <summary>Syncs the copy from <paramref name="sessionDir"/> and remembers that store for
    /// <see cref="Refresh"/>.</summary>
    public static void Follow(string sessionDir, string transcriptPath) {
        Sync(sessionDir, transcriptPath);
        try { File.WriteAllText(SourceFileFor(transcriptPath), sessionDir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Brings the copy up to date from the store its hook recorded, when that store has
    /// changed since the last sync.</summary>
    public static void Refresh(string transcriptPath) {
        string sessionDir;
        try { sessionDir = File.ReadAllText(SourceFileFor(transcriptPath)).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        if (!Directory.Exists(sessionDir)) return;

        var signature = StoreSignature(sessionDir);
        if (LastSynced.TryGetValue(transcriptPath, out var seen) && seen == signature) return;
        if (Sync(sessionDir, transcriptPath)) LastSynced[transcriptPath] = signature;
    }

    /// <summary>A publish rewrites <c>CURRENT</c> and every change between publishes appends to the
    /// journal, so their sizes and write times move whenever the history does.</summary>
    static (long Size, long Ticks) StoreSignature(string sessionDir) {
        var journal = Path.Combine(sessionDir, "journal");
        var files   = Directory.Exists(journal) ? Directory.EnumerateFiles(journal, "*.jsonl") : [];
        long size = 0, ticks = 0;
        foreach (var file in files.Append(Path.Combine(sessionDir, "CURRENT"))) {
            var info = new FileInfo(file);
            if (!info.Exists) continue;
            size += info.Length;
            ticks = Math.Max(ticks, info.LastWriteTimeUtc.Ticks);
        }
        return (size, ticks);
    }

    /// <summary>Deletes the copy once its session has ended. A resumed session rebuilds it in the
    /// same order, so the server's line positions still line up. Only kcap's own copy is touched:
    /// a legacy session tails Vibe's <c>messages.jsonl</c> itself.</summary>
    public static void Discard(ConfigRoot config, string sessionId, string transcriptPath) {
        if (!string.Equals(Path.GetFullPath(transcriptPath), Path.GetFullPath(PathFor(config, sessionId)), StringComparison.Ordinal)) return;

        foreach (var file in new[] { transcriptPath, SourceFileFor(transcriptPath) })
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>False when another writer holds the copy, so nothing was appended.</summary>
    public static bool Sync(string sessionDir, string transcriptPath) {
        var finished = MistralVibeUnifiedStore.ReadLines(sessionDir);
        if (finished.Count == 0 && File.Exists(transcriptPath)) return true;

        Directory.CreateDirectory(Path.GetDirectoryName(transcriptPath)!);

        FileStream stream;
        try {
            stream = new FileStream(transcriptPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        } catch (IOException) {
            return false;
        }

        using (stream) {
            var written = new HashSet<string>(StringComparer.Ordinal);
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true)) {
                while (reader.ReadLine() is { } line)
                    if (EntryKey(line) is { } key) written.Add(key);
            }

            stream.Seek(0, SeekOrigin.End);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
            foreach (var line in finished)
                if (EntryKey(line) is { } key && written.Add(key)) writer.WriteLine(line);
        }

        return true;
    }

    /// <summary>The entry's id, or the line itself for an entry that carries none.</summary>
    static string? EntryKey(string line) {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.Str("id") is { } id ? id : line;
        } catch (JsonException) {
            return null;
        }
    }
}
