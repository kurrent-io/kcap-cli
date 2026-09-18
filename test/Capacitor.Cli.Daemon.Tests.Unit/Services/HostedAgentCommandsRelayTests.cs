using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>Pins the relay's timing contract: a command list a runtime learns DURING start (an ACP
/// handshake snapshot, a Codex skills/list) is buffered and delivered the instant the orchestrator
/// attaches its callback afterwards — the window the picker would otherwise miss.</summary>
public class HostedAgentCommandsRelayTests {
    static readonly IReadOnlyList<HostedAgentCommand> Sample = [new HostedAgentCommand("compact", null, null)];

    [Test]
    public async Task Publish_before_a_callback_attaches_is_flushed_on_attach() {
        var relay = new HostedAgentCommandsRelay();
        relay.Publish(Sample); // captured during start, no callback yet

        IReadOnlyList<HostedAgentCommand>? seen = null;
        relay.Callback = c => seen = c; // orchestrator attaches after start

        await Assert.That(seen).IsNotNull();
        await Assert.That(seen!.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Publish_after_a_callback_attaches_flows_straight_through() {
        var relay = new HostedAgentCommandsRelay();
        var count = 0;
        relay.Callback = _ => count++;

        relay.Publish(Sample);
        relay.Publish(Sample);

        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task Attaching_a_null_callback_never_invokes_and_never_throws() {
        var relay = new HostedAgentCommandsRelay();
        relay.Publish(Sample);
        relay.Callback = null;

        await Assert.That(relay.Callback).IsNull();
    }
}
