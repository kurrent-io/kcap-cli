using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// <paramref name="FetchedRef"/> is the per-worktree ref the daemon fetched
/// the requested <c>baseRef</c> into (e.g. <c>refs/kcap/review/{name}</c>).
/// Tracked so <see cref="WorktreeManager.RemoveAsync"/> can delete it on
/// cleanup. Null for non-review launches.
/// </summary>
public record WorktreeInfo(string Path, string Branch, string SourceRepo, bool IsStandalone = false, string? FetchedRef = null) {
    /// <summary>A borrowed cwd (local in-place launch) the daemon does NOT own. Cleanup
    /// never removes it — the <see cref="AgentInstance.Work"/> guard enforces that.</summary>
    public static WorktreeInfo Borrowed(string cwd) => new(cwd, "", cwd, IsStandalone: false);
}

public partial class WorktreeManager(DaemonConfig config, ILogger<WorktreeManager> logger) {
    public async Task<WorktreeInfo> CreateAsync(string repoPath, string? name = null, string? baseRef = null) {
        name ??= $"agent-{Guid.NewGuid():N}"[..20];

        // Place worktrees under the repo's own .capacitor/ directory so they inherit
        // the repo's workspace trust in Claude Code (trust cascades up parent dirs).
        var worktreeRoot = Path.Combine(repoPath, ".capacitor", "worktrees");
        var worktreePath = Path.Combine(worktreeRoot, name);
        var branch       = $"capacitor/{name}";

        Directory.CreateDirectory(worktreeRoot);

        if (await IsGitRepoWithCommits(repoPath)) {
            if (!string.IsNullOrEmpty(baseRef)) {
                // Fetch into a per-worktree ref instead of the shared FETCH_HEAD
                // so concurrent review launches in the same source repo can't
                // race on each other's fetches. The unique ref carries the
                // worktree name so it's traceable and easy to clean up.
                var fetchedRef = $"refs/kcap/review/{name}";
                await RunGit(repoPath, FetchTimeout, "fetch", "origin", $"{baseRef}:{fetchedRef}");
                await RunGit(repoPath, GitTimeout, "worktree", "add", "-B", branch, worktreePath, fetchedRef);

                return new WorktreeInfo(worktreePath, branch, repoPath, FetchedRef: fetchedRef);
            }

            await RunGit(repoPath, GitTimeout, "worktree", "add", worktreePath, "-b", branch);

            return new WorktreeInfo(worktreePath, branch, repoPath);
        }

        // Standalone: copy files + git init
        Directory.CreateDirectory(worktreePath);
        CopyDirectory(repoPath, worktreePath);
        await RunGit(worktreePath, GitTimeout, "init");
        await RunGit(worktreePath, GitTimeout, "add", "-A");
        await RunGit(worktreePath, GitTimeout, "commit", "-m", "Initial snapshot");

        return new WorktreeInfo(worktreePath, "", repoPath, IsStandalone: true);
    }

    public static async Task RemoveAsync(WorktreeInfo worktree, bool deleteBranch = true) {
        if (worktree.IsStandalone) {
            if (Directory.Exists(worktree.Path)) {
                Directory.Delete(worktree.Path, true);
            }

            return;
        }

        await RunGit(worktree.SourceRepo, GitTimeout, "worktree", "remove", worktree.Path, "--force");

        if (deleteBranch && !string.IsNullOrEmpty(worktree.Branch)) {
            await RunGitBestEffort(worktree.SourceRepo, "branch", "-D", worktree.Branch);
        }

        if (!string.IsNullOrEmpty(worktree.FetchedRef)) {
            await RunGitBestEffort(worktree.SourceRepo, "update-ref", "-d", worktree.FetchedRef);
        }
    }

    /// <summary>Copies the source checkout's tracked and untracked non-ignored files into an owned
    /// worktree. This preserves dirty-review context without giving a reviewer the live checkout.</summary>
    public async Task SyncFromSourceAsync(
            string sourceRepoRoot, string targetWorktreePath,
            string[] excludePaths, CancellationToken ct) {
        if (string.IsNullOrEmpty(sourceRepoRoot))
            throw new ArgumentException("Source repo root must not be empty.", nameof(sourceRepoRoot));
        if (string.IsNullOrEmpty(targetWorktreePath))
            throw new ArgumentException("Target worktree path must not be empty.", nameof(targetWorktreePath));

        var source = Path.GetFullPath(sourceRepoRoot);
        var target = Path.GetFullPath(targetWorktreePath);
        if (string.Equals(source, target, StringComparison.Ordinal))
            throw new InvalidOperationException($"Source and target paths are the same: {source}");
        if (!Directory.Exists(source))
            throw new InvalidOperationException($"Source repo root does not exist: {source}");
        if (!File.Exists(Path.Combine(source, ".git")) && !Directory.Exists(Path.Combine(source, ".git")))
            throw new InvalidOperationException($"Source path does not appear to be a git repo (no .git entry): {source}");

        var psi = NewGitPsi(source, ["ls-files", "-co", "--exclude-standard", "-z"]);
        using var proc = Process.Start(psi)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(GitTimeout);
        string stdout;
        try {
            stdout = await proc.StandardOutput.ReadToEndAsync(timeout.Token);
            await proc.WaitForExitAsync(timeout.Token);
        } catch (OperationCanceledException) {
            try { proc.Kill(true); } catch { /* best-effort */ }
            throw;
        }
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"git ls-files failed in {source}: {await proc.StandardError.ReadToEndAsync(ct)}");

        var alwaysExcluded = new[] {
            ".git" + Path.DirectorySeparatorChar, ".git",
            ".capacitor" + Path.DirectorySeparatorChar + "worktrees"
        };
        var callerExcluded = excludePaths
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar))
            .ToArray();
        var copied = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawPath in stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)) {
            ct.ThrowIfCancellationRequested();
            var rel = rawPath.Replace('/', Path.DirectorySeparatorChar);
            if (rel.Contains(".." + Path.DirectorySeparatorChar) || rel == "..") continue;
            if (IsUnderExcluded(rel, alwaysExcluded) || IsUnderExcluded(rel, callerExcluded)) continue;

            var srcFile = Path.Combine(source, rel);
            if (!File.Exists(srcFile)) continue;
            var dstFile = Path.Combine(target, rel);
            if (Path.GetDirectoryName(dstFile) is { } dstDir) Directory.CreateDirectory(dstDir);
            File.Copy(srcFile, dstFile, overwrite: true);
            copied.Add(rel);
        }

        foreach (var existingFile in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)) {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(target, existingFile);
            if (IsUnderExcluded(rel, alwaysExcluded) || IsUnderExcluded(rel, callerExcluded)) continue;
            if (!copied.Contains(rel)) File.Delete(existingFile);
        }

        LogSyncCompleted(source, target, copied.Count);
    }

    static bool IsUnderExcluded(string rel, string[] prefixes) {
        foreach (var prefix in prefixes) {
            if (rel.Equals(prefix, StringComparison.Ordinal) ||
                rel.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public Task CleanupOrphanedAsync(IEnumerable<string>? activeWorktreePaths = null) {
        // Legacy global root — clean up any leftover worktrees from before the per-repo change
        var worktreePaths = activeWorktreePaths as string[] ?? [..activeWorktreePaths ?? []];
        CleanupDirectory(config.WorktreeRoot, worktreePaths);

        // Per-repo roots — scan each allowed repo for .capacitor/worktrees/
        foreach (var repoPath in config.AllowedRepoPaths) {
            var cleanPath   = repoPath.TrimEnd('/', '*');
            var perRepoRoot = Path.Combine(cleanPath, ".capacitor", "worktrees");
            CleanupDirectory(perRepoRoot, worktreePaths);
        }

        return Task.CompletedTask;
    }

    void CleanupDirectory(string root, IEnumerable<string>? activeWorktreePaths) {
        if (!Directory.Exists(root)) return;

        var activePaths = activeWorktreePaths?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        foreach (var dir in Directory.GetDirectories(root)) {
            if (activePaths.Contains(dir)) continue;

            LogCleaningUp(dir);
            try { Directory.Delete(dir, true); } catch (Exception ex) { LogCleanupFailed(ex, dir); }
        }
    }

    /// <summary>Default timeout for local git operations (worktree add, init, commit, …).</summary>
    static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Longer timeout for network git operations (fetch).</summary>
    static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(2);

    static async Task<bool> IsGitRepoWithCommits(string path) {
        try {
            var       psi  = NewGitPsi(path, ["rev-parse", "HEAD"]);
            using var proc = Process.Start(psi)!;
            using var cts  = new CancellationTokenSource(GitTimeout);

            try {
                await proc.WaitForExitAsync(cts.Token);
            } catch (OperationCanceledException) {
                try { proc.Kill(true); } catch {
                    /* best-effort */
                }

                return false;
            }

            return proc.ExitCode == 0;
        } catch { return false; }
    }

    static async Task RunGit(string cwd, TimeSpan timeout, params string[] args) {
        var       psi  = NewGitPsi(cwd, args);
        using var proc = Process.Start(psi)!;
        using var cts  = new CancellationTokenSource(timeout);

        try {
            await proc.WaitForExitAsync(cts.Token);
        } catch (OperationCanceledException) {
            try { proc.Kill(true); } catch {
                /* best-effort */
            }

            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} timed out after {timeout.TotalSeconds:F0}s"
            );
        }

        if (proc.ExitCode != 0) {
            var stderr = await proc.StandardError.ReadToEndAsync();

            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        }
    }

    static async Task RunGitBestEffort(string cwd, params string[] args) {
        try { await RunGit(cwd, GitTimeout, args); } catch {
            /* best-effort */
        }
    }

    /// <summary>
    /// Builds a <see cref="ProcessStartInfo"/> for git with prompts disabled
    /// (<c>GIT_TERMINAL_PROMPT=0</c>, <c>GCM_INTERACTIVE=Never</c>) so an
    /// unattended daemon can never block on a credential prompt.
    /// </summary>
    static ProcessStartInfo NewGitPsi(string cwd, string[] args) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
            Environment = {
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GCM_INTERACTIVE"]     = "Never"
            }
        };

        return psi;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cleaning up orphaned worktree: {Path}")]
    partial void LogCleaningUp(string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Synced {FileCount} files from {Source} into worktree {Target}")]
    partial void LogSyncCompleted(string source, string target, int fileCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to clean up {Path}")]
    partial void LogCleanupFailed(Exception ex, string path);

    static void CopyDirectory(string source, string dest) {
        foreach (var file in Directory.GetFiles(source)) {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(source)) {
            if (Path.GetFileName(dir) == ".git") {
                continue;
            }

            var destDir = Path.Combine(dest, Path.GetFileName(dir));
            Directory.CreateDirectory(destDir);
            CopyDirectory(dir, destDir);
        }
    }
}
