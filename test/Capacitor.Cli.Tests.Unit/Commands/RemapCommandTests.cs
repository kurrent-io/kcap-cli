using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class RemapCommandTests {
    static CwdRemap R(string from, string to) => new() { From = from, To = to };

    [Test]
    public async Task ApplyAdd_appends_new_entry() {
        var (next, replaced) = RemapCommand.ApplyAdd([], "/old", "/new");

        await Assert.That(replaced).IsFalse();
        await Assert.That(next).Count().IsEqualTo(1);
        await Assert.That(next[0].From).IsEqualTo("/old");
        await Assert.That(next[0].To).IsEqualTo("/new");
    }

    [Test]
    public async Task ApplyAdd_to_null_current_treats_as_empty() {
        var (next, replaced) = RemapCommand.ApplyAdd(null, "/old", "/new");

        await Assert.That(replaced).IsFalse();
        await Assert.That(next).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ApplyAdd_replaces_when_from_already_exists() {
        var current          = new[] { R("/old", "/v1"), R("/other", "/keep") };
        var (next, replaced) = RemapCommand.ApplyAdd(current, "/old", "/v2");

        await Assert.That(replaced).IsTrue();
        await Assert.That(next).Count().IsEqualTo(2);
        await Assert.That(next.Single(r => r.From == "/old").To).IsEqualTo("/v2");
        await Assert.That(next.Single(r => r.From == "/other").To).IsEqualTo("/keep");
    }

    [Test]
    public async Task ApplyAdd_treats_trailing_slash_as_same_entry() {
        // Normalize trims trailing separators, so "/old" and "/old/" collide.
        var current          = new[] { R("/old", "/v1") };
        var (next, replaced) = RemapCommand.ApplyAdd(current, "/old/", "/v2");

        await Assert.That(replaced).IsTrue();
        await Assert.That(next).Count().IsEqualTo(1);
        // Note: ApplyAdd does NOT normalize the args itself (HandleAsync does
        // that before calling). This test exercises the SameFrom comparator.
    }

    [Test]
    public async Task ApplyRemove_drops_matching_entry() {
        var current = new[] { R("/a", "/x"), R("/b", "/y") };
        var next    = RemapCommand.ApplyRemove(current, "/a");

        await Assert.That(next).Count().IsEqualTo(1);
        await Assert.That(next[0].From).IsEqualTo("/b");
    }

    [Test]
    public async Task ApplyRemove_returns_same_count_when_not_found() {
        var current = new[] { R("/a", "/x") };
        var next    = RemapCommand.ApplyRemove(current, "/missing");

        await Assert.That(next).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ApplyRemove_on_null_current_returns_empty() {
        var next = RemapCommand.ApplyRemove(null, "/a");
        await Assert.That(next).IsEmpty();
    }

    [Test]
    public async Task ApplyRemove_treats_trailing_slash_as_same_entry() {
        var current = new[] { R("/a", "/x") };
        var next    = RemapCommand.ApplyRemove(current, "/a/");
        await Assert.That(next).IsEmpty();
    }

    [Test]
    public async Task Normalize_trims_trailing_separators() {
        await Assert.That(RemapCommand.Normalize("/a/b/")).IsEqualTo("/a/b");
        await Assert.That(RemapCommand.Normalize(@"C:\a\b\")).IsEqualTo(@"C:\a\b");
    }

    [Test]
    public async Task Normalize_preserves_root_separator() {
        // "/" and "\" alone must not collapse to empty.
        await Assert.That(RemapCommand.Normalize("/")).IsEqualTo("/");
        await Assert.That(RemapCommand.Normalize(@"\")).IsEqualTo(@"\");
    }

    [Test]
    public async Task Normalize_returns_empty_for_whitespace() {
        await Assert.That(RemapCommand.Normalize("   ")).IsEqualTo("");
        await Assert.That(RemapCommand.Normalize(null)).IsEqualTo("");
    }

    [Test]
    public async Task Normalize_trims_whitespace_around_path() {
        await Assert.That(RemapCommand.Normalize("  /a/b  ")).IsEqualTo("/a/b");
    }

    [Test, NotInParallel]
    public async Task Add_refuses_extra_arguments_rather_than_storing_the_first_two() {
        using var tmp = new TempDir();
        int       exit;

        using (var capture = ConsoleOutput.StartErrorCapture()) {
            // What an unquoted '~/dev/repo/worktrees/*' looks like once the shell
            // has expanded it — the '*' is already gone by the time we see it.
            exit = await new RemapCommand(new(tmp.Path))
                .HandleAsync(["remap", "/dev/repo/worktrees/ai-1", "/dev/repo/worktrees/ai-2", "/dev/repo"]);

            await Assert.That(capture.GetCapturedError()).Contains("takes exactly two paths");
        }

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That((await AppConfig.LoadProfileConfig(new(tmp.Path))).CwdRemap ?? []).IsEmpty();
    }

    [Test, NotInParallel]
    public async Task Remove_refuses_extra_arguments_rather_than_dropping_the_first() {
        using var tmp  = new TempDir();
        var       root = new ConfigRoot(tmp.Path);

        await new RemapCommand(root).HandleAsync(["remap", "/dev/repo/worktrees/ai-1", "/dev/repo"]);

        int exit;

        using (var capture = ConsoleOutput.StartErrorCapture()) {
            exit = await new RemapCommand(root)
                .HandleAsync(["remap", "--remove", "/dev/repo/worktrees/ai-1", "/dev/repo/worktrees/ai-2"]);

            await Assert.That(capture.GetCapturedError()).Contains("takes exactly one path");
        }

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That((await AppConfig.LoadProfileConfig(root)).CwdRemap!).Count().IsEqualTo(1);
    }

    [Test, NotInParallel]
    public async Task Add_refuses_a_wildcard_in_the_to_path() {
        using var tmp = new TempDir();
        int       exit;

        using (var capture = ConsoleOutput.StartErrorCapture()) {
            exit = await new RemapCommand(new(tmp.Path)).HandleAsync(["remap", "/dev/repo", "/dev/*"]);

            await Assert.That(capture.GetCapturedError()).Contains("the to path is literal");
        }

        await Assert.That(exit).IsEqualTo(1);
    }

    [Test, NotInParallel]
    public async Task Remove_drops_an_entry_the_matcher_would_refuse() {
        // A hand-edited config can hold a pattern no import can use; the CLI is
        // the way back out, so removal must not apply the wildcard grammar.
        using var tmp  = new TempDir();
        var       root = new ConfigRoot(tmp.Path);

        Directory.CreateDirectory(tmp.Path);
        await File.WriteAllTextAsync(
            AppConfig.GetConfigPath(root),
            """{"profiles":{},"cwd_remap":[{"from":"/dev/wt-*","to":"/dev/repo"}]}""");

        var exit = await new RemapCommand(root).HandleAsync(["remap", "--remove", "/dev/wt-*"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That((await AppConfig.LoadProfileConfig(root)).CwdRemap ?? []).IsEmpty();
    }

    [Test, NotInParallel]
    public async Task List_shows_a_stored_pattern_verbatim() {
        using var tmp  = new TempDir();
        var       root = new ConfigRoot(tmp.Path);

        await new RemapCommand(root).HandleAsync(["remap", "~/dev/repo/worktrees/*", "~/dev/repo"]);

        using var capture = ConsoleOutput.StartCapture();

        await new RemapCommand(root).HandleAsync(["remap", "--list"]);

        await Assert.That(capture.GetCapturedOutput()).Contains("~/dev/repo/worktrees/* → ~/dev/repo");
    }

    [Test]
    public async Task TryNormalizeFrom_accepts_a_wildcard_segment() {
        await Assert.That(RemapCommand.TryNormalizeFrom("  ~/dev/repo/worktrees/*  ", out var from, out _)).IsTrue();
        await Assert.That(from).IsEqualTo("~/dev/repo/worktrees/*");
    }

    [Test]
    public async Task TryNormalizeFrom_trims_a_trailing_separator_after_the_wildcard() {
        await Assert.That(RemapCommand.TryNormalizeFrom("~/dev/repo/worktrees/*/", out var from, out _)).IsTrue();
        await Assert.That(from).IsEqualTo("~/dev/repo/worktrees/*");
    }

    [Test]
    public async Task TryNormalizeFrom_rejects_a_partial_segment_wildcard() {
        await Assert.That(RemapCommand.TryNormalizeFrom("~/dev/wt-*", out _, out var error)).IsFalse();
        await Assert.That(error).Contains("stand alone as a path segment");
    }

    [Test]
    public async Task TryNormalizeFrom_rejects_two_wildcards() {
        await Assert.That(RemapCommand.TryNormalizeFrom("~/dev/*/worktrees/*", out _, out var error)).IsFalse();
        await Assert.That(error).Contains("only one '*'");
    }

    [Test]
    public async Task ApplyAdd_with_OrdinalIgnoreCase_replaces_case_variant_entry() {
        // On Windows, "C:\Users\Alice" and "c:\users\alice" refer to the same
        // dir at import time, so kcap remap should replace — not duplicate —
        // when the user re-adds with different casing.
        var current = new[] { R(@"C:\Users\Alice\Dev", @"C:\Users\Alice\Old") };
        var (next, replaced) = RemapCommand.ApplyAdd(
            current, @"c:\users\alice\dev", @"C:\Users\Alice\New", StringComparison.OrdinalIgnoreCase);

        await Assert.That(replaced).IsTrue();
        await Assert.That(next).Count().IsEqualTo(1);
        await Assert.That(next[0].From).IsEqualTo(@"c:\users\alice\dev"); // new casing wins
        await Assert.That(next[0].To).IsEqualTo(@"C:\Users\Alice\New");
    }

    [Test]
    public async Task ApplyAdd_with_Ordinal_keeps_case_variant_as_separate_entry() {
        // On non-Windows hosts, case differences must stay distinct — two
        // separate paths.
        var current = new[] { R("/dev/Foo", "/dev/v1") };
        var (next, replaced) = RemapCommand.ApplyAdd(
            current, "/dev/foo", "/dev/v2", StringComparison.Ordinal);

        await Assert.That(replaced).IsFalse();
        await Assert.That(next).Count().IsEqualTo(2);
    }

    [Test]
    public async Task ApplyRemove_with_OrdinalIgnoreCase_removes_case_variant_entry() {
        var current = new[] { R(@"C:\Users\Alice\Dev", @"C:\Users\Alice\New") };
        var next    = RemapCommand.ApplyRemove(current, @"c:\users\alice\dev", StringComparison.OrdinalIgnoreCase);
        await Assert.That(next).IsEmpty();
    }

    [Test]
    public async Task ApplyRemove_with_Ordinal_keeps_case_variant_entry() {
        var current = new[] { R("/dev/Foo", "/dev/x") };
        var next    = RemapCommand.ApplyRemove(current, "/dev/foo", StringComparison.Ordinal);
        await Assert.That(next).Count().IsEqualTo(1);
    }
}
