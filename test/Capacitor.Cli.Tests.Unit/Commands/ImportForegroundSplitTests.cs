using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using static Capacitor.Cli.Commands.ImportCommand;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// A capped run imports the newest sessions and leaves the rest for the background import. Its output
/// says so, and the work it left counts as queued, so the Done grid accounts for every session the
/// Plan grid found rather than presenting the first batch as all of it.
/// </summary>
public class ImportForegroundSplitTests {
    static SessionClassification Cls(string sessionId, ClassificationStatus status, HarnessId vendor = HarnessId.Claude) =>
        new() {
            SessionId  = sessionId,
            FilePath   = $"/tmp/{sessionId}.jsonl",
            EncodedCwd = "-tmp",
            Meta       = new SessionMetadata(),
            Status     = status,
            Vendor     = vendor,
        };

    static FinalCounts Final(
            int loaded = 0, int resumed = 0, int alreadyLoaded = 0, int tooShort = 0, int excluded = 0,
            int probeError = 0, int errored = 0, int deferred = 0) =>
        new(loaded, resumed, alreadyLoaded, tooShort, excluded, probeError, errored, 0, 0, 0, 0, 0,
            RanBackground: false, RequestedSummaries: false, Deferred: deferred);

    [Test]
    public async Task The_cap_leaves_every_eligible_session_it_does_not_run_and_nothing_else() {
        var all = new[] {
            Cls("a", ClassificationStatus.New),
            Cls("b", ClassificationStatus.Partial),
            Cls("c", ClassificationStatus.New),
            Cls("d", ClassificationStatus.New),
            Cls("e", ClassificationStatus.Partial),
            Cls("f", ClassificationStatus.AlreadyLoaded),
            Cls("g", ClassificationStatus.Excluded),
            Cls("h", ClassificationStatus.ProbeError),
        };

        var deferred = ForegroundSelection.Deferred(all, [[all[0], all[1]]], [all[2]]);

        await Assert.That(deferred).IsEquivalentTo(new[] { "d", "e" });
    }

    [Test]
    public async Task The_split_names_what_was_found_what_needs_importing_and_how_it_is_divided() {
        var plan = new ClassificationCounts(New: 6, Partial: 1, AlreadyLoaded: 2, TooShort: 1, Excluded: 2, ProbeError: 0);

        await Assert.That(ForegroundSplitNotice(plan, deferred: 2))
                    .IsEqualTo("Of 12 sessions found, 7 need importing: the newest 5 now, and 2 queued for the background import.");
    }

    /// <summary>The reported case: seven new sessions, five run now. Five imported and nothing else reads as
    /// the whole history; the two queued are what close the gap.</summary>
    [Test]
    public async Task Seven_found_five_imported_now_and_two_queued_add_up() {
        var plan  = new ClassificationCounts(New: 7, Partial: 0, AlreadyLoaded: 0, TooShort: 0, Excluded: 0, ProbeError: 0);
        var final = Final(loaded: 5, deferred: 2);

        await Assert.That(final.Imported).IsEqualTo(5);
        await Assert.That(final.Skipped).IsEqualTo(0);
        await Assert.That(final.Failed).IsEqualTo(0);
        await Assert.That(final.Total).IsEqualTo(plan.Found);
    }

    [Test]
    public async Task Exclusions_resumable_sessions_and_partial_failures_reconcile_with_the_plan() {
        var plan = new ClassificationCounts(New: 4, Partial: 2, AlreadyLoaded: 1, TooShort: 1, Excluded: 2, ProbeError: 1);

        // Four of the six eligible ran: two loaded, a resumable one resumed, one errored. Two were queued.
        var final = Final(loaded: 2, resumed: 1, alreadyLoaded: 1, tooShort: 1, excluded: 2, probeError: 1, errored: 1, deferred: 2);

        await Assert.That(final.Imported).IsEqualTo(3);
        await Assert.That(final.Skipped).IsEqualTo(4);
        await Assert.That(final.Failed).IsEqualTo(2);
        await Assert.That(final.Deferred).IsEqualTo(2);
        await Assert.That(final.Total).IsEqualTo(plan.Found);
        await Assert.That(final.Imported + final.Errored + final.Deferred).IsEqualTo(plan.Eligible);
    }

    [Test]
    public async Task A_source_row_counts_its_queued_sessions_so_the_rows_add_up_to_the_totals() {
        var claude = new[] {
            Cls("c1", ClassificationStatus.New),
            Cls("c2", ClassificationStatus.Partial),
            Cls("c3", ClassificationStatus.New),
            Cls("c4", ClassificationStatus.Excluded),
        };
        var codex = new[] {
            Cls("x1", ClassificationStatus.New, HarnessId.Codex),
            Cls("x2", ClassificationStatus.Partial, HarnessId.Codex),
        };
        var deferred = new HashSet<string> { "c3", "x2" };

        // c1 loaded and c2 resumed; x1 errored.
        var claudeRow = ComputePerSourceFinalCounts(claude, imported: 2, routedOutcomes: null, resumed: 1, deferredIds: deferred);
        var codexRow  = ComputePerSourceFinalCounts(codex, imported: 0, routedOutcomes: null, deferredIds: deferred);

        await Assert.That(claudeRow.Deferred).IsEqualTo(1);
        await Assert.That(claudeRow.Total).IsEqualTo(claude.Length);
        await Assert.That(codexRow.Deferred).IsEqualTo(1);
        await Assert.That(codexRow.Total).IsEqualTo(codex.Length);
        await Assert.That(CountDeferred([.. claude, .. codex], deferred)).IsEqualTo(claudeRow.Deferred + codexRow.Deferred);
    }

    [Test]
    public async Task An_uncapped_run_queues_nothing() =>
        await Assert.That(CountDeferred([Cls("a", ClassificationStatus.New)], null)).IsEqualTo(0);

    /// <summary>The closing line stays exactly the three totals, on a line of its own: the desktop app
    /// parses it as the run's whole total, and the queued count follows separately.</summary>
    [Test]
    public async Task The_done_grid_reports_the_queued_sessions_beside_an_unchanged_closing_line() {
        var terminal = new TestImportTerminal();

        terminal.Display().WriteDoneGrid(Final(loaded: 5, deferred: 2));

        var lines = terminal.Plain.ToString().Split('\n').Select(l => l.Trim()).ToList();

        await Assert.That(lines).Contains("5 imported · 0 skipped · 0 failed");
        await Assert.That(lines).Contains(DeferredNote(2));
        await Assert.That(lines).Contains("Queued              2");
    }

    [Test]
    public async Task A_run_with_nothing_queued_says_nothing_about_a_background_import() {
        var terminal = new TestImportTerminal();

        terminal.Display().WriteDoneGrid(Final(loaded: 5));

        var output = terminal.Plain.ToString();

        await Assert.That(output).DoesNotContain("Queued");
        await Assert.That(output).DoesNotContain("background import");
    }

    [Test]
    public async Task The_by_source_section_names_each_source_s_queued_sessions() {
        var terminal = new TestImportTerminal();

        terminal.Display().WriteDoneGrid(
            Final(loaded: 3, deferred: 2),
            new Dictionary<string, FinalCounts> {
                ["claude"] = Final(loaded: 2, deferred: 2),
                ["codex"]  = Final(loaded: 1),
            });

        var output = terminal.Plain.ToString();

        await Assert.That(output).Contains("[claude]");
        await Assert.That(output).Contains("  Queued              2");
    }
}
