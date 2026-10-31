using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Commands;

partial class SessionsCommand(
        ConfigRoot config, ProfileContext profiles, ICapacitorHttpClient http, GitProviderRouter router,
        WorkingDirectory workdir, TimeProvider time) {
    internal const string TimeFilterUnsupported = "The time filter needs a newer server; ask your admin to update.";

    internal const string NoRepository =
        "Not in a git repository with a remote origin. Pass --repo <owner/name>, or --repo all for every repository.";

    public async Task<int> HandleAsync(string[] args) {
        var options = SessionsArgs.Parse(args, time, out var error);

        if (options is null) {
            await Console.Error.WriteLineAsync($"kcap sessions: {error}");
            await Console.Error.WriteLineAsync(SessionsArgs.Usage);

            return 1;
        }

        string? repoHash;
        string  label;

        if (options.AllRepos) {
            repoHash = null;
            label    = "any repository";
        } else if (options.Repo is null) {
            var repo = await RepositoryDetection.DetectRepositoryAsync(
                router,
                config, workdir.Path, time, detectPullRequest: false);

            if (repo?.Owner is null || repo.RepoName is null) {
                await Console.Error.WriteLineAsync(NoRepository);

                return 1;
            }

            repoHash = RepoHashHelper.ComputeRepoHash(repo.Owner, repo.RepoName);
            label    = $"{repo.Owner}/{repo.RepoName}";
        } else {
            repoHash = options.RepoHash!;
            label    = options.Repo;
        }

        var       baseUrl    = profiles.Resolution.ServerUrl!;
        using var httpClient = await http.ForCommandAsync();

        HttpResponseMessage resp;

        try {
            resp = await httpClient.GetWithRetryAsync(BuildUrl(baseUrl, repoHash, options), time);
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl, ex);

            return 1;
        }

        if (await HttpClientExtensions.HandleUnauthorizedAsync(resp)) return 1;

        if (resp.StatusCode == HttpStatusCode.NotFound) {
            await Console.Error.WriteLineAsync("Session listing needs a newer server; ask your admin to update.");

            return 1;
        }

        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode) {
            await Console.Error.WriteLineAsync($"HTTP {(int)resp.StatusCode}: {body}");

            return 1;
        }

        RepoSessionsResponse? page;

        try {
            page = JsonSerializer.Deserialize(body, CapacitorJsonContext.Default.RepoSessionsResponse);
        } catch (JsonException) {
            page = null;
        }

        if (page?.Items is null) {
            await Console.Error.WriteLineAsync("Unexpected response from the server.");

            return 1;
        }

        if (IgnoredWindow(options, page)) {
            await Console.Error.WriteLineAsync(TimeFilterUnsupported);

            return 1;
        }

        await Console.Out.WriteAsync(options.Json
            ? body + Environment.NewLine
            : Render(page, label, options, StatusGlyphs.ConsoleEncodable, TerminalWidth()));

        return 0;
    }

    /// <summary>A server that does not know the window ignores it and answers with an unfiltered
    /// list, which carries no echo. Rendering that would show a result that looks filtered.</summary>
    internal static bool IgnoredWindow(SessionsOptions options, RepoSessionsResponse page) =>
        options.Windowed && page.Since is null && page.Until is null;

    internal static string BuildUrl(string baseUrl, string? repoHash, SessionsOptions options) {
        var route = repoHash is null
            ? $"{baseUrl}/api/sessions/listing"
            : $"{baseUrl}/api/repositories/{repoHash}/sessions";

        if (options.Cursor is { } cursor) return $"{route}?cursor={Uri.EscapeDataString(cursor)}&limit={options.Limit}";

        var qs = new List<string> { $"state={options.State}", $"limit={options.Limit}" };

        if (options.Mine) qs.Add("owner=me");

        if (options.Touching is { Length: > 0 } touching) qs.Add($"touching_path={Uri.EscapeDataString(touching)}");

        if (options.Since is { } since) qs.Add($"since={Uri.EscapeDataString(WhenParser.Format(since))}");

        if (options.Until is { } until) qs.Add($"until={Uri.EscapeDataString(WhenParser.Format(until))}");

        return $"{route}?" + string.Join("&", qs);
    }

    /// <param name="encodable">Whether the output carries an ellipsis; a cut cell ends in "..." when it
    /// does not, never in a look-alike the encoder picked.</param>
    /// <param name="width">The terminal's width, which the title is cut to fit; null leaves every title
    /// whole, as for redirected output.</param>
    internal static string Render(
            RepoSessionsResponse page, string repoLabel, SessionsOptions options, bool encodable = true, int? width = null) {
        var sb = new StringBuilder();

        if (page.Items.Count == 0) {
            sb.AppendLine(Empty(repoLabel, options));
        } else {
            var rows = page.Items.Select(string?[] (row) => [
                    row.SessionId,
                    row.Status == "active" && row.Stale ? "stale" : row.Status,
                    row.AccessLevel,
                    row.Owner?.Username ?? row.Owner?.UserId ?? "",
                    row.Vendor ?? "",
                    options.AllRepos ? Repository(row.Repo) : null,
                    row.Branch ?? "",
                    options.Windowed ? Local(row.StartedAt) : null,
                    Local(row.LastActivityAt),
                ])
                .ToList();

            string?[] heads = ["SESSION", "STATUS", "ACCESS", "OWNER", "VENDOR", options.AllRepos ? "REPO" : null, "BRANCH",
                options.Windowed ? "STARTED" : null, "LAST ACTIVITY"];
            int[] caps = [36, 7, 9, 14, 8, 24, 24, 16, 16];

            var widths = heads.Select((head, i) => head is null
                    ? 0
                    : Math.Min(Math.Max(caps[i], head.Length), Math.Max(head.Length, rows.Max(r => r[i]!.Length))))
                .ToArray();

            Narrow(widths, heads, width);

            string Lead(IReadOnlyList<string?> cells) {
                var line = new StringBuilder();

                for (var i = 0; i < cells.Count; i++) {
                    if (heads[i] is null) continue;

                    line.Append(StatusGlyphs.Fit(cells[i]!, widths[i], encodable).PadRight(widths[i])).Append(' ');
                }

                return line.ToString();
            }

            var lead = Lead(heads).Length;

            sb.AppendLine(Lead(heads) + "TITLE");

            for (var i = 0; i < rows.Count; i++)
                sb.AppendLine(Lead(rows[i]) + Title(page.Items[i].Title ?? "(untitled)", lead, encodable, width));

            if (page.Total > page.Items.Count)
                sb.AppendLine(
                    options.Windowed
                        ? $"Showing {page.Items.Count} of {page.Total} in this period."
                        : $"Showing {page.Items.Count} of {page.Total}; raise --limit or narrow with --mine / --touching.");
        }

        if (page.Items.Count > 0) {
            sb.AppendLine();
            sb.AppendLine("ACCESS is what you may read of a session: overview, activity or full.");
            sb.AppendLine("Details (full access): kcap recap --full <session-id>");
        }

        if (page.NextCursor is { } next) sb.AppendLine(NextPage(options, repoLabel, next));

        return sb.ToString();
    }

    const int FewestTitleColumns = 12;

    /// <summary>Takes columns from the owner, repository and branch, widest first, until a title fits
    /// beside them on the terminal. The other cells are ids and times, which a cut would make useless.</summary>
    static void Narrow(int[] widths, string?[] heads, int? width) {
        if (width is not { } columns) return;

        int[] narrowable = [Array.IndexOf(heads, "OWNER"), Array.IndexOf(heads, "REPO"), Array.IndexOf(heads, "BRANCH")];
        const int fewest = 10;

        var excess = widths.Sum() + heads.Count(h => h is not null) + FewestTitleColumns - (columns - 1);

        while (excess > 0) {
            var widest = narrowable.Where(i => i >= 0 && widths[i] > fewest).OrderByDescending(i => widths[i]).FirstOrDefault(-1);

            if (widest < 0) return;

            widths[widest]--;
            excess--;
        }
    }

    /// <summary>The title cut to the room left on a terminal line, so a row never wraps. A terminal
    /// too narrow to show a useful part of it gets it whole.</summary>
    static string Title(string title, int lead, bool encodable, int? width) {
        if (width is not { } columns || columns - 1 - lead < FewestTitleColumns) return title;

        return StatusGlyphs.Fit(title, columns - 1 - lead, encodable);
    }

    static string Empty(string repoLabel, SessionsOptions options) {
        if (options.Cursor is not null) return "No sessions on this page.";

        var what = options.State == "all" ? "sessions" : $"{options.State} sessions";
        var when = options.Windowed ? " in that period" : "";

        return $"No {what} visible to you on {repoLabel}{when}.";
    }

    // The line is meant to be pasted anywhere: it names the repository even when this run took it from
    // the checkout, and it is printed only from values a shell can read as nothing but arguments.
    static string NextPage(SessionsOptions options, string repoLabel, string cursor) {
        var repo = options.Repo ?? repoLabel;

        return Token().IsMatch(repo) && Token().IsMatch(cursor)
            ? $"More: kcap sessions --repo {repo} --cursor {cursor} --limit {options.Limit}"
            : "More: rows follow; read next_cursor with --json and pass it as --cursor.";
    }

    [GeneratedRegex("^[A-Za-z0-9_./=-]+$")]
    private static partial Regex Token();

    static string Repository(RepoSessionRepositoryDto? repo) =>
        repo switch {
            null                                 => "",
            { Owner: { } owner, Name: { } name } => $"{owner}/{name}",
            _                                    => repo.Hash
        };

    static string Local(DateTimeOffset instant) => instant.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    static int? TerminalWidth() {
        if (Console.IsOutputRedirected) return null;

        try {
            return Console.WindowWidth > 0 ? Console.WindowWidth : null;
        } catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) {
            return null;
        }
    }
}
