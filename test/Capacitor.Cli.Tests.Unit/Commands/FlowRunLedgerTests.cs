using Capacitor.Cli.Commands;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class FlowRunLedgerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly DateTimeOffset Start = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    readonly FakeTimeProvider _time = new(Start);

    FlowRunLedger Ledger() => new(Config.Root, _time);

    static List<string> Ids(FlowRunLedger ledger, string workspace) =>
        ledger.Retained(workspace).Select(e => e.FlowRunId).ToList();

    [Test]
    public async Task Retained_lists_this_workspaces_runs_newest_first() {
        var ledger = Ledger();
        ledger.Record("run-1", "/repo/a");
        _time.Advance(TimeSpan.FromMinutes(1));
        ledger.Record("run-2", "/repo/b");
        _time.Advance(TimeSpan.FromMinutes(1));
        ledger.Record("run-3", "/repo/a");

        await Assert.That(Ids(Ledger(), "/repo/a")).IsEquivalentTo(["run-3", "run-1"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Retained_carries_when_each_run_was_recorded() {
        var ledger = Ledger();
        ledger.Record("run-1", "/repo/a");

        await Assert.That(ledger.Retained("/repo/a").Single().StartedAt).IsEqualTo(Start);
    }

    [Test]
    public async Task Recording_a_run_again_moves_it_to_the_front_without_a_duplicate() {
        var ledger = Ledger();
        ledger.Record("run-1", "/repo/a");
        _time.Advance(TimeSpan.FromSeconds(1));
        ledger.Record("run-2", "/repo/a");
        _time.Advance(TimeSpan.FromSeconds(1));
        ledger.Record("run-1", "/repo/a");

        await Assert.That(Ids(ledger, "/repo/a")).IsEquivalentTo(["run-1", "run-2"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Runs_past_the_retention_are_not_returned() {
        var ledger = Ledger();
        ledger.Record("old", "/repo/a");
        _time.Advance(FlowRunLedger.Retention + TimeSpan.FromMinutes(1));
        ledger.Record("new", "/repo/a");

        await Assert.That(Ids(ledger, "/repo/a")).IsEquivalentTo(["new"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task The_ledger_keeps_only_the_newest_entries() {
        var ledger = Ledger();
        for (var i = 0; i < FlowRunLedger.MaxEntries + 3; i++) {
            ledger.Record($"run-{i}", "/repo/a");
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        var all = Ids(ledger, "/repo/a");
        await Assert.That(all.Count).IsEqualTo(FlowRunLedger.MaxEntries);
        await Assert.That(all[0]).IsEqualTo($"run-{FlowRunLedger.MaxEntries + 2}");
        await Assert.That(all).DoesNotContain("run-2");
    }

    [Test]
    public async Task A_corrupt_file_reads_as_empty_and_is_replaced_on_the_next_record() {
        File.WriteAllText(Config.Root.Path(FlowRunLedger.FileName), "{not json");
        var ledger = Ledger();

        await Assert.That(Ids(ledger, "/repo/a")).IsEmpty();

        ledger.Record("run-1", "/repo/a");
        await Assert.That(Ids(ledger, "/repo/a")).IsEquivalentTo(["run-1"], CollectionOrdering.Matching);
    }
}
