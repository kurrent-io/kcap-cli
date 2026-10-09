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
/// line it has already read would never reach the server. One writer at a time — a sync that finds
/// the file locked appends nothing, and the next one appends what it would have.
/// </remarks>
internal static class MistralVibeLiveTranscript {
    public static string PathFor(ConfigRoot config, string sessionId) =>
        config.Path("mistral-vibe", $"{sessionId}.jsonl");

    static string SourceFileFor(string transcriptPath) => transcriptPath + ".source";

    static readonly ConcurrentDictionary<string, (long Size, long Ticks)> LastSynced = new(StringComparer.Ordinal);

    /// <summary>Records which store, and which configured model, the copy follows, then syncs it.</summary>
    public static void Follow(string sessionDir, string transcriptPath, string? model) {
        Directory.CreateDirectory(Path.GetDirectoryName(transcriptPath)!);
        try { File.WriteAllLines(SourceFileFor(transcriptPath), [sessionDir, model ?? ""]); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        Sync(sessionDir, transcriptPath, model);
    }

    /// <summary>The store directory and configured model a copy follows, or null for a copy no
    /// hook has recorded.</summary>
    public static (string SessionDir, string? Model)? SourceOf(string transcriptPath) {
        string[] lines;
        try { lines = File.ReadAllLines(SourceFileFor(transcriptPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        return lines is [{ Length: > 0 } dir, ..]
            ? (dir, lines.Length > 1 && lines[1].Length > 0 ? lines[1] : null)
            : null;
    }

    /// <summary>Brings the copy up to date when its store has changed since the last sync, and
    /// returns the subagents that store has spawned; empty when nothing changed.</summary>
    public static IReadOnlyList<MistralVibeSubagent> Refresh(string transcriptPath) {
        if (SourceOf(transcriptPath) is not { } source || !Directory.Exists(source.SessionDir)) return [];

        var signature = StoreSignature(source.SessionDir);
        if (LastSynced.TryGetValue(transcriptPath, out var seen) && seen == signature) return [];

        var session = Sync(source.SessionDir, transcriptPath, source.Model);
        if (session is null) return [];

        LastSynced[transcriptPath] = signature;
        return session.Subagents;
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

    /// <summary>Deletes the copy, and its subagents' copies, once the session has ended. A resumed
    /// session rebuilds them in the same order, so the server's line positions still line up. Only
    /// kcap's own copies are touched: a legacy session tails Vibe's <c>messages.jsonl</c> itself.</summary>
    /// <remarks>Each copy's last usage stamp outlives it in a sidecar: the server already holds the
    /// tokens stamped so far, and a rebuilt copy that started from zero would count them again.</remarks>
    public static void Discard(ConfigRoot config, string sessionId, string transcriptPath) {
        var own = PathFor(config, sessionId);
        if (!string.Equals(Path.GetFullPath(transcriptPath), Path.GetFullPath(own), StringComparison.Ordinal)) return;

        var dir   = Path.GetDirectoryName(own)!;
        var files = (Directory.Exists(dir) ? Directory.EnumerateFiles(dir, $"{sessionId}-*").ToList() : [])
            .Append(own).Append(SourceFileFor(own))
            .Where(file => !file.EndsWith(UsageSuffix, StringComparison.Ordinal))
            .ToList();

        foreach (var copy in files.Where(file => file.EndsWith(".jsonl", StringComparison.Ordinal))) KeepLastStamp(copy);
        foreach (var file in files)
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    const string UsageSuffix = ".usage";

    static void KeepLastStamp(string copy) {
        try {
            if (!File.Exists(copy)) return;
            if (File.ReadLines(copy).LastOrDefault(line => line.Contains(MistralVibeUsageStamp.Key, StringComparison.Ordinal)) is { } stamped)
                File.WriteAllLines(copy + UsageSuffix, [stamped]);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static IEnumerable<string> StampedBefore(string transcriptPath) {
        try { return File.Exists(transcriptPath + UsageSuffix) ? File.ReadAllLines(transcriptPath + UsageSuffix) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Appends the entries finished since the last sync, stamping the tokens counted since
    /// the last stamp. Null when another writer holds the copy, so nothing was appended.</summary>
    public static MistralVibeSession? Sync(string sessionDir, string transcriptPath, string? model) {
        var session = MistralVibeUnifiedStore.Read(sessionDir);
        if (session.Lines.Count == 0 && File.Exists(transcriptPath)) return session;

        Directory.CreateDirectory(Path.GetDirectoryName(transcriptPath)!);

        FileStream stream;
        try {
            stream = new FileStream(transcriptPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        } catch (IOException) {
            return null;
        }

        using (stream) {
            var existing = new List<string>();
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true)) {
                while (reader.ReadLine() is { } line) existing.Add(line);
            }

            var written = existing.Select(EntryKey).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var fresh   = session.Lines.Where(line => EntryKey(line) is { } key && written.Add(key)).ToList();
            var since   = MistralVibeUsageStamp.LastStamped(StampedBefore(transcriptPath).Concat(existing));
            var stamped = MistralVibeUsageStamp.Apply(fresh, since, session.Usage, model);

            stream.Seek(0, SeekOrigin.End);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
            foreach (var line in stamped) writer.WriteLine(line);
        }

        return session;
    }

    /// <summary>The entry's id, or the line itself for an entry that carries none.</summary>
    static string? EntryKey(string line) {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.IsObject && doc.RootElement.Str("id") is { } id ? id : line;
        } catch (JsonException) {
            return null;
        }
    }
}
