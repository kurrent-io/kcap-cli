using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;
using Capacitor.Models.Transcripts.Harness.Claude;

namespace Capacitor.Cli;

/// <summary>The watcher side of <see cref="CommitObserver"/>: git placement through the repository
/// detector, and every message redacted before it leaves the host, since git supplies text the
/// transcript may never have held.</summary>
static class ObservedCommits {
    public static CommitObserver NewObserver(GitProviderRouter router, ConfigRoot config, TimeProvider time) {
        var runGit = RepositoryDetection.DefaultRunner(time);

        return new CommitObserver(
            (arguments, dir, timeout) => runGit("git", arguments, dir, timeout),
            async top => await RepositoryDetection.DetectRepositoryAsync(router, config, top, time, detectPullRequest: false) is { } repo
                ? (repo.Owner, repo.RepoName)
                : (null, null));
    }

    /// <summary>Adds the commits <paramref name="rawLines"/> show landing. Reads the lines before
    /// redaction, which swaps an oversized one, such as a noisy pre-commit hook's result, for a
    /// placeholder.</summary>
    public static async Task CollectAsync(CommitObserver observer, IEnumerable<string> rawLines, List<ObservedCommit> into) {
        foreach (var line in rawLines)
            if (ClaudeShellSteps.Read(line) is { } steps)
                foreach (var commit in await observer.ObserveAsync(steps))
                    if (Redacted(commit) is var redacted && !into.Contains(redacted)) into.Add(redacted);
    }

    static ObservedCommit Redacted(ObservedCommit commit) =>
        commit with { Message = SecretRedactor.RedactValue(commit.Message, keyIsSecret: false) ?? commit.Message };
}
