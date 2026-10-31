using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>Pins the ReportAgentTerminalDialog payload's field names, which the server binds by name
/// and relays as <c>terminal_dialog</c> under the same names.</summary>
public class ServerConnectionTerminalDialogWireTests {
    [Test]
    public async Task The_dialog_report_serializes_with_the_names_the_server_binds() {
        var options = new JsonSerializerOptions();
        ServerConnection.ConfigureHubPayload(options);
        var args = new ReportAgentTerminalDialogArgs("a1",
            new TerminalDialogDto("Heading", "Body", ["One", "Two"], 1, "screen"));

        var json = JsonSerializer.Serialize(args, options);

        await Assert.That(json).IsEqualTo(
            """{"agent_id":"a1","dialog":{"heading":"Heading","body":"Body","options":["One","Two"],"selected":1,"screen":"screen"}}""");
    }

    [Test]
    public async Task A_closed_dialog_serializes_as_null() {
        var options = new JsonSerializerOptions();
        ServerConnection.ConfigureHubPayload(options);

        var json = JsonSerializer.Serialize(new ReportAgentTerminalDialogArgs("a1", null), options);

        await Assert.That(json).IsEqualTo("""{"agent_id":"a1","dialog":null}""");
    }
}
