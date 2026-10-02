using System.Text.Json;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class StartAgentToolSchemaTests {
    static McpTool Tool() => McpFlowsServer.BuildToolsList().Single(t => t.Name == "start_agent");

    [Test]
    public async Task The_tool_requires_cwd_prompt_and_work_item_and_nothing_else() {
        await Assert.That(Tool().InputSchema.Required).IsEquivalentTo(new[] { "cwd", "prompt", "work_item" });
    }

    [Test]
    public async Task The_tool_offers_the_seven_arguments_and_each_is_a_string() {
        var properties = Tool().InputSchema.Properties;

        await Assert.That(properties.Keys.ToArray())
            .IsEquivalentTo(new[] { "cwd", "prompt", "work_item", "vendor", "model", "daemon", "session_id" });

        foreach (var (name, property) in properties)
            await Assert.That(property.Type).IsEqualTo("string").Because(name);
    }

    /// <summary>A launch is never read-only and never idempotent: a harness that decides approval from
    /// annotations must prompt for it.</summary>
    [Test]
    public async Task The_tool_is_a_launch() {
        await Assert.That(Tool().Annotations).IsEqualTo(McpToolAnnotations.Launch);
    }

    [Test]
    public async Task The_description_says_the_call_returns_at_once_and_promises_no_attachment() {
        var description = Tool().Description;

        await Assert.That(description).Contains("return at once");
        await Assert.That(description).Contains("does not block");
        await Assert.That(description).Contains("`requested`");
        await Assert.That(description).Contains("only after the user asked");
        await Assert.That(description).Contains("nothing is attached when this call returns");
        await Assert.That(description).Contains("no default");
    }

    /// <summary>The look-alikes an agent reaches for instead: a subagent that stays inside this
    /// session, a flow it would have to drive, and the CLI that bypasses the server's limits.</summary>
    [Test]
    public async Task The_description_turns_the_agent_away_from_the_look_alikes() {
        var description = Tool().Description;

        await Assert.That(description).Contains("not your harness's built-in subagent or background-agent tool");
        await Assert.That(description).Contains("not a flow");
        await Assert.That(description).Contains("not the `kcap agent` CLI");
    }

    [Test]
    public async Task The_request_is_snake_case_and_leaves_absent_options_off_the_wire() {
        var json = JsonSerializer.Serialize(
            new StartAgentDto("s1", "/r/src", "/r", "Fix the retry.", "none", "claude"),
            McpJsonContext.Default.StartAgentDto);

        await Assert.That(json).IsEqualTo(
            """{"session_id":"s1","cwd":"/r/src","repo_path":"/r","prompt":"Fix the retry.","work_item":"none","vendor":"claude"}""");
    }

    [Test]
    public async Task The_request_carries_every_option_that_is_set() {
        var json = JsonSerializer.Serialize(
            new StartAgentDto("s1", "/r", "/r", "p", "none", "codex", "m-1", "a1b2c3d4", "gpt-5-codex", "mac-studio"),
            McpJsonContext.Default.StartAgentDto);

        await Assert.That(json).Contains("\"machine_id\":\"m-1\"");
        await Assert.That(json).Contains("\"caller_agent_id\":\"a1b2c3d4\"");
        await Assert.That(json).Contains("\"model\":\"gpt-5-codex\"");
        await Assert.That(json).Contains("\"daemon\":\"mac-studio\"");
    }
}
