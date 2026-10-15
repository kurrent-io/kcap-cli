using Capacitor.Cli.Harness.MistralVibe;

namespace Capacitor.Cli.Tests.Unit.Harness.MistralVibe;

public class MistralVibeUsageStampTests {
    const string User      = """{"type":"message","id":"u","role":"user","content":[{"type":"text","text":"go"}]}""";
    const string Assistant = """{"type":"message","id":"a","role":"assistant","content":[{"type":"text","text":"done"}]}""";
    const string Effect    = """{"type":"effect","id":"e","detail":{"toolName":"file_system.bash"},"state":{"status":"completed"}}""";

    [Test]
    public async Task The_tokens_since_the_last_stamp_go_on_the_latest_assistant_message() {
        var lines = MistralVibeUsageStamp.Apply([User, Assistant, Effect], new(100, 10, 50), new(400, 30, 250), "codestral-latest");

        await Assert.That(lines[0]).IsEqualTo(User);
        await Assert.That(lines[2]).IsEqualTo(Effect);
        await Assert.That(lines[1]).Contains("\"kcapUsage\":{\"inputTokens\":300,\"outputTokens\":20,\"cachedInputTokens\":200,\"model\":\"codestral-latest\"");
        await Assert.That(MistralVibeUsageStamp.LastStamped(lines)).IsEqualTo(new MistralVibeTokenUsage(400, 30, 250));
    }

    [Test]
    public async Task Nothing_is_stamped_without_new_tokens_or_an_assistant_message() {
        IReadOnlyList<string> lines = [User, Assistant];

        await Assert.That(MistralVibeUsageStamp.Apply(lines, new(5, 5, 0), new(5, 5, 0), null)).IsEquivalentTo(lines);
        await Assert.That(MistralVibeUsageStamp.Apply([User, Effect], MistralVibeTokenUsage.Zero, new(9, 9, 0), null)).IsEquivalentTo([User, Effect]);
        await Assert.That(MistralVibeUsageStamp.LastStamped(lines)).IsEqualTo(MistralVibeTokenUsage.Zero);
    }

    [Test]
    [Arguments("child-1d89390ca0ec23cdd4c58954", "1d89390ca0ec23cdd4c58954")]
    [Arguments("child-", null)]
    [Arguments("child-ab-cd", null)]
    [Arguments("1d89390ca0ec23cdd4c58954", null)]
    public async Task A_child_store_name_yields_a_hyphen_free_agent_id(string childSessionId, string? agentId) {
        await Assert.That(new MistralVibeSubagent(childSessionId, null).AgentId).IsEqualTo(agentId);
    }
}
