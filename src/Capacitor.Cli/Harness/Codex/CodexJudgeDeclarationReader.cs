using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Policy;

namespace Capacitor.Cli.Harness.Codex;

/// <summary>The turn set a watched Codex session declares to the judge, or why it declares none.</summary>
internal sealed record CodexDeclaration(PolicyJudgeTurnSetV1? Turns, string? WindowlessReason);

/// <summary>
/// Declares a watched Codex session's newest human turns as the server stored them. Only lines the root
/// watcher has had acknowledged are declared, since the server holds at least those and its mark can be no
/// further; each is identified by the hash of the line as the watcher sent it, after redaction. A line the
/// redactor could not finish was never stored as itself, so the set goes windowless rather than name it.
/// </summary>
internal static class CodexJudgeDeclarationReader {
    public const int MaxTurns = 16;

    /// <summary>A rollout past this is not scanned inside a hook's budget; the consultation goes windowless.</summary>
    internal const long MaxBytes = 64L * 1024 * 1024;

    static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    sealed record HumanLine(int Number, string Raw, string? TurnId);

    public static async Task<CodexDeclaration> ReadAsync(
            string transcriptPath, string sessionId, WatchAckCursor cursor, TimeProvider time, TimeSpan reReadBudget, CancellationToken ct) {
        var deadline = time.GetUtcNow() + reReadBudget;

        while (true) {
            if (cursor.TryRead(sessionId) is not { } acked) return new(null, "no_ack_cursor");

            var scan = Scan(transcriptPath, acked);
            if (scan.Reason is { } reason) return new(null, reason);

            // The newest human line is not yet acknowledged: the watcher is about to send it.
            if (scan.NewestHuman > acked && time.GetUtcNow() < deadline) {
                await Task.Delay(Poll, time, ct).ConfigureAwait(false);
                continue;
            }

            if (scan.Covered.Count == 0) return new(null, "no_turns");

            var declared = new List<PolicyJudgeDeclaredTurnV1>(scan.Covered.Count);
            foreach (var line in scan.Covered) {
                var sent = SecretRedactor.RedactLineWithOutcome(line.Raw);
                if (sent.Loss is not null) return new(null, "redaction_incomplete");
                declared.Add(new(TranscriptIds.CodexRecord(sent.Line).ToString(), line.TurnId));
            }

            return new(new PolicyJudgeTurnSetV1([.. declared], null), null);
        }
    }

    sealed record ScanResult(IReadOnlyList<HumanLine> Covered, int NewestHuman, string? Reason);

    static ScanResult Scan(string path, int acked) {
        var covered = new Queue<HumanLine>();
        var newest  = 0;

        try {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxBytes) return new([], 0, "transcript_too_large");

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var number = 0;
            while (reader.ReadLine() is { } raw) {
                number++;
                if (HumanTurn(raw) is not { } turnId) continue;

                newest = number;
                if (number > acked) continue;

                covered.Enqueue(new(number, raw, turnId.Length == 0 ? null : turnId));
                if (covered.Count > MaxTurns) covered.Dequeue();
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return new([], 0, "transcript_unreadable");
        }

        return new([.. covered], newest, null);
    }

    /// <summary>The line's turn id ("" when it carries none) when it is a human user message, else null.</summary>
    static string? HumanTurn(string raw) {
        if (!raw.Contains("\"role\":\"user\"", StringComparison.Ordinal)) return null;

        try {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !Is(root, "type", "response_item")
             || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
             || !Is(payload, "type", "message") || !Is(payload, "role", "user"))
                return null;

            IReadOnlyList<string>? kinds = null;
            string? turnId = null;
            if (payload.TryGetProperty("internal_chat_message_metadata_passthrough", out var pass) && pass.ValueKind == JsonValueKind.Object) {
                if (pass.TryGetProperty("turn_id", out var t) && t.ValueKind == JsonValueKind.String) turnId = t.GetString();
                if (pass.TryGetProperty("content_item_kinds", out var k) && k.ValueKind == JsonValueKind.Array)
                    kinds = [.. k.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)];
            }

            return CodexPrelude.IsHuman(Text(payload), kinds) ? turnId ?? "" : null;
        } catch (JsonException) {
            return null;
        }
    }

    // Joined as the server's normalizer joins them, so both sides judge the same text.
    static string Text(JsonElement payload) {
        if (!payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return "";

        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray()) {
            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var type)
             || type.GetString() is not ("input_text" or "output_text" or "text")
             || !block.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text.GetString());
        }
        return sb.ToString();
    }

    static bool Is(JsonElement e, string name, string value) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() == value;
}
