using System.Globalization;

namespace Capacitor.Cli.Core.Harness.Muse;

/// <summary>Muse Code's layout: config under <c>$XDG_CONFIG_HOME/muse</c> (else <c>~/.config/muse</c>),
/// data under <c>$XDG_DATA_HOME/muse</c> (else <c>~/.local/share/muse</c>). Session logs are dated
/// by the session's creation day in local time, so a lookup must tolerate a day boundary.</summary>
public sealed class MusePaths {
    public MusePaths(UserHome home, string? xdgConfigHome, string? xdgDataHome) {
        ConfigRoot = Path.Combine(string.IsNullOrWhiteSpace(xdgConfigHome) ? Path.Combine(home.Path, ".config") : xdgConfigHome, "muse");
        DataRoot   = Path.Combine(string.IsNullOrWhiteSpace(xdgDataHome)   ? Path.Combine(home.Path, ".local", "share") : xdgDataHome, "muse");
    }

    public string ConfigRoot { get; }
    public string DataRoot   { get; }

    public string SettingsJson => Path.Combine(ConfigRoot, "settings.json");
    public string SkillsDir    => Path.Combine(ConfigRoot, "skills");
    public string SessionsDir  => Path.Combine(DataRoot, "sessions");

    /// <summary>Muse's cache of each provider's served-model catalog, one JSON file per provider profile.</summary>
    public string ModelCatalogDir => Path.Combine(DataRoot, "model-catalog");

    /// <summary>Null for an id that is not a single path segment, so nothing can climb out of
    /// <see cref="SessionsDir"/>.</summary>
    public string? SessionLogPath(string sessionId, DateOnly day) =>
        !IsSingleSegment(sessionId) ? null : Path.Combine(SessionsDir,
            day.Year.ToString("D4", CultureInfo.InvariantCulture),
            day.Month.ToString("D2", CultureInfo.InvariantCulture),
            day.Day.ToString("D2", CultureInfo.InvariantCulture),
            sessionId, "session.jsonl");

    public static string ChildSessionLogPath(string parentLogPath, string childSessionId) =>
        Path.Combine(Path.GetDirectoryName(parentLogPath)!, "subagent", childSessionId, "session.jsonl");

    /// <summary>Today, then the adjacent days, then one directory-level glob over every date; null
    /// when no log exists yet — a caller must not record a path Muse has not created.</summary>
    public string? ResolveSessionLog(string sessionId, DateOnly today) {
        if (!IsSingleSegment(sessionId)) return null;

        foreach (var day in new[] { today, today.AddDays(-1), today.AddDays(1) }) {
            var candidate = SessionLogPath(sessionId, day);
            if (candidate is not null && File.Exists(candidate)) return candidate;
        }

        if (!Directory.Exists(SessionsDir)) return null;

        try {
            foreach (var year in Directory.EnumerateDirectories(SessionsDir))
            foreach (var month in Directory.EnumerateDirectories(year))
            foreach (var day in Directory.EnumerateDirectories(month)) {
                var candidate = Path.Combine(day, sessionId, "session.jsonl");
                if (File.Exists(candidate)) return candidate;
            }
        } catch (IOException) { } catch (UnauthorizedAccessException) { }

        return null;
    }

    static bool IsSingleSegment(string id) =>
        !string.IsNullOrWhiteSpace(id) && !id.Contains("..", StringComparison.Ordinal)
     && id.IndexOfAny(['/', '\\']) < 0 && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    public bool HasUserData() => File.Exists(SettingsJson) || Directory.Exists(SessionsDir);
}
