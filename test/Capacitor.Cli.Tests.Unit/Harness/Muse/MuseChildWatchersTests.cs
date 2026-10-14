using Capacitor.Cli.Harness.Muse;

namespace Capacitor.Cli.Tests.Unit.Harness.Muse;

public class MuseChildWatchersTests {
    static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    static readonly IReadOnlyDictionary<string, int> NoneReady = new Dictionary<string, int>();

    static Dictionary<string, int> Ready(params string[] keys) => keys.ToDictionary(k => k, _ => 1);

    static IReadOnlyList<string> Stop(MuseChildWatchers children, IReadOnlyDictionary<string, int> ready, DateTimeOffset now) {
        var stops = children.SelectStops(ready, now);
        children.Stopped(stops, ready);
        return stops;
    }

    [Test]
    public async Task An_announced_child_is_never_held_back() {
        var children = new MuseChildWatchers();

        await Assert.That(children.ShouldHold("/a/session.jsonl", announced: true)).IsFalse();
    }

    [Test]
    public async Task An_unannounced_child_is_held_for_the_grace_scans_then_registers() {
        var children = new MuseChildWatchers();

        for (var i = 0; i < MuseChildWatchers.AnnouncementGraceScans; i++)
            await Assert.That(children.ShouldHold("/a/session.jsonl", announced: false)).IsTrue();

        await Assert.That(children.ShouldHold("/a/session.jsonl", announced: false)).IsFalse();
        await Assert.That(children.ShouldHold("/a/session.jsonl", announced: false)).IsFalse();
    }

    [Test]
    public async Task Holdback_is_counted_per_child() {
        var children = new MuseChildWatchers();

        for (var i = 0; i < MuseChildWatchers.AnnouncementGraceScans; i++) children.ShouldHold("/a/session.jsonl", announced: false);

        await Assert.That(children.ShouldHold("/b/session.jsonl", announced: false)).IsTrue();
    }

    [Test]
    public async Task A_child_spawned_in_the_same_tick_it_is_finished_is_not_stopped() {
        var children = new MuseChildWatchers();
        children.Spawned("s-a", "/a/session.jsonl", T0);

        var stops = children.SelectStops(Ready("s-a"), T0);

        await Assert.That(stops).IsEmpty();
        await Assert.That(children.RecheckPending).IsTrue();
    }

    [Test]
    public async Task A_later_tick_stops_the_child_once_the_grace_has_passed() {
        var children = new MuseChildWatchers();
        children.Spawned("s-a", "/a/session.jsonl", T0);
        children.SelectStops(Ready("s-a"), T0);

        var early = children.SelectStops(Ready("s-a"), T0 + MuseChildWatchers.StopGrace - TimeSpan.FromMilliseconds(1));
        var due   = Stop(children, Ready("s-a"), T0 + MuseChildWatchers.StopGrace);

        await Assert.That(early).IsEmpty();
        await Assert.That(due).IsEquivalentTo(new[] { "s-a" });
        await Assert.That(children.RecheckPending).IsFalse();
    }

    [Test]
    public async Task A_finished_child_spawned_late_is_stopped_once_its_own_grace_passes() {
        var children = new MuseChildWatchers();
        children.Spawned("s-a", "/a/session.jsonl", T0);
        children.Spawned("s-b", "/b/session.jsonl", T0 + TimeSpan.FromSeconds(30));

        var stops = Stop(children, Ready("s-a", "s-b"), T0 + TimeSpan.FromSeconds(31));

        await Assert.That(stops).IsEquivalentTo(new[] { "s-a" });
        await Assert.That(children.RecheckPending).IsTrue();

        var later = Stop(children, Ready("s-a", "s-b"), T0 + TimeSpan.FromSeconds(30) + MuseChildWatchers.StopGrace);

        await Assert.That(later).IsEquivalentTo(new[] { "s-b" });
        await Assert.That(children.RecheckPending).IsFalse();
    }

    [Test]
    public async Task A_stopped_child_is_not_stopped_again() {
        var children = new MuseChildWatchers();
        children.Spawned("s-a", "/a/session.jsonl", T0);
        var now = T0 + MuseChildWatchers.StopGrace;
        Stop(children, Ready("s-a"), now);

        await Assert.That(children.SelectStops(Ready("s-a"), now.AddSeconds(5))).IsEmpty();
    }

    /// <summary>The kill can throw after the selection; until a stop is confirmed the child counts as
    /// running, so the next check still has a reason to run and selects it again.</summary>
    [Test]
    public async Task A_stop_that_is_never_confirmed_is_selected_again() {
        var children = new MuseChildWatchers();
        children.Spawned("s-a", "/a/session.jsonl", T0);
        var now = T0 + MuseChildWatchers.StopGrace;

        await Assert.That(children.SelectStops(Ready("s-a"), now)).IsEquivalentTo(new[] { "s-a" });
        await Assert.That(children.RecheckPending).IsTrue();

        await Assert.That(Stop(children, Ready("s-a"), now.AddSeconds(1))).IsEquivalentTo(new[] { "s-a" });
        await Assert.That(children.RecheckPending).IsFalse();
        await Assert.That(children.SelectStops(Ready("s-a"), now.AddSeconds(2))).IsEmpty();
    }

    /// <summary>Only the kills that succeeded are confirmed; the failed one is still running, so it must
    /// stay selectable and keep the check armed rather than be lost to the parent's teardown.</summary>
    [Test]
    public async Task A_failed_kill_among_successful_ones_is_selected_again_until_it_succeeds() {
        var children = new MuseChildWatchers();
        children.Spawned("s-a", "/a/session.jsonl", T0);
        children.Spawned("s-b", "/b/session.jsonl", T0);
        var now   = T0 + MuseChildWatchers.StopGrace;
        var ready = Ready("s-a", "s-b");

        await Assert.That(children.SelectStops(ready, now)).IsEquivalentTo(new[] { "s-a", "s-b" });
        children.Stopped(["s-a"], ready);

        await Assert.That(children.RecheckPending).IsTrue();
        await Assert.That(children.SelectStops(ready, now.AddSeconds(1))).IsEquivalentTo(new[] { "s-b" });

        children.Stopped(["s-b"], ready);

        await Assert.That(children.RecheckPending).IsFalse();
        await Assert.That(children.SelectStops(ready, now.AddSeconds(2))).IsEmpty();
    }

    /// <summary>A kill that fails leaves the child's watcher running, so the stop is confirmed only by
    /// the retry that ends it.</summary>
    [Test]
    public async Task A_child_is_confirmed_stopped_only_once_its_watcher_is_gone() {
        using var config = new TempConfigRoot();
        var paths = new WatcherPaths(config.PathTo("watchers"));
        Directory.CreateDirectory(paths.Directory);
        var watchers = TestWatchers.In(paths, config.Root, Resolutions.None(config.Root), new FixedCapacitorHttpClient());

        using var watcher = DummyProcess.StartSleep(30);
        var       pidFile = paths.PidFile("s-a");
        await File.WriteAllTextAsync(pidFile, $"{watcher.Pid}\n{PidIdentity.Capture(watcher.Pid)}");

        var children = new MuseChildWatchers();
        children.Spawned("s-a", "/a/session.jsonl", T0);
        var now   = T0 + MuseChildWatchers.StopGrace;
        var ready = Ready("s-a");

        int failed;
        using (new FileStream(pidFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            failed = await children.Stop(watchers, children.SelectStops(ready, now), ready);

        await Assert.That(failed).IsEqualTo(0);
        await Assert.That(watcher.HasExited).IsFalse();
        await Assert.That(children.RecheckPending).IsTrue();

        var retried = await children.Stop(watchers, children.SelectStops(ready, now.AddSeconds(1)), ready);

        await Assert.That(retried).IsEqualTo(1);
        await Assert.That(watcher.WaitForExit(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(children.RecheckPending).IsFalse();
        await Assert.That(children.SelectStops(ready, now.AddSeconds(2))).IsEmpty();
    }

    [Test]
    public async Task A_child_that_is_not_finished_or_not_spawned_is_left_alone() {
        var children = new MuseChildWatchers();
        children.Spawned("s-a", "/a/session.jsonl", T0);

        await Assert.That(children.SelectStops(NoneReady, T0.AddMinutes(5))).IsEmpty();
        await Assert.That(children.SelectStops(Ready("s-unknown"), T0.AddMinutes(5))).IsEmpty();
        await Assert.That(children.RecheckPending).IsFalse();
    }

    [Test]
    public async Task A_stopped_child_whose_log_advances_is_respawned_and_not_stopped_for_the_same_result() {
        var children = new MuseChildWatchers();
        children.Spawned("s-a", "/a/session.jsonl", T0);
        var stopAt = T0 + MuseChildWatchers.StopGrace;
        Stop(children, Ready("s-a"), stopAt);
        var m0 = new DateTime(2026, 10, 13, 12, 0, 5, DateTimeKind.Utc);
        children.RecordLogMtime("s-a", m0);

        await Assert.That(children.Reengaged(_ => m0, stopAt.AddSeconds(1))).IsEmpty();

        var respawn = children.Reengaged(_ => m0.AddSeconds(3), stopAt.AddSeconds(2));

        await Assert.That(respawn).IsEquivalentTo(new[] { "s-a" });
        await Assert.That(children.SelectStops(Ready("s-a"), stopAt.AddMinutes(1))).IsEmpty();
        await Assert.That(children.Reengaged(_ => m0.AddSeconds(4), stopAt.AddMinutes(1))).IsEmpty();
    }

    [Test]
    public async Task A_respawned_child_is_stopped_again_by_a_new_result() {
        var children = new MuseChildWatchers();
        children.Spawned("s-a", "/a/session.jsonl", T0);
        var stopAt = T0 + MuseChildWatchers.StopGrace;
        Stop(children, Ready("s-a"), stopAt);
        var m0 = new DateTime(2026, 10, 13, 12, 0, 5, DateTimeKind.Utc);
        children.RecordLogMtime("s-a", m0);
        var respawnAt = stopAt.AddSeconds(2);
        children.Reengaged(_ => m0.AddSeconds(3), respawnAt);

        var twice = new Dictionary<string, int> { ["s-a"] = 2 };

        await Assert.That(children.SelectStops(twice, respawnAt)).IsEmpty();
        await Assert.That(Stop(children, twice, respawnAt + MuseChildWatchers.StopGrace)).IsEquivalentTo(new[] { "s-a" });
    }

    [Test]
    public async Task A_respawn_that_did_not_start_is_offered_again_until_it_does() {
        var children = new MuseChildWatchers();
        children.Spawned("s-a", "/a/session.jsonl", T0);
        var stopAt = T0 + MuseChildWatchers.StopGrace;
        Stop(children, Ready("s-a"), stopAt);
        var m0 = new DateTime(2026, 10, 13, 12, 0, 5, DateTimeKind.Utc);
        children.RecordLogMtime("s-a", m0);
        var grown = m0.AddSeconds(3);

        await Assert.That(children.Reengaged(_ => grown, stopAt.AddSeconds(2))).IsEquivalentTo(new[] { "s-a" });

        children.RestartFailed("s-a");

        await Assert.That(children.Reengaged(_ => grown, stopAt.AddSeconds(4))).IsEquivalentTo(new[] { "s-a" });
        await Assert.That(children.Reengaged(_ => grown, stopAt.AddSeconds(6))).IsEmpty();
    }
}
