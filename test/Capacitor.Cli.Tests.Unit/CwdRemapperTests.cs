using Capacitor.Cli;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit;

public class CwdRemapperTests {
    static CwdRemap R(string from, string to) => new() { From = from, To = to };

    [Test]
    public async Task Apply_with_null_rules_returns_cwd_unchanged() {
        var result = CwdRemapper.Apply("/Users/alexey/dev/foo", null);
        await Assert.That(result).IsEqualTo("/Users/alexey/dev/foo");
    }

    [Test]
    public async Task Apply_with_empty_rules_returns_cwd_unchanged() {
        var result = CwdRemapper.Apply("/Users/alexey/dev/foo", []);
        await Assert.That(result).IsEqualTo("/Users/alexey/dev/foo");
    }

    [Test]
    public async Task Apply_exact_prefix_match_rewrites_cwd() {
        var rules = new[] { R("/dev/kapacitor-cli", "/dev/kcap-cli") };
        var result = CwdRemapper.Apply("/dev/kapacitor-cli", rules);
        await Assert.That(result).IsEqualTo("/dev/kcap-cli");
    }

    [Test]
    public async Task Apply_path_boundary_prefix_match_preserves_tail() {
        var rules = new[] { R("/dev/kapacitor-cli", "/dev/kcap-cli") };
        var result = CwdRemapper.Apply("/dev/kapacitor-cli/src/Foo", rules);
        await Assert.That(result).IsEqualTo("/dev/kcap-cli/src/Foo");
    }

    [Test]
    public async Task Apply_does_not_match_across_path_boundary() {
        // Rule for "/dev/kapacitor" must NOT match "/dev/kapacitor-cli" — the
        // next char after the prefix is '-', not '/', so it's a different dir.
        var rules = new[] { R("/dev/kapacitor", "/dev/kcap") };
        var result = CwdRemapper.Apply("/dev/kapacitor-cli", rules);
        await Assert.That(result).IsEqualTo("/dev/kapacitor-cli");
    }

    [Test]
    public async Task Apply_longest_prefix_wins() {
        var rules = new[] {
            R("/dev/kapacitor",     "/dev/wrong"),
            R("/dev/kapacitor-cli", "/dev/kcap-cli"),
        };

        var result = CwdRemapper.Apply("/dev/kapacitor-cli/src", rules);
        await Assert.That(result).IsEqualTo("/dev/kcap-cli/src");
    }

    [Test]
    public async Task Apply_non_matching_cwd_returns_unchanged() {
        var rules = new[] { R("/dev/kapacitor", "/dev/kcap") };
        var result = CwdRemapper.Apply("/Users/alexey/other", rules);
        await Assert.That(result).IsEqualTo("/Users/alexey/other");
    }

    [Test]
    public async Task Apply_skips_rules_with_empty_from() {
        var rules = new[] { R("", "/whatever"), R("/dev/a", "/dev/b") };
        var result = CwdRemapper.Apply("/dev/a/x", rules);
        await Assert.That(result).IsEqualTo("/dev/b/x");
    }

    [Test]
    public async Task Apply_handles_empty_cwd() {
        var rules = new[] { R("/dev/a", "/dev/b") };
        var result = CwdRemapper.Apply("", rules);
        await Assert.That(result).IsEqualTo("");
    }

    [Test]
    public async Task Apply_exact_match_with_trailing_to_uses_to_verbatim() {
        // cwd == from: result is `to` verbatim, no trailing slash added.
        var rules = new[] { R("/dev/a", "/dev/b") };
        var result = CwdRemapper.Apply("/dev/a", rules);
        await Assert.That(result).IsEqualTo("/dev/b");
    }

    [Test]
    public async Task Apply_expands_tilde_in_from_and_to() {
        var rules = new[] { R("~/dev/kapacitor-cli", "~/dev/kcap-cli") };
        var result = CwdRemapper.Apply("/home/u/dev/kapacitor-cli/src", rules, "/home/u");
        await Assert.That(result).IsEqualTo("/home/u/dev/kcap-cli/src");
    }

    [Test]
    public async Task Apply_expands_bare_tilde_in_from() {
        var rules = new[] { R("~", "/elsewhere") };
        var result = CwdRemapper.Apply("/home/u/x", rules, "/home/u");
        await Assert.That(result).IsEqualTo("/elsewhere/x");
    }

    [Test]
    public async Task Apply_does_not_expand_tilde_username_form() {
        // "~alice" is the ~user form; we don't expand it. Since the resulting
        // 'from' starts with '~' and the cwd doesn't, no match → unchanged.
        var rules = new[] { R("~alice/dev", "/home/alice/dev") };
        var result = CwdRemapper.Apply("/home/u/dev", rules, "/home/u");
        await Assert.That(result).IsEqualTo("/home/u/dev");
    }
}
