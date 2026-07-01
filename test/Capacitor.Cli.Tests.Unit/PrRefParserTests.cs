using Capacitor.Cli.Core.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class PrRefParserTests {
    [Test]
    public async Task Shorthand_form_parses() {
        var ok = PrRefParser.TryParse("kurrent-io/kcap-cli#101", out var owner, out var repo, out var pr);

        await Assert.That(ok).IsTrue();
        await Assert.That(owner).IsEqualTo("kurrent-io");
        await Assert.That(repo).IsEqualTo("kcap-cli");
        await Assert.That(pr).IsEqualTo(101);
    }

    [Test]
    public async Task Plain_url_parses() {
        var ok = PrRefParser.TryParse("https://github.com/kurrent-io/kcap-cli/pull/101", out var owner, out var repo, out var pr);

        await Assert.That(ok).IsTrue();
        await Assert.That(owner).IsEqualTo("kurrent-io");
        await Assert.That(repo).IsEqualTo("kcap-cli");
        await Assert.That(pr).IsEqualTo(101);
    }

    [Test]
    public async Task Url_with_query_parses() {
        var ok = PrRefParser.TryParse("https://github.com/kurrent-io/kcap-cli/pull/101?diff=split", out var owner, out var repo, out var pr);

        await Assert.That(ok).IsTrue();
        await Assert.That(owner).IsEqualTo("kurrent-io");
        await Assert.That(repo).IsEqualTo("kcap-cli");
        await Assert.That(pr).IsEqualTo(101);
    }

    [Test]
    public async Task Url_with_fragment_parses() {
        var ok = PrRefParser.TryParse("https://github.com/kurrent-io/kcap-cli/pull/101#issuecomment-12345", out var owner, out var repo, out var pr);

        await Assert.That(ok).IsTrue();
        await Assert.That(pr).IsEqualTo(101);
    }

    [Test]
    public async Task Url_with_trailing_path_parses() {
        var ok = PrRefParser.TryParse("https://github.com/kurrent-io/kcap-cli/pull/101/files", out var owner, out var repo, out var pr);

        await Assert.That(ok).IsTrue();
        await Assert.That(pr).IsEqualTo(101);
    }

    [Test]
    public async Task Input_with_surrounding_whitespace_is_trimmed() {
        var ok = PrRefParser.TryParse("  kurrent-io/kcap-cli#101  ", out var owner, out var repo, out var pr);

        await Assert.That(ok).IsTrue();
        await Assert.That(owner).IsEqualTo("kurrent-io");
        await Assert.That(repo).IsEqualTo("kcap-cli");
    }

    [Test]
    public async Task Shorthand_rejects_slash_in_repo() {
        // Without the fix this parsed `repo = "foo/bar"`, producing a malformed
        // API URL path segment when interpolated.
        var ok = PrRefParser.TryParse("owner/foo/bar#42", out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Empty_input_rejected() {
        await Assert.That(PrRefParser.TryParse("", out _, out _, out _)).IsFalse();
        await Assert.That(PrRefParser.TryParse("   ", out _, out _, out _)).IsFalse();
    }

    [Test]
    public async Task Github_url_on_enterprise_host_parses() {
        var ok = PrRefParser.TryParse("https://ghe.corp.com/team/app/pull/7", out var owner, out var repo, out var pr);
        await Assert.That(ok).IsTrue();
        await Assert.That(owner).IsEqualTo("team");
        await Assert.That(repo).IsEqualTo("app");
        await Assert.That(pr).IsEqualTo(7);
    }

    [Test]
    public async Task Gitlab_mr_url_parses() {
        var ok = PrRefParser.TryParse("https://gitlab.com/group/project/-/merge_requests/42", out var owner, out var repo, out var pr);
        await Assert.That(ok).IsTrue();
        await Assert.That(owner).IsEqualTo("group");
        await Assert.That(repo).IsEqualTo("project");
        await Assert.That(pr).IsEqualTo(42);
    }

    [Test]
    public async Task Gitlab_mr_url_with_browser_suffix_parses() {
        foreach (var suffix in new[] { "/diffs", "/commits", "?tab=1", "#note_9" }) {
            var ok = PrRefParser.TryParse($"https://gitlab.com/group/project/-/merge_requests/42{suffix}", out _, out _, out var pr);
            await Assert.That(ok).IsTrue();
            await Assert.That(pr).IsEqualTo(42);
        }
    }

    [Test]
    public async Task Gitlab_nested_group_mr_url_is_rejected_in_first_pass() {
        // Multi-segment namespace deferred (§3/§6b): server routes are /{owner}/{repo}.
        var ok = PrRefParser.TryParse("https://gitlab.com/group/sub/project/-/merge_requests/42", out _, out _, out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Garbage_input_rejected() {
        await Assert.That(PrRefParser.TryParse("not-a-pr-ref", out _, out _, out _)).IsFalse();
        // A GitHub-shaped /pull/ URL now parses on any host (see Github_url_on_enterprise_host_parses);
        // reject genuinely malformed refs instead.
        await Assert.That(PrRefParser.TryParse("https://gitlab.com/owner/repo/-/merge_requests/", out _, out _, out _)).IsFalse();
        await Assert.That(PrRefParser.TryParse("https://gitlab.com/owner/repo/issues/3", out _, out _, out _)).IsFalse();
    }
}
