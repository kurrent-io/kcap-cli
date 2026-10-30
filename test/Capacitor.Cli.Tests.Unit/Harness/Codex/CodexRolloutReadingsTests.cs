using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Harness.Codex;

namespace Capacitor.Cli.Tests.Unit.Harness.Codex;

public class CodexRolloutReadingsTests {
    const string Stamp = "2026-10-24T23:27:20.165Z";

    static readonly DateTimeOffset Produced = DateTimeOffset.Parse(Stamp, System.Globalization.CultureInfo.InvariantCulture);
    static readonly DateTimeOffset Reset    = DateTimeOffset.FromUnixTimeSeconds(1793449575);

    [TempHome] public required TempHome Home { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    string       _rollout = "";
    AccountStore _accounts = null!;

    [Before(Test)]
    public void Register() {
        var dir = Home.CreateDir(".codex-b");
        _rollout = Home.CreateFile(".codex-b/sessions/2026/10/24/rollout-x.jsonl", "");
        _accounts = new AccountStore(Tmp.PathTo("accounts"));
        _accounts.Mutate(r => (r with { Accounts = [new VendorAccount("b", HarnessId.Codex, AccountDirectory.Normalize(dir), "b", DateTimeOffset.UnixEpoch, Generation: 3)] }, 0));
    }

    CodexRolloutReadings Producer() => CodexRolloutReadings.For(_accounts, _rollout, Home)!;

    AccountReadingDto? Stored() => _accounts.Readings.LoadAll().TryGetValue("b", out var file) ? file.Reading : null;

    static string Line(string rateLimits, string timestamp = Stamp, string type = "token_count") =>
        $$$"""{"timestamp":"{{{timestamp}}}","type":"event_msg","payload":{"type":"{{{type}}}","info":null,"rate_limits":{{{rateLimits}}}}}""";

    static string Window(double used, int minutes = 10080, long resets = 1793449575) =>
        $$"""{"used_percent":{{used}},"window_minutes":{{minutes}},"resets_at":{{resets}}}""";

    [Test]
    public async Task Primary_only_records_the_window_confirmed_at_the_line_timestamp() {
        Producer().Observe(Line($$"""{"limit_id":"codex","primary":{{Window(1.0)}},"secondary":null,"plan_type":"pro"}"""));

        var reading = Stored()!;
        var window  = reading.Windows.Single();

        await Assert.That(reading.Generation).IsEqualTo(3L);
        await Assert.That(window.LimitId).IsEqualTo("codex");
        await Assert.That(window.WindowMinutes).IsEqualTo(10080);
        await Assert.That(window.UsedPercent).IsEqualTo(1.0);
        await Assert.That(window.ResetsAt).IsEqualTo(Reset);
        await Assert.That(window.ConfirmedAt).IsEqualTo(Produced);
        await Assert.That(reading.Plan).IsEqualTo("pro");
        await Assert.That(ReadingMerge.IsBlocked(reading, Produced)).IsFalse();
    }

    [Test]
    public async Task Both_windows_are_recorded() {
        Producer().Observe(Line($$"""{"limit_id":"codex","primary":{{Window(10, 300)}},"secondary":{{Window(40)}}}"""));

        var reading = Stored()!;

        await Assert.That(reading.Windows.Select(w => w.WindowMinutes).Order().ToArray()).IsEquivalentTo(new[] { 300, 10080 });
        await Assert.That(reading.Windows.All(w => w.ConfirmedAt == Produced)).IsTrue();
    }

    [Test]
    public async Task Several_limit_ids_are_kept_apart() {
        var producer = Producer();
        producer.Observe(Line($$"""{"limit_id":"codex","primary":{{Window(10, 300)}}}"""));
        producer.Observe(Line($$"""{"limit_id":"codex_other","primary":{{Window(20, 300)}}}""", "2026-10-24T23:28:00.000Z"));

        var reading = Stored()!;

        await Assert.That(reading.Windows.Select(w => w.LimitId).Order().ToArray()).IsEquivalentTo(new[] { "codex", "codex_other" });
    }

    const string Credits = """{"has_credits":true,"unlimited":false,"balance":"62500.0000000000"}""";

    [Test]
    public async Task Credits_are_recorded() {
        Producer().Observe(Line($$"""{"limit_id":"codex","primary":{{Window(1)}},"credits":{{Credits}}}"""));

        var credits = Stored()!.Credits!;

        await Assert.That(credits.HasCredits).IsTrue();
        await Assert.That(credits.Unlimited).IsFalse();
        await Assert.That(credits.Balance).IsEqualTo("62500.0000000000");
    }

    [Test]
    public async Task Limit_reached_with_an_exhausted_window_blocks_until_its_reset() {
        Producer().Observe(Line($$"""{"limit_id":"codex","primary":{{Window(100)}},"rate_limit_reached_type":"rate_limit_reached"}"""));

        var reading = Stored()!;

        await Assert.That(ReadingMerge.IsBlocked(reading, Produced)).IsTrue();
        await Assert.That(ReadingMerge.BlockedUntil(reading)).IsEqualTo(Reset);
    }

    [Test]
    public async Task Limit_reached_without_a_matching_window_blocks_with_an_unknown_end() {
        Producer().Observe(Line("""{"limit_id":"codex","primary":null,"rate_limit_reached_type":"rate_limit_reached"}"""));

        var reading = Stored()!;

        await Assert.That(ReadingMerge.IsBlocked(reading, Produced)).IsTrue();
        await Assert.That(ReadingMerge.BlockedUntil(reading)).IsNull();
    }

    [Test]
    public async Task Limit_reached_blocks_even_when_cached_percentages_are_below_full() {
        var producer = Producer();
        producer.Observe(Line($$"""{"limit_id":"codex","primary":{{Window(40)}}}""", "2026-10-24T23:00:00.000Z"));
        producer.Observe(Line("""{"limit_id":"codex","primary":null,"rate_limit_reached_type":"rate_limit_reached"}"""));

        var reading = Stored()!;

        await Assert.That(reading.Windows.Single().UsedPercent).IsEqualTo(40.0);
        await Assert.That(ReadingMerge.IsBlocked(reading, Produced)).IsTrue();
    }

    [Test]
    public async Task Line_in_no_registered_account_records_nothing() {
        var elsewhere = Home.CreateFile(".codex-c/sessions/2026/10/24/rollout-y.jsonl", "");

        var producer = CodexRolloutReadings.For(_accounts, elsewhere, Home);

        await Assert.That(producer).IsNull();
        await Assert.That(_accounts.Readings.LoadAll().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Null_rate_limits_writes_nothing() {
        Producer().Observe(Line("null"));

        await Assert.That(_accounts.Readings.LoadAll().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Other_lines_and_malformed_lines_write_nothing_and_do_not_throw() {
        var producer = Producer();
        producer.Observe("""{"type":"event_msg","payload":{"type":"agent_message","message":"token_count"}}""");
        producer.Observe("""{"timestamp":"not a date","type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":1,"window_minutes":300}}}}""");
        producer.Observe("""{"timestamp":"2026-10-24T23:27:20.165Z","type":"event_msg","payload":{"type":"token_count""");
        producer.Observe("");

        await Assert.That(_accounts.Readings.LoadAll().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Replayed_older_line_never_lowers_confirmedAt() {
        var producer = Producer();
        producer.Observe(Line($$"""{"limit_id":"codex","primary":{{Window(5)}}}""", "2026-10-24T23:30:00.000Z"));
        producer.Observe(Line($$"""{"limit_id":"codex","primary":{{Window(2)}}}""", "2026-10-24T23:10:00.000Z"));

        await Assert.That(Stored()!.Windows.Single().ConfirmedAt).IsEqualTo(DateTimeOffset.Parse("2026-10-24T23:30:00.000Z", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task A_batch_writes_once_and_equals_applying_the_lines_one_by_one() {
        string[] lines = [
            Line($$"""{"limit_id":"codex","primary":{{Window(10, 300)}},"secondary":{{Window(20)}},"plan_type":"pro"}""", "2026-10-24T23:01:00.000Z"),
            Line($$"""{"limit_id":"codex","primary":{{Window(30, 300)}}}""", "2026-10-24T23:02:00.000Z"),
            Line($$"""{"limit_id":"codex","primary":{{Window(100, 300)}},"rate_limit_reached_type":"rate_limit_reached"}""", "2026-10-24T23:03:00.000Z"),
        ];

        var second = new AccountStore(Tmp.PathTo("accounts2"));
        second.Mutate(r => (r with { Accounts = _accounts.Load().Accounts }, 0));
        var single = new CodexRolloutReadings(second.Readings, second.Load().Accounts.Single());
        foreach (var line in lines) single.Observe(line);

        var before = _accounts.Readings.Stat();
        Producer().Observe(lines);

        await Assert.That(ReadingMerge.Equivalent(Stored()!, second.Readings.LoadAll()["b"].Reading)).IsTrue();
        await Assert.That(_accounts.Readings.Stat().Length).IsEqualTo(before.Length + 1);
        await Assert.That(Stored()!.Windows.Single(w => w.WindowMinutes == 300).UsedPercent).IsEqualTo(100.0);
    }

    [Test]
    public async Task A_block_followed_by_a_later_clear_in_one_batch_ends_cleared() {
        Producer().Observe([
            Line($$"""{"limit_id":"codex","primary":{{Window(100)}},"rate_limit_reached_type":"rate_limit_reached"}""", "2026-10-24T23:00:00.000Z"),
            Line($$"""{"limit_id":"codex","primary":{{Window(5)}}}""", "2026-10-24T23:10:00.000Z"),
        ]);

        await Assert.That(ReadingMerge.IsBlocked(Stored()!, Produced)).IsFalse();
    }

    [Test]
    public async Task A_batch_without_token_counts_does_not_touch_the_readings() {
        Producer().Observe([
            """{"type":"event_msg","payload":{"type":"agent_message","message":"hi"}}""",
            """{"type":"response_item","payload":{"type":"message"}}""",
        ]);

        await Assert.That(_accounts.Readings.Stat().Length).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(_accounts.Directory, "readings.lock"))).IsFalse();
    }

    void StartGenerationAt(string timestamp) =>
        _accounts.Mutate(r => (r with { Accounts = [r.Accounts.Single() with { GenerationStartedAt = DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture) }] }, 0));

    [Test]
    public async Task Lines_before_the_generation_start_are_dropped_from_a_batch() {
        StartGenerationAt("2026-10-24T23:05:00.000Z");

        Producer().Observe([
            Line($$"""{"limit_id":"codex","primary":{{Window(80, 300)}}}""", "2026-10-24T23:00:00.000Z"),
            Line($$"""{"limit_id":"codex","primary":{{Window(10, 10080)}}}""", "2026-10-24T23:10:00.000Z"),
        ]);

        var reading = Stored()!;

        await Assert.That(reading.Windows.Single().WindowMinutes).IsEqualTo(10080);
        await Assert.That(reading.Windows.Single().UsedPercent).IsEqualTo(10.0);
    }

    [Test]
    public async Task Batch_entirely_before_the_generation_start_writes_nothing() {
        StartGenerationAt("2026-10-24T23:30:00.000Z");

        Producer().Observe([
            Line($$"""{"limit_id":"codex","primary":{{Window(80, 300)}}}""", "2026-10-24T23:00:00.000Z"),
            Line($$"""{"limit_id":"codex","primary":{{Window(10, 300)}}}""", "2026-10-24T23:10:00.000Z"),
        ]);

        await Assert.That(_accounts.Readings.Stat().Length).IsEqualTo(0);
    }
}
