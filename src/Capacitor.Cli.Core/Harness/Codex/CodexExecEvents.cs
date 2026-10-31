using System.Text.Json;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>Folds the JSONL event stream <c>codex exec --json</c> writes to stdout.</summary>
sealed class CodexExecEvents {
    long _input;
    long _cachedInput;
    long _cacheWrite;
    long _output;

    public int     CompletedTurns   { get; private set; }
    public string? LastAgentMessage { get; private set; }

    /// <summary>The <c>turn.failed</c> error, else the last top-level <c>error</c> event.</summary>
    public string? FailureMessage { get; private set; }

    public bool TurnFailed { get; private set; }

    /// <summary>Null until a turn reports usage. Codex counts cached tokens inside <c>input_tokens</c>;
    /// they are split out so the shape matches the Claude runner's.</summary>
    public ClaudeCliUsage? Usage =>
        CompletedTurns == 0 ? null : new(Math.Max(0, _input - _cachedInput), _output, _cachedInput, _cacheWrite, null);

    public void Accept(string line) {
        if (string.IsNullOrWhiteSpace(line)) return;

        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            switch (root.Str("type")) {
                case "turn.completed":
                    CompletedTurns++;

                    if (root.Obj("usage") is { } usage) {
                        _input       += usage.Num("input_tokens")            ?? 0;
                        _cachedInput += usage.Num("cached_input_tokens")     ?? 0;
                        _cacheWrite  += usage.Num("cache_write_input_tokens") ?? 0;
                        _output      += usage.Num("output_tokens")           ?? 0;
                    }

                    break;
                case "turn.failed":
                    TurnFailed     = true;
                    FailureMessage = root.Obj("error")?.Str("message") ?? FailureMessage;

                    break;
                case "error" when !TurnFailed:
                    FailureMessage = root.Str("message") ?? FailureMessage;

                    break;
                case "item.completed" when root.Obj("item") is { } item && item.Str("type") == "agent_message":
                    LastAgentMessage = item.Str("text");

                    break;
            }
        } catch (JsonException) { }
    }

    /// <summary>Codex surfaces both conditions only as error text, so they are matched on the codes and
    /// statuses it embeds there.</summary>
    public static ClaudeCliFailure ClassifyFailure(string? message) {
        if (message is null) return ClaudeCliFailure.ProcessFailure;

        if (message.Contains("401 Unauthorized", StringComparison.Ordinal)
         || message.Contains("refresh_token_failed", StringComparison.Ordinal)
         || message.Contains("gateway_login_required", StringComparison.Ordinal))
            return ClaudeCliFailure.NotSignedIn;

        if (message.Contains("usage_limit_reached", StringComparison.Ordinal)
         || message.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
         || message.Contains("quota_exceeded", StringComparison.Ordinal)
         || message.Contains("insufficient_quota", StringComparison.Ordinal)
         || message.Contains("usage_not_included", StringComparison.Ordinal))
            return ClaudeCliFailure.SpendBudget;

        return ClaudeCliFailure.ProcessFailure;
    }
}
