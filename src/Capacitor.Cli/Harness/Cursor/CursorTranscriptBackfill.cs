using System.Net;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Capture;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Harness.Cursor;

// Backfill respects the same source watermark, quarantine and attachment barriers as live capture.
public static class CursorTranscriptBackfill {
    static readonly TimeSpan WatermarkTimeout = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan BatchPostTimeout = TimeSpan.FromMilliseconds(1500);

    public readonly record struct Stats(int LinesPosted, bool Failed);

    public static async Task<Stats> RunAsync(
            CursorMarkers     markers,
            HttpClient        client,
            string            baseUrl,
            string            sessionId,
            string?           transcriptPath,
            Func<bool>        budget,
            TimeProvider      time,
            CancellationToken ct,
            string?           agentId    = null,
            bool              finalDrain = false
        ) {
        if (string.IsNullOrEmpty(transcriptPath) || !File.Exists(transcriptPath)) {
            return new Stats(0, false);
        }

        if (markers.IsQuarantined(sessionId)) {
            return new Stats(0, false);
        }

        if (markers.BarrierPending(sessionId, time.GetUtcNow(), CursorMarkers.DefaultBarrierBound)) {
            return new Stats(0, false);
        }

        int resumeFrom;

        var watermarkUrl = string.IsNullOrEmpty(agentId)
            ? $"{baseUrl}/api/sessions/{sessionId}/last-line"
            : $"{baseUrl}/api/sessions/{sessionId}/last-line?agentId={Uri.EscapeDataString(agentId)}";

        try {
            using var resp = await client.GetOnceAsync(
                watermarkUrl,
                time,
                WatermarkTimeout,
                ct
            );

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

        List<string> lines;
        List<int>    lineNumbers;

        try {
            await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var policy = finalDrain
                ? WatchCommand.IncompleteFinalLinePolicy.ConsumeIfComplete
                : WatchCommand.IncompleteFinalLinePolicy.Hold;
            var read = await WatchCommand.ReadNewCompleteLinesAsync(stream, resumeFrom, policy, ct);

            lines = TranscriptCapture.EncodeLines(read.Lines,
                (reason, count) => Console.Error.WriteLine($"Cursor capture loss: {count} record(s), {CaptureLossMarker.ReasonName(reason)}"));
            lineNumbers = read.LineNumbers;
        } catch { return new(0, Failed: true); }

        if (lines.Count == 0 || budget()) return new(0, Failed: false);

        if (markers.IsQuarantined(sessionId)
         || markers.BarrierPending(sessionId, time.GetUtcNow(), CursorMarkers.DefaultBarrierBound)) {
            return new Stats(0, false);
        }

        var batch = new TranscriptBatch {
            SessionId   = sessionId,
            AgentId     = agentId,
            Lines       = [..lines],
            LineNumbers = [..lineNumbers],
            Vendor      = "cursor",
        };

        var posted = 0;
        try {
            foreach (var chunk in TranscriptBatchBuffer.Split(batch)) {
                if (budget() || markers.IsQuarantined(sessionId)
                    || markers.BarrierPending(sessionId, time.GetUtcNow(), CursorMarkers.DefaultBarrierBound))
                    return new Stats(posted, Failed: false);
                var json = JsonSerializer.Serialize(chunk, CapacitorJsonContext.Default.TranscriptBatch);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await client.PostOnceAsync($"{baseUrl}/hooks/transcript", content, time, BatchPostTimeout, ct);
                if (!response.IsSuccessStatusCode) return new Stats(posted, Failed: true);
                posted += chunk.Lines.Length;
            }
            return new Stats(posted, Failed: false);
        } catch {
            return new Stats(posted, Failed: true);
        }
    }
}
