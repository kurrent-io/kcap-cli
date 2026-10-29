using Capacitor.Cli.Core;

namespace Capacitor.Cli;

sealed record GitHookInvocation(string[] Args, int Pid, string Dir) {
    /// <summary>
    /// git runs a hook at the root of the committing worktree.
    /// </summary>
    public static GitHookInvocation Current(string[] args, WorkingDirectory workdir) =>
        new(args, Environment.ProcessId, workdir.Path);
}
