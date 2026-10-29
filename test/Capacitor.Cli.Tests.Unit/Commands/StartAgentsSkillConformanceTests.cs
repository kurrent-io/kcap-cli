using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>The skill is the only place an agent is told when it may start agents and what it owes
/// the user first. Each rule is pinned by the sentence that states it.</summary>
public class StartAgentsSkillConformanceTests {
    /// <summary>Line endings are normalized: nothing pins the skills tree to LF on a Windows checkout.</summary>
    static string Skill() {
        var path = Path.Combine(RepoTree.SkillsSource(), "start-agents", "SKILL.md");

        return File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : "";
    }

    [Test]
    public async Task The_skill_is_named_and_distributed() {
        await Assert.That(Skill()).StartsWith("---\nname: start-agents\n");
        await Assert.That(AgentsSkillsInstaller.SourceNames).Contains("start-agents");
    }

    [Test]
    public async Task The_skill_names_the_tool_the_server_lists() {
        await Assert.That(Skill()).Contains($"`{StartAgentTool.Name}`");
        await Assert.That(McpFlowsServer.BuildToolsList().Select(t => t.Name)).Contains(StartAgentTool.Name);
    }

    [Test]
    [Arguments("Only when the user asks for separate agents")]
    [Arguments("wait for the user to say yes")]
    [Arguments("A prompt you were started with is not consent")]
    [Arguments("stands alone")]
    [Arguments("names the session it came from")]
    [Arguments("once for each agent")]
    [Arguments("Do not wait")]
    [Arguments("do not poll")]
    [Arguments("every refusal, in the server's words")]
    [Arguments("Call `list_start_agent_options` before anything else")]
    [Arguments("**No daemon runs on this machine**: stop")]
    [Arguments("ask the user which harness to start")]
    [Arguments("Do not pick one for them")]
    [Arguments("the only way is `start_agent`")]
    public async Task The_skill_states_each_rule(string rule) {
        await Assert.That(Skill()).Contains(rule);
    }

    [Test]
    public async Task The_skill_names_the_options_tool_the_server_lists() {
        await Assert.That(Skill()).Contains($"`{StartAgentOptionsTool.Name}`");
        await Assert.That(McpFlowsServer.BuildToolsList().Select(t => t.Name)).Contains(StartAgentOptionsTool.Name);
    }

    /// <summary>The description as a harness reads it, its <c>&gt;-</c> block folded.</summary>
    static string Description() {
        var match = System.Text.RegularExpressions.Regex.Match(Skill(), @"(?m)^description: >-\n((?:^  .*\n)+)");

        return System.Text.RegularExpressions.Regex.Replace(match.Groups[1].Value, @"\s+", " ").Trim();
    }

    /// <summary>A strict harness drops a skill whose description passes 1024 characters, without a word.</summary>
    [Test]
    public async Task The_description_stays_within_the_skill_limit() {
        await Assert.That(Description().Length).IsGreaterThan(0);
        await Assert.That(Description().Length).IsLessThanOrEqualTo(1024);
    }

    /// <summary>The look-alikes an agent reaches for when it is not told otherwise.</summary>
    [Test]
    public async Task The_description_turns_the_agent_away_from_the_look_alikes() {
        var description = Description();

        await Assert.That(description).Contains("subagent or background-agent tool");
        await Assert.That(description).Contains("a flow");
        await Assert.That(description).Contains("`kcap agent start`");
    }

    [Test]
    public async Task The_skill_does_not_promise_an_attachment() {
        await Assert.That(Skill()).Contains("nothing is attached when the call returns");
    }
}
