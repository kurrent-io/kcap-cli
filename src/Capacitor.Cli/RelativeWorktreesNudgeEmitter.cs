using Capacitor.Cli.Core;

namespace Capacitor.Cli;

/// <summary>
/// The SessionStart nudge to switch a repository to relative worktree links when the session runs in a
/// sandbox, which can mount it at a different path from the host's. It asks rather than blocks: the session works
/// where it runs, and converting later rewrites the worktrees made in the meantime.
/// </summary>
static class RelativeWorktreesNudgeEmitter {
    /// <summary>Null when opted out, outside a repository, already enabled, declined in the repository's
    /// config, or not in a sandbox. <paramref name="sandboxed"/> and <paramref name="env"/> are test seams.</summary>
    public static string? Resolve(string? cwd, bool optedOut, UserHome home,
                                  Func<string, bool>? sandboxed = null, Func<string, string?>? env = null) {
        if (optedOut || string.IsNullOrEmpty(cwd)) return null;

        try {
            if (GitRepository.ResolveCommonDir(cwd) is not { } commonDir) return null;
            if (RelativeWorktreeLinks.IsDeclined(commonDir)) return null;
            if (RelativeWorktreeLinks.IsEnabled(commonDir, home, env ?? Environment.GetEnvironmentVariable)) return null;

            return (sandboxed ?? SandboxDetection.IsLikely)(commonDir) ? Text : null;
        } catch {
            return null; // a nudge must never break a hook
        }
    }

    internal const string Text =
        "## Worktree links\n" +
        "Ask the user:\n\n" +
        "> This session appears to be running in a sandbox. Git worktree relative paths need to be enabled in " +
        "this repository for the host to access the session worktree directly. When enabled, the host requires " +
        "git 2.48 or newer to be able to access the repository. Shall I enable it?\n\n" +
        "On yes, run `git config worktree.useRelativePaths true`, then `git worktree repair --relative-paths` " +
        "passing the directory of every worktree as it appears from here, such as each one under " +
        "`.capacitor/worktrees/`: without paths it skips worktrees the host made; " +
        $"on no, run `git config {RelativeWorktreeLinks.DeclinedKey} {RelativeWorktreeLinks.Declined}` so it is " +
        "not asked again. Never run `git worktree prune` here. The kcap worktrees skill covers undoing it and " +
        "reinstating a pruned worktree.";
}
