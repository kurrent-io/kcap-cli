using System.Globalization;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core;

/// <summary>Resolves a point in time as a person or an agent writes it into a UTC instant. The
/// clock and the zone come from the caller's <see cref="TimeProvider"/>, so a date means the start
/// of that day on the machine the command runs on.</summary>
public static partial class WhenParser {
    public const string Forms =
        "an instant ending in Z or an offset (2026-09-27T09:00:00Z), a date (2026-09-27) or a duration back from now (36h, 14d, 2w)";

    // Far longer than any recorded session is old, and well inside what subtracting from now can hold.
    static readonly TimeSpan LongestDuration = TimeSpan.FromDays(3650);

    public static bool TryParse(string? text, TimeProvider time, out DateTimeOffset instant, out string error) {
        instant = default;
        error   = "";

        var value = text?.Trim() ?? "";

        if (value.Length == 0) {
            error = $"a time is required: {Forms}";

            return false;
        }

        if (Duration().Match(value) is { Success: true } duration) return TryDuration(duration, time, out instant, out error);

        if (CountWithUnit().IsMatch(value)) {
            error = $"'{value}' is not a duration: use a count of up to six digits followed by h, d or w";

            return false;
        }

        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) {
            instant = StartOfDay(date, time.LocalTimeZone);

            return true;
        }

        if (!value.Contains('T')) {
            error = $"'{value}' is not a time: use {Forms}";

            return false;
        }

        if (!HasDesignator(value)) {
            error = $"'{value}' names no time zone: end it with Z or an offset such as +02:00";

            return false;
        }

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) {
            error = $"'{value}' is not a time: use {Forms}";

            return false;
        }

        instant = parsed.ToUniversalTime();

        return true;
    }

    public static string Format(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    static bool TryDuration(Match duration, TimeProvider time, out DateTimeOffset instant, out string error) {
        instant = default;
        error   = "";

        var count = int.Parse(duration.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
        var unit  = char.ToLowerInvariant(duration.Groups[2].ValueSpan[0]);

        if (count == 0) {
            error = $"'{duration.Value}' is no time ago: use at least 1{unit}";

            return false;
        }

        var span = unit switch {
            'h' => TimeSpan.FromHours(count),
            'd' => TimeSpan.FromDays(count),
            _   => TimeSpan.FromDays(7 * count)
        };

        if (span > LongestDuration) {
            error = $"'{duration.Value}' is more than ten years back: pass a date instead";

            return false;
        }

        instant = time.GetUtcNow() - span;

        return true;
    }

    static DateTimeOffset StartOfDay(DateOnly date, TimeZoneInfo zone) {
        var midnight = date.ToDateTime(TimeOnly.MinValue);

        // Where the clocks go forward at midnight the day begins at the jump; where they go back
        // across it the day begins at the earlier of the two midnights.
        if (zone.IsInvalidTime(midnight))
            return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight.AddHours(-3))).ToUniversalTime();

        if (zone.IsAmbiguousTime(midnight))
            return new DateTimeOffset(midnight, zone.GetAmbiguousTimeOffsets(midnight).Max()).ToUniversalTime();

        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight)).ToUniversalTime();
    }

    static bool HasDesignator(string value) {
        var clock = value.AsSpan(value.IndexOf('T') + 1);

        return clock.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || clock.IndexOfAny('+', '-') >= 0;
    }

    [GeneratedRegex("^([0-9]{1,6})([hdw])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Duration();

    [GeneratedRegex("^[0-9]+[A-Za-z]+$")]
    private static partial Regex CountWithUnit();
}
