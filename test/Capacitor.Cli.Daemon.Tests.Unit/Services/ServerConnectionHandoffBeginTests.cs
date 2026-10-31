using Capacitor.Cli.Core.Handoff;
using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.SignalR;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>Only the hub's own missing-method reply reads as an old server; every other failure is unreachable.</summary>
public class ServerConnectionHandoffBeginTests {
    [Test]
    public async Task An_answer_is_answered() {
        var server = new CaptureServerConnection { BeginHandoff = (_, _) => new HandoffTakeoverAdmission("t", null, 60) };

        var result = await server.BeginHandoffTakeoverAsync("a", "b", CancellationToken.None);

        await Assert.That(result).IsTypeOf<HandoffBeginResult.Answered>();
    }

    [Test]
    public async Task A_missing_method_is_unsupported() {
        var server = new CaptureServerConnection();

        await Assert.That(await server.BeginHandoffTakeoverAsync("a", "b", CancellationToken.None)).IsTypeOf<HandoffBeginResult.Unsupported>();
    }

    [Test]
    [Arguments("transport")]
    [Arguments("hub")]
    [Arguments("null")]
    public async Task Any_other_failure_is_unreachable(string failure) {
        var server = new CaptureServerConnection {
            BeginHandoff = (_, _) => failure switch {
                "transport" => throw new InvalidOperationException("The 'InvokeCoreAsync' method cannot be called if the connection is not active"),
                "hub"       => throw new HubException("Failed to invoke 'BeginHandoffTakeover' due to an error on the server."),
                _           => null
            }
        };

        await Assert.That(await server.BeginHandoffTakeoverAsync("a", "b", CancellationToken.None)).IsTypeOf<HandoffBeginResult.Unreachable>();
    }
}
