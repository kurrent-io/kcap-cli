using System.Globalization;
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
    /// is the only one applied. A rename is timed by the top-level <c>timestamp</c> of the last stamped line before
    /// the start of its last contiguous run.</summary>
    public static ClaudeTitle? TryExtractWithKind(string transcriptPath) {
        // Only title lines are parsed. The most recent other line carrying a "timestamp" is kept raw and parsed
        // only when a rename run starts; one whose only timestamp is nested falls back to the stamp resolved at
        // the previous run start, which is earlier, so the error favours a Regenerate.
        string?         lastStampLine   = null;
        DateTimeOffset? lastResolved    = null;
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
                                        lastResolved    = TopLevelTimestamp(lastStampLine) ?? lastResolved;
                                        lastStampLine   = null;
                                        renameChangedAt = lastResolved;
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

                if (line.Contains("\"timestamp\"")) lastStampLine = line;
            }
        } catch {
            return null;
        }

        if (currentRename is not null) return new ClaudeTitle(currentRename, IsRename: true, renameChangedAt);

        var auto = lastAutoTitle ?? lastSummary;
        return auto is null ? null : new ClaudeTitle(auto, IsRename: false, null);
    }

    static DateTimeOffset? TopLevelTimestamp(string? line) {
        if (line is null) return null;

        try {
            using var doc = JsonDocument.Parse(line);

            return DateTimeOffset.TryParse(doc.RootElement.Str("timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)
                ? ts.ToUniversalTime()
                : null;
        } catch (JsonException) {
            return null;
        }
    }
}
