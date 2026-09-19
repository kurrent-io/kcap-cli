using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Tests.Unit.Skills;

public class SkillsExclusionTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task The_block_is_idempotent_and_removable() {
        var gitDir = Tmp.CreateDir("main", ".git");
        var repo   = Tmp.GetResolvedPath("main");
        var roots  = new[] { Path.Combine(".agents", "skills"), Path.Combine(".claude", "skills") };

        gitDir.CreateFile(["info", "exclude"], "# existing\n*.log\n");

        SkillsExclusion.Apply(gitDir, repo, roots);
        var once = File.ReadAllText(gitDir.PathTo("info", "exclude"));
        SkillsExclusion.Apply(gitDir, repo, roots);
        var twice = File.ReadAllText(gitDir.PathTo("info", "exclude"));

        await Assert.That(twice).IsEqualTo(once);
        await Assert.That(once).Contains("*.log");
        await Assert.That(once).Contains("/.agents/skills/kcap-*/");
        await Assert.That(once).Contains("/.claude/skills/kcap-*/");

        SkillsExclusion.Remove(gitDir);
        var removed = File.ReadAllText(gitDir.PathTo("info", "exclude"));

        await Assert.That(removed).Contains("*.log");
        await Assert.That(removed).DoesNotContain("kcap-");
    }

    /// <summary>The file belongs to the user, and on Windows it is routinely CRLF: one managed block
    /// must not convert the whole of it.</summary>
    [Test]
    public async Task A_file_written_with_crlf_keeps_its_line_endings() {
        var gitDir = Tmp.CreateDir("main", ".git");
        var repo   = Tmp.GetResolvedPath("main");

        gitDir.CreateFile(["info", "exclude"], "# existing\r\n*.log\r\n");

        SkillsExclusion.Apply(gitDir, repo, [Path.Combine(".agents", "skills")]);
        var text = File.ReadAllText(gitDir.PathTo("info", "exclude"));

        await Assert.That(text).Contains("*.log\r\n");
        await Assert.That(text).Contains("/.agents/skills/kcap-*/\r\n");
        await Assert.That(text.Replace("\r\n", "")).DoesNotContain("\n");
    }

    /// <summary>A block whose closing marker was lost — a hand edit, an interrupted write — covers
    /// the patterns kcap wrote and nothing below them.</summary>
    [Test]
    public async Task An_unterminated_block_does_not_take_the_rest_of_the_file() {
        var gitDir = Tmp.CreateDir("main", ".git");
        var repo   = Tmp.GetResolvedPath("main");

        gitDir.CreateFile(["info", "exclude"],
                          "# kcap skills (managed) — do not edit between these markers\n"
                        + "/.agents/skills/kcap-*/\n"
                        + "# mine\nbuild/\n");

        SkillsExclusion.Apply(gitDir, repo, [Path.Combine(".claude", "skills")]);
        var text = File.ReadAllText(gitDir.PathTo("info", "exclude"));

        await Assert.That(text).Contains("# mine\nbuild/\n");
        await Assert.That(text).Contains("/.claude/skills/kcap-*/");
        await Assert.That(text).DoesNotContain("/.agents/skills/kcap-*/");
    }

    [Test]
    public async Task Patterns_are_relative_to_the_repository_root() {
        var gitDir = Tmp.CreateDir("main", ".git");
        var repo   = Tmp.GetResolvedPath("main");
        // Both sides resolved, or the relative pattern is computed across a symlinked temp root.
        var nested = Tmp.GetResolvedPath("main", "sub", "dir", ".agents", "skills");

        Tmp.CreateDir("main", "sub", "dir");

        SkillsExclusion.Apply(gitDir, repo, [nested]);

        // An anchor below the root still excludes by a root-relative pattern.
        await Assert.That(File.ReadAllText(gitDir.PathTo("info", "exclude")))
            .Contains("/sub/dir/.agents/skills/kcap-*/");
    }
}
