namespace Capacitor.Cli.Daemon.Services;

public partial class WorktreeManager {
    static GitConfigOverride[] HandoffFetchConfig() =>
        [new("maintenance.auto", "false"), new("gc.auto", "0"), .. NoSubmoduleRecursion()];

    /// <summary>Reads origin and decides where a hand-off worktree starts, without creating anything but
    /// fetched refs. A branch is reported absent only when origin answers that it has no such branch.</summary>
    public async Task<HandoffResolution> ResolveHandoffStartAsync(string repoPath, HandoffTarget target, CancellationToken ct) {
        if (target.Branch is { Length: > 0 } named && !await IsValidRefNameAsync(repoPath, named))
            throw new InvalidOperationException($"invalid_base_ref: {named}");

        if (target is { HeadIsFork: true, PullRequestNumber: { } number }) {
            var pullRef = $"refs/kcap/handoff/pull-{number}";
            var fetched = await RunGitCaptureResult(repoPath, FetchTimeout, time, false, HandoffFetchConfig(),
                "fetch", "--no-write-fetch-head", "origin", $"+refs/pull/{number}/head:{pullRef}");
            if (fetched.ExitCode != 0) return FetchFailed(fetched);

            var head = (await RunGitCapture(repoPath, GitTimeout, time, true, "rev-parse", "--verify", $"{pullRef}^{{commit}}")).Trim();

            return new(new(HandoffSource.ForkPullRequest, HandoffCheckout.Detached, head, target.Branch, HandoffDetachReason.ForkHead, null), null);
        }

        ct.ThrowIfCancellationRequested();

        if (target.Branch is { Length: > 0 } branch) {
            // ls-remote exits 2 only when origin answered and has no matching ref.
            var listed = await RunGitCaptureResult(repoPath, FetchTimeout, time, true, [],
                "ls-remote", "--exit-code", "origin", $"refs/heads/{branch}");
            if (listed.ExitCode == 0) {
                var (sha, error) = await FetchOriginBranchAsync(repoPath, branch);

                return sha is null ? new(null, error) : new(await DecideBranchStartAsync(repoPath, branch, sha), null);
            }

            if (listed.ExitCode != 2) return FetchFailed(listed);
        }

        ct.ThrowIfCancellationRequested();

        var symref = await RunGitCaptureResult(repoPath, FetchTimeout, time, true, [], "ls-remote", "--symref", "origin", "HEAD");
        if (symref.ExitCode != 0) return FetchFailed(symref);

        const string prefix = "ref: refs/heads/";
        var defaultBranch = symref.Stdout.Split('\n')
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line[prefix.Length..].Split('\t')[0])
            .FirstOrDefault();
        if (string.IsNullOrEmpty(defaultBranch)) return new(null, "origin did not name its default branch");

        var (defaultSha, defaultError) = await FetchOriginBranchAsync(repoPath, defaultBranch);

        return defaultSha is null
            ? new(null, defaultError)
            : new(new(HandoffSource.DefaultBranch, HandoffCheckout.Detached, defaultSha, defaultBranch, HandoffDetachReason.BranchAbsent, null), null);
    }

    async Task<(string? Sha, string? Error)> FetchOriginBranchAsync(string repoPath, string branch) {
        var tracking = $"refs/remotes/origin/{branch}";
        var fetched = await RunGitCaptureResult(repoPath, FetchTimeout, time, false, HandoffFetchConfig(),
            "fetch", "--no-write-fetch-head", "origin", $"+refs/heads/{branch}:{tracking}");
        if (fetched.ExitCode != 0) return (null, FetchFailed(fetched).FetchError);

        return ((await RunGitCapture(repoPath, GitTimeout, time, true, "rev-parse", "--verify", $"{tracking}^{{commit}}")).Trim(), null);
    }

    async Task<HandoffStart> DecideBranchStartAsync(string repoPath, string branch, string sha) {
        var (local, elsewhere) = await ReadLocalBranchAsync(repoPath, branch);

        if (local is null)
            return new(HandoffSource.Branch, HandoffCheckout.CreateBranch, sha, branch, HandoffDetachReason.None, null);
        if (elsewhere)
            return new(HandoffSource.Branch, HandoffCheckout.Detached, sha, branch, HandoffDetachReason.CheckedOutElsewhere, local);
        if (local == sha)
            return new(HandoffSource.Branch, HandoffCheckout.CheckoutBranch, sha, branch, HandoffDetachReason.None, local);

        var ancestor = await RunGitCaptureResult(repoPath, GitTimeout, time, true, [], "merge-base", "--is-ancestor", local, sha);

        return ancestor.ExitCode == 0
            ? new(HandoffSource.Branch, HandoffCheckout.FastForwardBranch, sha, branch, HandoffDetachReason.None, local)
            : new(HandoffSource.Branch, HandoffCheckout.Detached, sha, branch, HandoffDetachReason.LocalBranchDiverged, local);
    }

    /// <summary>The local branch tip and whether any worktree has it checked out, read together under the
    /// metadata gate so a concurrent <c>worktree add</c> cannot slip between them.</summary>
    async Task<(string? Tip, bool CheckedOut)> ReadLocalBranchAsync(string repoPath, string branch) {
        string? tip       = null;
        var     checkedOut = false;

        await WithWorktreeMetadataGate(repoPath, time, async () => {
            tip = await ResolveLocalBranchAsync(repoPath, branch);
            var listed = await RunGitCapture(repoPath, GitTimeout, time, true, "worktree", "list", "--porcelain");
            checkedOut = listed.Split('\n').Any(line => line.Trim() == $"branch refs/heads/{branch}");
        });

        return (tip, checkedOut);
    }

    static HandoffResolution FetchFailed((int ExitCode, string Stdout, string Stderr) result) =>
        new(null, GitFailureDetail(result.Stderr, result.Stdout) is { Length: > 0 } detail ? detail : $"git exited {result.ExitCode}");
}
