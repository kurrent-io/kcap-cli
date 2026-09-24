using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;
using Capacitor.Models.Transcripts.Harness.Claude;

namespace Capacitor.Cli;

/// <summary>Claude watchers observe commits through local git; every other vendor observes none.</summary>
static class ObservedCommits {
    public static CommitObservation For(string vendor, GitProviderRouter router, ConfigRoot config, TimeProvider time) =>
        vendor == "claude" ? Claude(Observer(router, config, time)) : CommitObservation.None;

    // git supplies message text the transcript may never have held.
    internal static CommitObservation Claude(CommitObserver observer) =>
        CommitObservation.Of(ClaudeShellSteps.Read, observer, message => SecretRedactor.RedactValue(message, keyIsSecret: false) ?? message);

    public static void RecallBefore(CommitObservation commits, string transcriptPath, int upToLine) {
        try {
            commits.Recall(File.ReadLinesShared(transcriptPath).Take(upToLine).Skip(upToLine - WatchCommand.ToolBackfillWindowLines));
        } catch (IOException) { }
    }

    static CommitObserver Observer(GitProviderRouter router, ConfigRoot config, TimeProvider time) {
        var runGit = RepositoryDetection.DefaultRunner(time);

        return new CommitObserver(
            (arguments, dir, timeout) => runGit("git", arguments, dir, timeout),
            async top => await RepositoryDetection.DetectRepositoryAsync(router, config, top, time, detectPullRequest: false) is { } repo
                ? (repo.Owner, repo.RepoName)
                : (null, null));
    }
}
