using System.Text.Json;

namespace Capacitor.Cli.Capture;

internal static class TranscriptCapture {
    internal static readonly int ReasonCount = Enum.GetValues<RedactionLossReason>().Length;

    public static List<string> EncodeLines(IEnumerable<string> rawLines, Action<RedactionLossReason, int> reportLoss) {
        var lines = new List<string>();
        var losses = new int[ReasonCount];
        foreach (var raw in rawLines) {
            var captured = Encode(raw);
            lines.Add(captured.Line);
            if (captured.Loss is { } reason) losses[(int)reason]++;
        }
        ReportLosses(losses, reportLoss);
        return lines;
    }

    /// <summary>
    /// Encodes a shutdown tail under one budget for all of it. Stops at a line that cannot redact in
    /// time: spooling what follows would make the server drop that line later.
    /// </summary>
    public static CapturedLines EncodeTail(
            IReadOnlyList<string> rawLines, RedactionBudget budget, Action<RedactionLossReason, int> reportLoss) {
        var lines = new List<string>(rawLines.Count);
        var losses = new int[ReasonCount];
        foreach (var raw in rawLines) {
            var captured = SecretRedactor.RedactLineWithOutcome(raw, budget, SecretRedactor.WatcherPatterns);
            if (captured.Loss is { } reason) {
                if (reason.IsTransient()) break;
                losses[(int)reason]++;
            }
            lines.Add(Mark(captured).Line);
        }
        ReportLosses(losses, reportLoss);
        return new(lines, lines.Count);
    }

    public static RedactionOutcome Encode(string rawLine) => Encode(rawLine, TimeProvider.System);

    internal static RedactionOutcome Encode(string rawLine, TimeProvider time) =>
        Mark(SecretRedactor.RedactLineWithOutcome(rawLine, time));

    internal static RedactionOutcome Mark(RedactionOutcome outcome) {
        if (outcome.Loss is not { } reason) return outcome;
        var marker = new CaptureLossMarker {
            Reason = CaptureLossMarker.ReasonName(reason),
            InputUtf16Length = outcome.InputUtf16Length,
            InputUtf8Bytes = outcome.InputUtf8Bytes
        };
        return outcome with { Line = JsonSerializer.Serialize(marker, CaptureJsonContext.Default.CaptureLossMarker) };
    }

    static void ReportLosses(int[] losses, Action<RedactionLossReason, int> reportLoss) {
        for (var i = 0; i < losses.Length; i++) {
            if (losses[i] > 0) reportLoss((RedactionLossReason)i, losses[i]);
        }
    }
}
