using System.Globalization;

namespace Capacitor.App.ViewModels;

public static class UsageText {
    public static string WindowLabel(int minutes) =>
        minutes <= 0              ? "—"
        : minutes == 300          ? "5h"
        : minutes == 10080        ? "Week"
        : minutes % 1440 == 0     ? $"{minutes / 1440}d"
        : minutes % 60 == 0       ? $"{minutes / 60}h"
        : $"{minutes}m";

    /// A countdown under 24 hours, otherwise the local day and time. Minutes floor.
    public static string Reset(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone) {
        var left = at - now;
        if (left < TimeSpan.FromMinutes(1)) return "resets in <1m";
        if (left < TimeSpan.FromHours(1))   return $"resets in {(int)left.TotalMinutes}m";
        if (left < TimeSpan.FromDays(1))    return $"resets in {(int)left.TotalHours}h {left.Minutes:00}m";

        return "resets " + TimeZoneInfo.ConvertTime(at, zone).ToString("ddd HH:mm", CultureInfo.InvariantCulture);
    }
}
