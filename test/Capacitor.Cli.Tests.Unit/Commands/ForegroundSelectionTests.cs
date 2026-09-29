using System.Globalization;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ForegroundSelectionTests {
    static ImportCommand.SessionClassification File(string id, string slug, string ts, ImportCommand.ClassificationStatus status = ImportCommand.ClassificationStatus.New) => new() {
        SessionId = id, FilePath = $"/tmp/{id}.jsonl", EncodedCwd = "-tmp", Status = status,
        Meta = new SessionMetadata { Slug = slug, FirstTimestamp = DateTimeOffset.Parse(ts, CultureInfo.InvariantCulture) },
    };
    static ImportCommand.SessionClassification Routed(string id, string ts, ImportCommand.ClassificationStatus status = ImportCommand.ClassificationStatus.New, string? parent = null) =>
        RoutedUnitTests.Row(id, status, parent, ts);

    static ForegroundPlan Select(IReadOnlyList<ImportCommand.SessionClassification> all, int max) {
        var fileBased = all.Where(c => !string.IsNullOrEmpty(c.FilePath)).ToList();
        var routed    = all.Where(c => string.IsNullOrEmpty(c.FilePath)
                             && c.Status is ImportCommand.ClassificationStatus.New or ImportCommand.ClassificationStatus.Partial or ImportCommand.ClassificationStatus.AlreadyLoaded).ToList();
        routed.Sort(ImportOrdering.RoutedDispatch);
        return ForegroundSelection.Select(ImportCommand.BuildImportChains(fileBased), routed, all, max);
    }

    [Test]
    public async Task A_session_id_in_multiple_dirs_appears_once_in_selected_and_candidate_ids() {
        static ImportCommand.SessionClassification Dup(string dir) => new() {
            SessionId = "dup", FilePath = $"/tmp/{dir}/dup.jsonl", EncodedCwd = "-tmp",
            Status = ImportCommand.ClassificationStatus.New,
            Meta = new SessionMetadata { Slug = dir, FirstTimestamp = DateTimeOffset.Parse("2026-03-01T00:00:00Z", CultureInfo.InvariantCulture) },
        };
        var all = new List<ImportCommand.SessionClassification> { Dup("a"), Dup("b"), File("uniq", "u", "2026-02-01T00:00:00Z") };

        var plan = Select(all, max: 5);

        await Assert.That(plan.Selection.SelectedIds.Count(id => id == "dup")).IsEqualTo(1);
        await Assert.That(plan.Selection.RunCandidateIds.Count(id => id == "dup")).IsEqualTo(1);
        await Assert.That(plan.Selection.RunCandidateIds).Contains("uniq");
    }

    [Test]
    public async Task Takes_whole_chains_newest_first_until_the_cap_and_overshoots_by_the_boundary_chain() {
        var all = new List<ImportCommand.SessionClassification> {
            File("n1", "new", "2026-03-01T00:00:00Z"), File("n2", "new", "2026-03-02T00:00:00Z"),
            File("m1", "mid", "2026-02-01T00:00:00Z"), File("m2", "mid", "2026-02-02T00:00:00Z"),
            File("m3", "mid", "2026-02-03T00:00:00Z"), File("m4", "mid", "2026-02-04T00:00:00Z"),
            File("o1", "old", "2026-01-01T00:00:00Z"),
        };

        var plan = Select(all, max: 5);

        await Assert.That(plan.Selection.SelectedIds).IsEquivalentTo(["n1", "n2", "m1", "m2", "m3", "m4"]);
        await Assert.That(plan.Selection.RemainderExists).IsTrue();
        await Assert.That(plan.Selection.RunCandidateIds).IsEquivalentTo(["n1", "n2", "m1", "m2", "m3", "m4", "o1"]);
        await Assert.That(plan.Selection.RunCandidateIds[0]).IsEqualTo("n2");
    }

    [Test]
    public async Task Tops_up_with_eligible_routed_units_counting_one_per_parent() {
        var all = new List<ImportCommand.SessionClassification> {
            File("n1", "new", "2026-03-01T00:00:00Z"),
            Routed("p", "2026-02-20T00:00:00Z"),
            Routed("c1", "2026-02-20T00:00:00Z", parent: "p"),
            Routed("c2", "2026-02-20T00:00:00Z", parent: "p", status: ImportCommand.ClassificationStatus.AlreadyLoaded),
            Routed("r2", "2026-02-10T00:00:00Z"),
        };

        var plan = Select(all, max: 2);

        await Assert.That(plan.Selection.SelectedIds).IsEquivalentTo(["n1", "p"]);
        await Assert.That(plan.Routed.Select(c => c.SessionId).ToList()).IsEquivalentTo(["p", "c1", "c2"]);
        await Assert.That(plan.Selection.RunCandidateIds).IsEquivalentTo(["n1", "p", "r2"]);
        await Assert.That(plan.Selection.RemainderExists).IsTrue();
    }

    [Test]
    public async Task An_already_loaded_parent_with_a_new_child_is_never_selected_and_contributes_no_candidate() {
        var all = new List<ImportCommand.SessionClassification> {
            Routed("p", "2026-03-01T00:00:00Z", status: ImportCommand.ClassificationStatus.AlreadyLoaded),
            Routed("c", "2026-03-01T00:00:00Z", parent: "p"),
        };

        var plan = Select(all, max: 5);

        await Assert.That(plan.Selection.SelectedIds).IsEmpty();
        await Assert.That(plan.Routed).IsEmpty();
        await Assert.That(plan.Selection.RunCandidateIds).IsEmpty();
        await Assert.That(plan.Selection.RemainderExists).IsTrue();
    }

    [Test]
    public async Task A_correlated_probe_error_child_is_not_a_candidate_but_an_orphaned_one_is() {
        var all = new List<ImportCommand.SessionClassification> {
            Routed("p", "2026-03-01T00:00:00Z"),
            Routed("cp", "2026-03-01T00:00:00Z", parent: "p", status: ImportCommand.ClassificationStatus.ProbeError),
            Routed("orphan", "2026-03-01T00:00:00Z", parent: "gone", status: ImportCommand.ClassificationStatus.ProbeError),
        };

        var plan = Select(all, max: 5);

        await Assert.That(plan.Selection.RunCandidateIds).IsEquivalentTo(["p", "orphan"]);
        await Assert.That(plan.Selection.RemainderExists).IsTrue();
    }

    [Test]
    public async Task Remainder_is_false_when_everything_actionable_was_selected() {
        var all = new List<ImportCommand.SessionClassification> {
            File("n1", "new", "2026-03-01T00:00:00Z"),
            File("done", "x", "2026-01-01T00:00:00Z", ImportCommand.ClassificationStatus.AlreadyLoaded),
            Routed("p", "2026-02-20T00:00:00Z"),
            Routed("c", "2026-02-20T00:00:00Z", parent: "p", status: ImportCommand.ClassificationStatus.AlreadyLoaded),
        };

        var plan = Select(all, max: 5);

        await Assert.That(plan.Selection.RemainderExists).IsFalse();
    }

    [Test]
    public async Task Routed_replay_rows_alone_are_remainder_but_file_based_already_loaded_rows_are_not() {
        var cursorOnly = Select([Routed("p", "2026-03-01T00:00:00Z", status: ImportCommand.ClassificationStatus.AlreadyLoaded)], max: 5);
        var claudeOnly = Select([File("a", "x", "2026-03-01T00:00:00Z", ImportCommand.ClassificationStatus.AlreadyLoaded)], max: 5);

        await Assert.That(cursorOnly.Selection.RemainderExists).IsTrue();
        await Assert.That(claudeOnly.Selection.RemainderExists).IsFalse();
    }

    [Test]
    public async Task Candidates_are_capped_at_500_newest_in_candidate_order() {
        var all = Enumerable.Range(0, 600).Select(i =>
            File($"s{i:000}", $"slug{i}", DateTimeOffset.UnixEpoch.AddDays(i).ToString("O"))).ToList();

        var plan = Select(all, max: 5);

        await Assert.That(plan.Selection.RunCandidateIds.Count).IsEqualTo(600);
        await Assert.That(plan.Selection.RunCandidateIds[0]).IsEqualTo("s599");
    }
}
