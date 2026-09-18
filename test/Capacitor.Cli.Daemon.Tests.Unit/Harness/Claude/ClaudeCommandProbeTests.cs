using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Claude;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

/// <summary>Pins the parse half of the Claude command probe against the real
/// <c>--input-format stream-json</c> initialize response shape — commands live at
/// <c>.response.response.commands</c>, each <c>{name, description, argumentHint}</c> — so a future
/// stream-json change surfaces here rather than as a silently empty picker.</summary>
public class ClaudeCommandProbeTests {
    // The control_response line, preceded by the system/hook lines the probe skips.
    const string Stdout =
        "{\"type\":\"system\",\"subtype\":\"hook_started\"}\n" +
        "{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"kcap-command-probe\"," +
        "\"response\":{\"commands\":[" +
        "{\"name\":\"compact\",\"description\":\"Compact the conversation\",\"argumentHint\":\"\"}," +
        "{\"name\":\"model\",\"description\":\"\",\"argumentHint\":\"[model]\"}," +
        "{\"name\":\"plugin:kcap:recap\",\"description\":\"Recap a session (user)\",\"argumentHint\":\"[id]\"}" +
        "]}}}\n";

    [Test]
    public async Task ParseCommands_reads_name_description_and_argument_hint_from_the_control_response() {
        var commands = ClaudeCommandProbe.ParseCommands(Stdout);

        await Assert.That(commands.Count).IsEqualTo(3);
        await Assert.That(commands[0]).IsEqualTo(new HostedAgentCommand("compact", "Compact the conversation", null));
        // Blank description and blank argumentHint both collapse to null.
        await Assert.That(commands[1]).IsEqualTo(new HostedAgentCommand("model", null, "[model]"));
        await Assert.That(commands[2].Name).IsEqualTo("plugin:kcap:recap");
    }

    [Test]
    public async Task ParseCommands_returns_empty_when_no_control_response_is_present() {
        var commands = ClaudeCommandProbe.ParseCommands("{\"type\":\"system\",\"subtype\":\"init\"}\n");

        await Assert.That(commands).IsEmpty();
    }

    [Test]
    public async Task ParseCommands_returns_empty_on_garbage() {
        await Assert.That(ClaudeCommandProbe.ParseCommands("not json\n\n")).IsEmpty();
    }
}
