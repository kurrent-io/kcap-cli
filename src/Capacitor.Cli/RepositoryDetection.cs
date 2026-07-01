using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli;

static class RepositoryDetection {
    // Bump whenever the cached shape/derivation changes so stale entries are ignored.
    // v2: added Host + provider-aware parsing.
    internal const int CacheSchemaVersion = 2;

    internal static CommandRunner DefaultRunner => RunCommandAsync;

    public static async Task<string> EnrichWithRepositoryInfo(string json, TimeSpan? budget = null) {
        try {
            var node = JsonNode.Parse(json);

            if (node is not JsonObject obj) {
                return json;
            }

            var cwd = obj["cwd"]?.GetValue<string>();

            if (cwd is null) {
                return json;
            }

            var repo = await DetectRepositoryAsync(cwd, budget);

            if (repo is null) {
                return json;
            }

            // Skip enrichment if repo info hasn't changed since last emit for this cwd
            var lastEmitted = LoadLastEmitted(cwd);

            if (lastEmitted is not null && RepoPayloadEquals(repo, lastEmitted)) {
                return json;
            }

            var repoNode = new JsonObject();

#pragma warning disable IDE0011
            if (repo.UserName is not null) repoNode["user_name"]    = repo.UserName;
            if (repo.UserEmail is not null) repoNode["user_email"]  = repo.UserEmail;
            if (repo.RemoteUrl is not null) repoNode["remote_url"]  = repo.RemoteUrl;
            if (repo.Host is not null) repoNode["host"]             = repo.Host;
            if (repo.Owner is not null) repoNode["owner"]           = repo.Owner;
            if (repo.RepoName is not null) repoNode["repo_name"]    = repo.RepoName;
            if (repo.Branch is not null) repoNode["branch"]         = repo.Branch;
            if (repo.PrNumber is not null) repoNode["pr_number"]    = repo.PrNumber;
            if (repo.PrTitle is not null) repoNode["pr_title"]      = repo.PrTitle;
            if (repo.PrUrl is not null) repoNode["pr_url"]          = repo.PrUrl;
            if (repo.PrHeadRef is not null) repoNode["pr_head_ref"] = repo.PrHeadRef;
#pragma warning restore IDE0011

            obj["repository"] = repoNode;

            SaveLastEmitted(cwd, repo);

            return obj.ToJsonString();
        } catch {
            return json; // on any error, forward original payload
        }
    }

    static bool RepoPayloadEquals(RepositoryPayload a, RepositoryPayload b) =>
        a.Owner     == b.Owner
     && a.RepoName  == b.RepoName
     && a.Host      == b.Host
     && a.Branch    == b.Branch
     && a.PrNumber  == b.PrNumber
     && a.PrUrl     == b.PrUrl
     && a.PrTitle   == b.PrTitle
     && a.UserName  == b.UserName
     && a.UserEmail == b.UserEmail;

    public static async Task<RepositoryPayload?> DetectRepositoryAsync(string cwd, TimeSpan? budget = null) {
        if (budget is { } b0 && b0 <= TimeSpan.Zero) return null;
        try {
            // Bound the git/gh probes by the remaining hook budget so a slow repo never
            // overruns the deadline. When no budget is set, keep the historical 5s/2s caps.
            var sw     = Stopwatch.StartNew();
            var gitCap = budget is { } b
                ? TimeSpan.FromSeconds(Math.Min(5, Math.Max(0, b.TotalSeconds)))
                : TimeSpan.FromSeconds(5);

            // Try loading cached base info
            var cache = LoadCache(cwd);

            string? userName, userEmail, remoteUrl, owner, repoName, branch, host;

            // Always detect branch fresh — it changes frequently during a session
            var branchTask = RunCommandAsync("git", "branch --show-current", cwd, gitCap);

            if (cache is not null) {
                userName  = cache.UserName;
                userEmail = cache.UserEmail;
                remoteUrl = cache.RemoteUrl;
                owner     = cache.Owner;
                repoName  = cache.RepoName;
                host      = cache.Host;
                branch    = await branchTask;
            } else {
                // Run git commands in parallel
                var userNameTask  = RunCommandAsync("git", "config user.name", cwd, gitCap);
                var userEmailTask = RunCommandAsync("git", "config user.email", cwd, gitCap);
                var remoteUrlTask = RunCommandAsync("git", "remote get-url origin", cwd, gitCap);

                await Task.WhenAll(userNameTask, userEmailTask, remoteUrlTask, branchTask);

                userName  = userNameTask.Result;
                userEmail = userEmailTask.Result;
                remoteUrl = remoteUrlTask.Result;
                branch    = branchTask.Result;

                // Not a git repo if we can't get branch or remote
                if (branch is null && remoteUrl is null) {
                    return null;
                }

                (owner, repoName) = GitUrlParser.ParseRemoteUrl(remoteUrl);
                host = remoteUrl is null ? null : RemoteMatcher.ExtractHost(remoteUrl);

                // Save to cache (without branch — it's always detected fresh)
                SaveCache(
                    cwd,
                    new() {
                        UserName      = userName,
                        UserEmail     = userEmail,
                        RemoteUrl     = remoteUrl,
                        Owner         = owner,
                        RepoName      = repoName,
                        Host          = host,
                        SchemaVersion = CacheSchemaVersion,
                        CachedAt      = DateTimeOffset.UtcNow
                    }
                );
            }

            // Always try fresh PR detection (not cached). Bound by whatever budget remains after
            // the git probes; skip entirely if the budget is already exhausted.
            int?    prNumber = null;
            string? prTitle  = null, prUrl = null, prHeadRef = null;

            var ghCap = TimeSpan.FromSeconds(2);
            if (budget is { } bgh) {
                var remainingBudget = bgh - sw.Elapsed;
                ghCap = TimeSpan.FromSeconds(Math.Min(2, Math.Max(0, remainingBudget.TotalSeconds)));
            }

            // Effective provider budget: the post-git remainder when the caller passed a budget
            // (only ClaudeHookCommand does), else the historical 2s cap. Covers probe + detector.
            var providerCap = ghCap;

            if (providerCap > TimeSpan.Zero && host is not null) {
                var kind = await GitProviderRouter.ResolveAsync(host, cwd, providerCap, DefaultRunner);
                var pr = kind switch {
                    GitProviderKind.GitHub => await GitHubPrDetector.DetectAsync(cwd, providerCap, DefaultRunner),
                    GitProviderKind.GitLab when owner is not null && repoName is not null
                        => await GitLabPrDetector.DetectAsync(host, owner, repoName, branch, cwd, providerCap, DefaultRunner),
                    _ => null
                };

                if (pr is not null) {
                    prNumber  = pr.Number;
                    prTitle   = pr.Title;
                    prUrl     = pr.Url;
                    prHeadRef = pr.HeadRef;
                }
            }

            return new() {
                UserName  = userName,
                UserEmail = userEmail,
                RemoteUrl = remoteUrl,
                Host      = host,
                Owner     = owner,
                RepoName  = repoName,
                Branch    = branch,
                PrNumber  = prNumber,
                PrTitle   = prTitle,
                PrUrl     = prUrl,
                PrHeadRef = prHeadRef
            };
        } catch {
            return null;
        }
    }

    static async Task<string?> RunCommandAsync(string cmd, string arguments, string cwd, TimeSpan timeout) {
        Process? process = null;
        try {
            var psi = new ProcessStartInfo(cmd, arguments) {
                WorkingDirectory       = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            process = Process.Start(psi);

            if (process is null) {
                return null;
            }

            using var cts    = new CancellationTokenSource(timeout);
            var       output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            return process.ExitCode == 0 ? output.Trim() : null;
        } catch {
            // Timed out or failed — kill the child (and its tree) so a slow git/gh probe doesn't
            // outlive the hook and accumulate across repeated invocations. Disposing alone only
            // releases handles; it does not stop the process.
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
            return null;
        } finally {
            process?.Dispose();
        }
    }

    static string GetCachePath(string cwd) {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cwd)))[..16];

        return Path.Combine(PathHelpers.ConfigPath("cache"), $"{hash}.json");
    }

    static GitCacheEntry? LoadCache(string cwd) {
        try {
            var path = GetCachePath(cwd);

            if (!File.Exists(path)) {
                return null;
            }

            var json  = File.ReadAllText(path);
            var entry = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.GitCacheEntry);

            if (entry is null || entry.SchemaVersion != CacheSchemaVersion) {
                return null;
            }

            // 1-hour TTL
            return DateTimeOffset.UtcNow - entry.CachedAt > TimeSpan.FromHours(1) ? null : entry;
        } catch {
            return null;
        }
    }

    static void SaveCache(string cwd, GitCacheEntry entry) {
        try {
            var path = GetCachePath(cwd);
            var dir  = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(entry, CapacitorJsonContext.Default.GitCacheEntry));
        } catch {
            // Cache write failure is non-critical
        }
    }

    static string GetLastEmittedPath(string cwd) {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cwd)))[..16];

        return Path.Combine(PathHelpers.ConfigPath("cache"), $"{hash}.repo-emitted.json");
    }

    static RepositoryPayload? LoadLastEmitted(string cwd) {
        try {
            var path = GetLastEmittedPath(cwd);

            if (!File.Exists(path)) {
                return null;
            }

            var json = File.ReadAllText(path);

            return JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.RepositoryPayload);
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Clear the last-emitted cache for a cwd so the next enrichment always includes repo info.
    /// Must be called on session-start — each new session needs its own RepositoryDetected event.
    /// </summary>
    public static void ClearLastEmitted(string cwd) {
        try {
            var path = GetLastEmittedPath(cwd);

            if (File.Exists(path)) {
                File.Delete(path);
            }
        } catch {
            // Non-critical
        }
    }

    static void SaveLastEmitted(string cwd, RepositoryPayload payload) {
        try {
            var path = GetLastEmittedPath(cwd);
            var dir  = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(payload, CapacitorJsonContext.Default.RepositoryPayload));
        } catch {
            // Cache write failure is non-critical
        }
    }
}
