using Capacitor.Cli.Core.Handoff;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class HandoffOutcomeStoreTests {
    [TempDir] public required TempDir Tmp { get; init; }

    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    static readonly HandoffOutcomeItemDto[] NoItems = [];

    static string Session(int n) => $"0f0f0f0f-2222-4222-8222-{n:D12}";

    static HandoffOutcomeDto Done(int n) =>
        new("source-" + n, "Source " + n, "done", null, [new HandoffOutcomeItemDto("work_item", "#1 — One", "carried", null)]);

    HandoffOutcomeStore Store() => new(Tmp.Path, _time);

    string FilePath => Tmp.PathTo("handoff-outcomes.json");

    [Test]
    public async Task Round_trips_and_caps_at_200() {
        var store = Store();
        for (var n = 1; n <= 205; n++) {
            store.Put(Session(n), Done(n));
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        var reloaded = Store();
        var newest   = reloaded.Get(Session(205))!;
        await Assert.That(newest with { Items = NoItems }).IsEqualTo(Done(205) with { Items = NoItems });
        await Assert.That(newest.Items).IsEquivalentTo(Done(205).Items);
        await Assert.That(reloaded.Get(Session(6))!.SourceSessionId).IsEqualTo("source-6");
        await Assert.That(reloaded.Get(Session(5))).IsNull();
        await Assert.That(reloaded.Get(Session(1))).IsNull();
    }

    /// <summary>A GUID in any spelling names the same session.</summary>
    [Test]
    public async Task Keys_are_canonical_session_ids() {
        var store = Store();
        store.Put(Session(1).ToUpperInvariant(), Done(1));

        await Assert.That(Store().Get(Session(1).Replace("-", ""))).IsNotNull();
    }

    [Test]
    public async Task A_corrupt_file_reads_empty() {
        await File.WriteAllTextAsync(FilePath, "{ not json");

        var store = Store();
        await Assert.That(store.Get(Session(1))).IsNull();

        store.Put(Session(1), Done(1));
        await Assert.That(Store().Get(Session(1))!.State).IsEqualTo("done");
    }

    /// <summary>Only the process that wrote running can still finish it.</summary>
    [Test]
    public async Task A_running_outcome_reloaded_at_startup_reads_interrupted() {
        var first = Store();
        first.Put(Session(1), HandoffOutcomeDto.Running("source-1", "Source 1"));
        first.Put(Session(2), Done(2));
        await Assert.That(first.Get(Session(1))!.State).IsEqualTo("running");

        var restarted = Store();
        var outcome   = restarted.Get(Session(1))!;
        await Assert.That(outcome.State).IsEqualTo("interrupted");
        await Assert.That(outcome.SourceTitle).IsEqualTo("Source 1");
        await Assert.That(restarted.Get(Session(2))!.State).IsEqualTo("done");

        restarted.Put(Session(3), Done(3));
        await Assert.That(Store().Get(Session(1))!.State).IsEqualTo("interrupted");
    }

    [Test]
    public async Task The_file_is_owner_only() {
        if (OperatingSystem.IsWindows()) return;

        Store().Put(Session(1), Done(1));

        await Assert.That(File.GetUnixFileMode(FilePath)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Assert.That(Directory.GetFiles(Tmp.Path)).IsEquivalentTo([FilePath]);
    }
}
