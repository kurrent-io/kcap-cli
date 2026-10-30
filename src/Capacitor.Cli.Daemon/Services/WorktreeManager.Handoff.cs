namespace Capacitor.Cli.Daemon.Services;

public partial class WorktreeManager {
    static GitConfigOverride[] HandoffFetchConfig() =>
        [new("maintenance.auto", "false"), new("gc.auto", "0"), .. NoSubmoduleRecursion()];

    /// <summary>Reads origin and decides where a hand-off worktree starts, without creating anything but
    /// fetched refs. A branch is reported absent only when origin answers that it has no such branch.</summary>
    public async Task<HandoffResolution> ResolveHandoffStartAsync(string repoPath, HandoffTarget target, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();

        if (target is { HeadIsFork: true, PullRequestNumber: null })
            throw new InvalidOperationException("invalid_handoff_target: a fork head needs its pull request number");
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
            var listed = await RunGitCaptureResult(repoPath, FetchTimeout, time, false, [],
                "ls-remote", "--exit-code", "origin", $"refs/heads/{branch}");
            if (listed.ExitCode == 0) {
                var (sha, error) = await FetchOriginBranchAsync(repoPath, branch);

                return sha is null ? new(null, error) : new(await DecideBranchStartAsync(repoPath, branch, sha), null);
            }

            if (listed.ExitCode != 2) return FetchFailed(listed);
        }

        ct.ThrowIfCancellationRequested();

        var symref = await RunGitCaptureResult(repoPath, FetchTimeout, time, false, [], "ls-remote", "--symref", "origin", "HEAD");
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
        (string? Tip, bool CheckedOut) state = default;
        await WithWorktreeMetadataGate(repoPath, time, async () => state = await ReadLocalBranchUngatedAsync(repoPath, branch));

        return state;
    }

    async Task<(string? Tip, bool CheckedOut)> ReadLocalBranchUngatedAsync(string repoPath, string branch) {
        var tip    = await ResolveLocalBranchAsync(repoPath, branch);
        var listed = await RunGitCapture(repoPath, GitTimeout, time, true, "worktree", "list", "--porcelain");

        return (tip, listed.Split('\n').Any(line => line.Trim() == $"branch refs/heads/{branch}"));
    }

    /// <summary>Materialises a start from <see cref="ResolveHandoffStartAsync"/>. The local branch is
    /// re-read first and creation refuses with <c>handoff_start_changed</c> if it moved or was checked out
    /// since; a pre-existing branch is never owned, so no rollback or removal deletes it.</summary>
    public async Task<WorktreeInfo> CreateHandoffAsync(string repoPath, HandoffStart start, string? name = null) {
        name ??= $"agent-{Guid.NewGuid():N}"[..20];

        var worktreeRoot = Path.Combine(repoPath, ".capacitor", "worktrees");
        var worktreePath = Path.Combine(worktreeRoot, name);
        var detached     = start.Checkout == HandoffCheckout.Detached;
        var branch       = detached ? "" : start.Branch ?? "";

        if (!detached && !await IsValidRefNameAsync(repoPath, branch))
            throw new InvalidOperationException($"invalid_base_ref: {branch}");

        Directory.CreateDirectory(worktreeRoot);

        // Pinning the start under the worktree's own ref keeps a fork head out of the unsaved-work count,
        // and removal deletes it.
        var fetchedRef = FetchedRefFor(name);
        var created    = new WorktreeInfo(worktreePath, branch, repoPath, FetchedRef: fetchedRef) {
            OwnsBranch = start.Checkout == HandoffCheckout.CreateBranch,
        };
        var noHooks = NoBranchHooks();

        try {
            await WithWorktreeMetadataGate(repoPath, time, async () => {
                if (!detached) {
                    var (tip, checkedOut) = await ReadLocalBranchUngatedAsync(repoPath, branch);
                    if (tip != start.LocalBranchSha) throw StartChanged($"{branch} is at {tip ?? "nothing"}, not {start.LocalBranchSha ?? "nothing"}");
                    if (checkedOut) throw StartChanged($"{branch} is checked out in another worktree");
                }

                await RunGit(repoPath, GitTimeout, time, "update-ref", fetchedRef, start.Sha);

                switch (start.Checkout) {
                    case HandoffCheckout.CreateBranch:
                        await RunGit(repoPath, GitTimeout, time, noHooks, "worktree", "add", "--no-checkout", "-b", branch, worktreePath, start.Sha);
                        break;
                    case HandoffCheckout.FastForwardBranch:
                        var moved = await RunGitCaptureResult(repoPath, GitTimeout, time, false, [],
                            "update-ref", $"refs/heads/{branch}", start.Sha, start.LocalBranchSha!);
                        if (moved.ExitCode != 0) throw StartChanged($"{branch} moved before it could be fast-forwarded");
                        await RunGit(repoPath, GitTimeout, time, noHooks, "worktree", "add", "--no-checkout", worktreePath, branch);
                        break;
                    case HandoffCheckout.CheckoutBranch:
                        await RunGit(repoPath, GitTimeout, time, noHooks, "worktree", "add", "--no-checkout", worktreePath, branch);
                        break;
                    default:
                        await RunGit(repoPath, GitTimeout, time, noHooks, "worktree", "add", "--no-checkout", "--detach", worktreePath, start.Sha);
                        break;
                }
            });
        } catch {
            await RunGitBestEffort(repoPath, time, "update-ref", "-d", fetchedRef);
            throw;
        }

        await StripOrRollBackAsync(created);

        try {
            // Written directly: --set-upstream-to refuses a branch outside a single-branch clone's refspec.
            if (start.Checkout == HandoffCheckout.CreateBranch) {
                await RunGit(repoPath, GitTimeout, time, "config", $"branch.{branch}.remote", "origin");
                await RunGit(repoPath, GitTimeout, time, "config", $"branch.{branch}.merge", $"refs/heads/{branch}");
            }

            var head = (await RunGitCapture(worktreePath, GitTimeout, time, true, "rev-parse", "HEAD")).Trim();
            if (head != start.Sha) throw StartChanged($"the worktree is at {head}, not {start.Sha}");
        } catch {
            try { await RemoveAsync(created, time, keepUnsavedWork: false); } catch { /* keep the original failure */ }
            throw;
        }

        return created;
    }

    /// <summary>Checks the session's commits against the starting point. <paramref name="sessionCommits"/>
    /// null means they are unknown; <paramref name="mayHaveUnpushedWork"/> means the session may have left
    /// work that no commit list can show. A missing or reverted commit outranks either uncertainty.</summary>
    public async Task<HandoffWork> CheckHandoffWorkAsync(
            string repoPath, string startSha, IReadOnlyList<string>? sessionCommits, bool mayHaveUnpushedWork) {
        var missing = new List<string>();

        foreach (var commit in sessionCommits ?? []) {
            if (!IsFullSha(commit) || !await IsPresentAsync(commit)) missing.Add(commit);
        }

        var completeness = missing.Count > 0                            ? HandoffCompleteness.PartlyMissing
                         : sessionCommits is null || mayHaveUnpushedWork ? HandoffCompleteness.Unconfirmed
                         :                                                  HandoffCompleteness.Complete;

        return new(completeness, missing);

        // Reachable is not enough: a later `git revert` of the commit takes its change back out.
        async Task<bool> IsPresentAsync(string commit) {
            var ancestor = await RunGitCaptureResult(repoPath, GitTimeout, time, true, [], "merge-base", "--is-ancestor", commit, startSha);
            if (ancestor.ExitCode != 0) return false;

            var reverts = await RunGitCaptureResult(repoPath, GitTimeout, time, true, [],
                "rev-list", "--count", "--fixed-strings", $"--grep=This reverts commit {commit}", $"{commit}..{startSha}");

            return reverts.ExitCode == 0 && reverts.Stdout.Trim() == "0";
        }
    }

    static bool IsFullSha(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigitLower);

    static InvalidOperationException StartChanged(string why) => new($"handoff_start_changed: {why}");

    static HandoffResolution FetchFailed((int ExitCode, string Stdout, string Stderr) result) =>
        new(null, GitFailureDetail(result.Stderr, result.Stdout) is { Length: > 0 } detail ? detail : $"git exited {result.ExitCode}");
}
