using Kapacitor.Cli.Commands;

namespace Kapacitor.Cli.Tests.Unit;

public class ImportScopePromptTests {
    // --- BuildRepoChoices ---

    [Test]
    public async Task BuildRepoChoices_orders_current_first_then_alphabetical() {
        var choices = ImportScopePrompt.BuildRepoChoices(
            currentRepo: ("EventStore", "kapacitor"),
            discoveredRepos: [
                ("EventStore", "kurrentdb"),
                ("EventStore", "kapacitor"),  // dup with current
                ("alexeyzimarev", "scratchpad"),
            ]);

        await Assert.That(choices).IsEquivalentTo(new[] {
            "EventStore/kapacitor (current)",
            "alexeyzimarev/scratchpad",
            "EventStore/kurrentdb",
        });
    }

    [Test]
    public async Task BuildRepoChoices_no_current_repo_just_sorts_alphabetically() {
        var choices = ImportScopePrompt.BuildRepoChoices(
            currentRepo: null,
            discoveredRepos: [("Z-org", "z"), ("A-org", "a"), ("M-org", "m")]);

        await Assert.That(choices).IsEquivalentTo(new[] {
            "A-org/a",
            "M-org/m",
            "Z-org/z",
        });
    }

    [Test]
    public async Task BuildRepoChoices_deduplicates_discovered_set() {
        var choices = ImportScopePrompt.BuildRepoChoices(
            currentRepo: null,
            discoveredRepos: [("A", "x"), ("A", "x"), ("A", "y")]);

        await Assert.That(choices).IsEquivalentTo(new[] { "A/x", "A/y" });
    }

    // --- FormatSummary ---

    [Test]
    public async Task FormatSummary_All_scope() {
        var s = ImportScopePrompt.FormatSummary(
            scope: new ImportScope.All(),
            matchedCount: 47,
            repoSamples: ["EventStore/kapacitor", "EventStore/kurrentdb"],
            visibilityDescription: "org_public (from profile)");

        await Assert.That(s).Contains("scope:   everything");
        await Assert.That(s).Contains("matched: 47 sessions");
        await Assert.That(s).Contains("visibility: org_public (from profile)");
    }

    [Test]
    public async Task FormatSummary_Org_includes_org_name() {
        var s = ImportScopePrompt.FormatSummary(
            scope: new ImportScope.Org("EventStore"),
            matchedCount: 5,
            repoSamples: ["EventStore/kapacitor"],
            visibilityDescription: "private (--private)");

        await Assert.That(s).Contains("org repos only (EventStore)");
        await Assert.That(s).Contains("visibility: private (--private)");
    }

    [Test]
    public async Task FormatSummary_Repo_includes_owner_name() {
        var s = ImportScopePrompt.FormatSummary(
            scope: new ImportScope.Repo("EventStore", "kapacitor"),
            matchedCount: 3,
            repoSamples: ["EventStore/kapacitor"],
            visibilityDescription: "org_public (from profile)");

        await Assert.That(s).Contains("repository EventStore/kapacitor");
    }

    [Test]
    public async Task FormatSummary_caps_repo_samples_at_5() {
        var samples = Enumerable.Range(1, 9).Select(i => $"EventStore/r{i}").ToArray();
        var s = ImportScopePrompt.FormatSummary(
            scope: new ImportScope.All(),
            matchedCount: 50,
            repoSamples: samples,
            visibilityDescription: "org_public (from profile)");

        await Assert.That(s).Contains("EventStore/r1");
        await Assert.That(s).Contains("EventStore/r5");
        await Assert.That(s).DoesNotContain("EventStore/r6");
        await Assert.That(s).Contains("+4 more");
    }
}
