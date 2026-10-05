using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>agent-flows is the one skill that drives any catalogue flow, offered ones included. Its folded
/// description is the trigger surface a harness sees; a strict harness drops a skill over 1024 characters.</summary>
public class AgentFlowsSkillConformanceTests {
    static string SkillText() => File.ReadAllText(Path.Combine(RepoTree.SkillsSource(), "agent-flows", "SKILL.md"));

    static string FoldedDescription() {
        var text = SkillText().Replace("\r\n", "\n").Replace("\r", "\n");
        var m = System.Text.RegularExpressions.Regex.Match(text, @"(?m)^description: >-\n((?:^  .*\n)+)");
        return System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim();
    }

    [Test]
    public async Task Description_stays_within_the_1024_char_skill_limit() {
        var len = FoldedDescription().Length;
        await Assert.That(len).IsGreaterThan(0);
        await Assert.That(len).IsLessThanOrEqualTo(1024);
    }

    [Test]
    public async Task Description_triggers_on_an_offered_flow_from_session_context() {
        var d = FoldedDescription();
        await Assert.That(d).Contains("Flows you may offer");
        await Assert.That(d).Contains("get_flow_definition");
    }

    [Test]
    public async Task Body_reads_the_guide_before_starting() =>
        await Assert.That(SkillText()).Contains("## Read the flow's guide first");

    [Test]
    public async Task Body_names_no_built_in_definition_as_the_default_choice() {
        var text = SkillText();
        await Assert.That(text).DoesNotContain("Spec or design document → `definition_id: \"spec-review\"`");
        await Assert.That(text).DoesNotContain("For a code review flow (`definition_id: \"code-review\"`)");
    }

    [Test]
    public async Task Suggest_review_flow_is_retired() {
        await Assert.That(Directory.Exists(Path.Combine(RepoTree.SkillsSource(), "suggest-review-flow"))).IsFalse();
        await Assert.That(AgentsSkillsInstaller.RetiredSourceNames).Contains("suggest-review-flow");
    }
}
