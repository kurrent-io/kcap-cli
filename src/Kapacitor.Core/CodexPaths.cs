namespace kapacitor;

static class CodexPaths {
    static readonly string Home = Path.Combine(PathHelpers.HomeDirectory, ".codex");

    public static string Sessions { get; } = Path.Combine(Home, "sessions");

    /// <summary>
    /// Walk <c>~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl</c>, optionally pruning
    /// directories below <paramref name="since"/>. Returns one entry per rollout
    /// with the session id parsed from the filename suffix
    /// (<c>rollout-&lt;ISO-ts&gt;-&lt;uuid&gt;.jsonl</c>) and a synthesised
    /// EncodedCwd matching Claude's <c>/</c>→<c>-</c> encoding so callers that
    /// share the Claude path's plumbing (<see cref="kapacitor.Commands.SessionImporter.DecodeCwdFromDirName"/>)
    /// keep working without a special case.
    /// </summary>
    /// <param name="sessionsDir">Override of the <c>~/.codex/sessions</c> root, primarily for tests.</param>
    /// <param name="since">Inclusive lower bound — files in date directories before this are skipped.</param>
    public static List<(string SessionId, string FilePath, string EncodedCwd)> Discover(string? sessionsDir = null, DateOnly? since = null) {
        var root    = sessionsDir ?? Sessions;
        var results = new List<(string, string, string)>();

        if (!Directory.Exists(root)) return results;

        foreach (var year in EnumerateNumericDirs(root, since?.Year)) {
            foreach (var month in EnumerateNumericDirs(year.Path, MonthBound(since, year.Value))) {
                foreach (var day in EnumerateNumericDirs(month.Path, DayBound(since, year.Value, month.Value))) {
                    foreach (var jsonl in Directory.GetFiles(day.Path, "rollout-*.jsonl")) {
                        var sid = ExtractSessionIdFromFileName(jsonl);

                        if (sid is null) continue;

                        // Codex rollouts have no project-hashed parent dir, so synthesise an EncodedCwd
                        // from the day path. The session_meta line is the source of truth for cwd —
                        // this fallback only matters if metadata extraction fails.
                        var encoded = Path.GetFileName(day.Path);
                        results.Add((sid, jsonl, encoded));
                    }
                }
            }
        }

        return results;
    }

    static IEnumerable<(string Path, int Value)> EnumerateNumericDirs(string parent, int? minInclusive) {
        if (!Directory.Exists(parent)) yield break;

        foreach (var dir in Directory.GetDirectories(parent)) {
            var name = Path.GetFileName(dir);

            if (!int.TryParse(name, out var value)) continue;
            if (minInclusive is { } min && value < min) continue;

            yield return (dir, value);
        }
    }

    static int? MonthBound(DateOnly? since, int year) =>
        since is { } s && s.Year == year ? s.Month : null;

    static int? DayBound(DateOnly? since, int year, int month) =>
        since is { } s && s.Year == year && s.Month == month ? s.Day : null;

    /// <summary>
    /// Parse the trailing UUID from <c>rollout-&lt;ISO-ts&gt;-&lt;uuid&gt;.jsonl</c>.
    /// The UUID is normalized to dashless form so it round-trips with the rest of
    /// the CLI's NormalizeGuidField behaviour.
    /// </summary>
    public static string? ExtractSessionIdFromFileName(string filePath) {
        var name = Path.GetFileNameWithoutExtension(filePath);

        // rollout-2026-05-07T17-50-21-019e0322-05fc-7570-be65-75719c3ea861
        // Last 5 dash-separated chunks form the UUID.
        var parts = name.Split('-');

        if (parts.Length < 5) return null;

        var uuid = string.Join('-', parts[^5..]);

        return Guid.TryParse(uuid, out var guid) ? guid.ToString("N") : null;
    }
}
