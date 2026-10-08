using Capacitor.Cli.Core.Harness.MistralVibe;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.MistralVibe;

public class MistralVibeHooksParserTests {
    [Test]
    [Arguments("kcap hook --mistral-vibe", true)]
    [Arguments("/usr/local/bin/kcap hook --mistral-vibe", true)]   // path-qualified executable still matches
    [Arguments("kcap.exe hook --mistral-vibe", true)]
    [Arguments("echo kcap hook --mistral-vibe", false)]            // marker buried in an argument, not the exe
    [Arguments("kcap hook --gemini", false)]
    [Arguments("kcap hook", false)]
    [Arguments("", false)]
    public async Task Ownership_is_matched_by_tokens_not_substring(string command, bool owned) {
        await Assert.That(MistralVibeHooksParser.IsCapacitorVibeHookCommand(command)).IsEqualTo(owned);
    }

    [Test]
    public async Task Build_entry_carries_name_type_and_command() {
        var entry = MistralVibeHooksParser.BuildKcapEntry("post_agent");

        await Assert.That(entry["name"]).IsEqualTo("kcap");
        await Assert.That(entry["type"]).IsEqualTo("post_agent");
        await Assert.That(entry["command"]).IsEqualTo(MistralVibeHooksParser.HookCommand);
    }

    [Test]
    public async Task Hook_types_are_the_three_vibe_lifecycle_types() {
        await Assert.That(MistralVibeHooksParser.VibeHookTypes).IsEquivalentTo(new[] { "pre_tool", "post_tool", "post_agent" });
    }
}
