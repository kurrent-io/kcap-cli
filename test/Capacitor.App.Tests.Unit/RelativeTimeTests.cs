using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class RelativeTimeTests {
    static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Test]
    [Arguments(0, "just now")]
    [Arguments(59, "just now")]
    [Arguments(60, "1m ago")]
    [Arguments(59 * 60, "59m ago")]
    [Arguments(60 * 60, "1h ago")]
    [Arguments(23 * 60 * 60, "23h ago")]
    [Arguments(24 * 60 * 60, "1d ago")]
    [Arguments(6 * 24 * 60 * 60, "6d ago")]
    public async Task Within_a_week_the_text_is_relative(int secondsAgo, string expected) {
        await Assert.That(RelativeTime.Format(Now.AddSeconds(-secondsAgo), Now)).IsEqualTo(expected);
    }

    [Test]
    public async Task A_week_or_more_shows_the_date() {
        await Assert.That(RelativeTime.Format(Now.AddDays(-7), Now)).IsEqualTo("Aug 29");
        await Assert.That(RelativeTime.Format(new DateTimeOffset(2025, 12, 24, 0, 0, 0, TimeSpan.Zero), Now)).IsEqualTo("Dec 24, 2025");
    }

    /// The rail compares rows by eye, so an old row stays relative instead of turning into a date.
    [Test]
    [Arguments(6, "6d ago")]
    [Arguments(7, "1w ago")]
    [Arguments(59, "8w ago")]
    [Arguments(60, "2mo ago")]
    [Arguments(364, "12mo ago")]
    [Arguments(365, "1y ago")]
    public async Task Ago_stays_relative_at_every_age(int daysAgo, string expected) {
        await Assert.That(RelativeTime.Ago(Now.AddDays(-daysAgo), Now, TimeZoneInfo.Utc)).IsEqualTo(expected);
    }

    /// 2d 16h after an evening stamp is three dates later, and the row agrees with the day header.
    [Test]
    public async Task Ago_counts_calendar_days_rather_than_whole_24_hour_spans() {
        var mondayEvening = new DateTimeOffset(2026, 9, 2, 20, 0, 0, TimeSpan.Zero);
        await Assert.That(RelativeTime.Ago(mondayEvening, Now, TimeZoneInfo.Utc)).IsEqualTo("3d ago");
    }

    /// The same two instants fall two dates apart ten hours east of UTC: the day is the viewer's.
    [Test]
    public async Task Ago_counts_days_in_the_viewers_zone() {
        var east = TimeZoneInfo.CreateCustomTimeZone("utc+10", TimeSpan.FromHours(10), "utc+10", "utc+10");
        var mondayEvening = new DateTimeOffset(2026, 9, 2, 20, 0, 0, TimeSpan.Zero);
        await Assert.That(RelativeTime.Ago(mondayEvening, Now, east)).IsEqualTo("2d ago");
    }

    [Test]
    public async Task Ago_under_a_day_reads_in_hours_even_across_midnight() {
        await Assert.That(RelativeTime.Ago(Now.AddHours(-13), Now, TimeZoneInfo.Utc)).IsEqualTo("13h ago");
    }

    [Test]
    public async Task A_future_stamp_reads_as_just_now() {
        await Assert.That(RelativeTime.Format(Now.AddMinutes(5), Now)).IsEqualTo("just now");
    }
}
