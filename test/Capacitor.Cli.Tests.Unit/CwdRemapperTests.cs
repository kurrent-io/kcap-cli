using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit;

public class CwdRemapperTests {
    static CwdRemap R(string from, string to) => new() { From = from, To = to };

    [Test]
    public async Task Apply_with_null_rules_returns_cwd_unchanged() {
        var result = CwdRemapper.Apply("/Users/alexey/dev/foo", null, new("/home/u"));
        await Assert.That(result).IsEqualTo("/Users/alexey/dev/foo");
    }

    [Test]
    public async Task Apply_with_empty_rules_returns_cwd_unchanged() {
        var result = CwdRemapper.Apply("/Users/alexey/dev/foo", [], new("/home/u"));
        await Assert.That(result).IsEqualTo("/Users/alexey/dev/foo");
    }

    [Test]
    public async Task Apply_exact_prefix_match_rewrites_cwd() {
        var rules = new[] { R("/dev/kapacitor-cli", "/dev/kcap-cli") };
        var result = CwdRemapper.Apply("/dev/kapacitor-cli", rules, new("/home/u"));
        await Assert.That(result).IsEqualTo("/dev/kcap-cli");
    }

    [Test]
    public async Task Apply_path_boundary_prefix_match_preserves_tail() {
        var rules = new[] { R("/dev/kapacitor-cli", "/dev/kcap-cli") };
        var result = CwdRemapper.Apply("/dev/kapacitor-cli/src/Foo", rules, new("/home/u"));
        await Assert.That(result).IsEqualTo("/dev/kcap-cli/src/Foo");
    }

    [Test]
    public async Task Apply_does_not_match_across_path_boundary() {
        // Rule for "/dev/kapacitor" must NOT match "/dev/kapacitor-cli" — the
        // next char after the prefix is '-', not '/', so it's a different dir.
        var rules = new[] { R("/dev/kapacitor", "/dev/kcap") };
        var result = CwdRemapper.Apply("/dev/kapacitor-cli", rules, new("/home/u"));
        await Assert.That(result).IsEqualTo("/dev/kapacitor-cli");
    }

    [Test]
    public async Task Apply_longest_prefix_wins() {
        var rules = new[] {
            R("/dev/kapacitor",     "/dev/wrong"),
            R("/dev/kapacitor-cli", "/dev/kcap-cli"),
        };

        var result = CwdRemapper.Apply("/dev/kapacitor-cli/src", rules, new("/home/u"));
        await Assert.That(result).IsEqualTo("/dev/kcap-cli/src");
    }

    [Test]
    public async Task Apply_non_matching_cwd_returns_unchanged() {
        var rules = new[] { R("/dev/kapacitor", "/dev/kcap") };
        var result = CwdRemapper.Apply("/Users/alexey/other", rules, new("/home/u"));
        await Assert.That(result).IsEqualTo("/Users/alexey/other");
    }

    [Test]
    public async Task Apply_skips_rules_with_empty_from() {
        var rules = new[] { R("", "/whatever"), R("/dev/a", "/dev/b") };
        var result = CwdRemapper.Apply("/dev/a/x", rules, new("/home/u"));
        await Assert.That(result).IsEqualTo("/dev/b/x");
    }

    [Test]
    public async Task Apply_handles_empty_cwd() {
        var rules = new[] { R("/dev/a", "/dev/b") };
        var result = CwdRemapper.Apply("", rules, new("/home/u"));
        await Assert.That(result).IsEqualTo("");
    }

    [Test]
    public async Task Apply_matches_at_backslash_boundary_on_windows_style_paths() {
        // Cwd uses Windows separators; rule does too. Boundary check must
        // accept '\' as a separator regardless of host OS.
        var rules = new[] { R(@"C:\dev\foo", @"C:\dev\bar") };
        var result = CwdRemapper.Apply(@"C:\dev\foo\src\X", rules, "/home/u", StringComparison.OrdinalIgnoreCase);
        await Assert.That(result).IsEqualTo(@"C:\dev\bar\src\X");
    }

    [Test]
    public async Task Apply_does_not_cross_backslash_boundary() {
        var rules = new[] { R(@"C:\dev\foo", @"C:\dev\bar") };
        var result = CwdRemapper.Apply(@"C:\dev\foobar\x", rules, "/home/u", StringComparison.OrdinalIgnoreCase);
        await Assert.That(result).IsEqualTo(@"C:\dev\foobar\x");
    }

    [Test]
    public async Task Apply_is_case_insensitive_when_comparison_is_OrdinalIgnoreCase() {
        var rules = new[] { R(@"C:\Users\Alice\Dev", @"C:\Users\Alice\New") };
        var result = CwdRemapper.Apply(@"c:\users\alice\dev\src", rules, "/home/u", StringComparison.OrdinalIgnoreCase);
        await Assert.That(result).IsEqualTo(@"C:\Users\Alice\New\src");
    }

    [Test]
    public async Task Apply_is_case_sensitive_when_comparison_is_Ordinal() {
        var rules = new[] { R("/dev/Foo", "/dev/Bar") };
        var result = CwdRemapper.Apply("/dev/foo/x", rules, "/home/u", StringComparison.Ordinal);
        await Assert.That(result).IsEqualTo("/dev/foo/x");
    }

    [Test]
    public async Task Apply_expands_tilde_with_backslash_separator() {
        var rules = new[] { R(@"~\dev\foo", @"~\dev\bar") };
        var result = CwdRemapper.Apply(@"C:\Users\u\dev\foo\src", rules, @"C:\Users\u", StringComparison.OrdinalIgnoreCase);
        await Assert.That(result).IsEqualTo(@"C:\Users\u\dev\bar\src");
    }

    [Test]
    public async Task Apply_exact_match_with_trailing_to_uses_to_verbatim() {
        // cwd == from: result is `to` verbatim, no trailing slash added.
        var rules = new[] { R("/dev/a", "/dev/b") };
        var result = CwdRemapper.Apply("/dev/a", rules, new("/home/u"));
        await Assert.That(result).IsEqualTo("/dev/b");
    }

    [Test]
    public async Task Apply_expands_tilde_in_from_and_to() {
        var rules = new[] { R("~/dev/kapacitor-cli", "~/dev/kcap-cli") };
        var result = CwdRemapper.Apply("/home/u/dev/kapacitor-cli/src", rules, new("/home/u"));
        await Assert.That(result).IsEqualTo("/home/u/dev/kcap-cli/src");
    }

    [Test]
    public async Task Apply_expands_bare_tilde_in_from() {
        var rules = new[] { R("~", "/elsewhere") };
        var result = CwdRemapper.Apply("/home/u/x", rules, new("/home/u"));
        await Assert.That(result).IsEqualTo("/elsewhere/x");
    }

    [Test]
    public async Task Apply_wildcard_segment_rewrites_to_verbatim() {
        var rules  = new[] { R("~/dev/repo/worktrees/*", "~/dev/repo") };
        var result = CwdRemapper.Apply("/home/u/dev/repo/worktrees/ai-2441", rules, new("/home/u"));
        await Assert.That(result).IsEqualTo("/home/u/dev/repo");
    }

    [Test]
    public async Task Apply_wildcard_segment_preserves_tail() {
        var rules  = new[] { R("~/dev/repo/worktrees/*", "~/dev/repo") };
        var result = CwdRemapper.Apply("/home/u/dev/repo/worktrees/ai-2441/src/Foo", rules, new("/home/u"));
        await Assert.That(result).IsEqualTo("/home/u/dev/repo/src/Foo");
    }

    [Test]
    public async Task Apply_wildcard_does_not_match_a_missing_segment() {
        // The parent directory itself is not one of the family.
        var rules  = new[] { R("/dev/repo/worktrees/*", "/dev/repo") };
        var result = CwdRemapper.Apply("/dev/repo/worktrees", rules, new("/home/u"));
        await Assert.That(result).IsEqualTo("/dev/repo/worktrees");
    }

    [Test]
    public async Task Apply_wildcard_does_not_match_an_empty_segment() {
        var rules  = new[] { R("/dev/repo/worktrees/*", "/dev/repo") };
        var result = CwdRemapper.Apply("/dev/repo/worktrees/", rules, new("/home/u"));
        await Assert.That(result).IsEqualTo("/dev/repo/worktrees/");
    }

    [Test]
    public async Task Apply_interior_wildcard_matches_one_segment_and_keeps_the_tail() {
        var rules  = new[] { R("/dev/worktrees/*/server", "/dev/server") };
        var result = CwdRemapper.Apply("/dev/worktrees/capacitor/server/src", rules, new("/home/u"));
        await Assert.That(result).IsEqualTo("/dev/server/src");
    }

    [Test]
    public async Task Apply_wildcard_spans_exactly_one_segment() {
        // '*' must not swallow "capacitor/nested" to reach the literal tail.
        var rules  = new[] { R("/dev/worktrees/*/server", "/dev/server") };
        var result = CwdRemapper.Apply("/dev/worktrees/capacitor/nested/server", rules, new("/home/u"));
        await Assert.That(result).IsEqualTo("/dev/worktrees/capacitor/nested/server");
    }

    [Test]
    public async Task Apply_wildcard_tail_requires_a_path_boundary() {
        var rules  = new[] { R("/dev/worktrees/*/server", "/dev/server") };
        var result = CwdRemapper.Apply("/dev/worktrees/capacitor/server-cli", rules, new("/home/u"));
        await Assert.That(result).IsEqualTo("/dev/worktrees/capacitor/server-cli");
    }

    [Test]
    public async Task Apply_wildcard_reaches_further_than_a_shorter_literal() {
        var rules = new[] {
            R("/dev/repo",              "/dev/wrong"),
            R("/dev/repo/worktrees/*",  "/dev/repo"),
        };

        var result = CwdRemapper.Apply("/dev/repo/worktrees/ai-1/src", rules, new("/home/u"));
        await Assert.That(result).IsEqualTo("/dev/repo/src");
    }

    [Test]
    public async Task Apply_literal_beats_a_wildcard_reaching_equally_far() {
        // The one-off override has to survive the family rule, whichever order
        // the two were added in.
        var rules = new[] {
            R("/dev/repo/worktrees/*",     "/dev/repo"),
            R("/dev/repo/worktrees/ai-1",  "/dev/elsewhere"),
        };

        await Assert.That(CwdRemapper.Apply("/dev/repo/worktrees/ai-1/src", rules, new("/home/u")))
            .IsEqualTo("/dev/elsewhere/src");

        await Assert.That(CwdRemapper.Apply("/dev/repo/worktrees/ai-1/src", rules.Reverse().ToArray(), new("/home/u")))
            .IsEqualTo("/dev/elsewhere/src");
    }

    [Test]
    public async Task Apply_skips_a_rule_whose_wildcard_is_not_a_whole_segment() {
        var rules  = new[] { R("/dev/wt-*", "/dev/repo") };
        var result = CwdRemapper.Apply("/dev/wt-ai-1/src", rules, new("/home/u"));
        await Assert.That(result).IsEqualTo("/dev/wt-ai-1/src");
    }

    [Test]
    public async Task Apply_skips_a_rule_with_two_wildcards() {
        var rules  = new[] { R("/dev/*/worktrees/*", "/dev/repo") };
        var result = CwdRemapper.Apply("/dev/repo/worktrees/ai-1", rules, new("/home/u"));
        await Assert.That(result).IsEqualTo("/dev/repo/worktrees/ai-1");
    }

    [Test]
    public async Task Apply_wildcard_matches_at_backslash_boundaries() {
        var rules  = new[] { R(@"C:\dev\repo\worktrees\*", @"C:\dev\repo") };
        var result = CwdRemapper.Apply(@"C:\dev\repo\worktrees\ai-1\src", rules, "/home/u", StringComparison.OrdinalIgnoreCase);
        await Assert.That(result).IsEqualTo(@"C:\dev\repo\src");
    }

    [Test]
    public async Task Apply_wildcard_follows_the_case_policy() {
        var rules = new[] { R(@"C:\Dev\Repo\Worktrees\*", @"C:\Dev\Repo") };

        await Assert.That(CwdRemapper.Apply(@"c:\dev\repo\worktrees\ai-1", rules, "/home/u", StringComparison.OrdinalIgnoreCase))
            .IsEqualTo(@"C:\Dev\Repo");

        await Assert.That(CwdRemapper.Apply(@"c:\dev\repo\worktrees\ai-1", rules, "/home/u", StringComparison.Ordinal))
            .IsEqualTo(@"c:\dev\repo\worktrees\ai-1");
    }

    [Test]
    public async Task TryParseFrom_accepts_a_literal_path() {
        await Assert.That(CwdRemapper.TryParseFrom("/dev/repo", out var pattern, out _)).IsTrue();
        await Assert.That(pattern.HasWildcard).IsFalse();
    }

    [Test]
    public async Task TryParseFrom_accepts_a_trailing_wildcard_segment() {
        await Assert.That(CwdRemapper.TryParseFrom("/dev/repo/worktrees/*", out var pattern, out _)).IsTrue();
        await Assert.That(pattern.HasWildcard).IsTrue();
        await Assert.That(pattern.Head).IsEqualTo("/dev/repo/worktrees/");
        await Assert.That(pattern.Tail).IsEqualTo("");
    }

    [Test]
    public async Task TryParseFrom_accepts_an_interior_wildcard_segment() {
        await Assert.That(CwdRemapper.TryParseFrom("/dev/worktrees/*/server", out var pattern, out _)).IsTrue();
        await Assert.That(pattern.Tail).IsEqualTo("/server");
    }

    [Test]
    public async Task TryParseFrom_rejects_a_partial_segment_wildcard() {
        await Assert.That(CwdRemapper.TryParseFrom("/dev/wt-*", out _, out var error)).IsFalse();
        await Assert.That(error).Contains("stand alone as a path segment");
    }

    [Test]
    public async Task TryParseFrom_rejects_a_leading_wildcard() {
        await Assert.That(CwdRemapper.TryParseFrom("*/worktrees", out _, out var error)).IsFalse();
        await Assert.That(error).Contains("stand alone as a path segment");
    }

    [Test]
    public async Task TryParseFrom_rejects_more_than_one_wildcard() {
        await Assert.That(CwdRemapper.TryParseFrom("/dev/*/worktrees/*", out _, out var error)).IsFalse();
        await Assert.That(error).Contains("only one '*'");
    }

    [Test]
    public async Task TryParseFrom_rejects_a_double_star() {
        await Assert.That(CwdRemapper.TryParseFrom("/dev/repo/**", out _, out var error)).IsFalse();
        await Assert.That(error).Contains("only one '*'");
    }

    [Test]
    public async Task Apply_does_not_expand_tilde_username_form() {
        // "~alice" is the ~user form; we don't expand it. Since the resulting
        // 'from' starts with '~' and the cwd doesn't, no match → unchanged.
        var rules = new[] { R("~alice/dev", "/home/alice/dev") };
        var result = CwdRemapper.Apply("/home/u/dev", rules, new("/home/u"));
        await Assert.That(result).IsEqualTo("/home/u/dev");
    }
}
