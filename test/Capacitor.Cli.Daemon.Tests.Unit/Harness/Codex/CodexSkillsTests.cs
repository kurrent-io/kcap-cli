using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Codex;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>Pins the defensive Codex <c>skills/list</c> mapping: a flat array or a hooks/list-style
/// grouping both yield <c>{name, description}</c> commands, and an absent or unexpected shape yields
/// an empty list rather than throwing, so the picker fails safe to empty.</summary>
public class CodexSkillsTests {
    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Test]
    public async Task Extract_reads_a_flat_data_array() {
        var result = Parse("""{ "data": [ { "name": "review", "description": "Review a PR" }, { "name": "plan" } ] }""");

        var commands = CodexSkills.Extract(result);

        await Assert.That(commands.Count).IsEqualTo(2);
        await Assert.That(commands[0]).IsEqualTo(new HostedAgentCommand("review", "Review a PR", null));
        await Assert.That(commands[1]).IsEqualTo(new HostedAgentCommand("plan", null, null));
    }

    [Test]
    public async Task Extract_reads_a_grouped_shape_with_nested_skills() {
        var result = Parse("""{ "data": [ { "skills": [ { "name": "a" }, { "name": "b", "description": "B" } ] } ] }""");

        var commands = CodexSkills.Extract(result);

        await Assert.That(commands.Count).IsEqualTo(2);
        await Assert.That(commands[1]).IsEqualTo(new HostedAgentCommand("b", "B", null));
    }

    [Test]
    public async Task Extract_returns_empty_for_an_unexpected_shape() {
        await Assert.That(CodexSkills.Extract(Parse("""{ "somethingElse": 1 }"""))).IsEmpty();
        await Assert.That(CodexSkills.Extract(Parse("""{ "data": [] }"""))).IsEmpty();
    }
}
