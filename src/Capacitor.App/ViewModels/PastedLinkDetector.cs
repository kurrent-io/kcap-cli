using System.Text.RegularExpressions;
using Capacitor.Cli.Core;

namespace Capacitor.App.ViewModels;

public static partial class PastedLinkDetector {
    public static PastedLinkMatch? TryDetect(string? text) {
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (GitHubPrRegex().Match(text) is { Success: true } pr) {
            var owner = pr.Groups["owner"].Value;
            var repo  = StripGitSuffix(pr.Groups["repo"].Value);
            var num   = int.TryParse(pr.Groups["number"].Value, out var n) ? n : (int?)null;
            return new PastedLinkMatch(
                PastedLinkKind.GitHubPullRequest,
                pr.Value,
                Owner: owner,
                Repo: repo,
                RepoHash: RepoHashHelper.ComputeRepoHash(owner, repo),
                Number: num);
        }

        if (GitHubIssueRegex().Match(text) is { Success: true } issue) {
            var owner = issue.Groups["owner"].Value;
            var repo  = StripGitSuffix(issue.Groups["repo"].Value);
            var num   = int.TryParse(issue.Groups["number"].Value, out var n) ? n : (int?)null;
            return new PastedLinkMatch(
                PastedLinkKind.GitHubIssue,
                issue.Value,
                Owner: owner,
                Repo: repo,
                RepoHash: RepoHashHelper.ComputeRepoHash(owner, repo),
                Number: num);
        }

        if (GitHubActionsRegex().Match(text) is { Success: true } run) {
            var owner = run.Groups["owner"].Value;
            var repo  = StripGitSuffix(run.Groups["repo"].Value);
            var id    = long.TryParse(run.Groups["runId"].Value, out var r) ? r : (long?)null;
            return new PastedLinkMatch(
                PastedLinkKind.GitHubActionsRun,
                run.Value,
                Owner: owner,
                Repo: repo,
                RepoHash: RepoHashHelper.ComputeRepoHash(owner, repo),
                RunId: id);
        }

        if (LinearIssueRegex().Match(text) is { Success: true } linear) {
            var workspace = linear.Groups["workspace"].Value;
            var key       = linear.Groups["key"].Value.ToUpperInvariant();
            var slug      = linear.Groups["slug"].Success ? linear.Groups["slug"].Value : null;
            return new PastedLinkMatch(
                PastedLinkKind.LinearIssue,
                linear.Value,
                IssueKey: key,
                Workspace: workspace,
                Slug: slug);
        }

        if (GitHubRepoUrlRegex().Match(text) is { Success: true } ghRepo) {
            var owner = ghRepo.Groups["owner"].Value;
            var repo  = StripGitSuffix(ghRepo.Groups["repo"].Value);
            return new PastedLinkMatch(
                PastedLinkKind.GitHubRepository,
                ghRepo.Value,
                Owner: owner,
                Repo: repo,
                RepoHash: RepoHashHelper.ComputeRepoHash(owner, repo));
        }

        return null;
    }

    static string StripGitSuffix(string repo) =>
        repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? repo[..^4] : repo;

    [GeneratedRegex(@"https?://github\.com/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)/pull/(?<number>\d+)(?:[/#?][^\s]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubPrRegex();

    [GeneratedRegex(@"https?://github\.com/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)/issues/(?<number>\d+)(?:[/#?][^\s]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubIssueRegex();

    [GeneratedRegex(@"https?://github\.com/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)/actions/runs/(?<runId>\d+)(?:[/#?][^\s]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubActionsRegex();

    [GeneratedRegex(@"https?://linear\.app/(?<workspace>[A-Za-z0-9_.-]+)/issue/(?<key>[A-Za-z0-9]+-\d+)(?:/(?<slug>[^\s?#]+))?(?:[?#][^\s]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex LinearIssueRegex();

    [GeneratedRegex(@"https?://github\.com/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)/(?:tree|blob|commit|commits|compare|releases|actions)(?:/[^\s]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubRepoUrlRegex();
}
