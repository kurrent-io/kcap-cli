using System.Text.Json;

namespace Capacitor.Cli.Capture;

internal static class TranscriptCapture {
    public static List<string> EncodeLines(IEnumerable<string> rawLines, Action<RedactionLossReason, int> reportLoss) {
        var lines = new List<string>();
        var losses = new int[5];
        foreach (var raw in rawLines) {
            var captured = Encode(raw);
            lines.Add(captured.Line);
            if (captured.Loss is { } reason) losses[(int)reason]++;
        }
        for (var i = 0; i < losses.Length; i++) {
            if (losses[i] > 0) reportLoss((RedactionLossReason)i, losses[i]);
        }
        return lines;
    }

    public static RedactionOutcome Encode(string rawLine) {
        var outcome = SecretRedactor.RedactLineWithOutcome(rawLine);
        if (outcome.Loss is not { } reason) return outcome;
        var marker = new CaptureLossMarker {
            Reason = CaptureLossMarker.ReasonName(reason),
            InputUtf16Length = outcome.InputUtf16Length,
            InputUtf8Bytes = outcome.InputUtf8Bytes
        };
        return outcome with { Line = JsonSerializer.Serialize(marker, CaptureJsonContext.Default.CaptureLossMarker) };
    }
}
