using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

internal sealed record SessionsOptions(
        string          State,
        string?         Repo,
        string?         RepoHash,
        bool            Mine,
        string?         Touching,
        int             Limit,
        bool            Json,
        bool            AllRepos = false,
        DateTimeOffset? Since    = null,
        DateTimeOffset? Until    = null,
        string?         Cursor   = null
    ) {
    public bool Windowed => Since is not null || Until is not null || Cursor is not null;
}

/// <summary>Parses <c>kcap sessions</c> flags. The generic top-level flag helper returns the first
/// value and validates nothing, so exclusivity, value presence and shapes are checked here.</summary>
internal static class SessionsArgs {
    public const string Usage =
        "Usage: kcap sessions [--active | --ended | --all] [--repo <owner/name|hash|all>] [--mine] [--touching <path>] [--since <when>] [--until <when>] [--cursor <value>] [--limit <n>] [--json]";

    public static SessionsOptions? Parse(string[] args, TimeProvider time, out string? error) {
        error = null;

        string?         state    = null;
        string?         repo     = null;
        string?         repoHash = null;
        var             allRepos = false;
        var             mine     = false;
        string?         touching = null;
        DateTimeOffset? since    = null;
        DateTimeOffset? until    = null;
        string?         cursor   = null;
        var             limit    = 20;
        var             json     = false;

        for (var i = 1; i < args.Length; i++) {
            switch (args[i]) {
                case "--active" or "--ended" or "--all":
                    if (state is not null) {
                        error = $"choose one of --active, --ended or --all (got {state} and {args[i]})";

                        return null;
                    }

                    state = args[i];

                    break;
                case "--mine": mine = true; break;
                case "--json": json = true; break;
                case "--repo":
                    if (!TryValue(args, ref i, out repo)) { error = "--repo needs a value"; return null; }

                    if (repo == "all") {
                        allRepos = true;
                        repoHash = null;

                        break;
                    }

                    if (!RepoHashHelper.TryParseRepoRef(repo, out var hash)) {
                        error = "--repo must be <owner>/<name>, a 16-hex repo hash, or all";

                        return null;
                    }

                    allRepos = false;
                    repoHash = hash;

                    break;
                case "--touching":
                    if (!TryValue(args, ref i, out touching)) { error = "--touching needs a value"; return null; }

                    break;
                case "--since":
                    if (!TryWhen(args, ref i, time, out since, out error)) return null;

                    break;
                case "--until":
                    if (!TryWhen(args, ref i, time, out until, out error)) return null;

                    break;
                case "--cursor":
                    if (!TryValue(args, ref i, out var page)) { error = "--cursor needs a value"; return null; }

                    cursor = page;

                    break;
                case "--limit":
                    if (!TryValue(args, ref i, out var raw) || !int.TryParse(raw, out limit) || limit is < 1 or > 100) {
                        error = "--limit must be a number from 1 to 100";

                        return null;
                    }

                    break;
                default:
                    error = $"unknown flag {args[i]}";

                    return null;
            }
        }

        if (cursor is not null && Replaced(state, mine, touching, since, until) is { Count: > 0 } replaced) {
            error = $"--cursor continues the listing it came from, filters included; drop {string.Join(", ", replaced)}";

            return null;
        }

        if (since > until) {
            error = "--since is later than --until";

            return null;
        }

        var resolved = state?.TrimStart('-') ?? (since is not null || until is not null ? "all" : "active");

        return new(resolved, repo, repoHash, mine, touching, limit, json, allRepos, since, until, cursor);
    }

    static List<string> Replaced(string? state, bool mine, string? touching, DateTimeOffset? since, DateTimeOffset? until) {
        var flags = new List<string>();

        if (state is not null)    flags.Add(state);
        if (mine)                 flags.Add("--mine");
        if (touching is not null) flags.Add("--touching");
        if (since is not null)    flags.Add("--since");
        if (until is not null)    flags.Add("--until");

        return flags;
    }

    static bool TryWhen(string[] args, ref int i, TimeProvider time, out DateTimeOffset? instant, out string? error) {
        var flag = args[i];

        instant = null;
        error   = null;

        if (!TryValue(args, ref i, out var text)) {
            error = $"{flag} needs a value";

            return false;
        }

        if (!WhenParser.TryParse(text, time, out var parsed, out var reason)) {
            error = $"{flag}: {reason}";

            return false;
        }

        instant = parsed;

        return true;
    }

    static bool TryValue(string[] args, ref int i, out string value) {
        value = "";

        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) return false;

        value = args[++i];

        return true;
    }
}
