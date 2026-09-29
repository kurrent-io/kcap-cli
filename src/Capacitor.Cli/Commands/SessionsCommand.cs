using System.Net;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Commands;

class SessionsCommand(
        ConfigRoot config, ProfileContext profiles, ICapacitorHttpClient http, GitProviderRouter router,
        WorkingDirectory workdir, TimeProvider time) {
    internal const string TimeFilterUnsupported = "The time filter needs a newer server; ask your admin to update.";

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
                await Console.Error.WriteLineAsync("Not in a git repository with a remote origin.");

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

        var page = JsonSerializer.Deserialize(body, CapacitorJsonContext.Default.RepoSessionsResponse);

        if (page is null) {
            await Console.Error.WriteLineAsync("Unexpected response from the server.");

            return 1;
        }

        if (IgnoredWindow(options, page)) {
            await Console.Error.WriteLineAsync(TimeFilterUnsupported);

            return 1;
        }

        await Console.Out.WriteAsync(options.Json ? body + Environment.NewLine : Render(page, label, options));

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

    internal static string Render(RepoSessionsResponse page, string repoLabel, SessionsOptions options) {
        var sb = new StringBuilder();

        if (page.Items.Count == 0) {
            sb.AppendLine(Empty(repoLabel, options));
        } else {
            var repoHead    = options.AllRepos ? $"{"REPO",-25} " : "";
            var startedHead = options.Windowed ? $"{"STARTED",-17} " : "";

            sb.AppendLine(
                $"{"SESSION",-33} {"STATUS",-7} {"ACCESS",-9} {"OWNER",-14} {"VENDOR",-8} {repoHead}{"BRANCH",-24} {startedHead}{"LAST ACTIVITY",-17} TITLE");

            foreach (var row in page.Items) {
                var status  = row.Status == "active" && row.Stale ? "stale" : row.Status;
                var owner   = row.Owner?.Username ?? row.Owner?.UserId ?? "";
                var repo    = options.AllRepos ? $"{Fit(Repository(row.Repo), 24),-25} " : "";
                var started = options.Windowed ? $"{Local(row.StartedAt),-17} " : "";

                sb.AppendLine(
                    $"{Fit(row.SessionId, 32),-33} {status,-7} {row.AccessLevel,-9} {Fit(owner, 13),-14} {Fit(row.Vendor ?? "", 7),-8} {repo}{Fit(row.Branch ?? "", 23),-24} {started}{Local(row.LastActivityAt),-17} {row.Title ?? "(untitled)"}");
            }

            if (page.Total > page.Items.Count)
                sb.AppendLine(
                    options.Windowed
                        ? $"Showing {page.Items.Count} of {page.Total} in this period."
                        : $"Showing {page.Items.Count} of {page.Total}; raise --limit or narrow with --mine / --touching.");
        }

        if (page.NextCursor is { } next) sb.AppendLine($"More: {NextPage(options, next)}");

        if (page.Items.Count > 0) {
            sb.AppendLine();
            sb.AppendLine("Details (full access): kcap recap --full <session-id>");
        }

        return sb.ToString();
    }

    static string Empty(string repoLabel, SessionsOptions options) {
        if (options.Cursor is not null) return "No further sessions.";

        var what = options.State == "all" ? "sessions" : $"{options.State} sessions";
        var when = options.Windowed ? " in that period" : "";

        return $"No {what} visible to you on {repoLabel}{when}.";
    }

    static string NextPage(SessionsOptions options, string cursor) {
        var repo = options.Repo is null ? "" : $" --repo {options.Repo}";

        return $"kcap sessions{repo} --cursor {cursor} --limit {options.Limit}";
    }

    static string Repository(RepoSessionRepositoryDto? repo) =>
        repo switch {
            null                                 => "",
            { Owner: { } owner, Name: { } name } => $"{owner}/{name}",
            _                                    => repo.Hash
        };

    static string Local(DateTimeOffset instant) => instant.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    static string Fit(string value, int width) => value.Length <= width ? value : value[..(width - 1)] + "…";
}
