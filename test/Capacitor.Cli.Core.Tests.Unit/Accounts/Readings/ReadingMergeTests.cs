using Capacitor.Cli.Core.Accounts.Readings;
using static Capacitor.Cli.Core.Tests.Unit.Accounts.Readings.ReadingFixtures;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts.Readings;

public class ReadingMergeTests {
    [Test]
    public async Task Fresh_equal_value_moves_confirmedAt() {
        var reading = Apply(
            Obs(10, fresh: true, [W("claude", 300, 40, At(300))]),
            Obs(20, fresh: true, [W("claude", 300, 40, At(300))])
        );

        await Assert.That(Window(reading, "claude", 300).ConfirmedAt).IsEqualTo(At(20));
    }

    [Test]
    public async Task Replay_equal_value_changes_nothing() {
        var held   = Apply(Obs(10, fresh: true, [W("claude", 300, 40, At(300))]));
        var result = ReadingMerge.Apply(held, Obs(20, fresh: false, [W("claude", 300, 40, At(300))]));

        await Assert.That(ReadingMerge.Equivalent(result, held)).IsTrue();
        await Assert.That(Window(result, "claude", 300).ConfirmedAt).IsEqualTo(At(10));
    }

    [Test]
    public async Task Replay_may_raise_percentage_without_moving_confirmedAt() {
        var reading = Apply(
            Obs(10, fresh: true,  [W("claude", 300, 40, At(300))]),
            Obs(20, fresh: false, [W("claude", 300, 55.5, At(300))])
        );

        var window = Window(reading, "claude", 300);
        await Assert.That(window.UsedPercent).IsEqualTo(55.5);
        await Assert.That(window.ConfirmedAt).IsEqualTo(At(10));
    }

    [Test]
    public async Task Never_confirmed_window_reads_freshness_unknown() {
        var reading = Apply(Obs(10, fresh: false, [W("claude", 300, 40, At(300))]));

        await Assert.That(Window(reading, "claude", 300).ConfirmedAt).IsNull();
    }

    [Test]
    public async Task Lower_value_same_reset_is_ignored() {
        var reading = Apply(
            Obs(10, fresh: true, [W("claude", 300, 60, At(300))]),
            Obs(20, fresh: true, [W("claude", 300, 30, At(300))])
        );

        await Assert.That(Window(reading, "claude", 300).UsedPercent).IsEqualTo(60);
    }

    [Test]
    public async Task Later_reset_replaces_window() {
        var reading = Apply(
            Obs(10,  fresh: true, [W("claude", 300, 90, At(300))]),
            Obs(320, fresh: true, [W("claude", 300, 5, At(600))])
        );

        var window = Window(reading, "claude", 300);
        await Assert.That(window.UsedPercent).IsEqualTo(5);
        await Assert.That(window.ResetsAt).IsEqualTo(At(600));
        await Assert.That(window.ConfirmedAt).IsEqualTo(At(320));
    }

    [Test]
    public async Task Earlier_reset_is_stale() {
        var reading = Apply(
            Obs(320, fresh: true, [W("claude", 300, 5, At(600))]),
            Obs(330, fresh: true, [W("claude", 300, 90, At(300))])
        );

        var window = Window(reading, "claude", 300);
        await Assert.That(window.UsedPercent).IsEqualTo(5);
        await Assert.That(window.ResetsAt).IsEqualTo(At(600));
        await Assert.That(window.ConfirmedAt).IsEqualTo(At(320));
    }

    [Test]
    public async Task Several_limit_ids_coexist() {
        var reading = Apply(Obs(10, fresh: true, [W("codex", 300, 10, At(300)), W("codex_other", 300, 70, At(200))]));

        await Assert.That(reading.Windows.Length).IsEqualTo(2);
        await Assert.That(Window(reading, "codex", 300).UsedPercent).IsEqualTo(10);
        await Assert.That(Window(reading, "codex_other", 300).UsedPercent).IsEqualTo(70);
    }

    [Test]
    public async Task Sparse_observation_keeps_unmentioned_windows() {
        var reading = Apply(
            Obs(10, fresh: true, [W("claude", 300, 10, At(300)), W("claude", 10080, 30, At(5000))]),
            Obs(20, fresh: true, [W("claude", 300, 20, At(300))])
        );

        await Assert.That(reading.Windows.Length).IsEqualTo(2);
        await Assert.That(Window(reading, "claude", 10080).ConfirmedAt).IsEqualTo(At(10));
        await Assert.That(Window(reading, "claude", 300).ConfirmedAt).IsEqualTo(At(20));
    }

    [Test]
    public async Task Metadata_only_observation_records_plan_and_credits() {
        var credits = new CreditsDto(true, false, "12.50");
        var reading = Apply(
            Obs(10, fresh: true, [W("codex", 300, 10, At(300))], plan: "pro"),
            Obs(20, fresh: true, plan: "plus", credits: credits)
        );

        await Assert.That(reading.Plan).IsEqualTo("plus");
        await Assert.That(reading.Credits).IsEqualTo(credits);
        await Assert.That(reading.Windows.Length).IsEqualTo(1);
    }

    [Test]
    public async Task Observation_without_plan_keeps_the_held_one() {
        var reading = Apply(
            Obs(10, fresh: true, plan: "pro", credits: new CreditsDto(false, false, null)),
            Obs(20, fresh: true, [W("codex", 300, 10, At(300))])
        );

        await Assert.That(reading.Plan).IsEqualTo("pro");
        await Assert.That(reading.Credits).IsNotNull();
    }

    [Test]
    public async Task Older_observation_never_overwrites_newer_plan_or_credits() {
        var credits = new CreditsDto(true, false, "12.50");
        var reading = Apply(
            Obs(20, fresh: true, plan: "plus", credits: credits),
            Obs(10, fresh: false, plan: "pro", credits: new CreditsDto(false, false, null))
        );

        await Assert.That(reading.Plan).IsEqualTo("plus");
        await Assert.That(reading.Credits).IsEqualTo(credits);
        await Assert.That(reading.MetadataAt).IsEqualTo(At(20));
    }

    [Test]
    public async Task Observation_without_metadata_keeps_metadataAt() {
        var reading = Apply(
            Obs(10, fresh: true, plan: "pro"),
            Obs(20, fresh: true, [W("codex", 300, 10, At(300))])
        );

        await Assert.That(reading.MetadataAt).IsEqualTo(At(10));
    }

    [Test]
    public async Task Equal_time_observation_takes_its_metadata() {
        var reading = Apply(Obs(10, fresh: true, plan: "pro"), Obs(10, fresh: true, plan: "plus"));

        await Assert.That(reading.Plan).IsEqualTo("plus");
    }

    [Test]
    public async Task Merge_takes_the_later_metadata_in_either_order() {
        var older = Apply(Obs(10, fresh: true, plan: "pro", credits: new CreditsDto(false, false, null)));
        var newer = Apply(Obs(20, fresh: true, plan: "plus", credits: new CreditsDto(true, false, "1")));

        foreach (var merged in new[] { ReadingMerge.Merge(older, newer), ReadingMerge.Merge(newer, older) }) {
            await Assert.That(merged.Plan).IsEqualTo("plus");
            await Assert.That(merged.Credits).IsEqualTo(new CreditsDto(true, false, "1"));
            await Assert.That(merged.MetadataAt).IsEqualTo(At(20));
        }
    }

    [Test]
    public async Task Merge_falls_back_per_member_when_the_winner_lacks_one() {
        var credits = new CreditsDto(true, false, "1");
        var held    = ReadingMerge.Empty(1) with { Plan = "pro", Credits = credits };
        var newer   = ReadingMerge.Empty(1) with { Plan = "plus", MetadataAt = At(20) };

        var merged = ReadingMerge.Merge(held, newer);

        await Assert.That(merged.Plan).IsEqualTo("plus");
        await Assert.That(merged.Credits).IsEqualTo(credits);
        await Assert.That(merged.MetadataAt).IsEqualTo(At(20));
        await Assert.That(ReadingMerge.Merge(newer, held).Plan).IsEqualTo("plus");
    }

    [Test]
    public async Task Merge_tie_goes_to_incoming() {
        var held     = ReadingMerge.Empty(1) with { Plan = "pro", MetadataAt = At(10) };
        var incoming = ReadingMerge.Empty(1) with { Plan = "plus", MetadataAt = At(10) };

        await Assert.That(ReadingMerge.Merge(held, incoming).Plan).IsEqualTo("plus");
        await Assert.That(ReadingMerge.Merge(ReadingMerge.Empty(1) with { Plan = "pro" }, ReadingMerge.Empty(1) with { Plan = "plus" }).Plan).IsEqualTo("plus");
    }

    [Test]
    public async Task Equivalent_sees_a_metadataAt_change() {
        var a = ReadingMerge.Empty(1) with { Plan = "pro", MetadataAt = At(10) };

        await Assert.That(ReadingMerge.Equivalent(a, a with { MetadataAt = At(20) })).IsFalse();
    }

    [Test]
    public async Task Snapshot_merge_twice_is_idempotent_and_never_restamps() {
        var a = Apply(
            Obs(10, fresh: true, [W("claude", 300, 40, At(300)), W("claude", 10080, 20, At(5000))], plan: "max"),
            Obs(15, fresh: true, limits: [new LimitSignal(null, null)])
        );
        var b = Apply(Obs(20, fresh: false, [W("claude", 300, 50, At(300))]));

        var once  = ReadingMerge.Merge(b, a);
        var twice = ReadingMerge.Merge(once, a);

        await Assert.That(ReadingMerge.Equivalent(once, twice)).IsTrue();
        await Assert.That(ReadingMerge.Equivalent(ReadingMerge.Merge(once, once), once)).IsTrue();
        await Assert.That(Window(twice, "claude", 300).ConfirmedAt).IsEqualTo(At(10));
        await Assert.That(Window(twice, "claude", 300).UsedPercent).IsEqualTo(50);
        await Assert.That(Window(twice, "claude", 10080).ConfirmedAt).IsEqualTo(At(10));
    }

    [Test]
    public async Task Snapshot_merge_is_order_independent() {
        var a = Apply(Obs(10, fresh: true, [W("claude", 300, 40, At(300)), W("claude", 10080, 20, At(5000))]));
        var b = Apply(Obs(20, fresh: true, [W("claude", 300, 50, At(300)), W("codex", 300, 1, null)]));

        await Assert.That(ReadingMerge.Equivalent(ReadingMerge.Merge(a, b), ReadingMerge.Merge(b, a))).IsTrue();
    }

    [Test]
    public async Task Replacement_by_later_reset_takes_incoming_confirmation() {
        var held = Reading(W("claude", 300, 90, At(300), At(250)));

        var unconfirmed = ReadingMerge.Merge(held, Reading(W("claude", 300, 5, At(600), null)));
        var older       = ReadingMerge.Merge(held, Reading(W("claude", 300, 5, At(600), At(100))));

        await Assert.That(Window(unconfirmed, "claude", 300).ConfirmedAt).IsNull();
        await Assert.That(Window(older, "claude", 300).ConfirmedAt).IsEqualTo(At(100));
    }

    [Test]
    public async Task Same_window_keeps_max_confirmation() {
        var a = Reading(W("claude", 300, 40, At(300), At(50)));
        var b = Reading(W("claude", 300, 30, At(300), At(80)));
        var c = Reading(W("claude", 300, 30, At(300), null));

        await Assert.That(Window(ReadingMerge.Merge(a, b), "claude", 300)).IsEqualTo(W("claude", 300, 40, At(300), At(80)));
        await Assert.That(Window(ReadingMerge.Merge(b, a), "claude", 300)).IsEqualTo(W("claude", 300, 40, At(300), At(80)));
        await Assert.That(Window(ReadingMerge.Merge(a, c), "claude", 300).ConfirmedAt).IsEqualTo(At(50));
        await Assert.That(Window(ReadingMerge.Merge(c, a), "claude", 300).ConfirmedAt).IsEqualTo(At(50));
    }

    [Test]
    public async Task Same_window_without_reset_keeps_max_percentage_and_confirmation() {
        var a = Reading(W("codex", 300, 40, null, At(50)));
        var b = Reading(W("codex", 300, 30, null, At(80)));

        await Assert.That(Window(ReadingMerge.Merge(a, b), "codex", 300)).IsEqualTo(W("codex", 300, 40, null, At(80)));
    }

    [Test]
    public async Task Non_null_reset_replaces_a_held_null_reset() {
        var held     = Reading(W("codex", 300, 90, null, At(50)));
        var incoming = Reading(W("codex", 300, 10, At(300), At(20)));

        await Assert.That(Window(ReadingMerge.Merge(held, incoming), "codex", 300)).IsEqualTo(W("codex", 300, 10, At(300), At(20)));
    }

    [Test]
    public async Task Null_reset_never_replaces_a_held_non_null_reset() {
        var held     = Reading(W("codex", 300, 10, At(300), At(20)));
        var incoming = Reading(W("codex", 300, 90, null, At(50)));

        await Assert.That(Window(ReadingMerge.Merge(held, incoming), "codex", 300)).IsEqualTo(W("codex", 300, 10, At(300), At(20)));
    }

    [Test]
    public async Task Merge_keeps_the_held_generation() {
        var merged = ReadingMerge.Merge(ReadingMerge.Empty(7), ReadingMerge.Empty(7));

        await Assert.That(merged.Generation).IsEqualTo(7);
        await Assert.That(merged.Blocked).IsNull();
    }

    static AccountReadingDto Reading(params UsageWindowDto[] windows) => ReadingMerge.Empty(1) with { Windows = windows };
}
