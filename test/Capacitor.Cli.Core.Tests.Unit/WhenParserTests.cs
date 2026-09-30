using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>Pins how a written point in time becomes an instant: the three accepted forms, the
/// zone a date is read in, and every shape that is refused with a message naming the fix.</summary>
public class WhenParserTests {
    static readonly DateTimeOffset Now = new(2026, 9, 28, 15, 0, 0, TimeSpan.Zero);

    // Clocks go forward at 02:00 on the last Sunday of March and back at 03:00 on the last Sunday of October.
    static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.CreateCustomTimeZone(
        "Test/Amsterdam", TimeSpan.FromHours(1), "Test/Amsterdam", "CET", "CEST",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday))
        ]);

    // Clocks go forward at midnight on the first Sunday of November, so that day has no 00:00.
    static readonly TimeZoneInfo MidnightJump = TimeZoneInfo.CreateCustomTimeZone(
        "Test/MidnightJump", TimeSpan.FromHours(-3), "Test/MidnightJump", "STD", "DST",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 0, 0, 0), 11, 1, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 0, 0, 0), 2, 3, DayOfWeek.Sunday))
        ]);

    static FakeTimeProvider Clock(TimeZoneInfo? zone = null) {
        var clock = new FakeTimeProvider(Now);
        clock.SetLocalTimeZone(zone ?? TimeZoneInfo.Utc);

        return clock;
    }

    static DateTimeOffset Parse(string text, TimeZoneInfo? zone = null) =>
        WhenParser.TryParse(text, Clock(zone), out var instant, out var error)
            ? instant
            : throw new InvalidOperationException(error);

    static string Refusal(string? text) =>
        WhenParser.TryParse(text, Clock(), out _, out var error) ? "" : error;

    [Test]
    public async Task An_instant_resolves_to_itself_in_utc() {
        var expected = new DateTimeOffset(2026, 9, 27, 9, 0, 0, TimeSpan.Zero);

        await Assert.That(Parse("2026-09-27T09:00:00Z")).IsEqualTo(expected);
        await Assert.That(Parse("2026-09-27T11:00:00+02:00")).IsEqualTo(expected);
        await Assert.That(Parse("2026-09-27T11:00:00+02:00").Offset).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task An_instant_without_a_zone_is_refused() {
        await Assert.That(Refusal("2026-09-27T09:00:00")).Contains("Z or an offset");
    }

    [Test]
    public async Task A_date_is_the_start_of_that_day_where_the_command_runs() {
        await Assert.That(Parse("2026-09-27", Amsterdam)).IsEqualTo(new DateTimeOffset(2026, 9, 26, 22, 0, 0, TimeSpan.Zero));
        await Assert.That(Parse("2026-09-27")).IsEqualTo(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
    }

    /// <summary>The day the clocks go forward is 23 hours long: it starts at the winter offset and
    /// the next day starts at the summer one.</summary>
    [Test]
    public async Task A_date_on_the_day_the_clocks_change_uses_the_offset_in_force_at_midnight() {
        var changeDay = Parse("2026-03-29", Amsterdam);
        var nextDay   = Parse("2026-03-30", Amsterdam);

        await Assert.That(changeDay).IsEqualTo(new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero));
        await Assert.That(nextDay).IsEqualTo(new DateTimeOffset(2026, 3, 29, 22, 0, 0, TimeSpan.Zero));
        await Assert.That(nextDay - changeDay).IsEqualTo(TimeSpan.FromHours(23));
    }

    [Test]
    public async Task A_date_whose_midnight_the_clocks_skip_starts_at_the_jump() {
        await Assert.That(Parse("2026-11-01", MidnightJump)).IsEqualTo(new DateTimeOffset(2026, 11, 1, 3, 0, 0, TimeSpan.Zero));
    }

    [Test]
    [Arguments("36h", 36)]
    [Arguments("14d", 336)]
    [Arguments("2w", 336)]
    [Arguments("2W", 336)]
    public async Task A_duration_counts_back_from_now(string text, int hours) {
        await Assert.That(Parse(text)).IsEqualTo(Now.AddHours(-hours));
    }

    [Test]
    [Arguments("0d", "at least 1")]
    [Arguments("99999w", "ten years")]
    [Arguments("14m", "h, d or w")]
    [Arguments("9999999h", "h, d or w")]
    [Arguments("yesterday", "is not a time")]
    [Arguments("", "is required")]
    [Arguments("   ", "is required")]
    public async Task What_cannot_be_read_is_refused_naming_the_fix(string text, string expected) {
        await Assert.That(Refusal(text)).Contains(expected);
    }

    [Test]
    public async Task A_missing_value_is_refused() {
        await Assert.That(Refusal(null)).Contains("is required");
    }

    [Test]
    public async Task The_wire_form_is_utc_with_z_and_no_empty_fraction() {
        var whole = new DateTimeOffset(2026, 9, 27, 11, 0, 0, TimeSpan.FromHours(2));

        await Assert.That(WhenParser.Format(whole)).IsEqualTo("2026-09-27T09:00:00Z");
        await Assert.That(WhenParser.Format(whole.AddMilliseconds(500))).IsEqualTo("2026-09-27T09:00:00.5Z");
    }
}
