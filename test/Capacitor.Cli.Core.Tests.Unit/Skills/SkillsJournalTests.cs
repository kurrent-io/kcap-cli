using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Tests.Unit.Skills;

public class SkillsJournalTests {
    static PendingPrune P(string path) => new(path, "/repo/.claude/skills");

    [Test]
    public async Task Merging_keeps_what_an_earlier_transition_still_owes() {
        var merged = SkillsJournal.Merge([P("/a"), P("/b")], [P("/b"), P("/c")]);

        await Assert.That(merged.Select(p => p.Path)).IsEquivalentTo(["/a", "/b", "/c"]);
    }

    [Test]
    public async Task A_path_that_is_live_again_is_not_deleted() {
        // x renamed to y, crash, then renamed back to x before the retry.
        var journal = SkillsJournal.Merge(null, [P("/repo/.claude/skills/kcap-x")]);

        var reconciled = SkillsJournal.Reconcile(journal, ["/repo/.claude/skills/kcap-x"]);

        await Assert.That(reconciled).IsEmpty();
    }

    [Test]
    public async Task Reconciliation_compares_resolved_destinations() {
        var journal = SkillsJournal.Merge(null, [P("/repo/./.claude/skills/../skills/kcap-x")]);

        var reconciled = SkillsJournal.Reconcile(journal, ["/repo/.claude/skills/kcap-x"]);

        await Assert.That(reconciled).IsEmpty();
    }
}
