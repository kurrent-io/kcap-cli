using System.Globalization;

namespace Capacitor.App.ViewModels;

public static class RelativeTime {
    /// "just now", then minutes, hours and days for a week, then the date. Never negative: a stamp
    /// ahead of the clock reads as now.
    public static string Format(DateTimeOffset at, DateTimeOffset now) {
        var age = now - at;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1))   return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1))    return $"{(int)age.TotalHours}h ago";
        if (age < TimeSpan.FromDays(7))    return $"{(int)age.TotalDays}d ago";
        return at.ToString(at.Year == now.Year ? "MMM d" : "MMM d, yyyy", CultureInfo.InvariantCulture);
    }

    /// Relative at every age, for a list read by comparing rows: as Format under a day, then days,
    /// weeks under two months, months under a year, then years. From a day on it counts calendar
    /// days in the viewer's zone, as the rail's day groups do: whole 24-hour spans would call Monday
    /// evening "2d ago" on Thursday morning, under a header that names Monday.
    public static string Ago(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone) {
        if (now - at < TimeSpan.FromDays(1)) return Format(at, now);
        var days = Math.Max(1, LocalDay(now, zone).DayNumber - LocalDay(at, zone).DayNumber);
        if (days < 7)   return $"{days}d ago";
        if (days < 60)  return $"{days / 7}w ago";
        if (days < 365) return $"{days / 30}mo ago";
        return $"{days / 365}y ago";
    }

    static DateOnly LocalDay(DateTimeOffset at, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
}
