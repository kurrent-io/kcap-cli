using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Policy;

namespace Capacitor.Cli.Harness.Codex;

/// <summary>The turn set a watched Codex session declares to the judge, or why it declares none.</summary>
internal sealed record CodexDeclaration(PolicyJudgeTurnSetV1? Turns, string? WindowlessReason);

/// <summary>
/// Declares a watched Codex session's newest human turns as the server stored them, each identified by the hash
/// of the line as the watcher sent it, after redaction. The newest human line must be acknowledged first: the server
/// cannot see one it has not stored, so declaring only older turns would let a stale prompt stand in for the one the
/// call belongs to. A line the redactor could not finish was never stored as itself, so the set goes windowless
/// rather than name it.
/// </summary>
internal static class CodexJudgeDeclarationReader {
    public const int MaxTurns = 16;

    /// <summary>A rollout past this is not scanned inside a hook's budget; the consultation goes windowless.</summary>
    internal const long MaxBytes = 64L * 1024 * 1024;

    static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    sealed record HumanLine(int Number, string Raw, string? TurnId);

    public static async Task<CodexDeclaration> ReadAsync(
            string transcriptPath, string sessionId, WatchAckCursor cursor, TimeProvider time, TimeSpan reReadBudget, CancellationToken ct) {
        // Only the count is polled while it waits; the rollout is scanned before the wait and once after it.
        var scan = Scan(transcriptPath);
        if (scan.Reason is { } reason) return new(null, reason);

        var deadline = time.GetUtcNow() + reReadBudget;
        int? known = null;
        while (true) {
            // A cursor that cannot be read may be mid-replace, so only a missing one means no watcher.
            if (cursor.TryRead(sessionId, out var missing) is { } count) known = count;
            else if (missing) return new(null, "no_ack_cursor");

            // The newest human line is not yet acknowledged: the watcher is about to send it.
            if (scan.NewestHuman <= known || time.GetUtcNow() >= deadline) break;
            await Task.Delay(Poll, time, ct).ConfigureAwait(false);
        }

        if (known is not { } acked) return new(null, "no_ack_cursor");

        // A human line appended during the wait may be one the server does not hold yet.
        if (scan.NewestHuman <= acked) {
            scan = Scan(transcriptPath);
            if (scan.Reason is { } again) return new(null, again);
        }

        if (scan.Recent.Count == 0) return new(null, "no_turns");
        if (scan.NewestHuman > acked) return new(null, "newest_turn_unacknowledged");

        var declared = new List<PolicyJudgeDeclaredTurnV1>(scan.Recent.Count);
        foreach (var line in scan.Recent) {
            var sent = SecretRedactor.RedactLineWithOutcome(line.Raw);
            if (sent.Loss is not null) return new(null, "redaction_incomplete");
            declared.Add(new(TranscriptIds.CodexRecord(sent.Line).ToString(), line.TurnId));
        }

        return new(new PolicyJudgeTurnSetV1([.. declared], null), null);
    }

    /// <summary>The newest human lines, oldest first.</summary>
    sealed record ScanResult(IReadOnlyList<HumanLine> Recent, int NewestHuman, string? Reason);

    static ScanResult Scan(string path) {
        var recent = new Queue<HumanLine>();
        var newest = 0;

        try {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxBytes) return new([], 0, "transcript_too_large");

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var number = 0;
            while (reader.ReadLine() is { } raw) {
                number++;
                if (HumanTurn(raw) is not { } turnId) continue;

                newest = number;
                recent.Enqueue(new(number, raw, turnId.Length == 0 ? null : turnId));
                if (recent.Count > MaxTurns) recent.Dequeue();
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return new([], 0, "transcript_unreadable");
        }

        return new([.. recent], newest, null);
    }

    /// <summary>The line's turn id ("" when it carries none) when it is a human user message, else null.</summary>
    static string? HumanTurn(string raw) {
        // A cheap skip for the many lines that cannot be a user message, whatever their spacing.
        if (!raw.Contains("\"user\"", StringComparison.Ordinal)) return null;

        try {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.Str("type") != "response_item" || root.Obj("payload") is not { } payload
             || payload.Str("type") != "message" || payload.Str("role") != "user")
                return null;

            var pass  = payload.Obj("internal_chat_message_metadata_passthrough");
            IReadOnlyList<string>? kinds = pass?.Arr("content_item_kinds") is { } k
                ? [.. k.EnumerateArray().Where(e => e.IsString).Select(e => e.GetString()!)]
                : null;

            return CodexPrelude.IsHuman(Text(payload), kinds) ? pass?.Str("turn_id") ?? "" : null;
        } catch (JsonException) {
            return null;
        }
    }

    // Joined as the server's normalizer joins them, so both sides judge the same text.
    static string Text(JsonElement payload) {
        if (payload.Arr("content") is not { } content) return "";

        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray()) {
            if (block.Str("type") is not ("input_text" or "output_text" or "text") || block.Str("text") is not { } text) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text);
        }
        return sb.ToString();
    }
}
