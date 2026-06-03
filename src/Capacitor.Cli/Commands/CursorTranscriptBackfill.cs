using System.Net;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

/// <summary>
/// One-shot transcript-line backfill. Reads the shared transcript watermark
/// for <paramref name="sessionId"/> (<c>GET /api/sessions/{sid}/last-line</c>
/// — the same route every transcript-driven normalizer uses), opens the
/// JSONL transcript file, and POSTs every line past the watermark as a
/// single batch to <c>POST /hooks/transcript</c> with
/// <c>Vendor: "cursor"</c>. No internal retry — the next hook invocation
/// re-reads the (advanced) server watermark and resumes from the new HWM.
/// </summary>
public static class CursorTranscriptBackfill {
    static readonly TimeSpan WatermarkTimeout = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan BatchPostTimeout = TimeSpan.FromMilliseconds(1500);

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
                $"{baseUrl}/api/sessions/{sessionId}/last-line",
                WatermarkTimeout,
                ct
            );

            // 200 — body has last_line_number; 204 — stream exists but no
            // lines yet (resume from 0); 404 — stream doesn't exist (resume
            // from 0); any other non-2xx — fail-open, retry next hook.
            if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) {
                resumeFrom = 0;
            } else if (!resp.IsSuccessStatusCode) {
                return new Stats(0, Failed: true);
            } else {
                var       body = await resp.Content.ReadAsStringAsync(ct);
                using var doc  = JsonDocument.Parse(body);

                resumeFrom = doc.RootElement.TryGetProperty("last_line_number", out var ln) && ln.ValueKind == JsonValueKind.Number
                    ? ln.GetInt32() + 1
                    : 0;
            }
        } catch { return new Stats(0, Failed: true); }

        // Read every line past the watermark into the batch. Cursor's JSONL
        // is bounded by the agent turn count — practical sizes are dozens of
        // lines, not thousands; the server's HandleTranscript ingests them
        // in one shot.
        var lines       = new List<string>();
        var lineNumbers = new List<int>();

        try {
            await using var stream    = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var       reader    = new StreamReader(stream);
            var             lineIndex = 0;

            while (await reader.ReadLineAsync(ct) is { } line) {
                if (lineIndex >= resumeFrom && !string.IsNullOrWhiteSpace(line)) {
                    lines.Add(line);
                    lineNumbers.Add(lineIndex);
                }

                lineIndex++;
            }
        } catch { return new(0, Failed: true); }

        if (lines.Count == 0 || budget()) return new(0, Failed: false);

        var batch = new TranscriptBatch {
            SessionId   = sessionId,
            Lines       = [..lines],
            LineNumbers = [..lineNumbers],
            Vendor      = "cursor",
        };

        var json = JsonSerializer.Serialize(batch, KapacitorJsonContext.Default.TranscriptBatch);

        HttpResponseMessage? resp2 = null;

        try {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            resp2 = await client.PostOnceAsync(
                $"{baseUrl}/hooks/transcript",
                content,
                BatchPostTimeout,
                ct
            );

            return resp2.IsSuccessStatusCode ? new(lines.Count, Failed: false) : new Stats(0, Failed: true);
        } catch {
            return new Stats(0, Failed: true);
        } finally {
            resp2?.Dispose();
        }
    }
}
