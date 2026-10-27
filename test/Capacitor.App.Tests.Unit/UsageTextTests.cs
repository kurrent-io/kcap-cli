using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class UsageTextTests {
    static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Test]
    [Arguments(300, "5h")]
    [Arguments(10080, "Week")]
    [Arguments(2880, "2d")]
    [Arguments(120, "2h")]
    [Arguments(90, "90m")]
    [Arguments(0, "—")]
    [Arguments(-5, "—")]
    public async Task Window_label(int minutes, string expected) =>
        await Assert.That(UsageText.WindowLabel(minutes)).IsEqualTo(expected);

    [Test]
    public async Task Reset_under_a_minute() =>
        await Assert.That(UsageText.Reset(Now.AddSeconds(30), Now, TimeZoneInfo.Utc)).IsEqualTo("resets in <1m");

    [Test]
    public async Task Reset_under_an_hour() =>
        await Assert.That(UsageText.Reset(Now.AddMinutes(12), Now, TimeZoneInfo.Utc)).IsEqualTo("resets in 12m");

    [Test]
    public async Task Reset_pads_minutes_to_two_digits() =>
        await Assert.That(UsageText.Reset(Now.AddHours(2).AddMinutes(5), Now, TimeZoneInfo.Utc)).IsEqualTo("resets in 2h 05m");

    [Test]
    public async Task Reset_floors_just_under_a_day() =>
        await Assert.That(UsageText.Reset(Now.AddHours(23).AddMinutes(59).AddSeconds(59), Now, TimeZoneInfo.Utc)).IsEqualTo("resets in 23h 59m");

    [Test]
    public async Task Reset_from_a_day_is_the_local_weekday_and_time() =>
        await Assert.That(UsageText.Reset(new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero), Now, TimeZoneInfo.Utc))
            .IsEqualTo("resets Thu 09:00");
}
