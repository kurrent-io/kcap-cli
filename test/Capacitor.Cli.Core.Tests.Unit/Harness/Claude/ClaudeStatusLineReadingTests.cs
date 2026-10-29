using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

public class ClaudeStatusLineReadingTests {
    static readonly DateTimeOffset T0          = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset FiveReset   = new(2026, 10, 26, 15, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset SevenReset  = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);

    static ReadingFile Empty => ReadingFile.Empty(0);

    static JsonElement Payload(
        string? session    = "s1",
        long?   duration   = 1000,
        double? five       = 20,
        double? seven      = 40,
        bool    rateLimits = true
    ) {
        var root = new JsonObject();
        if (session is not null) root["session_id"] = session;
        if (duration is not null) root["cost"] = new JsonObject { ["total_api_duration_ms"] = duration };

        if (rateLimits) {
            var limits = new JsonObject();
            if (five is not null) limits["five_hour"] = new JsonObject { ["used_percentage"] = five, ["resets_at"] = FiveReset.ToUnixTimeSeconds() };
            if (seven is not null) limits["seven_day"] = new JsonObject { ["used_percentage"] = seven, ["resets_at"] = SevenReset.ToUnixTimeSeconds() };
            root["rate_limits"] = limits;
        }

        return JsonDocument.Parse(root.ToJsonString()).RootElement.Clone();
    }

    static ReadingFile Run(ReadingFile held, JsonElement payload, DateTimeOffset at) =>
        ClaudeStatusLineReading.Apply(held, payload, at) ?? throw new InvalidOperationException("expected a reading");

    static UsageWindowDto Window(ReadingFile file, int minutes) => file.Reading.Windows.Single(w => w.LimitId == "claude" && w.WindowMinutes == minutes);

    [Test]
    public async Task First_run_records_baseline_and_leaves_freshness_unknown() {
        var file = Run(Empty, Payload(duration: 1000), T0);

        await Assert.That(Window(file, 300)).IsEqualTo(new UsageWindowDto("claude", 300, 20, FiveReset, null));
        await Assert.That(Window(file, 10080)).IsEqualTo(new UsageWindowDto("claude", 10080, 40, SevenReset, null));
        await Assert.That(file.Sessions).IsEquivalentTo(new[] { new ReadingSession("s1", 1000) });
    }

    [Test]
    public async Task Unchanged_repeat_after_baseline_stays_unknown() {
        var first  = Run(Empty, Payload(duration: 1000), T0);
        var second = Run(first, Payload(duration: 1000), T0.AddMinutes(1));

        await Assert.That(Window(second, 300).ConfirmedAt).IsNull();
        await Assert.That(Window(second, 10080).ConfirmedAt).IsNull();
        await Assert.That(second.Sessions).IsEquivalentTo(new[] { new ReadingSession("s1", 1000) });
    }

    [Test]
    public async Task Api_duration_increase_confirms() {
        var first  = Run(Empty, Payload(duration: 1000), T0);
        var second = Run(first, Payload(duration: 1500, five: 25), T0.AddMinutes(1));

        await Assert.That(Window(second, 300)).IsEqualTo(new UsageWindowDto("claude", 300, 25, FiveReset, T0.AddMinutes(1)));
        await Assert.That(Window(second, 10080).ConfirmedAt).IsEqualTo(T0.AddMinutes(1));
        await Assert.That(second.Sessions).IsEquivalentTo(new[] { new ReadingSession("s1", 1500) });
    }

    [Test]
    public async Task Repeated_payload_with_unchanged_counter_does_not_move_confirmedAt() {
        var baseline  = Run(Empty, Payload(duration: 1000), T0);
        var confirmed = Run(baseline, Payload(duration: 1500), T0.AddMinutes(1));
        var repeated  = Run(confirmed, Payload(duration: 1500), T0.AddMinutes(2));

        await Assert.That(Window(repeated, 300).ConfirmedAt).IsEqualTo(T0.AddMinutes(1));
        await Assert.That(Window(repeated, 10080).ConfirmedAt).IsEqualTo(T0.AddMinutes(1));
    }

    [Test]
    public async Task New_response_with_equal_values_moves_confirmedAt() {
        var baseline  = Run(Empty, Payload(duration: 1000), T0);
        var confirmed = Run(baseline, Payload(duration: 1500), T0.AddMinutes(1));
        var next      = Run(confirmed, Payload(duration: 1800), T0.AddMinutes(2));

        await Assert.That(Window(next, 300)).IsEqualTo(new UsageWindowDto("claude", 300, 20, FiveReset, T0.AddMinutes(2)));
        await Assert.That(Window(next, 10080).ConfirmedAt).IsEqualTo(T0.AddMinutes(2));
    }

    [Test]
    public async Task Payload_without_counter_keeps_its_windows_but_is_never_fresh() {
        var first  = Run(Empty, Payload(duration: null, five: 20), T0);
        var second = Run(first, Payload(duration: null, five: 30), T0.AddMinutes(1));

        await Assert.That(Window(second, 300)).IsEqualTo(new UsageWindowDto("claude", 300, 30, FiveReset, null));
        await Assert.That(Window(second, 10080)).IsEqualTo(new UsageWindowDto("claude", 10080, 40, SevenReset, null));
        await Assert.That(second.Sessions).IsEmpty();
    }

    [Test]
    public async Task Missing_rate_limits_writes_nothing() {
        await Assert.That(ClaudeStatusLineReading.Apply(Empty, Payload(rateLimits: false), T0)).IsNull();
        await Assert.That(ClaudeStatusLineReading.Apply(Empty, Payload(five: null, seven: null), T0)).IsNull();
        await Assert.That(ClaudeStatusLineReading.Apply(Empty, JsonDocument.Parse("[]").RootElement, T0)).IsNull();
    }

    [Test]
    public async Task One_window_only_merges_that_window() {
        var file = Run(Empty, Payload(seven: null), T0);

        await Assert.That(file.Reading.Windows).IsEquivalentTo(new[] { new UsageWindowDto("claude", 300, 20, FiveReset, null) });
    }

    [Test]
    public async Task Two_sessions_keep_separate_baselines() {
        var a1 = Run(Empty, Payload(session: "a", duration: 5000), T0);
        var b1 = Run(a1, Payload(session: "b", duration: 100), T0.AddMinutes(1));

        // b's counter is below a's baseline, but b is judged against its own.
        var b2 = Run(b1, Payload(session: "b", duration: 200), T0.AddMinutes(2));
        await Assert.That(Window(b2, 300).ConfirmedAt).IsEqualTo(T0.AddMinutes(2));

        // a's counter is above b's baseline, but unchanged against its own.
        var a2 = Run(b2, Payload(session: "a", duration: 5000), T0.AddMinutes(3));
        await Assert.That(Window(a2, 300).ConfirmedAt).IsEqualTo(T0.AddMinutes(2));

        await Assert.That(a2.Sessions.OrderBy(s => s.SessionId, StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(new[] { new ReadingSession("a", 5000), new ReadingSession("b", 200) });
    }

    [Test]
    public async Task Session_list_keeps_the_most_recent_64() {
        var file = Empty;
        for (var i = 0; i < 65; i++) file = Run(file, Payload(session: $"s{i}"), T0.AddMinutes(i));

        file = Run(file, Payload(session: "s1", duration: 2000), T0.AddMinutes(70));
        file = Run(file, Payload(session: "new"), T0.AddMinutes(71));

        var ids = file.Sessions.Select(s => s.SessionId).ToArray();
        await Assert.That(ids.Length).IsEqualTo(64);
        await Assert.That(ids).DoesNotContain("s0");
        await Assert.That(ids).DoesNotContain("s2");
        await Assert.That(ids[^2..]).IsEquivalentTo(new[] { "s1", "new" });
    }

    [Test]
    public async Task Counter_below_its_baseline_is_a_new_baseline_that_later_confirms() {
        var first   = Run(Empty, Payload(duration: 5000), T0);
        var dropped = Run(first, Payload(duration: 1000), T0.AddMinutes(1));

        await Assert.That(Window(dropped, 300).ConfirmedAt).IsNull();
        await Assert.That(dropped.Sessions).IsEquivalentTo(new[] { new ReadingSession("s1", 1000) });

        var grown = Run(dropped, Payload(duration: 2000), T0.AddMinutes(2));

        await Assert.That(Window(grown, 300).ConfirmedAt).IsEqualTo(T0.AddMinutes(2));
        await Assert.That(grown.Sessions).IsEquivalentTo(new[] { new ReadingSession("s1", 2000) });
    }
}
