using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Capacitor.Models.Transcripts.Harness.Claude;

/// <summary>
/// Extracts Claude Code's own session title from a project transcript: a <c>custom-title</c>
/// line (the user's own rename) beats the last <c>ai-title</c> line, which beats the older
/// <c>{"type":"summary","summary":...}</c> shape still written by earlier Claude Code versions.
/// Returns null when the file is unreadable or carries no title; never throws.
///
/// <para>Stateless: a caller re-extracting the same path on every poll owns any
/// skip-when-unchanged caching itself.</para>
/// </summary>
public static class ClaudeNativeTitle {
    /// <summary>The title capped at 120 characters, for display.</summary>
    public static string? TryExtract(string transcriptPath) =>
        TryExtractWithKind(transcriptPath)?.Title is { } title ? title.Length > 120 ? title[..120] : title : null;

    /// <summary>The title uncapped, so every sender of one rename sends the same value and the server's own clamp
    /// is the only one applied. A rename is timed by the last top-level <c>timestamp</c> before the start of its last
    /// contiguous run.</summary>
    public static ClaudeTitle? TryExtractWithKind(string transcriptPath) {
        // Only title lines are parsed into a document. Every other line mentioning "timestamp" is scanned for a
        // top-level one without building a document, so a nested or malformed stamp never displaces a valid one.
        DateTimeOffset? lastStamp       = null;
        string?         currentRename   = null;
        DateTimeOffset? renameChangedAt = null;
        string?         lastAutoTitle   = null;
        string?         lastSummary     = null;

        try {
            // The agent owns this file and appends to it live — the open must not deny writers.
            using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            while (reader.ReadLine() is { } line) {
                if (line.Contains("\"custom-title\"") || line.Contains("\"ai-title\"") || line.Contains("\"summary\"")) {
                    try {
                        using var doc  = JsonDocument.Parse(line);
                        var       root = doc.RootElement;

                        switch (root.Str("type")) {
                            case "custom-title":
                                if (root.Str("customTitle") is { } custom && !string.IsNullOrWhiteSpace(custom)) {
                                    var trimmed = custom.Trim();
                                    if (!string.Equals(trimmed, currentRename, StringComparison.Ordinal)) {
                                        renameChangedAt = lastStamp;
                                        currentRename   = trimmed;
                                    }
                                }
                                break;
                            case "ai-title":
                                if (root.Str("aiTitle") is { } ai && !string.IsNullOrWhiteSpace(ai)) lastAutoTitle = ai.Trim();
                                break;
                            case "summary":
                                if (root.Str("summary") is { } summary && !string.IsNullOrWhiteSpace(summary)) lastSummary = summary.Trim();
                                break;
                        }
                    } catch (JsonException) { }
                }

                if (line.Contains("\"timestamp\"") && TopLevelTimestamp(line) is { } stamp) lastStamp = stamp;
            }
        } catch {
            return null;
        }

        if (currentRename is not null) return new ClaudeTitle(currentRename, IsRename: true, renameChangedAt);

        var auto = lastAutoTitle ?? lastSummary;
        return auto is null ? null : new ClaudeTitle(auto, IsRename: false, null);
    }

    static DateTimeOffset? TopLevelTimestamp(string line) {
        var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(line.Length));

        try {
            var reader = new Utf8JsonReader(buffer.AsSpan(0, Encoding.UTF8.GetBytes(line, buffer)));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;

            DateTimeOffset? found = null;

            // Read to the end even after a find: a line that is not whole JSON contributes nothing.
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName) {
                var isStamp = reader.ValueTextEquals("timestamp"u8);
                if (!reader.Read()) return null;

                if (isStamp && reader.TokenType == JsonTokenType.String
                 && DateTimeOffset.TryParse(reader.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)) {
                    found = ts.ToUniversalTime();
                }

                reader.Skip();
            }

            return reader.TokenType == JsonTokenType.EndObject && !reader.Read() ? found : null;
        } catch (JsonException) {
            return null;
        } finally {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
