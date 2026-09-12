using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.PrDetection;

/// <summary>
/// The remote branch the current branch tracks, when it goes by another name in the session's own
/// repository. What cannot be shown to be that resolves to nothing: an upstream on another
/// repository's remote, a name unsafe to pass as an argument, and the remote's default branch — a
/// branch cut from <c>origin/main</c> tracks <c>main</c> without being its PR, and when the remote's
/// HEAD is unknown so is the default.
/// </summary>
internal static class TrackedBranch {
    const string HeadsPrefix = "refs/heads/";

    public static async Task<string?> ResolveAsync(
            string? branch, string host, string owner, string repo, string cwd, Func<TimeSpan> remaining,
            CommandRunner run) {
        if (!CommandArgument.IsPlain(branch)) return null;

        var remoteTask = Git($"config --get branch.{branch}.remote");
        var mergeTask  = Git($"config --get branch.{branch}.merge");
        await Task.WhenAll(remoteTask, mergeTask);

        var remote = remoteTask.Result;
        var merge  = mergeTask.Result;

        if (!CommandArgument.IsPlain(remote) || merge is null || !merge.StartsWith(HeadsPrefix, StringComparison.Ordinal)) {
            return null;
        }

        var tracked = merge[HeadsPrefix.Length..];
        if (tracked == branch || !CommandArgument.IsPlain(tracked)) return null;

        var urlTask     = Git($"remote get-url {remote}");
        var defaultTask = Git($"symbolic-ref --quiet refs/remotes/{remote}/HEAD");
        await Task.WhenAll(urlTask, defaultTask);

        var defaultRef = defaultTask.Result;
        if (defaultRef is null || defaultRef == $"refs/remotes/{remote}/{tracked}") return null;

        return PointsAt(urlTask.Result, host, owner, repo) ? tracked : null;

        Task<string?> Git(string arguments) {
            var cap = remaining();
            return cap > TimeSpan.Zero ? run("git", arguments, cwd, cap) : Task.FromResult<string?>(null);
        }
    }

    static bool PointsAt(string? url, string host, string owner, string repo) {
        if (url is null) return false;

        var (urlOwner, urlRepo) = GitUrlParser.ParseRemoteUrl(url);

        return string.Equals(RemoteMatcher.ExtractHost(url), host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(urlOwner, owner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(urlRepo, repo, StringComparison.OrdinalIgnoreCase);
    }
}
