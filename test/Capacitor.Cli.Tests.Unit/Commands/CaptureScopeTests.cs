using System.Collections.Frozen;
using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Capture scope is decided in one place for every import source. These cover that place.
/// </summary>
public class CaptureScopeTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    static ImportCommand.SessionClassification Session(
            string sessionId, string? cwd,
            ImportCommand.ClassificationStatus status = ImportCommand.ClassificationStatus.New,
            string encodedCwd = "") => new() {
        SessionId  = sessionId,
        FilePath   = "",
        EncodedCwd = encodedCwd,
        Meta       = new SessionMetadata { SessionId = sessionId, Cwd = cwd },
        Status     = status,
        Vendor     = HarnessId.Claude,
    };

    CaptureScope Scope(
            string[]? allowedPaths = null, string[]? excludedPaths = null,
            string[]? allowedRepos = null, string[]? excludedRepos = null,
            Func<string, Task<RepositoryPayload?>>? repoDetector = null) =>
        new(new GitProviderRouter(), Config.Root, Home,
            allowedPaths, excludedPaths, allowedRepos, excludedRepos, TimeProvider.System, repoDetector);

    static Func<string, Task<RepositoryPayload?>> Detects(string owner, string repoName) =>
        _ => Task.FromResult<RepositoryPayload?>(new RepositoryPayload { Owner = owner, RepoName = repoName });

    static readonly IReadOnlyDictionary<string, string> NoResolved = FrozenDictionary<string, string>.Empty;

    [Test]
    public async Task Configured_is_false_when_the_profile_scopes_nothing() {
        await Assert.That(Scope().Configured).IsFalse();
        await Assert.That(Scope(allowedPaths: ["/x"]).Configured).IsTrue();
        await Assert.That(Scope(excludedRepos: ["o/r"]).Configured).IsTrue();
    }

    [Test]
    public async Task Excluded_repo_is_tagged_on_a_new_session() {
        var result = await Scope(excludedRepos: ["acme/secret"], repoDetector: Detects("acme", "secret"))
            .ApplyAsync([Session("s1", "/tmp/anywhere")], NoResolved);

        await Assert.That(result[0].ExcludedRepoKeys).IsEquivalentTo(["acme/secret"]);
        await Assert.That(result[0].OutsideAllowlist).IsFalse();
    }

    [Test]
    public async Task A_settled_session_is_stamped_too() {
        // An already-loaded session still reaches ImportSessionAsync to re-assert its lifecycle
        // hooks, so the verdict has to be on it or an ignored directory gets POSTed again. Whether
        // it is worth a prompt is ImportCommand's call, not this one's.
        var result = await Scope(excludedRepos: ["acme/secret"], repoDetector: Detects("acme", "secret"))
            .ApplyAsync([Session("s1", "/tmp/anywhere", ImportCommand.ClassificationStatus.AlreadyLoaded)], NoResolved);

        await Assert.That(result[0].ExcludedRepoKeys).IsEquivalentTo(["acme/secret"]);
    }

    [Test]
    public async Task A_session_whose_probe_failed_is_stamped() {
        // Otherwise an ignored session counts as a failure while the server is down and as
        // excluded once it is back.
        var result = await Scope(excludedPaths: ["/tmp/capturescope/secret"])
            .ApplyAsync([Session("s1", "/tmp/capturescope/secret/x", ImportCommand.ClassificationStatus.ProbeError)], NoResolved);

        await Assert.That(result[0].ExcludedPathKey)
            .IsEqualTo(PathExclusion.Normalize("/tmp/capturescope/secret", Home));
    }

    [Test]
    public async Task A_too_short_session_is_not_stamped() {
        var result = await Scope(excludedPaths: ["/tmp/capturescope/secret"])
            .ApplyAsync([Session("s1", "/tmp/capturescope/secret/x", ImportCommand.ClassificationStatus.TooShort)], NoResolved);

        await Assert.That(result[0].ExcludedPathKey).IsNull();
    }

    [Test]
    public async Task A_cwd_decoded_from_the_project_dir_name_is_placed() {
        // The Claude and Codex branch: no cwd on Meta, only the encoded project directory.
        // Hyphen-free segments deliberately — DecodeCwdFromDirName maps every '-' back to '/', so a
        // hyphen anywhere in the real path decodes to a different one, and this would be pinning
        // that defect instead of the fallback.
        var result = await Scope(excludedPaths: ["/tmp/capturescope/secret"])
            .ApplyAsync([Session("s1", cwd: null, encodedCwd: "-tmp-capturescope-secret-project")], NoResolved);

        await Assert.That(result[0].ExcludedPathKey)
            .IsEqualTo(PathExclusion.Normalize("/tmp/capturescope/secret", Home));
    }

    [Test]
    public async Task A_detector_that_throws_leaves_the_session_unplaceable() {
        var result = await Scope(allowedRepos: ["acme/widgets"],
                                 repoDetector: _ => throw new InvalidOperationException("git exploded"))
            .ApplyAsync([Session("s1", "/tmp/anywhere")], NoResolved);

        await Assert.That(result[0].OutsideAllowlist).IsTrue();
    }

    // The ordinary miss: a cwd that is simply not in a repository. The detector returns null
    // rather than throwing, and that null has to survive the warm-up cache to reach the verdict.
    [Test]
    public async Task A_detector_that_finds_no_repository_leaves_the_session_unplaceable() {
        var result = await Scope(allowedRepos: ["acme/widgets"],
                                 repoDetector: _ => Task.FromResult<RepositoryPayload?>(null))
            .ApplyAsync([Session("s1", "/tmp/not-a-repo")], NoResolved);

        await Assert.That(result[0].OutsideAllowlist).IsTrue();
    }

    // One unresolvable cwd is a verdict about that session, not an outcome for the run: the
    // sessions warmed alongside it still get theirs.
    [Test]
    public async Task An_unresolvable_cwd_does_not_stop_the_sessions_beside_it() {
        var result = await Scope(
                allowedRepos: ["acme/widgets"],
                repoDetector: cwd => Task.FromResult<RepositoryPayload?>(
                    cwd == "/tmp/widgets" ? new RepositoryPayload { Owner = "acme", RepoName = "widgets" } : null))
            .ApplyAsync([Session("unplaceable", "/tmp/not-a-repo"), Session("admitted", "/tmp/widgets")], NoResolved);

        await Assert.That(result[0].OutsideAllowlist).IsTrue();
        await Assert.That(result[1].OutsideAllowlist).IsFalse();
    }

    [Test]
    public async Task Excluded_path_is_tagged_with_the_normalized_entry() {
        using var tmp = new TempDir();
        var       dir = tmp.CreateDir("secret");

        var result = await Scope(excludedPaths: [dir]).ApplyAsync([Session("s1", dir.PathTo("project"))], NoResolved);

        await Assert.That(result[0].ExcludedPathKey).IsEqualTo(PathExclusion.Normalize(dir, Home));
    }

    [Test]
    public async Task A_session_that_reaches_nothing_is_not_stamped() {
        using var tmp = new TempDir();
        var       dir = tmp.CreateDir("secret");

        var result = await Scope(excludedPaths: [dir]).ApplyAsync(
            [
                Session("short", dir.PathTo("project"), ImportCommand.ClassificationStatus.TooShort),
                Session("internal", dir.PathTo("project"), ImportCommand.ClassificationStatus.InternalSubSession),
            ],
            NoResolved);

        await Assert.That(result[0].ExcludedPathKey).IsNull();
        await Assert.That(result[1].ExcludedPathKey).IsNull();
    }

    [Test]
    public async Task A_cwd_outside_allowed_paths_is_outside_the_allowlist() {
        using var tmp     = new TempDir();
        var       allowed = tmp.CreateDir("dev");
        var       other   = tmp.CreateDir("personal");

        var result = await Scope(allowedPaths: [allowed])
            .ApplyAsync([Session("in", allowed.PathTo("work")), Session("out", other.PathTo("diary"))], NoResolved);

        await Assert.That(result[0].OutsideAllowlist).IsFalse();
        await Assert.That(result[1].OutsideAllowlist).IsTrue();
    }

    [Test]
    public async Task A_repo_off_allowed_repos_is_outside_the_allowlist() {
        var result = await Scope(allowedRepos: ["acme/widgets"], repoDetector: Detects("someone", "personal"))
            .ApplyAsync([Session("s1", "/tmp/anywhere")], NoResolved);

        await Assert.That(result[0].OutsideAllowlist).IsTrue();
    }

    [Test]
    public async Task An_unresolvable_session_is_outside_a_configured_allowlist() {
        // No cwd at all, so neither gate can place it — and unplaceable is outside.
        var result = await Scope(allowedPaths: ["/tmp/dev"]).ApplyAsync([Session("s1", cwd: null)], NoResolved);

        await Assert.That(result[0].OutsideAllowlist).IsTrue();
    }

    [Test]
    public async Task The_denylist_still_subtracts_inside_an_allowed_root() {
        using var tmp     = new TempDir();
        var       allowed = tmp.CreateDir("dev");
        var       denied  = allowed.CreateDir("client-x");

        var result = await Scope(allowedPaths: [allowed], excludedPaths: [denied])
            .ApplyAsync([Session("in", allowed.PathTo("work")), Session("out", denied.PathTo("repo"))], NoResolved);

        await Assert.That(result[0].OutsideAllowlist).IsFalse();
        await Assert.That(result[0].ExcludedPathKey).IsNull();
        await Assert.That(result[1].ExcludedPathKey).IsEqualTo(PathExclusion.Normalize(denied, Home));
    }

    [Test]
    public async Task Repo_detection_runs_once_per_cwd_across_every_source() {
        // One cache for every source: sessions from different vendors in one directory cost a
        // single detection between them.
        var calls = 0;

        var scope = Scope(excludedRepos: ["o/r"], repoDetector: _ => {
            calls++;

            return Task.FromResult<RepositoryPayload?>(new RepositoryPayload { Owner = "o", RepoName = "r" });
        });

        var claude = Session("a", "/tmp/shared");
        var cursor = Session("b", "/tmp/shared") with { Vendor = HarnessId.Cursor };
        var codex  = Session("c", "/tmp/shared") with { Vendor = HarnessId.Codex };

        await scope.ApplyAsync([claude, cursor, codex], NoResolved);

        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task Repo_detection_is_skipped_when_no_repo_list_is_configured() {
        var calls = 0;

        var scope = Scope(excludedPaths: ["/tmp/secret"], repoDetector: _ => {
            calls++;

            return Task.FromResult<RepositoryPayload?>(null);
        });

        await scope.ApplyAsync([Session("s1", "/tmp/anywhere")], NoResolved);

        await Assert.That(calls).IsEqualTo(0);
    }

    // A deleted worktree's recorded cwd resolves to no repo. The scope reaches the repo through
    // cwd_remap or the dot-worktree strip, and the repo lists have to judge that same cwd.
    [Test]
    public async Task A_session_remapped_into_an_allowed_repo_is_admitted() {
        var (repo, cwd, resolved) = await ResolveDeletedWorktree("worktrees", "ai-1");

        var result = await Scope(allowedRepos: ["acme/widgets"], repoDetector: DetectsAt(repo))
            .ApplyAsync([Session("s1", cwd)], resolved);

        await Assert.That(result[0].OutsideAllowlist).IsFalse();
    }

    [Test]
    public async Task A_session_remapped_into_an_excluded_repo_is_excluded() {
        var (repo, cwd, resolved) = await ResolveDeletedWorktree("worktrees", "ai-1");

        var result = await Scope(excludedRepos: ["acme/widgets"], repoDetector: DetectsAt(repo))
            .ApplyAsync([Session("s1", cwd)], resolved);

        await Assert.That(result[0].ExcludedRepoKeys).IsEquivalentTo(["acme/widgets"]);
    }

    [Test]
    public async Task A_session_from_a_stripped_dot_worktree_is_admitted_to_its_repo() {
        var (repo, cwd, resolved) = await ResolveDeletedWorktree(".capacitor", "worktrees", "agent-1");

        var result = await Scope(allowedRepos: ["acme/widgets"], repoDetector: DetectsAt(repo))
            .ApplyAsync([Session("s1", cwd)], resolved);

        await Assert.That(result[0].OutsideAllowlist).IsFalse();
    }

    [Test]
    public async Task A_session_from_a_stripped_dot_worktree_of_an_excluded_repo_is_excluded() {
        var (repo, cwd, resolved) = await ResolveDeletedWorktree(".capacitor", "worktrees", "agent-1");

        var result = await Scope(excludedRepos: ["acme/widgets"], repoDetector: DetectsAt(repo))
            .ApplyAsync([Session("s1", cwd)], resolved);

        await Assert.That(result[0].ExcludedRepoKeys).IsEquivalentTo(["acme/widgets"]);
    }

    // The transcript still carries the recorded path, so a remap that lands outside an excluded
    // path must not lift the exclusion.
    [Test]
    public async Task A_remap_out_of_an_excluded_path_does_not_lift_the_exclusion() {
        var (repo, cwd, resolved) = await ResolveDeletedWorktree("worktrees", "ai-1");
        var excluded              = Path.Combine(repo, "worktrees");

        var result = await Scope(excludedPaths: [excluded])
            .ApplyAsync([Session("s1", cwd)], resolved);

        await Assert.That(result[0].ExcludedPathKey).IsEqualTo(PathExclusion.Normalize(excluded, Home));
    }

    // A remap applies whether or not its source still exists, so a checkout of an excluded repo can
    // be remapped onto one that is not. The transcript still carries the recorded path.
    [Test]
    public async Task A_remap_away_from_an_excluded_repo_does_not_lift_the_exclusion() {
        var scope = Scope(excludedRepos: ["acme/secret"], repoDetector: cwd => Task.FromResult<RepositoryPayload?>(
            new RepositoryPayload { Owner = "acme", RepoName = cwd == "/tmp/old" ? "secret" : "public" }));

        var result = await scope.ApplyAsync(
            [Session("s1", "/tmp/old")], new Dictionary<string, string> { ["s1"] = "/tmp/new" });

        await Assert.That(result[0].ExcludedRepoKeys).IsEquivalentTo(["acme/secret"]);
    }

    // Opting into one excluded repo must not admit a session that also maps to another.
    [Test]
    public async Task A_remap_between_two_excluded_repos_keeps_both() {
        var scope = Scope(excludedRepos: ["acme/secret", "acme/public"], repoDetector: cwd => Task.FromResult<RepositoryPayload?>(
            new RepositoryPayload { Owner = "acme", RepoName = cwd == "/tmp/old" ? "secret" : "public" }));

        var result = await scope.ApplyAsync(
            [Session("s1", "/tmp/old")], new Dictionary<string, string> { ["s1"] = "/tmp/new" });

        await Assert.That(result[0].ExcludedRepoKeys).IsEquivalentTo(["acme/public", "acme/secret"]);
    }

    // The allow-side twin of the path exclusion above, kept conservative: a remap into an allowed
    // path still prompts, since the recorded path is the one that would be uploaded.
    [Test]
    public async Task A_remap_into_an_allowed_path_does_not_admit_the_session() {
        var result = await Scope(allowedPaths: ["/tmp/dev"])
            .ApplyAsync([Session("s1", "/tmp/old/x")], new Dictionary<string, string> { ["s1"] = "/tmp/dev/x" });

        await Assert.That(result[0].OutsideAllowlist).IsTrue();
    }

    static Func<string, Task<RepositoryPayload?>> DetectsAt(string repo) =>
        cwd => Task.FromResult<RepositoryPayload?>(
            cwd == repo ? new RepositoryPayload { Owner = "acme", RepoName = "widgets" } : null);

    /// <summary>
    /// A repo that exists and a session recorded in <paramref name="worktree"/> under it, which does
    /// not. A wildcard remap folds <c>worktrees/*</c> back onto the repo; a dot-worktree is
    /// stripped without one.
    /// </summary>
    async Task<(string Repo, string Cwd, IReadOnlyDictionary<string, string> Resolved)> ResolveDeletedWorktree(
            params string[] worktree) {
        string repo       = Tmp.CreateDir("widgets");
        var    cwd        = Tmp.PathTo(["widgets", .. worktree]);
        var    transcript = Tmp.CreateFile("s1.jsonl",
            [$$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":{{{JsonSerializer.Serialize(cwd)}}},"message":{"content":"hello"}}"""]);

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);

        await new ImportCommand(Config.Root, Resolutions.None(Config.Root), Home, TestHarnesses.Under(Home),
                                new FixedCapacitorHttpClient(), router: new GitProviderRouter(), time: TimeProvider.System, console: new TestImportTerminal().Console)
            .ResolveTranscriptReposAsync(
                [("s1", transcript, "")],
                codex: false,
                new TestImportTerminal().Display(),
                cwdRemap: [new CwdRemap { From = Path.Combine(repo, "worktrees", "*"), To = repo }],
                sessionCwds: resolved
            );

        return (repo, cwd, resolved);
    }

    [Test]
    public async Task ClassifyContext_carries_no_scope_so_a_source_cannot_filter_on_its_own() {
        // The guard that makes the central gate hold: an import source has nothing to consult, so
        // it cannot quietly skip the scope.
        var names = typeof(ClassifyContext).GetProperties().Select(p => p.Name).ToArray();

        await Assert.That(names).DoesNotContain("ExcludedPaths");
        await Assert.That(names).DoesNotContain("ExcludedRepos");
        await Assert.That(names).DoesNotContain("AllowedPaths");
        await Assert.That(names).DoesNotContain("AllowedRepos");
    }
}
