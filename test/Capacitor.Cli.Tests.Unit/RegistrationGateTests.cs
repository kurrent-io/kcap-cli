using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.SignalR.Client;

namespace Capacitor.Cli.Tests.Unit;

public class RegistrationGateTests {
    [Test]
    public async Task Not_ready_until_registered() {
        var gate = new RegistrationGate();

        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsFalse();

        gate.MarkRegistered();

        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsTrue();
    }

    [Test]
    public async Task Connection_loss_clears_readiness_even_if_state_still_connected() {
        var gate = new RegistrationGate();
        gate.MarkRegistered();

        gate.MarkUnregistered();

        // Models the Reconnected-before-RegisterDaemon window: state reports
        // Connected but we have not re-registered yet.
        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsFalse();
    }

    [Test]
    public async Task Disconnected_states_are_never_ready() {
        var gate = new RegistrationGate();
        gate.MarkRegistered();

        await Assert.That(gate.IsReady(HubConnectionState.Reconnecting)).IsFalse();
        await Assert.That(gate.IsReady(HubConnectionState.Disconnected)).IsFalse();
        await Assert.That(gate.IsReady(HubConnectionState.Connecting)).IsFalse();
    }

    [Test]
    public async Task Re_registration_restores_readiness() {
        var gate = new RegistrationGate();
        gate.MarkRegistered();
        gate.MarkUnregistered();

        gate.MarkRegistered();

        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsTrue();
    }

    [Test]
    public async Task Slot_displacement_without_transport_loss_drops_readiness_until_reregistered() {
        var gate = new RegistrationGate();
        gate.MarkRegistered();
        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsTrue();

        // Heartbeat sees PingAsync()==false and re-registers. RegisterDaemon()
        // brackets the call: MarkUnregistered() at start, MarkRegistered() on
        // success — the transport never dropped, so state stays Connected.
        gate.MarkUnregistered();
        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsFalse();

        gate.MarkRegistered();
        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsTrue();
    }
}
