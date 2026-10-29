using System.Globalization;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.App.ViewModels;

/// The SESSION section's server-derived facts in words, matching the web's session figures.
public static class SessionFactText {
    public const string Unknown = "—";

    public static string Tokens(SessionStatsDto? stats) =>
        stats is { TokenUsageByModel.Count: > 0 } ? Count(stats.TokenUsageByModel.Sum(t => t.Total)) : Unknown;

    public static string Cost(SessionStatsDto? stats) =>
        stats?.EstimatedCostUsd is { } usd ? "≈ $" + usd.ToString("0.00", CultureInfo.InvariantCulture) : Unknown;

    /// "142k / 200k (71%)", or as much of it as is known.
    public static string Context(SessionStatsDto? stats) {
        if (stats is null) return Unknown;
        var peak = stats.PeakContextTokens;
        // An older server sends no resolved window; a stored zero means none was reported.
        var window = stats.ContextWindow ?? (stats.PeakContextWindow is > 0 ? stats.PeakContextWindow : null);
        var pct = stats.ContextUsagePercent;
        if (peak > 0 && window is > 0)
            return $"{Count(peak)} / {Count(window.Value)} ({Math.Round(pct ?? 100.0 * peak / window.Value)}%)";
        if (pct is { } p) return $"{Math.Round(p)}% ctx";
        if (peak > 0) return $"{Count(peak)} ctx";
        return Unknown;
    }

    /// Who sees the session, and how much of it when anyone else does.
    public static string SessionSharing(SessionSummaryDto summary) {
        var audience = summary.Visibility ?? DefaultAudience(summary.DefaultVisibility);
        if (audience == "none") return Audience(audience);
        return Level(summary.DetailLevel) is { } level ? $"{Audience(audience)} · {level}" : Audience(audience);
    }

    public static string AgentSharing(string? visibility) => visibility is null ? Unknown : Audience(visibility);

    internal static string Count(long value) =>
        value >= 1_000_000 ? (value / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M"
        : value >= 1000    ? (value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k"
        : value.ToString(CultureInfo.InvariantCulture);

    // Must match the server's reading of a session with no audience override.
    static string DefaultAudience(string? defaultVisibility) => defaultVisibility switch {
        "private" or "prestart"          => "none",
        "public" or "org_public" or null => "org",
        "project"                        => "project",
        _                                => "none",
    };

    static string Audience(string visibility) => visibility switch {
        "none"    => "Private",
        "org"     => "Org members",
        "scoped"  => "People & teams",
        "project" => "Project members",
        "public"  => "Public link",
        _         => visibility,
    };

    static string? Level(string? detailLevel) => detailLevel switch {
        "overview" => "Overview",
        "activity" => "Activity",
        "full"     => "Full",
        _          => null,
    };
}
