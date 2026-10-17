using System.Text.Json;

namespace Capacitor.Cli.Capture;

internal static class TranscriptCapture {
    internal static readonly int ReasonCount = Enum.GetValues<RedactionLossReason>().Length;

    /// <summary>
    /// Encodes lines another capture may also be sending, inside a hook's time. Stops at a line
    /// that cannot redact in time instead of marking it, since a marker sent under its number makes
    /// the server drop the redacted line a later pass sends; and before the next line once
    /// <paramref name="stop"/> says the caller's time is up, after at least one line.
    /// </summary>
    public static CapturedLines EncodeUntilHeld(
            IReadOnlyList<string> rawLines, Func<bool> stop, Action<RedactionLossReason, int> reportLoss) =>
        EncodeUntilHeld(rawLines, stop, TimeProvider.System, reportLoss);

    internal static CapturedLines EncodeUntilHeld(
            IReadOnlyList<string> rawLines, Func<bool> stop, TimeProvider time, Action<RedactionLossReason, int> reportLoss,
            SecretPatterns? patterns = null) {
        var lines = new List<string>(rawLines.Count);
        var losses = new int[ReasonCount];
        foreach (var raw in rawLines) {
            if (lines.Count > 0 && stop()) break;
            var captured = SecretRedactor.RedactLineWithOutcome(raw, new RedactionBudget(time), patterns ?? SecretRedactor.WatcherPatterns);
            if (captured.Loss is { } reason) {
                if (reason.IsTransient()) break;
                losses[(int)reason]++;
            }
            lines.Add(Mark(captured).Line);
        }
        ReportLosses(losses, reportLoss);
        return new(lines, lines.Count);
    }

    /// <summary>
    /// Encodes a shutdown tail under one budget for all of it, taking a line's finished retry from
    /// <paramref name="redacted"/> when there is one. Stops at a line that cannot redact in time:
    /// spooling what follows would make the server drop that line later.
    /// </summary>
    public static CapturedLines EncodeTail(
            IReadOnlyList<string> rawLines, IReadOnlyList<int> lineNumbers, RedactionBudget budget,
            Func<int, string, RedactionOutcome?> redacted, Action<RedactionLossReason, int> reportLoss) {
        var lines = new List<string>(rawLines.Count);
        var losses = new int[ReasonCount];
        for (var i = 0; i < rawLines.Count; i++) {
            var captured = redacted(lineNumbers[i], rawLines[i])
                        ?? SecretRedactor.RedactLineWithOutcome(rawLines[i], budget, SecretRedactor.WatcherPatterns);
            if (captured.Loss is { } reason) {
                if (reason.IsTransient()) break;
                losses[(int)reason]++;
            }
            lines.Add(Mark(captured).Line);
        }
        ReportLosses(losses, reportLoss);
        return new(lines, lines.Count);
    }

    internal static RedactionOutcome Encode(string rawLine, TimeProvider time, SecretPatterns? patterns = null) =>
        Mark(SecretRedactor.RedactLineWithOutcome(rawLine, new RedactionBudget(time), patterns ?? SecretRedactor.WatcherPatterns));

    /// <summary>Encodes a line of a recording read once, end to end, under the recording budget.</summary>
    public static RedactionOutcome EncodeRecorded(string rawLine) => EncodeRecorded(rawLine, TimeProvider.System);

    internal static RedactionOutcome EncodeRecorded(string rawLine, TimeProvider time) => Mark(RedactRecorded(rawLine, time));

    // A pass over a whole recording has no watcher loop to keep responsive: it gets the
    // out-of-process regex deadline and the record budget a held line's retries end at.
    internal static readonly TimeSpan RecordingBudget = TimeSpan.FromSeconds(60);

    static RedactionOutcome RedactRecorded(string rawLine, TimeProvider time) =>
        SecretRedactor.RedactLineWithOutcome(rawLine, new RedactionBudget(time, RecordingBudget), SecretRedactor.OutOfProcessPatterns.Value);

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
