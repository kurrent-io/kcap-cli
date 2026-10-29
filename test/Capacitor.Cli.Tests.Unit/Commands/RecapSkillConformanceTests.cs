using System.Text.RegularExpressions;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>The frontmatter description is the whole trigger surface of the skill. These pins keep
/// it loadable and keep the questions about a period in it.</summary>
public partial class RecapSkillConformanceTests {
    static string FoldedDescription() {
        var text = File.ReadAllText(Path.Combine(RepoTree.Root(), "kcap", "skills", "recap", "SKILL.md"))
            .Replace("\r\n", "\n").Replace("\r", "\n");

        return Whitespace().Replace(Description().Match(text).Groups[1].Value, " ").Trim();
    }

    /// <summary>A strict harness refuses a description over 1024 characters and then never loads
    /// the skill, without saying so.</summary>
    [Test]
    public async Task Description_stays_within_the_1024_char_skill_limit() {
        var length = FoldedDescription().Length;

        await Assert.That(length).IsGreaterThan(0);
        await Assert.That(length).IsLessThanOrEqualTo(1024);
    }

    [Test]
    public async Task Description_is_triggered_by_a_question_about_a_period() {
        var description = FoldedDescription();

        await Assert.That(description).Contains("in the last two weeks");
        await Assert.That(description).Contains("what did the team do yesterday");
    }

    [GeneratedRegex(@"(?m)^description: >-\n((?:^  .*\n)+)")]
    private static partial Regex Description();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
