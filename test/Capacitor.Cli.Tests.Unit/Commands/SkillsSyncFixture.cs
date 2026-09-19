using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Skills;
using Capacitor.Cli.PrDetection;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>kcap skills sync</c> wired to one checkout, over a config root under the test's throwaway
/// directory and a stored credential whose subject claim is the account the sync records.
///
/// <para>Only Claude is detected, so exactly one target — <c>claude</c> — is adopted and a run
/// makes exactly one snapshot request. Repository detection is served from its own cache with no
/// host recorded, which is what keeps the provider probe (a <c>gh</c> round-trip) out of a suite
/// about skills.</para>
/// </summary>
sealed class SkillsSyncFixture {
    public const string TargetKey = "claude";
    public const string ServerUrl = "https://stub.kcap.test";

    public static readonly DateTimeOffset Now = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);

    public SkillsSyncFixture(
            TempDir tmp, string checkout, StubSkillsApi api,
            string subject = "signed-in-user", string owner = "acme", string repoName = "widgets") {
        Api      = api;
        Anchor   = checkout;
        GitDir   = GitRepository.ResolveGitDir(checkout)!;
        RepoHash = RepoHashHelper.ComputeRepoHash(owner, repoName);
        RepoHome = $"repo:{owner}/{repoName}";
        Config   = new ConfigRoot(tmp.CreateDir("config"));
        Identity = new SkillsIdentity(subject, ServerUrl);
        Home     = tmp.PathTo("home");

        SeedDetectionCache(tmp, checkout, owner, repoName);

        var time   = new FakeTimeProvider(Now);
        var tokens = AuthFixtures.NewTokenStore(Config, time: time);

        tokens.SaveAsync(ProfileName, new StoredTokens {
            AccessToken    = AccessTokens.ForSubject(subject),
            ExpiresAt      = Now.AddHours(1),
            GitHubUsername = "tester",
            ServerUrl      = ServerUrl,
        }).GetAwaiter().GetResult();

        Command = new SkillsCommand(
            Config, TestHarnesses.All(detected: [HarnessId.Claude]), api, new GitProviderRouter(),
            new WorkingDirectory(checkout), tokens, Resolutions.At(ServerUrl, Config),
            MachineAuth.None, new UserHome(Home), time);
    }

    public SkillsCommand Command { get; }
    public StubSkillsApi Api     { get; }
    public ConfigRoot    Config  { get; }

    /// <summary>The checkout the sync runs in — the anchor every destination is relative to.</summary>
    public string Anchor { get; }

    /// <summary>This worktree's own git directory, where its manifest lives.</summary>
    public string GitDir { get; }

    /// <summary>The user home the global trees hang off — the only roots a legacy retirement may
    /// delete from.</summary>
    public string Home { get; }

    public string         RepoHash { get; }
    public string         RepoHome { get; }
    public SkillsIdentity Identity { get; }

    public string ManifestPath => Path.Combine(GitDir, "kcap", "skills", TargetKey + ".json");

    public string SkillsRoot => Path.Combine(Anchor, ClaudePaths.RepoSkillsRelativePath);

    public string LegacyManifestPath => Config.Path("skills", RepoHash, TargetKey, "manifest.json");

    public string SkillDir(string slug) => SkillsMaterializer.SkillDirFor(SkillsRoot, slug);

    public string SkillFile(string slug) => SkillsMaterializer.SkillFileFor(SkillDir(slug));

    public bool HasSkill(string slug) => Directory.Exists(SkillDir(slug));

    public SkillsManifest ReadManifest() =>
        JsonSerializer.Deserialize(File.ReadAllText(ManifestPath), CapacitorJsonContext.Default.SkillsManifest)!;

    public void WriteManifest(SkillsManifest manifest) => Write(ManifestPath, Serialize(manifest));

    /// <summary>The ledger under the config root that owns user-global copies — the one migration
    /// retires, and the one whose existence alone adopts a target.</summary>
    public void WriteLegacyManifest(SkillsManifest manifest) =>
        Write(LegacyManifestPath, Serialize(manifest));

    /// <summary>The same ledger verbatim — for a test whose point is one that will not parse.
    /// </summary>
    public void WriteLegacyManifest(string json) => Write(LegacyManifestPath, json);

    /// <summary>Writes one skill and the manifest entry that owns it, exactly as a completed sync
    /// would — so the entry reads as served rather than drifted.</summary>
    public SkillsManifestEntry Materialize(SkillSnapshotItem item) {
        var rendered = SkillsSyncPlanner.RenderSkillFile(item);
        var dir      = SkillDir(item.Slug);

        Write(SkillsMaterializer.SkillFileFor(dir), rendered);

        return new SkillsManifestEntry {
            DocId       = item.DocId, Slug = item.Slug, Version = item.Version,
            ContentHash = item.ContentHash, Path = dir,
            FileHash    = SkillsMaterializer.FileHash(rendered),
            Home        = item.Home ?? RepoHome, Applicability = item.Applicability,
        };
    }

    /// <summary>One snapshot row. <paramref name="docId"/> is the server's stable key, so a rename
    /// keeps it and passes a new slug.</summary>
    public static SkillSnapshotItem Skill(
            string slug, Guid? docId = null, int version = 1, string? body = null,
            string? home = null, SkillApplicability? applicability = null) =>
        new() {
            DocId   = docId ?? DocIdFor(slug), Slug = slug, Title = slug.Replace('-', ' '),
            Body    = body ?? $"# {slug}\n\nWhat {slug} does.\n",
            Version = version, ContentHash = $"content-{slug}-v{version}",
            Home    = home, Applicability = applicability,
        };

    const string ProfileName = "default";

    static string Serialize(SkillsManifest manifest) =>
        JsonSerializer.Serialize(manifest, CapacitorJsonContext.Default.SkillsManifest);

    // The destinations come from production, so the directory a test needs is under whichever tree
    // the code under test chose; the helper is what creates the missing parents.
    static void Write(string path, string content) =>
        new TempDirHandle(Path.GetDirectoryName(path)!).CreateFile(Path.GetFileName(path), content);

    static Guid DocIdFor(string slug) => new(SHA256.HashData(Encoding.UTF8.GetBytes(slug)).AsSpan(0, 16));

    // No host recorded, so the PR-provider probe never runs. The schema version comes from the
    // production constant: a bump then fails here rather than quietly re-deriving through git.
    static void SeedDetectionCache(TempDir tmp, string cwd, string owner, string repoName) {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cwd)))[..16];

        tmp.CreateFile(["config", "cache", $"{key}.json"], $$"""
            {"user_name":"Test","user_email":"test@example.com","owner":"{{owner}}",
             "repo_name":"{{repoName}}","schema_version":{{RepositoryDetection.CacheSchemaVersion}},
             "cached_at":"{{Now:O}}"}
            """);
    }
}
