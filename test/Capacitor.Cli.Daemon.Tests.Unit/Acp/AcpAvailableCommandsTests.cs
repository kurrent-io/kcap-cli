using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Daemon.Tests.Unit.Acp;

/// <summary>Pins the ACP <c>availableCommands</c> extraction the daemon feeds to the composer's `/`
/// picker — the same <c>{name, description}</c> shape whether it rides an
/// <c>available_commands_update</c> or a <c>session/new</c> result. ACP carries no argument hint.</summary>
public class AcpAvailableCommandsTests {
    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Test]
    public async Task Extract_reads_name_and_description_and_leaves_argument_hint_null() {
        var update = Parse("""
            { "sessionUpdate": "available_commands_update", "availableCommands": [
                { "name": "simplify", "description": "Find low-info comments" },
                { "name": "babysit", "description": "" }
            ] }
            """);

        var commands = AcpAvailableCommands.Extract(update);

        await Assert.That(commands.Count).IsEqualTo(2);
        await Assert.That(commands[0]).IsEqualTo(new HostedAgentCommand("simplify", "Find low-info comments", null));
        await Assert.That(commands[1]).IsEqualTo(new HostedAgentCommand("babysit", null, null));
    }

    [Test]
    public async Task Extract_skips_entries_with_no_name() {
        var update = Parse("""{ "availableCommands": [ { "description": "nameless" }, { "name": "keep" } ] }""");

        var commands = AcpAvailableCommands.Extract(update);

        await Assert.That(commands.Count).IsEqualTo(1);
        await Assert.That(commands[0].Name).IsEqualTo("keep");
    }

    [Test]
    public async Task Extract_returns_empty_when_the_property_is_absent_or_the_source_is_null() {
        await Assert.That(AcpAvailableCommands.Extract(Parse("""{ "sessionUpdate": "plan" }"""))).IsEmpty();
        await Assert.That(AcpAvailableCommands.Extract(null)).IsEmpty();
    }
}
