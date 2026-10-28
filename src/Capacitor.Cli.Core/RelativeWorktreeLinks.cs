namespace Capacitor.Cli.Core;

/// <summary>
/// A repository's stance on relative worktree links (<c>worktree.useRelativePaths</c>, git 2.48+), read
/// from its config files with no git process. Absolute links resolve only on the side that wrote them,
/// so a repository opened from a sandbox and from the host that mounts it at another path needs
/// relative ones.
/// </summary>
public static class RelativeWorktreeLinks {
    /// <summary>Set to <see cref="Declined"/> in the repository's own config when the user said no, so
    /// neither side asks again; <c>git config --unset kcap.relativeWorktrees</c> undoes it.</summary>
    public const string DeclinedKey = "kcap.relativeWorktrees";

    public const string Declined = "declined";

    /// <summary>Whether git writes relative links here: the repository's config, else the global one.
    /// The system config is not read.</summary>
    public static bool IsEnabled(string commonDir, UserHome home, Func<string, string?> env) {
        var local = GitConfigFile.Get(Path.Combine(commonDir, "config"), "worktree", "useRelativePaths");
        if (local is not null) return GitConfigFile.IsTrue(local);

        string? global = null;
        foreach (var path in GlobalConfigPaths(home, env))
            global = GitConfigFile.Get(path, "worktree", "useRelativePaths") ?? global;

        return GitConfigFile.IsTrue(global);
    }

    public static bool IsDeclined(string commonDir) =>
        string.Equals(GitConfigFile.Get(Path.Combine(commonDir, "config"), "kcap", "relativeWorktrees"),
                      Declined, StringComparison.OrdinalIgnoreCase);

    /// <summary>Git's global config, lowest precedence first: <c>$GIT_CONFIG_GLOBAL</c> replaces both
    /// files when set.</summary>
    static IEnumerable<string> GlobalConfigPaths(UserHome home, Func<string, string?> env) {
        if (env("GIT_CONFIG_GLOBAL") is { Length: > 0 } explicitGlobal) {
            yield return explicitGlobal;
            yield break;
        }

        var xdg = env("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(home.Path, ".config");
        yield return Path.Combine(xdg, "git", "config");
        yield return Path.Combine(home.Path, ".gitconfig");
    }
}
