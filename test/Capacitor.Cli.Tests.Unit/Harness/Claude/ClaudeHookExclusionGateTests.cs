using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

/// <summary>
/// Covers the repo/path exclusion gate (<see cref="ClaudeHookCommand.IsSessionExcludedAsync"/>)
/// that guards the permission-request watcher self-heal — so a permission prompt in an
/// excluded project does not start a transcript-uploading watcher that session-start
/// intentionally skipped.
/// </summary>
public class ClaudeHookExclusionGateTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    readonly HookClock _clock = new(TimeProvider.System);

    // Instance, not static: the hook writes under the config dir (the repo-detection cache,
    // the lease store), so it must be handed this test's own root — which a static helper
    // cannot see, TUnit injecting it after construction.
    ClaudeHookCommand Hook() =>
        new(Config.Root, Resolutions.None(Config.Root), _clock, Home, TestHarnesses.Under(Home),
            HostedAgent.Terminal, new FixedCapacitorHttpClient(),
            TestWatchers.For(Config.Root, Resolutions.None(Config.Root), new FixedCapacitorHttpClient()),
            SystemProcessStarter.Instance, router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory));

    // The gate reads the budget only for the repo probe, which these path-exclusion payloads never
    // reach; what they vary is the profile, not the clock. Any live ceiling will do.
    HookBudget Budget() => _clock.Budget(TimeSpan.FromSeconds(5));

    static string Body(string cwd) => new JsonObject { ["cwd"] = cwd }.ToJsonString();

    [Test]
    public async Task ExcludedPath_ReturnsTrue() {
        using var tmp = new TempDir();
        var excludedDir = tmp.CreateDir("excl");

        var profile  = new Profile { ExcludedPaths = [excludedDir] };
        var body     = Body(excludedDir.PathTo("project"));

        var excluded = await Hook().IsSessionExcludedAsync(profile, body, Budget());

        await Assert.That(excluded).IsTrue();
    }

    [Test]
    public async Task NonExcludedPath_ReturnsFalse() {
        using var tmp = new TempDir();
        var excludedDir = tmp.CreateDir("excl");
        var otherDir    = tmp.CreateDir("other");

        var profile  = new Profile { ExcludedPaths = [excludedDir] };
        var body     = Body(otherDir.PathTo("project"));

        var excluded = await Hook().IsSessionExcludedAsync(profile, body, Budget());

        await Assert.That(excluded).IsFalse();
    }

    [Test]
    public async Task NullProfile_ReturnsFalse() {
        var excluded = await Hook().IsSessionExcludedAsync(profile: null, Body("/tmp/anything"), Budget());

        await Assert.That(excluded).IsFalse();
    }

    [Test]
    public async Task ProfileWithoutExclusions_ReturnsFalse() {
        var excluded = await Hook().IsSessionExcludedAsync(new Profile(), Body("/tmp/anything"), Budget());

        await Assert.That(excluded).IsFalse();
    }

    [Test]
    public async Task AllowedPath_InsideRoot_ReturnsFalse() {
        using var tmp        = new TempDir();
        var       allowedDir = tmp.CreateDir("dev");

        var profile  = new Profile { AllowedPaths = [allowedDir] };
        var excluded = await Hook().IsSessionExcludedAsync(profile, Body(allowedDir.PathTo("project")), Budget());

        await Assert.That(excluded).IsFalse();
    }

    [Test]
    public async Task AllowedPath_OutsideRoot_ReturnsTrue() {
        using var tmp        = new TempDir();
        var       allowedDir = tmp.CreateDir("dev");
        var       otherDir   = tmp.CreateDir("personal");

        var profile  = new Profile { AllowedPaths = [allowedDir] };
        var excluded = await Hook().IsSessionExcludedAsync(profile, Body(otherDir.PathTo("diary")), Budget());

        await Assert.That(excluded).IsTrue();
    }

    [Test]
    public async Task ExcludedPath_InsideAllowedRoot_ReturnsTrue() {
        // The denylist keeps subtracting inside an admitted root.
        using var tmp        = new TempDir();
        var       allowedDir = tmp.CreateDir("dev");
        var       deniedDir  = allowedDir.CreateDir("client-x");

        var profile  = new Profile { AllowedPaths = [allowedDir], ExcludedPaths = [deniedDir] };
        var excluded = await Hook().IsSessionExcludedAsync(profile, Body(deniedDir.PathTo("repo")), Budget());

        await Assert.That(excluded).IsTrue();
    }

    [Test]
    public async Task AllowedPaths_WithUnreadableBody_ReturnsTrue() {
        // No cwd to place against a configured allowlist — the session is not admitted. The
        // denylist-only case below keeps the opposite answer on the same body.
        using var tmp        = new TempDir();
        var       allowedDir = tmp.CreateDir("dev");

        var profile  = new Profile { AllowedPaths = [allowedDir] };
        var excluded = await Hook().IsSessionExcludedAsync(profile, "not json at all", Budget());

        await Assert.That(excluded).IsTrue();
    }

    [Test]
    public async Task ExcludedPathsOnly_WithUnreadableBody_ReturnsFalse() {
        using var tmp         = new TempDir();
        var       excludedDir = tmp.CreateDir("excl");

        var profile  = new Profile { ExcludedPaths = [excludedDir] };
        var excluded = await Hook().IsSessionExcludedAsync(profile, "not json at all", Budget());

        await Assert.That(excluded).IsFalse();
    }

    [Test]
    public async Task EmptyAllowedPaths_AdmitsEverything() {
        var excluded = await Hook().IsSessionExcludedAsync(new Profile { AllowedPaths = [] }, Body("/tmp/anything"), Budget());

        await Assert.That(excluded).IsFalse();
    }

    // The repository block is what enrichment stamps onto a real payload, so these match without
    // paying for git detection. The unresolvable cases below take the cwd-detection path instead.
    static string BodyWithRepo(string owner, string repoName) =>
        new JsonObject {
            ["cwd"]        = "/tmp/anything",
            ["repository"] = new JsonObject { ["owner"] = owner, ["repo_name"] = repoName },
        }.ToJsonString();

    [Test]
    public async Task AllowedRepo_OnTheList_ReturnsFalse() {
        var profile  = new Profile { AllowedRepos = ["acme/widgets"] };
        var excluded = await Hook().IsSessionExcludedAsync(profile, BodyWithRepo("acme", "widgets"), Budget());

        await Assert.That(excluded).IsFalse();
    }

    [Test]
    public async Task AllowedRepo_NotOnTheList_ReturnsTrue() {
        var profile  = new Profile { AllowedRepos = ["acme/widgets"] };
        var excluded = await Hook().IsSessionExcludedAsync(profile, BodyWithRepo("someone", "personal"), Budget());

        await Assert.That(excluded).IsTrue();
    }

    [Test]
    public async Task AllowedRepo_MatchesCaseInsensitively() {
        var profile  = new Profile { AllowedRepos = ["ACME/Widgets"] };
        var excluded = await Hook().IsSessionExcludedAsync(profile, BodyWithRepo("acme", "widgets"), Budget());

        await Assert.That(excluded).IsFalse();
    }

    [Test]
    public async Task ExcludedRepo_OnTheAllowList_StillReturnsTrue() {
        // The denylist keeps subtracting inside an admitted repo, as it does for paths.
        var profile = new Profile { AllowedRepos = ["acme/widgets"], ExcludedRepos = ["acme/widgets"] };

        var excluded = await Hook().IsSessionExcludedAsync(profile, BodyWithRepo("acme", "widgets"), Budget());

        await Assert.That(excluded).IsTrue();
    }

    [Test]
    public async Task AllowedRepos_WithUnresolvableRepo_ReturnsTrue() {
        // A cwd that is not in a repo at all: nothing to admit it by, so it is not captured.
        using var tmp = new TempDir();

        var profile  = new Profile { AllowedRepos = ["acme/widgets"] };
        var excluded = await Hook().IsSessionExcludedAsync(profile, Body(tmp.Path), Budget());

        await Assert.That(excluded).IsTrue();
    }

    [Test]
    public async Task ExcludedReposOnly_WithUnresolvableRepo_ReturnsFalse() {
        using var tmp = new TempDir();

        var profile  = new Profile { ExcludedRepos = ["acme/widgets"] };
        var excluded = await Hook().IsSessionExcludedAsync(profile, Body(tmp.Path), Budget());

        await Assert.That(excluded).IsFalse();
    }

    [Test]
    public async Task EmptyAllowedRepos_AdmitsEverything() {
        var excluded = await Hook().IsSessionExcludedAsync(
            new Profile { AllowedRepos = [] }, BodyWithRepo("someone", "personal"), Budget());

        await Assert.That(excluded).IsFalse();
    }

    [Test]
    public async Task AllowedRepos_AndAllowedPaths_BothMustAdmit() {
        // Independent gates, the dual of the two denylists: either one can keep a session out.
        using var tmp        = new TempDir();
        var       allowedDir = tmp.CreateDir("dev");
        var       otherDir   = tmp.CreateDir("personal");

        var profile = new Profile { AllowedRepos = ["acme/widgets"], AllowedPaths = [allowedDir] };

        var insideBoth = new JsonObject {
            ["cwd"]        = allowedDir.PathTo("widgets"),
            ["repository"] = new JsonObject { ["owner"] = "acme", ["repo_name"] = "widgets" },
        }.ToJsonString();

        var rightRepoWrongPath = new JsonObject {
            ["cwd"]        = otherDir.PathTo("widgets"),
            ["repository"] = new JsonObject { ["owner"] = "acme", ["repo_name"] = "widgets" },
        }.ToJsonString();

        await Assert.That(await Hook().IsSessionExcludedAsync(profile, insideBoth, Budget())).IsFalse();
        await Assert.That(await Hook().IsSessionExcludedAsync(profile, rightRepoWrongPath, Budget())).IsTrue();
    }

    // The Resolved half of the verdict is what keeps a transient failure from being persisted as a
    // DisabledSessions marker, so it is pinned separately from the OutOfScope half.
    [Test]
    public async Task UnresolvableRepo_UnderAllowlist_IsOutOfScope_ButNotResolved() {
        using var tmp = new TempDir();

        var verdict = await RepoExclusion.IsOutOfScopeAsync(
            new GitProviderRouter(), Config.Root, Body(tmp.Path), ["acme/widgets"], null);

        await Assert.That(verdict.OutOfScope).IsTrue();
        await Assert.That(verdict.Resolved).IsFalse();
    }

    [Test]
    public async Task RejectedRepo_UnderAllowlist_IsOutOfScope_AndResolved() {
        var verdict = await RepoExclusion.IsOutOfScopeAsync(
            new GitProviderRouter(), Config.Root, BodyWithRepo("someone", "personal"), ["acme/widgets"], null);

        await Assert.That(verdict.OutOfScope).IsTrue();
        await Assert.That(verdict.Resolved).IsTrue();
    }

    [Test]
    public async Task UnresolvableRepo_WithDenylistOnly_StaysInScope() {
        using var tmp = new TempDir();

        var verdict = await RepoExclusion.IsOutOfScopeAsync(
            new GitProviderRouter(), Config.Root, Body(tmp.Path), null, ["acme/widgets"]);

        await Assert.That(verdict.OutOfScope).IsFalse();
    }
}

