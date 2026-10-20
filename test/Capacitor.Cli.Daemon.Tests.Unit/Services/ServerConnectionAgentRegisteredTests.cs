using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>Registration asks the server for its answer through <c>AgentRegisteredAcked</c>, and falls back to
/// the unanswered <c>AgentRegistered</c> against a server that has no such method.</summary>
public class ServerConnectionAgentRegisteredTests {
    sealed class TestServerConnection() : ServerConnection(
        new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1" },
        new FixedCredentialSource(),
        NullLoggerFactory.Instance,
        NullLogger<ServerConnection>.Instance,
        TimeProvider.System) {
        public string? ConnectionId { get; set; } = "connection-1";

        internal override string? CurrentConnectionId => ConnectionId;

        public Func<AgentRegistered, Task<AgentRegisteredResponse?>> Acked { get; set; } = _ => Task.FromResult<AgentRegisteredResponse?>(AgentRegisteredResponse.Accepted);

        public List<AgentRegistered> AckedCalls  { get; } = [];
        public List<AgentRegistered> LegacyCalls { get; } = [];

        internal override Task<AgentRegisteredResponse?> InvokeAgentRegisteredAckedRawAsync(AgentRegistered registration) {
            AckedCalls.Add(registration);
            return Acked(registration);
        }

        internal override Task InvokeAgentRegisteredRawAsync(AgentRegistered registration) {
            LegacyCalls.Add(registration);
            return Task.CompletedTask;
        }
    }

    static Task<AgentRegisteredResponse> Register(ServerConnection conn, string agentId = "agent-1") =>
        conn.AgentRegisteredAsync(agentId, "prompt", "model", null, "/repo", runtimeTransport: "pty");

    static Task<AgentRegisteredResponse?> UnknownMethod(AgentRegistered _) =>
        Task.FromException<AgentRegisteredResponse?>(new HubException("Unknown hub method 'AgentRegisteredAcked'"));

    [Test]
    public async Task An_accepted_registration_is_returned_and_sends_only_the_acked_call() {
        var conn = new TestServerConnection();

        var response = await Register(conn);

        await Assert.That(response.Registered).IsTrue();
        await Assert.That(conn.AckedCalls.Single().AgentId).IsEqualTo("agent-1");
        await Assert.That(conn.AckedCalls.Single().RuntimeTransport).IsEqualTo("pty");
        await Assert.That(conn.LegacyCalls).IsEmpty();
    }

    [Test]
    public async Task A_refusal_is_returned_with_its_reason() {
        var conn = new TestServerConnection {
            Acked = _ => Task.FromResult<AgentRegisteredResponse?>(new AgentRegisteredResponse(false, "recorded_failure"))
        };

        var response = await Register(conn);

        await Assert.That(response).IsEqualTo(new AgentRegisteredResponse(false, "recorded_failure"));
        await Assert.That(conn.LegacyCalls).IsEmpty();
    }

    [Test]
    public async Task A_server_without_the_acked_method_gets_the_legacy_call_and_reads_as_accepted() {
        var conn = new TestServerConnection { Acked = UnknownMethod };

        var response = await Register(conn);

        await Assert.That(response.Registered).IsTrue();
        await Assert.That(conn.LegacyCalls.Single().AgentId).IsEqualTo("agent-1");
        await Assert.That(conn.LegacyCalls.Single().RuntimeTransport).IsEqualTo("pty");
    }

    /// <summary>The missing method is remembered per connection: a reconnect may reach an upgraded server.</summary>
    [Test]
    public async Task The_missing_method_is_not_probed_again_until_the_connection_changes() {
        var conn = new TestServerConnection { Acked = UnknownMethod };

        await Register(conn, "agent-1");
        await Register(conn, "agent-2");

        await Assert.That(conn.AckedCalls.Count).IsEqualTo(1);
        await Assert.That(conn.LegacyCalls.Select(c => c.AgentId)).IsEquivalentTo(["agent-1", "agent-2"]);

        conn.ConnectionId = "connection-2";
        conn.Acked = _ => Task.FromResult<AgentRegisteredResponse?>(new AgentRegisteredResponse(false, "no_capacity"));

        var response = await Register(conn, "agent-3");

        await Assert.That(conn.AckedCalls.Count).IsEqualTo(2);
        await Assert.That(response.Registered).IsFalse();
        await Assert.That(conn.LegacyCalls.Count).IsEqualTo(2);
    }

    /// <summary>Any other hub failure is no proof the method is missing, so it surfaces to the caller's retry.</summary>
    [Test]
    public async Task Another_hub_failure_propagates_without_the_legacy_call() {
        var conn = new TestServerConnection {
            Acked = _ => Task.FromException<AgentRegisteredResponse?>(new HubException("Caller is not a registered daemon"))
        };

        await Assert.That(async () => await Register(conn)).Throws<HubException>();
        await Assert.That(conn.LegacyCalls).IsEmpty();
    }
}
