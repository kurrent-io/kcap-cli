using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

/// <summary>Reads the result envelope `claude -p --output-format json` prints: the failure kind of an unrecovered run, and
/// the usage it reported, null when it reported none.</summary>
public class ClaudeCliRunnerEnvelopeTests {
    // What Claude Code 2.1 prints with no credentials.
    const string SignedOut = """
        {"type":"result","subtype":"success","is_error":true,"api_error_status":null,"num_turns":1,
         "result":"Not logged in · Please run /login","total_cost_usd":0,"modelUsage":{}}
        """;

    [Test]
    public async Task Signed_out_envelope_is_not_signed_in() {
        var outcome = ClaudeCliRunner.ClassifyFailure(SignedOut, nonZeroExit: true);

        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.NotSignedIn);
        await Assert.That(outcome.Usage).IsNull();
    }

    [Test]
    public async Task An_api_401_is_not_signed_in() {
        const string envelope = """{"type":"result","is_error":true,"api_error_status":401,"result":"OAuth token has expired"}""";

        await Assert.That(ClaudeCliRunner.ClassifyFailure(envelope, nonZeroExit: true).Failure).IsEqualTo(ClaudeCliFailure.NotSignedIn);
    }

    [Test]
    public async Task Usage_limit_envelope_is_spend_budget() {
        const string envelope = """{"type":"result","is_error":true,"result":"Claude AI usage limit reached|1793480400"}""";

        await Assert.That(ClaudeCliRunner.ClassifyFailure(envelope, nonZeroExit: true).Failure).IsEqualTo(ClaudeCliFailure.SpendBudget);
    }

    [Test]
    public async Task A_failed_run_keeps_the_usage_it_reported() {
        const string envelope = """
            {"type":"result","subtype":"error_max_turns","is_error":true,"total_cost_usd":0.02,
             "modelUsage":{"claude-haiku":{"inputTokens":10,"outputTokens":5,"cacheReadInputTokens":3,"cacheCreationInputTokens":2}}}
            """;

        var outcome = ClaudeCliRunner.ClassifyFailure(envelope, nonZeroExit: true);

        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.ProcessFailure);
        await Assert.That(outcome.Usage).IsEqualTo(new ClaudeCliUsage(10, 5, 3, 2, 0.02));
    }

    [Test]
    public async Task Reported_usage_sums_every_model() {
        const string envelope = """
            {"total_cost_usd":0.5,"modelUsage":{
              "a":{"inputTokens":1,"outputTokens":2,"cacheReadInputTokens":3,"cacheCreationInputTokens":4},
              "b":{"inputTokens":10,"outputTokens":20,"cacheReadInputTokens":30,"cacheCreationInputTokens":40}}}
            """;

        await Assert.That(ClaudeCliRunner.ReportedUsage(envelope)).IsEqualTo(new ClaudeCliUsage(11, 22, 33, 44, 0.5));
    }

    [Test]
    public async Task Empty_model_usage_reports_null() {
        await Assert.That(ClaudeCliRunner.ReportedUsage(SignedOut)).IsNull();
        await Assert.That(ClaudeCliRunner.ReportedUsage("plain text")).IsNull();
    }
}
