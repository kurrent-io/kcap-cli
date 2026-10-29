using System.Text.Json.Nodes;

namespace Capacitor.Cli.PrDetection;

/// <summary>Spawns a CLI and returns trimmed stdout, or null on failure/timeout.</summary>
internal delegate Task<string?> CommandRunner(string cmd, string arguments, string cwd, TimeSpan timeout);

internal sealed record PrInfo(int Number, string? Title, string? Url, string? HeadRef);

/// <summary>GitHub / GitHub Enterprise detection via `gh` (auto-targets the remote's host).</summary>
internal static class GitHubPrDetector {
    const string Fields = "number,title,url,headRefName";

    public static async Task<PrInfo?> DetectAsync(string cwd, TimeSpan cap, CommandRunner run) =>
        Parse(await run("gh", $"pr view --json {Fields}", cwd, cap))?.Pr;

    /// <summary>
    /// The PR whose head is <paramref name="branch"/> in host/owner/repo itself: a fork's head of the
    /// same name, or a reply naming another head, is not this branch's PR.
    /// </summary>
    public static async Task<PrInfo?> DetectForBranchAsync(
            string host, string owner, string repo, string branch, string cwd, TimeSpan cap, CommandRunner run) {
        if (!CommandArgument.IsPlain(host) || !CommandArgument.IsPlain(owner)
         || !CommandArgument.IsPlain(repo) || !CommandArgument.IsPlain(branch)) {
            return null;
        }

        // gh reads an all-digit selector as a PR number, so a branch named "123" would look up PR
        // #123. The head check below would refuse that answer anyway; asking spends a round trip.
        if (branch.All(char.IsAsciiDigit)) return null;

        var json = await run("gh", $"pr view {branch} --repo {host}/{owner}/{repo} --json {Fields},isCrossRepository", cwd, cap);

        return Parse(json) is ({ } pr, var o)
            && pr.HeadRef == branch
            && o["isCrossRepository"] is JsonValue cross && cross.TryGetValue<bool>(out var isCross) && !isCross
            ? pr
            : null;
    }

    /// <summary>The PR at <paramref name="url"/>, from any directory; a reply for another number is not it.</summary>
    public static async Task<PrInfo?> DescribeAsync(string url, int number, string cwd, TimeSpan cap, CommandRunner run) {
        if (!CommandArgument.IsPlain(url)) return null;

        return Parse(await run("gh", $"pr view {url} --json {Fields}", cwd, cap))?.Pr is { } pr && pr.Number == number ? pr : null;
    }

    const int HistoryDepth = 100;

    /// <summary>
    /// The caller's open PR in host/owner/repo whose head is the nearest commit in HEAD's history:
    /// found by commit, it survives a branch pushed under another name with no upstream, and a
    /// local commit not yet pushed. Only open PRs qualify, and only commits off every remote's
    /// default branch: a merged PR's head, or an open PR from the default branch into another, sits
    /// in the history of every checkout cut from it. A nearest commit heading two PRs links neither.
    /// </summary>
    public static async Task<PrInfo?> DetectForHistoryAsync(
            string host, string owner, string repo, string cwd, Func<TimeSpan> remaining, CommandRunner run) {
        if (!CommandArgument.IsPlain(host) || !CommandArgument.IsPlain(owner) || !CommandArgument.IsPlain(repo)) return null;

        var cap = remaining();
        if (cap <= TimeSpan.Zero) return null;

        var historyTask = run("git", $"rev-list -n {HistoryDepth} HEAD --not --remotes=*/HEAD", cwd, cap);
        var prsTask = run("gh",
            $"pr list --repo {host}/{owner}/{repo} --author @me --state open --limit 100 --json {Fields},headRefOid,isCrossRepository",
            cwd, cap);
        await Task.WhenAll(historyTask, prsTask);

        if (historyTask.Result is not { } history || prsTask.Result is not { } prs) return null;

        // A null value marks a head shared by several PRs.
        var heads = new Dictionary<string, PrInfo?>(StringComparer.OrdinalIgnoreCase);
        try {
            if (JsonNode.Parse(prs) is not JsonArray rows) return null;

            foreach (var row in rows) {
                if (row is not JsonObject o
                 || o["isCrossRepository"] is not JsonValue cross || !cross.TryGetValue<bool>(out var isCross) || isCross
                 || o["headRefOid"]?.GetValue<string>() is not { Length: > 0 } oid
                 || ParseObject(o) is not { } pr) {
                    continue;
                }

                heads[oid] = heads.ContainsKey(oid) ? null : pr;
            }
        } catch {
            return null;
        }

        foreach (var line in history.Split('\n')) {
            if (heads.TryGetValue(line.Trim(), out var pr)) return pr;
        }

        return null;
    }

    static (PrInfo Pr, JsonObject Json)? Parse(string? json) {
        if (json is null) return null;
        try {
            return JsonNode.Parse(json) is JsonObject o && ParseObject(o) is { } pr ? (pr, o) : null;
        } catch {
            return null; // best-effort
        }
    }

    // Throws on a mistyped field; callers treat that as no PR.
    static PrInfo? ParseObject(JsonObject o) =>
        o["number"]?.GetValue<int>() is { } number
            ? new PrInfo(number, o["title"]?.GetValue<string>(), o["url"]?.GetValue<string>(), o["headRefName"]?.GetValue<string>())
            : null;
}
