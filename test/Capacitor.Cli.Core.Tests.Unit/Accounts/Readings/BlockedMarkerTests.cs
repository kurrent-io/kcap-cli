using Capacitor.Cli.Core.Accounts.Readings;
using static Capacitor.Cli.Core.Tests.Unit.Accounts.Readings.ReadingFixtures;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts.Readings;

public class BlockedMarkerTests {
    static readonly LimitSignal Anything  = new(null, null);
    static readonly LimitSignal Primary   = new("codex", 300);
    static readonly LimitSignal Secondary = new("codex", 10080);

    static UsageWindowDto[] CodexBelow(int primaryReset = 300, int secondaryReset = 5000) =>
        [W("codex", 300, 20, At(primaryReset)), W("codex", 10080, 30, At(secondaryReset))];

    [Test]
    public async Task Block_before_any_reading_is_unknown_and_blocked() {
        var reading = Apply(Obs(10, fresh: true, limits: [Anything], known: false));

        await Assert.That(reading.Blocked!.Constraints).IsEquivalentTo([new BlockConstraintDto(null, null, null, At(10))]);
        await Assert.That(ReadingMerge.IsBlocked(reading, At(100_000))).IsTrue();
        await Assert.That(ReadingMerge.BlockedUntil(reading)).IsNull();
    }

    [Test]
    public async Task Limit_only_observation_still_blocks() {
        var reading = Apply(
            Obs(10, fresh: true, CodexBelow()),
            Obs(20, fresh: true, limits: [Anything], known: false)
        );

        await Assert.That(reading.Blocked!.BlockedAt).IsEqualTo(At(20));
        await Assert.That(ReadingMerge.IsBlocked(reading, At(21))).IsTrue();
    }

    [Test]
    public async Task Two_exhausted_windows_block_until_the_later_reset() {
        var reading = Apply(Obs(10, fresh: true, [W("claude", 300, 100, At(300)), W("claude", 10080, 100.5, At(5000))], [Anything]));

        await Assert.That(reading.Blocked!.Constraints.Length).IsEqualTo(2);
        await Assert.That(ReadingMerge.BlockedUntil(reading)).IsEqualTo(At(5000));
        await Assert.That(ReadingMerge.IsBlocked(reading, At(301))).IsTrue();
        await Assert.That(ReadingMerge.IsBlocked(reading, At(5001))).IsFalse();
    }

    [Test]
    public async Task Partial_newer_observation_does_not_clear() {
        var reading = Apply(
            Obs(10, fresh: true, CodexBelow(), [Anything]),
            Obs(20, fresh: true, [W("codex", 300, 10, At(300))])
        );

        await Assert.That(reading.Blocked!.ClearedAt).IsNull();
        await Assert.That(ReadingMerge.IsBlocked(reading, At(21))).IsTrue();
    }

    [Test]
    public async Task Unknown_block_survives_time_and_a_replayed_all_below_observation() {
        var reading = Apply(
            Obs(10, fresh: true, CodexBelow(), [Anything]),
            Obs(20, fresh: false, CodexBelow())
        );

        await Assert.That(reading.Blocked!.ClearedAt).IsNull();
        await Assert.That(ReadingMerge.IsBlocked(reading, At(1_000_000))).IsTrue();
    }

    [Test]
    public async Task Fresh_complete_all_below_observation_clears() {
        var reading = Apply(
            Obs(10, fresh: true, CodexBelow(), [Anything]),
            Obs(20, fresh: true, CodexBelow())
        );

        await Assert.That(reading.Blocked!.ClearedAt).IsEqualTo(At(20));
        await Assert.That(reading.Blocked.Constraints).IsEmpty();
        await Assert.That(ReadingMerge.IsBlocked(reading, At(21))).IsFalse();
    }

    [Test]
    public async Task Reported_reached_type_never_clears() {
        var reading = Apply(
            Obs(10, fresh: true, CodexBelow(), [Anything]),
            Obs(20, fresh: true, CodexBelow(), reached: true)
        );

        await Assert.That(ReadingMerge.IsBlocked(reading, At(21))).IsTrue();
    }

    [Test]
    public async Task Passing_the_last_reset_unblocks() {
        var reading = Apply(Obs(10, fresh: true, [W("claude", 300, 100, At(300))], [Anything]));

        await Assert.That(ReadingMerge.IsBlocked(reading, At(299))).IsTrue();
        await Assert.That(ReadingMerge.IsBlocked(reading, At(300))).IsFalse();
    }

    [Test]
    public async Task Primary_then_secondary_signals_accumulate() {
        var reading = Apply(
            Obs(10, fresh: true, CodexBelow(300, 5000), [Primary]),
            Obs(20, fresh: true, limits: [Secondary])
        );

        await Assert.That(reading.Blocked!.Constraints.Length).IsEqualTo(2);
        await Assert.That(ReadingMerge.BlockedUntil(reading)).IsEqualTo(At(5000));
        await Assert.That(ReadingMerge.IsBlocked(reading, At(301))).IsTrue();
        await Assert.That(ReadingMerge.IsBlocked(reading, At(5001))).IsFalse();
    }

    [Test]
    public async Task Secondary_then_primary_signals_accumulate() {
        var reading = Apply(
            Obs(10, fresh: true, CodexBelow(5000, 300), [Secondary]),
            Obs(20, fresh: true, limits: [Primary])
        );

        await Assert.That(reading.Blocked!.Constraints.Length).IsEqualTo(2);
        await Assert.That(ReadingMerge.IsBlocked(reading, At(301))).IsTrue();
        await Assert.That(ReadingMerge.IsBlocked(reading, At(5001))).IsFalse();
    }

    [Test]
    public async Task Block_and_clear_merged_in_either_order_converge() {
        var block = Apply(Obs(10, fresh: true, CodexBelow(), [Anything]));
        var clear = ReadingMerge.Apply(block, Obs(20, fresh: true, CodexBelow()));

        var blockFirst = ReadingMerge.Merge(block, clear);
        var clearFirst = ReadingMerge.Merge(clear, block);

        await Assert.That(ReadingMerge.Equivalent(blockFirst, clearFirst)).IsTrue();
        await Assert.That(ReadingMerge.IsBlocked(blockFirst, At(21))).IsFalse();
        await Assert.That(ReadingMerge.IsBlocked(clearFirst, At(21))).IsFalse();
    }

    [Test]
    public async Task Newer_block_survives_an_older_clear_in_either_order() {
        var clear = Apply(Obs(10, fresh: true, CodexBelow()));
        var block = ReadingMerge.Apply(clear, Obs(20, fresh: true, limits: [Anything]));

        await Assert.That(ReadingMerge.IsBlocked(ReadingMerge.Merge(block, clear), At(21))).IsTrue();
        await Assert.That(ReadingMerge.IsBlocked(ReadingMerge.Merge(clear, block), At(21))).IsTrue();
    }

    [Test]
    public async Task Rewound_older_all_below_observation_after_a_newer_block_does_not_clear() {
        var reading = Apply(
            Obs(20, fresh: true, CodexBelow(), [Anything]),
            Obs(10, fresh: true, CodexBelow())
        );

        await Assert.That(reading.Blocked!.ClearedAt).IsNull();
        await Assert.That(ReadingMerge.IsBlocked(reading, At(21))).IsTrue();
    }

    [Test]
    public async Task Older_snapshot_replayed_after_expiry_stays_unblocked() {
        var older   = Apply(Obs(10, fresh: true, [W("codex", 300, 100, At(300))], [Anything]));
        var current = Apply(Obs(400, fresh: true, [W("codex", 300, 100, At(700))], [Anything]));

        var merged = ReadingMerge.Merge(current, older);

        await Assert.That(ReadingMerge.IsBlocked(merged, At(701))).IsFalse();
    }

    [Test]
    public async Task Unknown_constraint_with_expired_window_constraints_stays_blocked() {
        var reading = Apply(
            Obs(10, fresh: true, [W("codex", 300, 100, At(300))], [Anything]),
            Obs(20, fresh: true, limits: [new LimitSignal("codex_other", null)])
        );

        await Assert.That(ReadingMerge.IsBlocked(reading, At(1000))).IsTrue();
        await Assert.That(ReadingMerge.BlockedUntil(reading)).IsNull();
    }

    [Test]
    public async Task Explicit_signal_blocks_below_100_percent() {
        var reading = Apply(Obs(10, fresh: true, CodexBelow(), [Primary], reached: true));

        await Assert.That(reading.Blocked!.Constraints).IsEquivalentTo([new BlockConstraintDto("codex", 300, At(300), At(10))]);
        await Assert.That(ReadingMerge.IsBlocked(reading, At(11))).IsTrue();
    }

    [Test]
    public async Task Late_older_clear_never_moves_clearedAt_back() {
        var block   = Apply(Obs(10, fresh: true, CodexBelow(), [Anything]));
        var cleared = ReadingMerge.Apply(block, Obs(30, fresh: true, CodexBelow()));
        var late    = ReadingMerge.Apply(cleared, Obs(20, fresh: true, CodexBelow()));

        var blockAt25 = Apply(Obs(25, fresh: true, CodexBelow(), [Anything]));
        var merged    = ReadingMerge.Merge(late, blockAt25);

        await Assert.That(merged.Blocked!.ClearedAt).IsEqualTo(At(30));
        await Assert.That(merged.Blocked.Constraints).IsEmpty();
        await Assert.That(ReadingMerge.IsBlocked(merged, At(31))).IsFalse();
    }

    [Test]
    public async Task Replayed_old_signal_after_clear_cannot_poison_a_later_block() {
        var reading = Apply(
            Obs(10, fresh: true, CodexBelow(), [Anything]),
            Obs(30, fresh: true, CodexBelow()),
            Obs(20, fresh: true, limits: [Anything]),
            Obs(40, fresh: true, limits: [Primary])
        );

        await Assert.That(reading.Blocked!.Constraints).IsEquivalentTo([new BlockConstraintDto("codex", 300, At(300), At(40))]);
        await Assert.That(reading.Blocked.BlockedAt).IsEqualTo(At(40));
        await Assert.That(ReadingMerge.IsBlocked(reading, At(301))).IsFalse();
    }

    [Test]
    public async Task Named_limit_signal_constrains_only_that_limit() {
        var reading = Apply(Obs(10, fresh: true,
            [W("codex", 300, 100, At(300)), W("codex", 10080, 40, At(5000)), W("codex_other", 300, 100, At(200))],
            [new LimitSignal("codex", null)]));

        await Assert.That(reading.Blocked!.Constraints).IsEquivalentTo([new BlockConstraintDto("codex", 300, At(300), At(10))]);
    }

    [Test]
    public async Task Named_limit_with_one_window_below_100_uses_that_window() {
        var reading = Apply(Obs(10, fresh: true, [W("codex", 300, 40, At(300)), W("codex_other", 300, 100, At(200))], [new LimitSignal("codex", null)]));

        await Assert.That(reading.Blocked!.Constraints).IsEquivalentTo([new BlockConstraintDto("codex", 300, At(300), At(10))]);
    }

    [Test]
    public async Task Named_limit_with_several_windows_below_100_is_unknown() {
        var reading = Apply(Obs(10, fresh: true, CodexBelow(), [new LimitSignal("codex", null)]));

        await Assert.That(ReadingMerge.BlockedUntil(reading)).IsNull();
        await Assert.That(reading.Blocked!.Constraints.Length).IsEqualTo(1);
    }

    [Test]
    public async Task Named_window_below_100_uses_its_reset() {
        var reading = Apply(
            Obs(10, fresh: true, [W("codex", 300, 50, At(300))]),
            Obs(20, fresh: true, limits: [Primary])
        );

        await Assert.That(reading.Blocked!.Constraints).IsEquivalentTo([new BlockConstraintDto("codex", 300, At(300), At(20))]);
    }

    [Test]
    public async Task Signal_naming_nothing_with_every_window_below_100_is_unknown() {
        var reading = Apply(Obs(10, fresh: true, CodexBelow(), [Anything]));

        await Assert.That(reading.Blocked!.Constraints).IsEquivalentTo([new BlockConstraintDto(null, null, null, At(10))]);
    }

    [Test]
    public async Task Metadata_only_observation_records_plan_and_never_clears() {
        var reading = Apply(
            Obs(10, fresh: true, CodexBelow(), [Anything]),
            Obs(20, fresh: true, plan: "pro", credits: new CreditsDto(true, false, "3"))
        );

        await Assert.That(reading.Plan).IsEqualTo("pro");
        await Assert.That(reading.Credits).IsNotNull();
        await Assert.That(reading.Blocked!.ClearedAt).IsNull();
        await Assert.That(ReadingMerge.IsBlocked(reading, At(21))).IsTrue();
    }

    [Test]
    public async Task Unbounded_signal_stays_blocked_past_every_window_reset() {
        var blocked = Apply(Obs(10, fresh: true, [W("codex", 300, 100, At(300))], [new LimitSignal("codex", 300, Unbounded: true)], reached: true));

        await Assert.That(blocked.Blocked!.Constraints.Single().ResetsAt).IsNull();
        await Assert.That(ReadingMerge.IsBlocked(blocked, At(10_000))).IsTrue();

        var cleared = ReadingMerge.Apply(blocked, Obs(10_000, fresh: true, [W("codex", 300, 0, At(10_300))]));

        await Assert.That(ReadingMerge.IsBlocked(cleared, At(10_001))).IsFalse();
    }

    [Test]
    public async Task Sparse_notification_covering_every_window_never_clears() {
        var blocked = Apply(Obs(10, fresh: true, CodexBelow(), [Anything]));
        var sparse  = ReadingMerge.Apply(blocked, Obs(20, fresh: true, CodexBelow(), known: false));

        await Assert.That(ReadingMerge.IsBlocked(sparse, At(21))).IsTrue();

        var known = ReadingMerge.Apply(sparse, Obs(30, fresh: true, CodexBelow()));

        await Assert.That(ReadingMerge.IsBlocked(known, At(31))).IsFalse();
    }

    [Test]
    public async Task Complete_multi_limit_response_clears_an_unknown_block() {
        UsageWindowDto[] both = [W("codex", 300, 20, At(300)), W("codex_other", 300, 30, At(400))];
        var blocked = Apply(Obs(10, fresh: true, both, [Anything]));

        var partial = ReadingMerge.Apply(blocked, Obs(20, fresh: true, [W("codex", 300, 20, At(300))]));
        await Assert.That(ReadingMerge.IsBlocked(partial, At(21))).IsTrue();

        var cleared = ReadingMerge.Apply(partial, Obs(30, fresh: true, both));
        await Assert.That(ReadingMerge.IsBlocked(cleared, At(31))).IsFalse();
    }

    [Test]
    public async Task Held_window_reset_before_the_observation_does_not_block_a_clear() {
        var blocked = Apply(Obs(10, fresh: true, [W("codex", 300, 20, At(300)), W("codex_other", 300, 30, At(50))], [Anything]));

        var cleared = ReadingMerge.Apply(blocked, Obs(60, fresh: true, [W("codex", 300, 20, At(300))]));

        await Assert.That(cleared.Blocked!.ClearedAt).IsEqualTo(At(60));
        await Assert.That(ReadingMerge.IsBlocked(cleared, At(61))).IsFalse();
    }

    [Test]
    public async Task Held_window_without_a_reset_still_blocks_a_clear() {
        var blocked = Apply(Obs(10, fresh: true, [W("codex", 300, 20, At(300)), W("codex_other", 300, 30, null)], [Anything]));

        var reading = ReadingMerge.Apply(blocked, Obs(60, fresh: true, [W("codex", 300, 20, At(300))]));

        await Assert.That(reading.Blocked!.ClearedAt).IsNull();
        await Assert.That(ReadingMerge.IsBlocked(reading, At(61))).IsTrue();
    }

    [Test]
    public async Task LiveConstraints_drops_lapsed_and_keeps_unknown() {
        var lapsed  = new BlockConstraintDto("codex", 300, At(300), At(10));
        var unknown = new BlockConstraintDto(null, null, null, At(10));
        var future  = new BlockConstraintDto("codex", 10080, At(5000), At(10));
        var reading = new AccountReadingDto(1, [], null, null, new BlockedMarkerDto(At(10), null, [lapsed, unknown, future]));

        await Assert.That(ReadingMerge.LiveConstraints(reading, At(301))).IsEquivalentTo([unknown, future]);
    }

    [Test]
    public async Task LiveConstraints_is_empty_once_cleared() {
        var future  = new BlockConstraintDto("codex", 10080, At(5000), At(10));
        var reading = new AccountReadingDto(1, [], null, null, new BlockedMarkerDto(At(10), At(20), [future]));

        await Assert.That(ReadingMerge.LiveConstraints(reading, At(21))).IsEmpty();
    }
}
