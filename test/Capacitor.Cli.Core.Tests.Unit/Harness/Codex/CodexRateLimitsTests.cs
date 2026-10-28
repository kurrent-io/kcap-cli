using System.Globalization;
using System.Text.Json;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Tests.Unit.Accounts;
using static Capacitor.Cli.Core.Tests.Unit.Accounts.Readings.ReadingFixtures;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

public class CodexRateLimitsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static JsonElement Json(string json) {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    static long Unix(DateTimeOffset at) => at.ToUnixTimeSeconds();

    static DateTimeOffset Seconds(DateTimeOffset at) => DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds());

    [Test]
    public async Task Rollout_line_from_codex_0_164_reads_its_single_window_credits_and_plan() {
        var producedAt = DateTimeOffset.Parse("2026-10-24T23:27:20.165Z", CultureInfo.InvariantCulture);
        var rateLimits = Json("""
            {"limit_id":"codex","limit_name":null,"primary":{"used_percent":1.0,"window_minutes":10080,"resets_at":1793449575},"secondary":null,"credits":{"has_credits":true,"unlimited":false,"balance":"62500.0000000000"},"individual_limit":null,"spend_control_reached":null,"plan_type":"pro","rate_limit_reached_type":null}
            """);

        var observation = CodexRateLimits.FromRollout(rateLimits, producedAt)!;

        await Assert.That(observation.ProducedAt).IsEqualTo(producedAt);
        await Assert.That(observation.Fresh).IsTrue();
        await Assert.That(observation.LimitStateKnown).IsTrue();
        await Assert.That(observation.LimitReachedReported).IsFalse();
        await Assert.That(observation.Limits).IsEmpty();
        await Assert.That(observation.Plan).IsEqualTo("pro");
        await Assert.That(observation.Credits).IsEqualTo(new CreditsDto(true, false, "62500.0000000000"));
        await Assert.That(observation.Windows.Single())
            .IsEqualTo(new UsageWindowDto("codex", 10080, 1.0, DateTimeOffset.FromUnixTimeSeconds(1793449575), null));
    }

    [Test]
    public async Task Rollout_reads_both_windows() {
        var observation = CodexRateLimits.FromRollout(Json($$$"""
            {"limit_id":null,"primary":{"used_percent":12.5,"window_minutes":300,"resets_at":{{{Unix(At(60))}}}},"secondary":{"used_percent":40,"window_minutes":10080,"resets_at":{{{Unix(At(6000))}}}}}
            """), At(0))!;

        await Assert.That(observation.Windows).IsEquivalentTo([
            new UsageWindowDto("codex", 300, 12.5, Seconds(At(60)), null),
            new UsageWindowDto("codex", 10080, 40, Seconds(At(6000)), null),
        ]);
    }

    [Test]
    public async Task Rollout_lines_of_several_limit_ids_keep_a_window_per_limit() {
        var codex = CodexRateLimits.FromRollout(Json("""{"limit_id":"codex","primary":{"used_percent":10,"window_minutes":300,"resets_at":null}}"""), At(0))!;
        var spark = CodexRateLimits.FromRollout(Json("""{"limit_id":"codex_spark","primary":{"used_percent":70,"window_minutes":300,"resets_at":null}}"""), At(1))!;

        var reading = Apply(codex, spark);

        await Assert.That(reading.Windows.Select(w => (w.LimitId, w.UsedPercent))).IsEquivalentTo([("codex", 10.0), ("codex_spark", 70.0)]);
    }

    [Test]
    public async Task Window_without_a_duration_is_skipped() {
        var observation = CodexRateLimits.FromRollout(Json("""
            {"primary":{"used_percent":5,"window_minutes":null,"resets_at":null},"secondary":{"used_percent":6,"window_minutes":10080,"resets_at":null}}
            """), At(0))!;

        await Assert.That(observation.Windows.Single().WindowMinutes).IsEqualTo(10080);
    }

    [Test]
    public async Task Credits_alone_are_an_observation() {
        var observation = CodexRateLimits.FromRollout(Json("""{"credits":{"has_credits":false,"unlimited":false,"balance":null}}"""), At(0))!;

        await Assert.That(observation.Credits).IsEqualTo(new CreditsDto(false, false, null));
        await Assert.That(observation.Windows).IsEmpty();
    }

    [Test]
    public async Task Null_rate_limits_and_an_empty_snapshot_are_no_observation() {
        await Assert.That(CodexRateLimits.FromRollout(Json("null"), At(0))).IsNull();
        await Assert.That(CodexRateLimits.FromRollout(Json("""{"limit_id":"codex","primary":null,"secondary":null,"rate_limit_reached_type":null}"""), At(0))).IsNull();
        await Assert.That(CodexRateLimits.FromAppServer(Json("null"), At(0))).IsNull();
    }

    [Test]
    public async Task Both_windows_null_with_plan_records_only_the_plan() {
        var observation = CodexRateLimits.FromRollout(Json("""{"limit_id":"codex","primary":null,"secondary":null,"plan_type":"plus"}"""), At(0))!;

        await Assert.That(observation.Plan).IsEqualTo("plus");
        await Assert.That(observation.Windows).IsEmpty();
        await Assert.That(observation.Credits).IsNull();
        await Assert.That(observation.Limits).IsEmpty();
        await Assert.That(observation.LimitReachedReported).IsFalse();
    }

    [Test]
    public async Task Limit_reached_with_a_window_at_100_constrains_that_window() {
        var observation = CodexRateLimits.FromRollout(Json($$$"""
            {"limit_id":"codex","primary":{"used_percent":100,"window_minutes":300,"resets_at":{{{Unix(At(60))}}}},"secondary":{"used_percent":30,"window_minutes":10080,"resets_at":{{{Unix(At(6000))}}}},"rate_limit_reached_type":"rate_limit_reached"}
            """), At(0))!;

        await Assert.That(observation.LimitReachedReported).IsTrue();
        await Assert.That(observation.Limits.Single()).IsEqualTo(new LimitSignal("codex", null));

        var reading = Apply(observation);
        await Assert.That(ReadingMerge.IsBlocked(reading, At(1))).IsTrue();
        await Assert.That(ReadingMerge.BlockedUntil(reading)).IsEqualTo(Seconds(At(60)));
    }

    [Test]
    public async Task Limit_reached_without_a_window_is_an_unknown_constraint() {
        var observation = CodexRateLimits.FromRollout(Json("""{"limit_id":"codex","rate_limit_reached_type":"rate_limit_reached"}"""), At(0))!;

        var reading = Apply(observation);

        await Assert.That(ReadingMerge.IsBlocked(reading, At(100_000))).IsTrue();
        await Assert.That(ReadingMerge.BlockedUntil(reading)).IsNull();
    }

    [Test]
    public async Task Reached_type_with_cached_percentages_below_100_constrains_the_limits_only_window() {
        var observation = CodexRateLimits.FromRollout(Json($$$"""
            {"limit_id":"codex","primary":{"used_percent":80,"window_minutes":300,"resets_at":{{{Unix(At(60))}}}},"rate_limit_reached_type":"rate_limit_reached"}
            """), At(0))!;

        var reading = Apply(observation);

        await Assert.That(ReadingMerge.BlockedUntil(reading)).IsEqualTo(Seconds(At(60)));
        await Assert.That(ReadingMerge.IsBlocked(reading, At(61))).IsFalse();
    }

    [Test]
    public async Task Reached_type_with_two_windows_at_different_resets_constrains_the_one_at_100() {
        var observation = CodexRateLimits.FromAppServer(Json($$$"""
            {"limitId":"codex","primary":{"usedPercent":60,"windowDurationMins":300,"resetsAt":{{{Unix(At(60))}}}},"secondary":{"usedPercent":100,"windowDurationMins":10080,"resetsAt":{{{Unix(At(6000))}}}},"rateLimitReachedType":"rate_limit_reached"}
            """), At(0))!;

        var reading = Apply(observation);

        await Assert.That(reading.Blocked!.Constraints.Single().WindowMinutes).IsEqualTo(10080);
        await Assert.That(ReadingMerge.BlockedUntil(reading)).IsEqualTo(Seconds(At(6000)));
    }

    [Test]
    [Arguments("workspace_owner_credits_depleted")]
    [Arguments("workspace_member_credits_depleted")]
    [Arguments("workspace_owner_usage_limit_reached")]
    [Arguments("workspace_member_usage_limit_reached")]
    [Arguments("a_reason_this_build_does_not_know")]
    public async Task Credits_depleted_and_spend_cap_signals_are_unbounded(string reason) {
        var observation = CodexRateLimits.FromRollout(Json($$$"""
            {"limit_id":"codex","primary":{"used_percent":100,"window_minutes":300,"resets_at":{{{Unix(At(60))}}}},"rate_limit_reached_type":"{{{reason}}}"}
            """), At(0))!;

        await Assert.That(observation.Limits.Single()).IsEqualTo(new LimitSignal("codex", null, Unbounded: true));
        await Assert.That(ReadingMerge.IsBlocked(Apply(observation), At(61))).IsTrue();
    }

    [Test]
    public async Task Spend_control_only_snapshot_blocks() {
        var rollout = CodexRateLimits.FromRollout(Json("""{"spend_control_reached":true}"""), At(0))!;
        var app     = CodexRateLimits.FromAppServer(Json("""{"limitId":null,"primary":null,"secondary":null,"credits":null,"planType":null,"rateLimitReachedType":null,"spendControlReached":true}"""), At(0))!;

        foreach (var observation in new[] { rollout, app }) {
            await Assert.That(observation.Limits.Single()).IsEqualTo(new LimitSignal("codex", null, Unbounded: true));
            await Assert.That(ReadingMerge.IsBlocked(ReadingMerge.Apply(ReadingMerge.Empty(0), observation), At(100_000))).IsTrue();
        }
    }

    [Test]
    public async Task App_server_shape_reads_camel_case() {
        var observation = CodexRateLimits.FromAppServer(Json($$$"""
            {"limitId":"codex","limitName":null,"primary":{"usedPercent":7,"windowDurationMins":300,"resetsAt":{{{Unix(At(60))}}}},"secondary":null,"credits":{"hasCredits":true,"unlimited":true,"balance":null},"planType":"team","rateLimitReachedType":null,"spendControlReached":false}
            """), At(0))!;

        await Assert.That(observation.Windows.Single()).IsEqualTo(new UsageWindowDto("codex", 300, 7, Seconds(At(60)), null));
        await Assert.That(observation.Credits).IsEqualTo(new CreditsDto(true, true, null));
        await Assert.That(observation.Plan).IsEqualTo("team");
        await Assert.That(observation.Limits).IsEmpty();
    }

    [Test]
    public async Task App_server_notification_is_not_limit_state_known() {
        var observation = CodexRateLimits.FromAppServer(Json("""{"limitId":"codex","primary":{"usedPercent":7,"windowDurationMins":300,"resetsAt":null}}"""), At(0))!;

        await Assert.That(observation.LimitStateKnown).IsFalse();
        await Assert.That(observation.Fresh).IsTrue();
    }

    [Test]
    public async Task Rate_limits_read_prefers_rate_limits_by_limit_id() {
        var observation = CodexRateLimits.FromRateLimitsRead(Json("""
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":99,"windowDurationMins":300,"resetsAt":null}},
             "rateLimitsByLimitId":{"codex":{"limitId":"codex","primary":{"usedPercent":10,"windowDurationMins":300,"resetsAt":null}}},
             "ordinaryUsageAllowed":true,"accountId":"acct-1"}
            """), At(0))!;

        await Assert.That(observation.Windows.Single().UsedPercent).IsEqualTo(10.0);
        await Assert.That(observation.LimitStateKnown).IsTrue();
    }

    [Test]
    public async Task Rate_limits_read_with_two_limit_ids_is_one_observation() {
        var observation = CodexRateLimits.FromRateLimitsRead(Json("""
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":10,"windowDurationMins":300,"resetsAt":null}},
             "rateLimitsByLimitId":{
               "codex":{"limitId":"codex","primary":{"usedPercent":10,"windowDurationMins":300,"resetsAt":null},"planType":"pro"},
               "codex_spark":{"limitId":"codex_spark","primary":{"usedPercent":20,"windowDurationMins":300,"resetsAt":null},"secondary":{"usedPercent":30,"windowDurationMins":10080,"resetsAt":null}}},
             "ordinaryUsageAllowed":true}
            """), At(0))!;

        await Assert.That(observation.Windows.Select(w => (w.LimitId, w.WindowMinutes))).IsEquivalentTo([("codex", 300), ("codex_spark", 300), ("codex_spark", 10080)]);
        await Assert.That(observation.Plan).IsEqualTo("pro");
    }

    [Test]
    public async Task A_complete_all_below_rate_limits_read_clears_a_block() {
        var blocked = Apply(Obs(0, fresh: true, windows: [W("codex", 300, 100, At(60))], limits: [new LimitSignal("codex", 300)], reached: true));

        var observation = CodexRateLimits.FromRateLimitsRead(Json("""
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":10,"windowDurationMins":300,"resetsAt":null}},"ordinaryUsageAllowed":true}
            """), At(10))!;

        await Assert.That(ReadingMerge.IsBlocked(ReadingMerge.Apply(blocked, observation), At(11))).IsFalse();
    }

    [Test]
    public async Task Rate_limits_read_with_ordinary_usage_null_cannot_clear() {
        var blocked = Apply(Obs(0, fresh: true, windows: [W("codex", 300, 100, At(60))], limits: [new LimitSignal("codex", 300)], reached: true));

        var observation = CodexRateLimits.FromRateLimitsRead(Json("""
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":10,"windowDurationMins":300,"resetsAt":null}},"ordinaryUsageAllowed":null}
            """), At(10))!;

        await Assert.That(observation.LimitStateKnown).IsFalse();
        await Assert.That(ReadingMerge.IsBlocked(ReadingMerge.Apply(blocked, observation), At(11))).IsTrue();
    }

    [Test]
    public async Task Rate_limits_read_with_ordinary_usage_false_and_no_reason_blocks_unknown() {
        var observation = CodexRateLimits.FromRateLimitsRead(Json("""
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":10,"windowDurationMins":300,"resetsAt":null}},"ordinaryUsageAllowed":false}
            """), At(0))!;

        await Assert.That(observation.Limits.Single()).IsEqualTo(new LimitSignal(null, null));
        await Assert.That(observation.LimitStateKnown).IsFalse();

        var reading = ReadingMerge.Apply(ReadingMerge.Empty(0), observation);
        await Assert.That(ReadingMerge.IsBlocked(reading, At(100_000))).IsTrue();
        await Assert.That(ReadingMerge.BlockedUntil(reading)).IsNull();
    }

    [Test]
    public async Task Ordinary_usage_false_with_rate_limit_reached_unblocks_at_its_reset() {
        var store = Registered();
        var limits = CodexRateLimits.FromRateLimitsRead(Json($$$"""
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":100,"windowDurationMins":300,"resetsAt":{{{Unix(At(60))}}}},"secondary":{"usedPercent":40,"windowDurationMins":10080,"resetsAt":{{{Unix(At(6000))}}}},"rateLimitReachedType":"rate_limit_reached"},
             "ordinaryUsageAllowed":false,"accountId":"acct-1"}
            """), At(0))!;

        await AccountRefresh.RunAsync(store, "a", FakeAccountProbe.Returning(SignedIn(limits)), TimeProvider.System, CancellationToken.None);

        var reading = store.Readings.LoadAll()["a"].Reading;
        await Assert.That(limits.Limits.Single()).IsEqualTo(new LimitSignal("codex", null));
        await Assert.That(ReadingMerge.IsBlocked(reading, At(1))).IsTrue();
        await Assert.That(ReadingMerge.IsBlocked(reading, At(61))).IsFalse();
    }

    [Test]
    public async Task Ordinary_usage_false_with_credits_depleted_survives_the_reset() {
        var store = Registered();
        var limits = CodexRateLimits.FromRateLimitsRead(Json($$$"""
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":100,"windowDurationMins":300,"resetsAt":{{{Unix(At(60))}}}},"rateLimitReachedType":"workspace_member_credits_depleted"},
             "ordinaryUsageAllowed":false}
            """), At(0))!;

        await AccountRefresh.RunAsync(store, "a", FakeAccountProbe.Returning(SignedIn(limits)), TimeProvider.System, CancellationToken.None);

        var reading = store.Readings.LoadAll()["a"].Reading;
        await Assert.That(ReadingMerge.IsBlocked(reading, At(61))).IsTrue();
        await Assert.That(ReadingMerge.BlockedUntil(reading)).IsNull();
    }

    [Test]
    public async Task Account_id_comes_from_the_rate_limits_read() {
        await Assert.That(CodexRateLimits.AccountIdFromRateLimitsRead(Json("""{"rateLimits":{},"accountId":"acct-9"}"""))).IsEqualTo("acct-9");
        await Assert.That(CodexRateLimits.AccountIdFromRateLimitsRead(Json("""{"rateLimits":{},"accountId":null}"""))).IsNull();
    }

    AccountStore Registered() {
        var store = new AccountStore(Tmp.PathTo("accounts"));
        store.Mutate(r => (r with { Accounts = [new VendorAccount("a", HarnessId.Codex, "/h/.codex-a", ".codex-a", DateTimeOffset.UnixEpoch)] }, 0));
        return store;
    }

    static ProbeResult SignedIn(ReadingObservation limits) =>
        new(SignInStates.SignedIn, new AccountIdentity("a@example.com", null, null, "pro", "acct-1"), null, limits, null);
}
