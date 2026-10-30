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

    /// Relative at every age, for a list read by comparing rows: as Format for a week, then weeks
    /// under two months, months under a year, then years.
    public static string Ago(DateTimeOffset at, DateTimeOffset now) {
        var age = now - at;
        if (age < TimeSpan.FromDays(7))   return Format(at, now);
        if (age < TimeSpan.FromDays(60))  return $"{(int)(age.TotalDays / 7)}w ago";
        if (age < TimeSpan.FromDays(365)) return $"{(int)(age.TotalDays / 30)}mo ago";
        return $"{(int)(age.TotalDays / 365)}y ago";
    }
}
