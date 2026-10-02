using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Tests.Unit.Commands;

public class HarnessTitleTrackerTests {
    static readonly DateTimeOffset T0 = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);

    static StoreTitle Title(string t, HarnessTitleKind k = HarnessTitleKind.Rename, DateTimeOffset? at = null) => new(t, k, at);

    [Test]
    public async Task First_read_without_change_time_is_untimed() {
        var tracker = new HarnessTitleTracker(recordsChangeTime: false);

        await Assert.That(tracker.Observe(Title("A"), T0)).IsEqualTo(new HarnessTitlePost("A", HarnessTitleKind.Rename, null));
    }

    [Test]
    public async Task Observed_change_is_timed_by_the_previous_read() {
        var tracker = new HarnessTitleTracker(recordsChangeTime: false);
        tracker.Settled(tracker.Observe(Title("A"), T0)!);
        tracker.Observe(Title("A"), T0.AddSeconds(30));

        await Assert.That(tracker.Observe(Title("B"), T0.AddSeconds(60))).IsEqualTo(new HarnessTitlePost("B", HarnessTitleKind.Rename, T0.AddSeconds(30)));
    }

    [Test]
    public async Task Recorded_change_time_is_sent_even_on_the_first_read() {
        var tracker = new HarnessTitleTracker(recordsChangeTime: true);

        await Assert.That(tracker.Observe(Title("A", at: T0.AddHours(-1)), T0)!.ChangedAt).IsEqualTo(T0.AddHours(-1));
    }

    [Test]
    public async Task An_unchanged_posted_value_is_not_posted_again() {
        var tracker = new HarnessTitleTracker(recordsChangeTime: false);
        tracker.Settled(tracker.Observe(Title("A"), T0)!);

        await Assert.That(tracker.Observe(Title("A"), T0.AddSeconds(30))).IsNull();
    }

    [Test]
    public async Task A_failed_post_is_retried_on_the_next_read() {
        var tracker = new HarnessTitleTracker(recordsChangeTime: false);
        tracker.Observe(Title("A"), T0);

        await Assert.That(tracker.Observe(Title("A"), T0.AddSeconds(30))).IsNotNull();
    }

    [Test]
    public async Task A_null_read_changes_nothing() {
        var tracker = new HarnessTitleTracker(recordsChangeTime: false);

        await Assert.That(tracker.Observe(null, T0)).IsNull();
    }

    [Test]
    public async Task A_retried_post_keeps_its_original_change_time() {
        var tracker = new HarnessTitleTracker(recordsChangeTime: false);
        tracker.Settled(tracker.Observe(Title("A"), T0)!);
        tracker.Observe(Title("A"), T0.AddSeconds(30));

        // B observed at T60, timed by the T30 read that still showed A; the post is never
        // acknowledged (Settled is never called), so a re-read at T90 must not re-time it.
        var first = tracker.Observe(Title("B"), T0.AddSeconds(60));
        var retry = tracker.Observe(Title("B"), T0.AddSeconds(90));

        await Assert.That(retry).IsEqualTo(first);
        await Assert.That(retry!.ChangedAt).IsEqualTo(T0.AddSeconds(30));
    }

    [Test]
    public async Task Recorded_store_with_missing_time_falls_back_to_previous_read() {
        var tracker = new HarnessTitleTracker(recordsChangeTime: true);
        tracker.Settled(tracker.Observe(Title("A", at: T0.AddHours(-1)), T0)!);

        await Assert.That(tracker.Observe(Title("B"), T0.AddSeconds(30))!.ChangedAt).IsEqualTo(T0);
    }

    [Test]
    public async Task Return_to_posted_value_drops_stale_pending() {
        var tracker = new HarnessTitleTracker(recordsChangeTime: false);
        tracker.Settled(tracker.Observe(Title("A"), T0)!);
        tracker.Observe(Title("B"), T0.AddSeconds(30));

        await Assert.That(tracker.Observe(Title("A"), T0.AddSeconds(60))).IsNull();
        await Assert.That(tracker.Observe(Title("B"), T0.AddSeconds(90))!.ChangedAt).IsEqualTo(T0.AddSeconds(60));
    }

    [Test]
    public async Task Kind_only_change_is_posted_timed_by_previous_read() {
        var tracker = new HarnessTitleTracker(recordsChangeTime: false);
        tracker.Settled(tracker.Observe(Title("A", HarnessTitleKind.Auto), T0)!);

        await Assert.That(tracker.Observe(Title("A", HarnessTitleKind.Rename), T0.AddSeconds(30)))
            .IsEqualTo(new HarnessTitlePost("A", HarnessTitleKind.Rename, T0));
    }

    /// <summary>B's post failed but may have committed, so the server may hold B; the store's return to A must be
    /// sent, timed by the read that still showed B.</summary>
    [Test]
    public async Task A_return_to_the_settled_value_after_an_unknown_outcome_is_sent() {
        var tracker = new HarnessTitleTracker(recordsChangeTime: false);
        tracker.Settled(tracker.Observe(Title("A"), T0)!);
        tracker.Observe(Title("B"), T0.AddSeconds(30));
        tracker.OutcomeUnknown();

        await Assert.That(tracker.Observe(Title("A"), T0.AddSeconds(60)))
            .IsEqualTo(new HarnessTitlePost("A", HarnessTitleKind.Rename, T0.AddSeconds(30)));
    }
}
