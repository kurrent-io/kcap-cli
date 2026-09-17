using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ImportMissingCwdsReportTests {
    // Every path reported below is outside this home, so the ~ shortening never fires.
    static readonly UserHome Home = new("/no/such/home");

    // A suggested rule is spelled with the separator the cwd was recorded with,
    // which under a TempDir is the host's.
    static readonly char Sep = Path.DirectorySeparatorChar;

    [Test, NotInParallel]
    public async Task Reports_missing_cwds_with_session_count_and_sample() {
        using var tmp = new TempDir();
        var sessionCwds = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["s1"] = "/does/not/exist/repo-a",
            ["s2"] = "/does/not/exist/repo-a", // dup cwd → 1 distinct path, 2 sessions
            ["s3"] = "/does/not/exist/repo-b",
            ["s4"] = tmp.Path,
        };

        var output = Capture(d => ImportCommand.ReportMissingCwds(sessionCwds, cwdRemap: null, d, Home));

        await Assert.That(output).Contains("3 sessions reference 2 distinct paths that no longer exist on disk");
        await Assert.That(output).Contains("/does/not/exist/repo-a");
        await Assert.That(output).Contains("/does/not/exist/repo-b");
        await Assert.That(output).DoesNotContain(tmp.Path); // existing dir not reported
        await Assert.That(output).Contains("kcap remap");
    }

    [Test, NotInParallel]
    public async Task Hint_shifts_to_update_when_cwd_remap_already_configured() {
        var sessionCwds = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["s1"] = "/does/not/exist/repo-a",
        };

        var rules  = new[] { new CwdRemap { From = "/old", To = "/new" } };
        var output = Capture(d => ImportCommand.ReportMissingCwds(sessionCwds, rules, d, Home));

        await Assert.That(output).Contains("update or add mappings");
    }

    [Test, NotInParallel]
    public async Task Stays_silent_when_all_cwds_exist() {
        using var tmp = new TempDir();
        var sessionCwds = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["s1"] = tmp.Path,
        };

        var output = Capture(d => ImportCommand.ReportMissingCwds(sessionCwds, cwdRemap: null, d, Home));

        await Assert.That(output).IsEmpty();
    }

    [Test, NotInParallel]
    public async Task Stays_silent_when_no_sessions_have_cwds() {
        var output = Capture(d => ImportCommand.ReportMissingCwds(new Dictionary<string, string>(), cwdRemap: null, d, Home));

        await Assert.That(output).IsEmpty();
    }

    [Test]
    public async Task ShortenHome_rewrites_home_prefix_to_tilde() {
        await Assert.That(ImportCommand.ShortenHome("/Users/alexey/dev/foo", "/Users/alexey"))
            .IsEqualTo("~/dev/foo");
    }

    [Test]
    public async Task ShortenHome_returns_tilde_for_exact_home() {
        await Assert.That(ImportCommand.ShortenHome("/Users/alexey", "/Users/alexey")).IsEqualTo("~");
    }

    [Test]
    public async Task ShortenHome_does_not_shrink_across_path_boundary() {
        // /Users/alexeyfoo must NOT become ~foo
        await Assert.That(ImportCommand.ShortenHome("/Users/alexeyfoo/dev", "/Users/alexey"))
            .IsEqualTo("/Users/alexeyfoo/dev");
    }

    [Test]
    public async Task ShortenHome_passes_through_non_home_paths() {
        await Assert.That(ImportCommand.ShortenHome("/var/tmp/x", "/Users/alexey")).IsEqualTo("/var/tmp/x");
    }

    [Test]
    public async Task ShortenHome_accepts_backslash_as_path_boundary() {
        // Even on a non-Windows host the helper must accept '\' as a separator
        // so transcripts that recorded Windows paths shrink correctly.
        await Assert.That(ImportCommand.ShortenHome(@"C:\Users\alexey\dev\foo", @"C:\Users\alexey"))
            .IsEqualTo(@"~\dev\foo");
    }

    [Test]
    public async Task CollapseDescendants_drops_worktree_when_parent_is_also_missing() {
        var input = new HashSet<string>(StringComparer.Ordinal) {
            "/dev/kapacitor",
            "/dev/kapacitor/.claude/worktrees/agent-1",
            "/dev/kapacitor/.claude/worktrees/agent-2",
        };

        var roots = ImportCommand.CollapseDescendants(input);

        await Assert.That(roots).IsEquivalentTo(new[] { "/dev/kapacitor" });
    }

    [Test]
    public async Task CollapseDescendants_keeps_siblings_when_no_common_parent_missing() {
        // /a/x and /a/y both missing, /a NOT missing → both remain.
        var input = new HashSet<string>(StringComparer.Ordinal) { "/a/x", "/a/y" };

        var roots = ImportCommand.CollapseDescendants(input);

        await Assert.That(roots).IsEquivalentTo(new[] { "/a/x", "/a/y" });
    }

    [Test, NotInParallel]
    public async Task Report_collapses_worktree_descendants_in_output() {
        var sessionCwds = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["s1"] = "/dev/kapacitor",
            ["s2"] = "/dev/kapacitor/.claude/worktrees/agent-1",
            ["s3"] = "/dev/kapacitor/.claude/worktrees/agent-2",
            ["s4"] = "/dev/other-repo",
        };

        var output = Capture(d => ImportCommand.ReportMissingCwds(sessionCwds, cwdRemap: null, d, Home));

        // 2 distinct roots (kapacitor + other-repo), 4 sessions still affected.
        await Assert.That(output).Contains("4 sessions reference 2 distinct paths");
        await Assert.That(output).Contains("/dev/kapacitor\n");
        await Assert.That(output).Contains("/dev/other-repo");
        await Assert.That(output).DoesNotContain("worktrees/agent-1");
        await Assert.That(output).DoesNotContain("worktrees/agent-2");
    }

    [Test, NotInParallel]
    public async Task ReportWorktreeAttributions_stays_silent_when_zero() {
        var output = Capture(d => ImportCommand.ReportWorktreeAttributions(0, d));
        await Assert.That(output).IsEmpty();
    }

    [Test, NotInParallel]
    public async Task ReportWorktreeAttributions_reports_singular_phrasing_for_one() {
        var output = Capture(d => ImportCommand.ReportWorktreeAttributions(1, d));
        await Assert.That(output).Contains("Attributed 1 session to a parent project via worktree path.");
    }

    [Test, NotInParallel]
    public async Task ReportWorktreeAttributions_reports_plural_phrasing_for_many() {
        var output = Capture(d => ImportCommand.ReportWorktreeAttributions(477, d));
        await Assert.That(output).Contains("Attributed 477 sessions to a parent project via worktree path.");
    }

    [Test, NotInParallel]
    public async Task Truncates_sample_to_five_distinct_paths() {
        var sessionCwds = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["s1"] = "/missing/a",
            ["s2"] = "/missing/b",
            ["s3"] = "/missing/c",
            ["s4"] = "/missing/d",
            ["s5"] = "/missing/e",
            ["s6"] = "/missing/f",
            ["s7"] = "/missing/g",
        };

        var output = Capture(d => ImportCommand.ReportMissingCwds(sessionCwds, cwdRemap: null, d, Home));

        await Assert.That(output).Contains("... and 2 more");
    }

    [Test]
    public async Task DetectWorktreeFamilies_groups_dead_siblings_under_their_repository() {
        var families = ImportCommand.DetectWorktreeFamilies(
            ["/dev/repo/worktrees/ai-1", "/dev/repo/worktrees/ai-2"],
            isRepo: p => p == "/dev/repo");

        await Assert.That(families).Count().IsEqualTo(1);
        await Assert.That(families[0].Parent).IsEqualTo("/dev/repo/worktrees");
        await Assert.That(families[0].Project).IsEqualTo("/dev/repo");
        await Assert.That(families[0].Paths).Count().IsEqualTo(2);
    }

    [Test]
    public async Task DetectWorktreeFamilies_needs_more_than_one_sibling() {
        var families = ImportCommand.DetectWorktreeFamilies(
            ["/dev/repo/worktrees/ai-1"],
            isRepo: p => p == "/dev/repo");

        await Assert.That(families).IsEmpty();
    }

    [Test]
    public async Task DetectWorktreeFamilies_reaches_past_a_subdirectory_the_session_ran_in() {
        // A cwd of <worktree>/src still belongs to the family its worktree does.
        var families = ImportCommand.DetectWorktreeFamilies(
            ["/dev/repo/worktrees/ai-1/src", "/dev/repo/worktrees/ai-2/test"],
            isRepo: p => p == "/dev/repo");

        await Assert.That(families).Count().IsEqualTo(1);
        await Assert.That(families[0].Parent).IsEqualTo("/dev/repo/worktrees");
        await Assert.That(families[0].Project).IsEqualTo("/dev/repo");
    }

    [Test]
    public async Task DetectWorktreeFamilies_counts_slugs_not_paths() {
        // Two dead directories inside ONE worktree are one slug, so there is no
        // family to describe — naming the two paths is the honest report.
        var families = ImportCommand.DetectWorktreeFamilies(
            ["/dev/repo/worktrees/ai-1/src", "/dev/repo/worktrees/ai-1/test"],
            isRepo: p => p == "/dev/repo");

        await Assert.That(families).IsEmpty();
    }

    [Test]
    public async Task DetectWorktreeFamilies_keeps_the_recorded_separator_on_windows_paths() {
        var families = ImportCommand.DetectWorktreeFamilies(
            [@"C:\dev\repo\worktrees\ai-1", @"C:\dev\repo\worktrees\ai-2"],
            isRepo: p => p == @"C:\dev\repo");

        await Assert.That(families).Count().IsEqualTo(1);
        await Assert.That(families[0].Parent).IsEqualTo(@"C:\dev\repo\worktrees");
    }

    [Test]
    public async Task DetectWorktreeFamilies_skips_a_parent_that_is_not_under_a_repository() {
        // ~/dev/worktrees/<project>/<tree>: the directory above the family is
        // not a repo, so only the user knows what these belong to.
        var families = ImportCommand.DetectWorktreeFamilies(
            ["/dev/worktrees/capacitor/a", "/dev/worktrees/capacitor/b"],
            isRepo: _ => false);

        await Assert.That(families).IsEmpty();
    }

    [Test, NotInParallel]
    public async Task Report_suggests_a_wildcard_rule_instead_of_listing_the_family() {
        using var tmp  = new TempDir();
        var       repo = tmp.CreateDir("repo");

        repo.CreateDir(".git");

        var sessionCwds = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["s1"] = repo.PathTo("worktrees", "ai-1"),
            ["s2"] = repo.PathTo("worktrees", "ai-1"),
            ["s3"] = repo.PathTo("worktrees", "ai-2"),
        };

        var output = Capture(d => ImportCommand.ReportMissingCwds(sessionCwds, cwdRemap: null, d, Home));
        var family = repo.PathTo("worktrees") + Sep;

        await Assert.That(output).Contains($"3 sessions under {family} (2 paths) belong to {repo.Path}, which still exists:");
        await Assert.That(output).Contains($"  kcap remap '{family}*' {repo.Path}");
        // The family stands for its members, so they are not also listed, and
        // the generic hint would only repeat the command already printed.
        await Assert.That(output).DoesNotContain($"  {repo.PathTo("worktrees", "ai-1")}");
        await Assert.That(output).DoesNotContain("Run `kcap remap <from> <to>`");
    }

    [Test, NotInParallel]
    public async Task Report_suggests_a_rule_for_a_session_that_ran_below_its_worktree() {
        using var tmp  = new TempDir();
        var       repo = tmp.CreateDir("repo");

        repo.CreateDir(".git");

        var sessionCwds = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["s1"] = repo.PathTo("worktrees", "ai-1", "src"),
            ["s2"] = repo.PathTo("worktrees", "ai-2", "src"),
        };

        var output = Capture(d => ImportCommand.ReportMissingCwds(sessionCwds, cwdRemap: null, d, Home));

        await Assert.That(output).Contains($"  kcap remap '{repo.PathTo("worktrees")}{Sep}*' {repo.Path}");
    }

    [Test, NotInParallel]
    public async Task Report_still_lists_paths_outside_any_family() {
        using var tmp  = new TempDir();
        var       repo = tmp.CreateDir("repo");

        repo.CreateDir(".git");

        var sessionCwds = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["s1"] = repo.PathTo("worktrees", "ai-1"),
            ["s2"] = repo.PathTo("worktrees", "ai-2"),
            ["s3"] = "/does/not/exist/repo-b",
        };

        var output = Capture(d => ImportCommand.ReportMissingCwds(sessionCwds, cwdRemap: null, d, Home));

        await Assert.That(output).Contains("3 sessions reference 3 distinct paths that no longer exist on disk:");
        await Assert.That(output).Contains("  /does/not/exist/repo-b");
        await Assert.That(output).Contains($"  kcap remap '{repo.PathTo("worktrees")}{Sep}*' {repo.Path}");
        await Assert.That(output).Contains("Run `kcap remap <from> <to>`");
    }

    [Test, NotInParallel]
    public async Task Report_lists_a_family_whose_project_is_not_a_repository() {
        using var tmp   = new TempDir();
        var       plain = tmp.CreateDir("plain");

        var sessionCwds = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["s1"] = plain.PathTo("worktrees", "ai-1"),
            ["s2"] = plain.PathTo("worktrees", "ai-2"),
        };

        var output = Capture(d => ImportCommand.ReportMissingCwds(sessionCwds, cwdRemap: null, d, Home));

        await Assert.That(output).DoesNotContain("kcap remap '");
        await Assert.That(output).Contains($"  {plain.PathTo("worktrees", "ai-1")}");
    }

    static string Capture(Action<ImportCommand.ImportDisplay> render) {
        using var capture = ConsoleOutput.StartCapture();
        render(new() { Tty = false });
        // Normalize CRLF→LF so line-anchored assertions (e.g. Contains("…\n"))
        // hold on Windows, where the writer emits Environment.NewLine.
        return capture.GetCapturedOutput().Replace("\r\n", "\n");
    }
}
