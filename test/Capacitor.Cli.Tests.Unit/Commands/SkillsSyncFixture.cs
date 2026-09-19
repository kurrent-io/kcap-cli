using System.Security.Cryptography;
using System.Text;
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
        // No override consulted: these tests stage the global copies under the plain home layout.
        LegacyRoots = new LegacySkillsRoots(new UserHome(Home), kiroHome: null, geminiCliHome: null);

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
            MachineAuth.None, LegacyRoots, time);
    }

    /// <summary>The global trees a retirement may delete from, rooted where these tests stage the
    /// copies a previous release left behind.</summary>
    public LegacySkillsRoots LegacyRoots { get; }

    public SkillsCommand Command { get; }
    public StubSkillsApi Api     { get; }
    public ConfigRoot    Config  { get; }

    /// <summary>The checkout the sync runs in — the anchor every destination is relative to.</summary>
    public string Anchor { get; }

    /// <summary>This worktree's own git directory, where its ledger lives.</summary>
    public string GitDir { get; }

    /// <summary>The user home the global trees hang off — the only roots a legacy retirement may
    /// delete from.</summary>
    public string Home { get; }

    public string         RepoHash { get; }
    public string         RepoHome { get; }
    public SkillsIdentity Identity { get; }

    public SkillsTarget Target => SkillsCommand.Targets(LegacyRoots).Single(t => t.Key == TargetKey);

    public string LedgerPath => Path.Combine(GitDir, "kcap", "skills", TargetKey + ".json");

    public string SkillsRoot => Path.Combine(Anchor, ClaudePaths.RepoSkillsRelativePath);

    public string LegacyLedgerPath => Config.Path("skills", RepoHash, TargetKey, "manifest.json");

    public string SkillDir(string slug) => SkillsMaterializer.SkillDirFor(SkillsRoot, slug);

    public string SkillFile(string slug) => SkillsMaterializer.SkillFileFor(SkillDir(slug));

    public bool HasSkill(string slug) => Directory.Exists(SkillDir(slug));

    /// <summary>The saved ledger, refusing any row the validator would not admit — so every test
    /// that reads one also pins that no run saved an illegal combination.</summary>
    public SkillsLedger ReadLedger() {
        var ledger = SkillsLedgerFile.ReadQuietly(LedgerPath, SkillOrigin.Repository)
                  ?? throw new InvalidOperationException($"no ledger at {LedgerPath}");

        foreach (var row in ledger.Rows)
            if (SkillsLedgerValidation.Reject(row) is { } reason)
                throw new InvalidOperationException($"the run saved an illegal row for {row.Path}: {reason}");

        return ledger;
    }

    public IReadOnlyList<OwnedSkillRow> Rows() => ReadLedger().Rows;

    public OwnedSkillRow? RowFor(string slug) =>
        Rows().SingleOrDefault(r => PathComparison.Equal(r.Path, SkillDir(slug)));

    public void WriteLedger(SkillsLedger ledger) => SkillsLedgerFile.Save(LedgerPath, ledger);

    /// <summary>The ledger under the config root that owns user-global copies — the one migration
    /// retires, and the one whose existence alone adopts a target.</summary>
    public void WriteLegacyLedger(SkillsLedger ledger) => SkillsLedgerFile.Save(LegacyLedgerPath, ledger);

    /// <summary>The same ledger verbatim — for a test whose point is the shape on disk.</summary>
    public void WriteLegacyLedger(string json) =>
        new TempDirHandle(Path.GetDirectoryName(LegacyLedgerPath)!)
            .CreateFile(Path.GetFileName(LegacyLedgerPath), json);

    public SkillsLedger ReadLegacyLedger() =>
        SkillsLedgerFile.ReadQuietly(LegacyLedgerPath, SkillOrigin.Legacy)
        ?? throw new InvalidOperationException($"no legacy ledger at {LegacyLedgerPath}");

    /// <summary>A ledger owning the given rows under the current identity — the state a completed
    /// sync leaves, for a test that starts from one.</summary>
    public SkillsLedger Owning(params OwnedSkillRow[] rows) => new() {
        SyncedAt = Now.AddDays(-1), Identity = Identity, Exposure = ["claude"], Owned = rows,
    };

    /// <summary>Writes one skill and the row that owns it, exactly as a completed sync would — so
    /// the row reads as served rather than drifted.</summary>
    public OwnedSkillRow Materialize(SkillSnapshotItem item, string? anchor = null) {
        var rendered = SkillsRendering.RenderSkillFile(item);
        var at       = SkillDestination.For(Target, anchor ?? Anchor, item.Slug);

        new TempDirHandle(at.Path).CreateFile("SKILL.md", rendered);

        return Published(item, at, SkillsMaterializer.FileHash(rendered));
    }

    /// <summary>A row for a path nothing local materialized — a global copy this checkout owns
    /// without holding a copy of its own.</summary>
    public OwnedSkillRow Global(SkillSnapshotItem item, string path, string body) => new() {
        Path = path, Root = Path.GetDirectoryName(path)!, Origin = SkillOrigin.Legacy,
        State = OwnedSkillState.Published,
        Confirmed = new SkillReceipt {
            FileHash = SkillsMaterializer.FileHash(body), Document = SkillDocument.Of(item, RepoHome),
        },
    };

    public OwnedSkillRow Published(SkillSnapshotItem item, SkillDestination at, string fileHash) => new() {
        Path = at.Path, Root = at.Root, Anchor = at.Anchor, Origin = SkillOrigin.Repository,
        State = OwnedSkillState.Published,
        Confirmed = new SkillReceipt { FileHash = fileHash, Document = SkillDocument.Of(item, RepoHome) },
    };

    /// <summary>Blocks one destination so a run genuinely aborts part-way through its writes: a
    /// plain file where the directory has to be created makes the write throw, which ends the run
    /// after the ownership save and before any outcome is recorded.</summary>
    public string Block(string slug) {
        var dir = SkillDir(slug);

        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        File.WriteAllText(dir, "in the way");

        return dir;
    }

    public void Unblock(string slug) => File.Delete(SkillDir(slug));

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
