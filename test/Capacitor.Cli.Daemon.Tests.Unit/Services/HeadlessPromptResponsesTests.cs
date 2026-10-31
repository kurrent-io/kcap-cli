using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>A runner outcome becomes the wire answer: a started run's text or structured object, its failure kind, and
/// token counts that stay null when the harness reported none.</summary>
public class HeadlessPromptResponsesTests {
    static readonly ClaudeCliResult Ok = new("{\"title\":\"t\"}", "haiku", 0, 0, 0, 0, 0.01, 2);

    [Test]
    [Arguments("Timeout", "deadline")]
    [Arguments("ProcessFailure", "process_failure")]
    [Arguments("OutputUnparseable", "unparseable_output")]
    [Arguments("SpendBudget", "spend_budget")]
    [Arguments("NotSignedIn", "not_signed_in")]
    public async Task A_failure_is_a_started_run_with_its_kind(string failure, string kind) {
        var response = HeadlessPromptResponses.From(new(null, Enum.Parse<ClaudeCliFailure>(failure)), structured: false);

        await Assert.That(response.Started).IsTrue();
        await Assert.That(response.FailureKind).IsEqualTo(kind);
        await Assert.That(response.Text).IsNull();
    }

    [Test]
    public async Task A_failure_keeps_the_usage_it_reported() {
        var response = HeadlessPromptResponses.From(new(null, ClaudeCliFailure.Timeout, new ClaudeCliUsage(10, 5, 3, 2, 0.4)), structured: false);

        await Assert.That((response.InputTokens, response.OutputTokens, response.CostUsd)).IsEqualTo((15L, 5L, 0.4));
    }

    [Test]
    public async Task Success_with_a_schema_fills_the_structured_json() {
        var response = HeadlessPromptResponses.From(new(Ok, null), structured: true);

        await Assert.That(response).IsEqualTo(new HeadlessPromptResponse(true, null, null, "{\"title\":\"t\"}", null, null, 0.01));
    }

    [Test]
    public async Task Success_without_a_schema_fills_the_text() {
        var response = HeadlessPromptResponses.From(new(Ok, null), structured: false);

        await Assert.That(response.Text).IsEqualTo("{\"title\":\"t\"}");
        await Assert.That(response.StructuredJson).IsNull();
    }

    [Test]
    public async Task Input_tokens_include_cache_reads_and_writes() {
        var response = HeadlessPromptResponses.From(new(Ok, null, new ClaudeCliUsage(2, 7, 100, 50, 0.02)), structured: false);

        await Assert.That((response.InputTokens, response.OutputTokens, response.CostUsd)).IsEqualTo((152L, 7L, 0.02));
    }

    [Test]
    public async Task Unreported_usage_stays_null() {
        var response = HeadlessPromptResponses.From(new(Ok with { CostUsd = null }, null), structured: false);

        await Assert.That((response.InputTokens, response.OutputTokens, response.CostUsd)).IsEqualTo(((long?)null, (long?)null, (double?)null));
    }
}
