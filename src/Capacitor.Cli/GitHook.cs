using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Mcp;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli;

/// <summary>
/// <c>kcap git-hook</c>, which git runs after a commit anywhere on this machine: it files the commit
/// under the session of the coding agent above it.
/// </summary>
static class GitHook {
    public const string Name = "kcap";

    static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Whether <c>git hook list</c> output names kcap's hook, enabled.
    /// </summary>
    public static bool ListedIn(string? hookList) =>
        hookList?.Split('\n').Any(hook => hook.Trim() == Name) == true;

    /// <summary>
    /// The <see cref="CommitObservation"/> a watcher starts with.
    /// </summary>
    public static async Task<CommitObservation> ObservationAsync(ConfigRoot config, string sessionId, string? agentId, string? cwd, TimeProvider time) {
        if (!await CoversAsync(config, sessionId, cwd, time)) return new CommitObservation.Uncovered();

        return agentId is null ? new CommitObservation.Covered(SessionCommits.Of(config, SessionId.Parse(sessionId)!)) : new CommitObservation.Subagent();
    }

    public static async Task<bool> CoversAsync(ConfigRoot config, string sessionId, string? cwd, TimeProvider time) =>
        SessionId.Parse(sessionId) is { } session
     && AgentSessions.OnThisMachine(config).IsClaimed(session)
     && await RunsInAsync(cwd ?? "", time);

    // Listed means git runs config hooks and the entry is enabled. The command must also be this
    // binary's: an entry left by a moved or removed kcap is listed, runs nothing, and would still
    // stop the server reading commits from the transcript.
    static async Task<bool> RunsInAsync(string dir, TimeProvider time) {
        var run = RepositoryDetection.DefaultRunner(time);

        return ListedIn(await run("git", "hook list post-commit", dir, GitTimeout))
            && await run("git", $"config --get hook.{Name}.command", dir, GitTimeout) == GitHookInstaller.CommandFor(KcapBinaryCommand.Resolve());
    }

    /// <summary>
    /// Always 0 and silent: the commit has already landed.
    /// </summary>
    public static async Task<int> RunAsync(GitHookInvocation invocation, ConfigRoot config, TimeProvider time) {
        try {
            await RecordAsync(invocation, AgentSessions.OnThisMachine(config), config, time);
        } catch { }

        return 0;
    }

    internal static async Task RecordAsync(GitHookInvocation invocation, AgentSessions sessions, ConfigRoot config, TimeProvider time) {
        if (sessions.Above(invocation.Pid) is not { } session) return;

        var run = RepositoryDetection.DefaultRunner(time);
        var dir = invocation.Dir;

        // post-commit passes no arguments and post-merge its squash flag. A squash merge has committed nothing yet.
        var committed = invocation.Args switch {
            []    => true,
            ["0"] => await MergedNowAsync(run, dir),
            _     => false,
        };

        if (!committed || await run("git", "log -1 --format=%H%x1f%B", dir, GitTimeout) is not { } log || log.Split('\x1f', 2) is not [var sha, var body])
            return;

        var repo    = await RepositoryDetection.DetectRepositoryAsync(new(), config, dir, time, detectPullRequest: false);
        var message = body.Trim();

        SessionCommits.Of(config, session).Append(new ObservedCommit {
            Sha      = sha,
            Owner    = repo?.Owner,
            RepoName = repo?.RepoName,
            Branch   = repo?.Branch is { Length: > 0 } branch ? branch : null,
            Message  = SecretRedactor.RedactValue(message, keyIsSecret: false) ?? message,
        });
    }

    // A fast-forward moves HEAD onto a commit another ref already holds. A merge git just made is
    // held by the current branch alone.
    static async Task<bool> MergedNowAsync(CommandRunner run, string dir) {
        // %(HEAD) prints '*' before the branch HEAD is on.
        var holders = await run("git", "for-each-ref --contains HEAD --format=%(HEAD)%(refname)", dir, GitTimeout);

        return holders is not null && holders
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .All(holder => holder.StartsWith('*'));
    }
}
