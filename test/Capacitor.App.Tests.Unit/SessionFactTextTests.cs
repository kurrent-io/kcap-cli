using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.App.Tests.Unit;

public class SessionFactTextTests {
    [Test]
    [Arguments(950L, "950")]
    [Arguments(12_340L, "12.3k")]
    [Arguments(2_500_000L, "2.5M")]
    public async Task Counts_read_like_the_web(long value, string expected) =>
        await Assert.That(SessionFactText.Count(value)).IsEqualTo(expected);

    [Test]
    public async Task No_stats_reads_unknown_everywhere() {
        await Assert.That(SessionFactText.Tokens(null)).IsEqualTo(SessionFactText.Unknown);
        await Assert.That(SessionFactText.Cost(null)).IsEqualTo(SessionFactText.Unknown);
        await Assert.That(SessionFactText.Context(null)).IsEqualTo(SessionFactText.Unknown);
    }

    [Test]
    public async Task Tokens_sum_input_and_output_across_models_and_leave_out_the_cache() {
        var stats = new SessionStatsDto {
            TokenUsageByModel = [
                new() { Model = "opus", InputTokens = 10_000, OutputTokens = 2_000, CacheReadTokens = 5_000_000, CacheWriteTokens = 300_000 },
                new() { Model = "haiku", InputTokens = 2_340, OutputTokens = 500, CacheReadTokens = 90_000 },
            ],
        };

        await Assert.That(SessionFactText.Tokens(stats)).IsEqualTo("12.3k in · 2.5k out");
    }

    /// An older server sends no resolved window; a reported one still makes the fraction.
    [Test]
    public async Task Context_falls_back_to_the_reported_window() {
        var stats = new SessionStatsDto { PeakContextTokens = 50_000, PeakContextWindow = 100_000 };

        await Assert.That(SessionFactText.Context(stats)).IsEqualTo("50k / 100k (50%)");
    }

    [Test]
    public async Task Context_with_only_a_percentage_says_so() {
        var stats = new SessionStatsDto { ContextUsagePercent = 42.4, PeakContextWindow = 0 };

        await Assert.That(SessionFactText.Context(stats)).IsEqualTo("42% ctx");
    }

    [Test]
    [Arguments(null, "private", "activity", "Private")]
    [Arguments(null, null, "full", "Org members · Full")]
    [Arguments("scoped", "private", null, "People & teams")]
    [Arguments("none", "org_public", "full", "Private")]
    public async Task Session_sharing_reads_the_override_else_the_default(string? visibility, string? defaultVisibility, string? level, string expected) {
        var summary = new SessionSummaryDto { SessionId = "s1", Visibility = visibility, DefaultVisibility = defaultVisibility, DetailLevel = level };

        await Assert.That(SessionFactText.SessionSharing(summary)).IsEqualTo(expected);
    }

    [Test]
    public async Task Agent_sharing_is_unknown_when_the_server_does_not_say() {
        await Assert.That(SessionFactText.AgentSharing(null)).IsEqualTo(SessionFactText.Unknown);
        await Assert.That(SessionFactText.AgentSharing("none")).IsEqualTo("Private");
    }
}
