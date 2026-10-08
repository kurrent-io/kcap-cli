using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Harness.MistralVibe;

/// <summary>
/// The append-only JSONL file the watcher tails for a unified Vibe session. Vibe hands hooks the
/// session's store directory as <c>transcript_path</c>, which no line tail can follow, so each hook
/// appends the entries that have finished since the last one.
/// </summary>
/// <remarks>
/// Appends only, keyed on entry id, because the watcher resumes from a line offset: rewriting a
/// line it has already read would never reach the server. One writer at a time — a second hook that
/// finds the file locked skips its sync, and the next hook appends what it would have.
/// </remarks>
internal static class MistralVibeLiveTranscript {
    public static string PathFor(ConfigRoot config, string sessionId) =>
        config.Path("mistral-vibe", $"{sessionId}.jsonl");

    public static void Sync(string sessionDir, string transcriptPath) {
        var finished = MistralVibeUnifiedStore.ReadLines(sessionDir);
        if (finished.Count == 0 && File.Exists(transcriptPath)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(transcriptPath)!);

        FileStream stream;
        try {
            stream = new FileStream(transcriptPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        } catch (IOException) {
            return;
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
