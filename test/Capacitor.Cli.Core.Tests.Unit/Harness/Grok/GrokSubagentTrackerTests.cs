using Capacitor.Cli.Core.Harness.Grok;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Grok;

public class GrokSubagentTrackerTests {
    static GrokSubagentEvent Spawned(string child) => new(child, "general-purpose", Finished: false);
    static GrokSubagentEvent Finished(string child) => new(child, null, Finished: true);

    [Test]
    public async Task A_spawned_child_awaits_its_start_until_it_is_started() {
        var t = new GrokSubagentTracker();
        t.Observe([Spawned("c1")]);

        await Assert.That(t.AwaitingStart().Select(c => c.Child)).IsEquivalentTo(new[] { "c1" });

        t.Started("c1");
        await Assert.That(t.AwaitingStart()).IsEmpty();
    }

    /// <summary>A child the parent re-reports, or that arrives in two batches, starts once.</summary>
    [Test]
    public async Task A_repeated_spawn_is_one_child() {
        var t = new GrokSubagentTracker();
        t.Observe([Spawned("c1"), Spawned("c1")]);
        t.Observe([Spawned("c1")]);

        await Assert.That(t.AwaitingStart().Count).IsEqualTo(1);
    }

    /// <summary>Only a started child is stopped, and only once; its spawn-time type is kept.</summary>
    [Test]
    public async Task A_finished_child_awaits_its_stop_once_started_and_only_once() {
        var t = new GrokSubagentTracker();
        t.Observe([Spawned("c1"), Finished("c1")]);
        await Assert.That(t.AwaitingStop()).IsEmpty();

        t.Started("c1");
        await Assert.That(t.AwaitingStop().Single()).IsEqualTo(("c1", (string?)"general-purpose"));

        t.Stopped("c1");
        t.Observe([Finished("c1")]);
        await Assert.That(t.AwaitingStop()).IsEmpty();
    }

    /// <summary>A restarted watcher resumes past the cursor, so its only sight of a child spawned
    /// earlier is the seed from the whole transcript; that child is registered again.</summary>
    [Test]
    public async Task A_seeded_tracker_registers_a_child_spawned_before_the_resumed_cursor() {
        var t = new GrokSubagentTracker();
        await Assert.That(t.Seeded).IsFalse();

        t.Seed([Spawned("c1")]);
        t.Observe([]);

        await Assert.That(t.Seeded).IsTrue();
        await Assert.That(t.AwaitingStart().Select(c => c.Child)).IsEquivalentTo(new[] { "c1" });
    }

    /// <summary>A registered child keeps its watcher ensured until it finishes, so one that failed to
    /// spawn or exited early is restarted on a later tick.</summary>
    [Test]
    public async Task A_started_child_is_active_until_it_finishes() {
        var t = new GrokSubagentTracker();
        t.Observe([Spawned("c1"), Spawned("c2")]);
        t.Started("c1");

        await Assert.That(t.Active().Select(c => c.Child)).IsEquivalentTo(new[] { "c1" });

        t.Observe([Finished("c1")]);
        await Assert.That(t.Active()).IsEmpty();
    }
}
