using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Commands;

/// <summary>
/// One-shot transcript-line backfill. Reads the watermark for
/// <paramref name="sessionId"/>, opens the transcript JSONL, and POSTs
/// each line whose index is past <c>last_line_number</c> until the
/// dispatcher budget expires (signalled via <paramref name="budget"/>) or
/// the transcript is fully drained or a POST fails. No internal retry —
/// the next hook invocation re-reads the (advanced) watermark.
/// </summary>
public static class CursorTranscriptBackfill {
    static readonly TimeSpan WatermarkTimeout = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan LinePostTimeout  = TimeSpan.FromSeconds(1);

    public readonly record struct Stats(int LinesPosted, bool Failed);

    public static async Task<Stats> RunAsync(
            HttpClient        client,
            string            baseUrl,
            string            sessionId,
            string?           transcriptPath,
            Func<bool>        budget,
            CancellationToken ct
        ) {
        if (string.IsNullOrEmpty(transcriptPath) || !File.Exists(transcriptPath)) {
            return new Stats(0, false);
        }

        int resumeFrom;
        try {
            using var resp = await client.GetOnceAsync(
                $"{baseUrl}/api/cursor-sessions/{sessionId}/transcript-watermark",
                WatermarkTimeout, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) {
                resumeFrom = 0;
            } else if (!resp.IsSuccessStatusCode) {
                return new Stats(0, Failed: true);
            } else {
                var body = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                resumeFrom = doc.RootElement.TryGetProperty("last_line_number", out var ln) && ln.ValueKind == JsonValueKind.Number
                    ? ln.GetInt32() + 1
                    : 0;
            }
        } catch { return new Stats(0, Failed: true); }

        var posted = 0;

        using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lineIndex = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null) {
            if (lineIndex < resumeFrom) { lineIndex++; continue; }
            if (budget()) return new Stats(posted, Failed: false);
            ct.ThrowIfCancellationRequested();

            var payload = new JsonObject {
                ["session_id"] = sessionId,
                ["line_index"] = lineIndex,
                ["line"]       = line
            }.ToJsonString();

            HttpResponseMessage? resp = null;
            try {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                resp = await client.PostOnceAsync(
                    $"{baseUrl}/hooks/transcript-line/cursor", content, LinePostTimeout, ct);
                if (!resp.IsSuccessStatusCode) return new Stats(posted, Failed: true);
                posted++;
            } catch { return new Stats(posted, Failed: true); }
            finally { resp?.Dispose(); }

            lineIndex++;
        }

        return new Stats(posted, Failed: false);
    }
}
