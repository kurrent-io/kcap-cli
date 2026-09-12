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

        var json = await run("gh", $"pr view {branch} --repo {host}/{owner}/{repo} --json {Fields},isCrossRepository", cwd, cap);

        return Parse(json) is ({ } pr, var o)
            && pr.HeadRef == branch
            && o["isCrossRepository"] is JsonValue cross && cross.TryGetValue<bool>(out var isCross) && !isCross
            ? pr
            : null;
    }

    static (PrInfo Pr, JsonObject Json)? Parse(string? json) {
        if (json is null) return null;
        try {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            var number = o["number"]?.GetValue<int>();
            if (number is null) return null;
            return (new PrInfo(number.Value, o["title"]?.GetValue<string>(),
                               o["url"]?.GetValue<string>(), o["headRefName"]?.GetValue<string>()), o);
        } catch {
            return null; // best-effort
        }
    }
}
