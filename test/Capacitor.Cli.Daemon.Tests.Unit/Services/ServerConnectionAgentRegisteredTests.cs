using System.Text.Json;
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

    /// <summary>What a JSON-protocol server without the method answers; <see cref="ServerConnectionLegacyHubWireTests"/>
    /// takes it from a real dispatcher.</summary>
    const string MethodDoesNotExist =
        "Failed to invoke 'AgentRegisteredAcked' due to an error on the server. HubException: Method does not exist.";

    static Task<AgentRegisteredResponse?> UnknownMethod(AgentRegistered _) =>
        Task.FromException<AgentRegisteredResponse?>(new HubException(MethodDoesNotExist));

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

    [Test]
    public async Task The_registration_names_the_account_on_both_calls() {
        var acked  = new TestServerConnection();
        var legacy = new TestServerConnection { Acked = UnknownMethod };

        await acked.AgentRegisteredAsync("agent-1", "prompt", "model", null, "/repo", accountDirectory: "/home/.claude-work", accountId: "work-id", accountLabel: "Work");
        await legacy.AgentRegisteredAsync("agent-1", "prompt", "model", null, "/repo", accountDirectory: "/home/.claude", accountLabel: ".claude");

        var named = acked.AckedCalls.Single();
        await Assert.That((named.AccountDirectory, named.AccountId, named.AccountLabel)).IsEqualTo(((string?)"/home/.claude-work", (string?)"work-id", (string?)"Work"));

        var fallback = legacy.LegacyCalls.Single();
        await Assert.That((fallback.AccountDirectory, fallback.AccountId, fallback.AccountLabel)).IsEqualTo(((string?)"/home/.claude", (string?)null, (string?)".claude"));
    }

    /// <summary>A server that predates the field ignores it, so the same registration serves both methods.</summary>
    [Test]
    public async Task The_continued_agent_rides_on_the_acked_and_the_legacy_call() {
        var acked  = new TestServerConnection();
        var legacy = new TestServerConnection { Acked = UnknownMethod };

        await acked.AgentRegisteredAsync("agent-2", "prompt", "model", null, "/repo", continuesAgentId: "agent-1");
        await legacy.AgentRegisteredAsync("agent-2", "prompt", "model", null, "/repo", continuesAgentId: "agent-1");

        await Assert.That(acked.AckedCalls.Single().ContinuesAgentId).IsEqualTo("agent-1");
        await Assert.That(legacy.LegacyCalls.Single().ContinuesAgentId).IsEqualTo("agent-1");
    }

    /// <summary>Serialized as the hub sends it: the claim travels only when there is one, so an ordinary agent's
    /// registration carries no continuation field at all.</summary>
    [Test]
    public async Task The_hub_payload_names_a_continued_agent_and_omits_the_field_otherwise() {
        var options = new JsonHubProtocolOptions().PayloadSerializerOptions;
        ServerConnection.ConfigureHubPayload(options);

        var successor = JsonSerializer.Serialize(new AgentRegistered("a2", null, "opus", null, "/r", ContinuesAgentId: "a1"), options);
        var ordinary  = JsonSerializer.Serialize(new AgentRegistered("a1", null, "opus", null, "/r"), options);

        await Assert.That(successor).Contains("\"continues_agent_id\":\"a1\"");
        await Assert.That(ordinary).DoesNotContain("continues_agent_id");
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

    /// <summary>A reply with no outcome proves nothing either way, so it is a failure rather than an acceptance.</summary>
    [Test]
    public async Task A_reply_without_an_outcome_fails_rather_than_reading_as_accepted() {
        var conn = new TestServerConnection { Acked = _ => Task.FromResult<AgentRegisteredResponse?>(null) };

        await Assert.That(async () => await Register(conn)).Throws<InvalidOperationException>();
        await Assert.That(conn.LegacyCalls).IsEmpty();
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

    /// <summary>Both dispatcher answers for a missing method count, and only for the method that was invoked;
    /// a handler's refusal, another method's absence or an exception that is not a hub answer never does.</summary>
    [Test]
    [Arguments("hub", MethodDoesNotExist, "AgentRegisteredAcked", true)]
    [Arguments("hub", "Unknown hub method 'AgentRegisteredAcked'", "AgentRegisteredAcked", true)]
    [Arguments("hub", MethodDoesNotExist, "ReportParticipantParked", false)]
    [Arguments("hub", "Unknown hub method 'AgentRegisteredAckedV2'", "AgentRegisteredAcked", false)]
    [Arguments("hub", "Failed to invoke 'AgentRegisteredAcked' due to an error on the server. HubException: Caller is not a registered daemon", "AgentRegisteredAcked", false)]
    [Arguments("hub", "Failed to invoke 'AgentRegisteredAcked' due to an error on the server.", "AgentRegisteredAcked", false)]
    [Arguments("invalid-operation", MethodDoesNotExist, "AgentRegisteredAcked", false)]
    [Arguments("canceled", MethodDoesNotExist, "AgentRegisteredAcked", false)]
    public async Task Only_the_invoked_methods_absence_reads_as_a_missing_method(string kind, string message, string method, bool missing) {
        Exception ex = kind switch {
            "hub"               => new HubException(message),
            "invalid-operation" => new InvalidOperationException(message),
            _                   => new OperationCanceledException(message),
        };

        await Assert.That(ServerConnection.IsUnknownHubMethod(ex, method)).IsEqualTo(missing);
    }
}
