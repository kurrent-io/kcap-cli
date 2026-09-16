using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="AgentCaptureScope"/> — the list arithmetic on its own, with the
/// orchestrator wiring covered by <see cref="AgentCaptureScopeLaunchTests"/>. The direction that
/// matters throughout is closed: an origin the rules cannot place must not be admitted by a
/// configured allowlist, because admitting what it cannot identify is an allowlist that restricts
/// nothing.
/// </summary>
public class AgentCaptureScopeTests {
    [TempHome] public required TempHome Home { get; init; }

    static Profile WithScope(
            string[]? allowedPaths = null, string[]? excludedPaths = null,
            string[]? allowedRepos = null, string[]? excludedRepos = null)
        => new() {
            AllowedPaths  = allowedPaths  ?? [],
            ExcludedPaths = excludedPaths ?? [],
            AllowedRepos  = allowedRepos  ?? [],
            ExcludedRepos = excludedRepos ?? [],
        };

    [Test]
    public async Task A_profile_with_no_lists_scopes_nothing() {
        await Assert.That(AgentCaptureScope.Configured(null)).IsFalse();
        await Assert.That(AgentCaptureScope.Configured(WithScope())).IsFalse();
    }

    [Test]
    public async Task Any_one_list_makes_the_profile_configured() {
        await Assert.That(AgentCaptureScope.Configured(WithScope(allowedPaths:  ["/work"]))).IsTrue();
        await Assert.That(AgentCaptureScope.Configured(WithScope(excludedPaths: ["/work"]))).IsTrue();
        await Assert.That(AgentCaptureScope.Configured(WithScope(allowedRepos:  ["acme/widgets"]))).IsTrue();
        await Assert.That(AgentCaptureScope.Configured(WithScope(excludedRepos: ["acme/widgets"]))).IsTrue();
    }

    [Test]
    public async Task An_origin_under_an_allowed_root_is_in_scope() {
        var root = Home.Path;

        await Assert.That(AgentCaptureScope.IsOutOfScope(
            Path.Combine(root, "widgets"), WithScope(allowedPaths: [root]), Home)).IsFalse();
    }

    [Test]
    public async Task An_origin_outside_every_allowed_root_is_out_of_scope() {
        await Assert.That(AgentCaptureScope.IsOutOfScope(
            "/somewhere/else", WithScope(allowedPaths: [Home.Path]), Home)).IsTrue();
    }

    // The denylist subtracts within the allowlist, so a client subtree under an allowed root is
    // still refused.
    [Test]
    public async Task An_excluded_subtree_is_out_of_scope_even_under_an_allowed_root() {
        var root  = Home.Path;
        var inner = Path.Combine(root, "client-x");

        await Assert.That(AgentCaptureScope.IsOutOfScope(
            inner, WithScope(allowedPaths: [root], excludedPaths: [inner]), Home)).IsTrue();
    }

    // A launch whose origin the daemon cannot name is the case the whole gate exists for: it must
    // not slip past a configured allowlist just because there is nothing to compare.
    [Test]
    public async Task An_origin_that_is_null_is_out_of_scope_against_an_allowlist() {
        await Assert.That(AgentCaptureScope.IsOutOfScope(
            null, WithScope(allowedPaths: [Home.Path]), Home)).IsTrue();
    }

    [Test]
    public async Task An_origin_that_is_null_is_in_scope_when_only_a_denylist_is_set() {
        await Assert.That(AgentCaptureScope.IsOutOfScope(
            null, WithScope(excludedPaths: ["/work/secrets"]), Home)).IsFalse();
    }

    // A temp directory is not a git repository, so no owner/repo key resolves — which an
    // allowed_repos list has to read as "not one of mine".
    [Test]
    public async Task A_non_repository_origin_is_out_of_scope_against_a_repo_allowlist() {
        await Assert.That(AgentCaptureScope.IsOutOfScope(
            Home.Path, WithScope(allowedRepos: ["acme/widgets"]), Home)).IsTrue();
    }

    // Repo lists are only consulted when configured; with none set an unresolvable repo is
    // irrelevant and the path rules decide alone.
    [Test]
    public async Task A_non_repository_origin_is_in_scope_when_no_repo_list_is_set() {
        await Assert.That(AgentCaptureScope.IsOutOfScope(
            Home.Path, WithScope(allowedPaths: [Home.Path]), Home)).IsFalse();
    }
}
