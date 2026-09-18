using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Tests.Unit.Skills;

public class SkillsExclusionTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task The_block_is_idempotent_and_removable() {
        var gitDir = Tmp.CreateDir("main/.git");
        var repo   = Tmp.GetResolvedPath("main");
        Tmp.CreateFile("main/.git/info/exclude", "# existing\n*.log\n");
        var roots  = new[] { Path.Combine(".agents", "skills"), Path.Combine(".claude", "skills") };

        SkillsExclusion.Apply(gitDir, repo, roots);
        var once = File.ReadAllText(Path.Combine(gitDir, "info", "exclude"));
        SkillsExclusion.Apply(gitDir, repo, roots);
        var twice = File.ReadAllText(Path.Combine(gitDir, "info", "exclude"));

        await Assert.That(twice).IsEqualTo(once);
        await Assert.That(once).Contains("*.log");
        await Assert.That(once).Contains("/.agents/skills/kcap-*/");
        await Assert.That(once).Contains("/.claude/skills/kcap-*/");

        SkillsExclusion.Remove(gitDir);
        var removed = File.ReadAllText(Path.Combine(gitDir, "info", "exclude"));

        await Assert.That(removed).Contains("*.log");
        await Assert.That(removed).DoesNotContain("kcap-");
    }

    [Test]
    public async Task Patterns_are_relative_to_the_repository_root() {
        var gitDir = Tmp.CreateDir("main/.git");
        var repo   = Tmp.GetResolvedPath("main");
        var nested = Path.Combine(repo, "sub", "dir");
        Directory.CreateDirectory(nested);

        SkillsExclusion.Apply(gitDir, repo, [Path.Combine(nested, ".agents", "skills")]);

        // An anchor below the root still excludes by a root-relative pattern.
        await Assert.That(File.ReadAllText(Path.Combine(gitDir, "info", "exclude")))
            .Contains("/sub/dir/.agents/skills/kcap-*/");
    }
}
