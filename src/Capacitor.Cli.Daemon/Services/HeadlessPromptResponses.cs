using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>The answers <c>RunHeadlessPrompt</c> gives. The server alone decides <c>no_daemon</c>, <c>queued</c> and
/// <c>result_lost</c>, so none of them is here.</summary>
static class HeadlessPromptResponses {
    public const string NoHarness      = "no_harness";
    public const string BinaryMissing  = "binary_missing";
    public const string DeadlinePassed = "deadline_passed";

    public const string ProcessFailure    = "process_failure";
    public const string Deadline          = "deadline";
    public const string UnparseableOutput = "unparseable_output";
    public const string SpendBudget       = "spend_budget";
    public const string NotSignedIn       = "not_signed_in";

    public static HeadlessPromptResponse NotStarted(string? reason, string? error = null) =>
        new(false, reason, null, null, null, null, Error: error);

    public static HeadlessPromptResponse Failed(string kind, string? error) =>
        new(true, null, null, null, null, null, FailureKind: kind, Error: error);

    /// <summary>Input tokens count cache reads and writes, as every other vendor's prompt count does.</summary>
    public static HeadlessPromptResponse From(ClaudeCliOutcome outcome, bool structured) {
        var usage = outcome.Usage;
        var input = usage is null ? (long?)null : usage.InputTokens + usage.CacheReadTokens + usage.CacheWriteTokens;
        var cost  = usage?.CostUsd ?? outcome.Result?.CostUsd;

        if (outcome.Result is { } result) {
            return new(true, null, structured ? null : result.Result, structured ? result.Result : null,
                input, usage?.OutputTokens, cost);
        }

        var kind = outcome.Failure switch {
            ClaudeCliFailure.Timeout           => Deadline,
            ClaudeCliFailure.OutputUnparseable => UnparseableOutput,
            ClaudeCliFailure.SpendBudget       => SpendBudget,
            ClaudeCliFailure.NotSignedIn       => NotSignedIn,
            _                                  => ProcessFailure
        };

        var subtype = outcome.Subtype is "success" ? null : outcome.Subtype;

        return new(true, null, null, null, input, usage?.OutputTokens, cost, kind, outcome.Error ?? subtype ?? kind);
    }
}
