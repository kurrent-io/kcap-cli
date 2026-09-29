using System.Globalization;
using System.Text.Json;

namespace Capacitor.Models.Transcripts.Harness.Claude;

/// <summary>
/// Extracts Claude Code's own session title from a project transcript: a <c>custom-title</c>
/// line (the user's own rename) beats the last <c>ai-title</c> line, which beats the older
/// <c>{"type":"summary","summary":...}</c> shape still written by earlier Claude Code versions.
/// Returns null when the file is unreadable or carries no title; never throws. Capped at 120
/// chars — the <c>/hooks/set-title</c> and <c>/hooks/harness-title</c> limit.
///
/// <para>Stateless by design — a caller re-extracting the same path on every poll (the daemon's
/// title loop) owns any skip-when-unchanged caching itself, scoped to its own per-agent
/// lifetime rather than this type's, which has none.</para>
/// </summary>
public static class ClaudeNativeTitle {
    public static string? TryExtract(string transcriptPath) => TryExtractWithKind(transcriptPath)?.Title;

    public static ClaudeTitle? TryExtractWithKind(string transcriptPath) {
        DateTimeOffset? lastTimestamp = null;
        // The value of the custom-title run currently in progress, and when that run began —
        // an identical re-append does not restart it, but a switch to a different value does,
        // even back to one seen earlier (a rename undone by the user is still a rename).
        string? currentRenameValue = null;
        DateTimeOffset? currentRenameChangedAt = null;
        string? lastAutoTitle = null;
        string? lastSummary = null;

        try {
            // The agent owns this file and appends to it live — the open must not deny writers.
            using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            while (reader.ReadLine() is { } line) {
                if (!line.Contains("\"timestamp\"") && !line.Contains("\"custom-title\"")
                 && !line.Contains("\"ai-title\"") && !line.Contains("\"summary\"")) continue;

                try {
                    using var doc  = JsonDocument.Parse(line);
                    var       root = doc.RootElement;

                    switch (root.Str("type")) {
                        case "custom-title":
                            if (root.Str("customTitle") is { } custom && !string.IsNullOrWhiteSpace(custom)) {
                                var trimmed = custom.Trim();
                                if (!string.Equals(trimmed, currentRenameValue, StringComparison.Ordinal)) {
                                    currentRenameChangedAt = lastTimestamp;
                                    currentRenameValue     = trimmed;
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

                    if (root.TryGetProperty("timestamp", out var tsElement) && tsElement.ValueKind == JsonValueKind.String
                     && DateTimeOffset.TryParse(tsElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)) {
                        lastTimestamp = ts.ToUniversalTime();
                    }
                } catch (JsonException) { }
            }
        } catch {
            return null;
        }

        if (currentRenameValue is not null) return new ClaudeTitle(Cap(currentRenameValue), IsRename: true, currentRenameChangedAt);

        var auto = lastAutoTitle ?? lastSummary;
        return auto is null ? null : new ClaudeTitle(Cap(auto), IsRename: false, null);
    }

    static string Cap(string title) => title.Length > 120 ? title[..120] : title;
}
