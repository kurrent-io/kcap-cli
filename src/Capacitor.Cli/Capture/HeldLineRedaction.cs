using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Capture;

/// <summary>
/// Captures one source's lines, holding the source back at a line whose redaction ran out of time.
/// The server drops a line numbered at or below one it has already taken, so nothing after a held
/// line is captured until a retry redacts it. Retries run off the watcher loop under a longer budget
/// each time and never end: a line is sent redacted or not at all.
/// </summary>
/// <param name="redactionClock">The clock record budgets read; the retry schedule reads <paramref name="time"/>.</param>
internal sealed class HeldLineRedaction(HeldLineStore store, TimeProvider time, TimeProvider redactionClock, Action<string> log) {
    HeldLine? _held;
    bool _loaded;
    Task<RedactionOutcome>? _attempt;
    string? _attemptSha256;

    // A retry's result outlives the hold until the source moves past the line: a failed send
    // re-reads it, and the live budget that failed once would start the ladder over.
    (int LineNumber, string Sha256, RedactionOutcome Outcome)? _settled;

    public HeldLine? Held {
        get {
            Load();
            return _held;
        }
    }

    public CapturedLines Capture(
            IReadOnlyList<string> rawLines, IReadOnlyList<int> lineNumbers, Action<RedactionLossReason, int> reportLoss) {
        Load();
        var lines  = new List<string>(rawLines.Count);
        var losses = new int[TranscriptCapture.ReasonCount];
        var consumed = 0;

        for (; consumed < rawLines.Count; consumed++) {
            if (Redact(rawLines[consumed], lineNumbers[consumed]) is not { } outcome) break;
            if (outcome.Loss is { } reason) losses[(int)reason]++;
            lines.Add(TranscriptCapture.Mark(outcome).Line);
        }

        for (var i = 0; i < losses.Length; i++) {
            if (losses[i] > 0) reportLoss((RedactionLossReason)i, losses[i]);
        }

        return new(lines, consumed);
    }

    /// <summary>Forgets a held line that reached the server some other way.</summary>
    public void ReleaseBelow(int nextLineNumber) {
        if (Held is { } held && held.LineNumber < nextLineNumber) Release();
    }

    RedactionOutcome? Redact(string raw, int lineNumber) {
        if (_settled is { } settled) {
            if (lineNumber == settled.LineNumber && Sha256(raw) == settled.Sha256) return settled.Outcome;
            if (lineNumber > settled.LineNumber) _settled = null;
        }

        if (_held is { } held && lineNumber >= held.LineNumber) {
            if (lineNumber == held.LineNumber && Sha256(raw) == held.LineSha256) return Retry(held, raw);
            Release();
        }

        var outcome = SecretRedactor.RedactLineWithOutcome(raw, redactionClock);
        if (outcome.Loss is not { } reason || !reason.IsTransient()) return outcome;

        var first = new HeldLine {
            SessionId     = store.SessionId,
            AgentId       = store.AgentId,
            LineNumber    = lineNumber,
            LineSha256    = Sha256(raw),
            Reason        = CaptureLossMarker.ReasonName(reason),
            Attempts      = 0,
            NextAttemptAt = time.GetUtcNow()
        };
        log($"Holding line {lineNumber} back: {first.Reason}; retrying off the loop");
        _attempt       = null;
        _attemptSha256 = null;
        Save(first);

        return Retry(first, raw);
    }

    RedactionOutcome? Retry(HeldLine held, string raw) {
        if (_attempt is { IsCompleted: true } done && _attemptSha256 == held.LineSha256) {
            _attempt = null;
            var outcome = done.IsCompletedSuccessfully ? done.Result : null;

            if (outcome is { Loss: null }) log($"Held line {held.LineNumber} redacted on attempt {held.Attempts + 1}");
            if (outcome is { Loss: null } || outcome?.Loss is { } final && !final.IsTransient()) {
                _settled = (held.LineNumber, held.LineSha256, outcome);
                Release();
                return outcome;
            }

            if (outcome is null) log($"Retry of held line {held.LineNumber} failed: {done.Exception?.GetBaseException().Message}");

            var attempts = held.Attempts + 1;
            var delay    = DelayAfter(attempts);
            log($"Held line {held.LineNumber} still unredacted after attempt {attempts}; next in {delay.TotalSeconds:0}s");
            Save(held with {
                Attempts      = attempts,
                Reason        = outcome?.Loss is { } r ? CaptureLossMarker.ReasonName(r) : held.Reason,
                NextAttemptAt = time.GetUtcNow() + delay
            });

            return null;
        }

        if (_attempt is null && time.GetUtcNow() >= held.NextAttemptAt) {
            var budget = new RedactionBudget(redactionClock, BudgetFor(held.Attempts + 1));
            _attemptSha256 = held.LineSha256;
            _attempt       = Task.Run(() => SecretRedactor.RedactLineWithOutcome(raw, budget, SecretRedactor.OutOfProcessPatterns.Value));
        }

        return null;
    }

    internal static TimeSpan BudgetFor(int attempt) => Doubling(attempt, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));

    internal static TimeSpan DelayAfter(int attempt) => Doubling(attempt, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));

    static TimeSpan Doubling(int attempt, TimeSpan first, TimeSpan cap) =>
        TimeSpan.FromSeconds(Math.Min(first.TotalSeconds * Math.Pow(2, Math.Max(0, attempt - 1)), cap.TotalSeconds));

    void Load() {
        if (_loaded) return;
        _loaded = true;
        _held   = store.Load();
    }

    void Save(HeldLine held) {
        _held = held;
        store.Save(held);
    }

    void Release() {
        _held          = null;
        _attempt       = null;
        _attemptSha256 = null;
        store.Delete();
    }

    static string Sha256(string raw) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
