using Kapacitor.Cli.Core.Commands;

namespace Kapacitor.Cli.Tests.Unit;

public class PrRefParserTests {
    [Test]
    public async Task Shorthand_form_parses() {
        var ok = PrRefParser.TryParse("kurrent-io/kapacitor-cli#101", out var owner, out var repo, out var pr);

        await Assert.That(ok).IsTrue();
        await Assert.That(owner).IsEqualTo("kurrent-io");
        await Assert.That(repo).IsEqualTo("kapacitor-cli");
        await Assert.That(pr).IsEqualTo(101);
    }

    [Test]
    public async Task Plain_url_parses() {
        var ok = PrRefParser.TryParse("https://github.com/kurrent-io/kapacitor-cli/pull/101", out var owner, out var repo, out var pr);

        await Assert.That(ok).IsTrue();
        await Assert.That(owner).IsEqualTo("kurrent-io");
        await Assert.That(repo).IsEqualTo("kapacitor-cli");
        await Assert.That(pr).IsEqualTo(101);
    }

    [Test]
    public async Task Url_with_query_parses() {
        var ok = PrRefParser.TryParse("https://github.com/kurrent-io/kapacitor-cli/pull/101?diff=split", out var owner, out var repo, out var pr);

        await Assert.That(ok).IsTrue();
        await Assert.That(owner).IsEqualTo("kurrent-io");
        await Assert.That(repo).IsEqualTo("kapacitor-cli");
        await Assert.That(pr).IsEqualTo(101);
    }

    [Test]
    public async Task Url_with_fragment_parses() {
        var ok = PrRefParser.TryParse("https://github.com/kurrent-io/kapacitor-cli/pull/101#issuecomment-12345", out var owner, out var repo, out var pr);

        await Assert.That(ok).IsTrue();
        await Assert.That(pr).IsEqualTo(101);
    }

    [Test]
    public async Task Url_with_trailing_path_parses() {
        var ok = PrRefParser.TryParse("https://github.com/kurrent-io/kapacitor-cli/pull/101/files", out var owner, out var repo, out var pr);

        await Assert.That(ok).IsTrue();
        await Assert.That(pr).IsEqualTo(101);
    }

    [Test]
    public async Task Input_with_surrounding_whitespace_is_trimmed() {
        var ok = PrRefParser.TryParse("  kurrent-io/kapacitor-cli#101  ", out var owner, out var repo, out var pr);

        await Assert.That(ok).IsTrue();
        await Assert.That(owner).IsEqualTo("kurrent-io");
        await Assert.That(repo).IsEqualTo("kapacitor-cli");
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
    public async Task Garbage_input_rejected() {
        await Assert.That(PrRefParser.TryParse("not-a-pr-ref", out _, out _, out _)).IsFalse();
        await Assert.That(PrRefParser.TryParse("https://gitlab.com/owner/repo/pull/1", out _, out _, out _)).IsFalse();
    }
}
