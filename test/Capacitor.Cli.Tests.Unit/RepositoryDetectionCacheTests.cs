using System.Diagnostics;
using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class RepositoryDetectionCacheTests {
    [Test]
    public async Task GitCacheEntry_without_schema_version_deserializes_as_stale() {
        // A pre-upgrade entry (no schema_version, no host) must be treated as stale.
        const string legacy = """{"owner":"o","repo_name":"r","cached_at":"2020-01-01T00:00:00+00:00"}""";
        var entry = JsonSerializer.Deserialize(legacy, CapacitorJsonContext.Default.GitCacheEntry);
        await Assert.That(entry!.SchemaVersion).IsEqualTo(0);        // absent → default 0
        await Assert.That(entry.SchemaVersion == RepositoryDetection.CacheSchemaVersion).IsFalse();
    }

    [Test]
    public async Task GitCacheEntry_roundtrips_host_and_version() {
        var entry = new GitCacheEntry {
            RemoteUrl = "git@gitlab.com:group/project.git",
            Owner = "group", RepoName = "project", Host = "gitlab.com",
            SchemaVersion = RepositoryDetection.CacheSchemaVersion, CachedAt = DateTimeOffset.UnixEpoch
        };
        var json = JsonSerializer.Serialize(entry, CapacitorJsonContext.Default.GitCacheEntry);
        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.GitCacheEntry);
        await Assert.That(back!.Host).IsEqualTo("gitlab.com");
        await Assert.That(back.SchemaVersion).IsEqualTo(RepositoryDetection.CacheSchemaVersion);
    }

    [Test]
    public async Task Detects_gitlab_repo_base_info_without_glab() {
        var repo = MakeTempRepo("git@gitlab.com:group/project.git");
        try {
            var payload = await RepositoryDetection.DetectRepositoryAsync(repo);

            await Assert.That(payload).IsNotNull();
            await Assert.That(payload!.Owner).IsEqualTo("group");
            await Assert.That(payload.RepoName).IsEqualTo("project");
            await Assert.That(payload.Host).IsEqualTo("gitlab.com");
            // No glab installed/authenticated in CI → PR fields stay null (best-effort).
        } finally {
            Directory.Delete(repo, true);
        }
    }

    // Mirrors RepoMatcherTests.MakeTempRepo: creates a throwaway git repo with a
    // controlled origin remote so router/detector dispatch can be exercised end-to-end.
    static string MakeTempRepo(string originUrl) {
        var root = Path.Combine(Path.GetTempPath(), "kcap-repo-detect-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        RunGit(root, "init", "-q");
        RunGit(root, "remote", "add", "origin", originUrl);

        return root;
    }

    static void RunGit(string cwd, params string[] args) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        if (proc.ExitCode != 0) {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {proc.StandardError.ReadToEnd()}");
        }
    }
}
