using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class ServerConnectionAdvertisementTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task DaemonConnect_carries_the_configured_vendor_models() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        config.VendorModels = new(StringComparer.Ordinal) { ["pi"] = [new("p/a", "A · p")] };
        await using var conn = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);

        var connect = conn.BuildDaemonConnect("mac", [], [], null);

        await Assert.That(ReferenceEquals(connect.VendorModels, config.VendorModels)).IsTrue();
    }

    [Test]
    public async Task DaemonConnect_before_any_probe_carries_null_vendor_models() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        await using var conn = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);

        await Assert.That(conn.BuildDaemonConnect("mac", [], [], null).VendorModels).IsNull();
    }

    [Test]
    public async Task DaemonConnect_advertises_raw_input_once_a_handler_is_wired() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        await using var conn = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);
        conn.OnSendRawInput += _ => Task.CompletedTask;

        await Assert.That(conn.BuildDaemonConnect("mac", [], [], null).SupportsRawInput).IsTrue();
    }

    [Test]
    public async Task DaemonConnect_without_a_raw_input_handler_does_not_advertise_it() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        await using var conn = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);

        await Assert.That(conn.BuildDaemonConnect("mac", [], [], null).SupportsRawInput).IsFalse();
    }

    [Test]
    public async Task DaemonConnect_advertises_branch_base_refs() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        await using var conn = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);

        await Assert.That(conn.BuildDaemonConnect("mac", [], [], null).SupportsBranchBaseRef).IsTrue();
    }

    [Test]
    public async Task DaemonConnect_serializes_its_capability_flags_with_snake_case_names() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        await using var conn = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);
        conn.OnSendRawInput += _ => Task.CompletedTask;

        var json = System.Text.Json.JsonSerializer.Serialize(
            conn.BuildDaemonConnect("mac", [], [], null), CapacitorJsonContext.Default.DaemonConnect);

        await Assert.That(json).Contains("\"supports_branch_base_ref\":true");
        await Assert.That(json).Contains("\"supports_raw_input\":true");
    }

    [Test]
    public async Task BuildDaemonConnect_carries_resuming_claims() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        await using var conn = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);
        ResumingAgentClaim[] claims = [new("agent-1", "instance-old", "session-1")];
        conn.GetResumingAgents = () => claims;

        var connect = conn.BuildDaemonConnect("mac", [], [], null);
        var json    = System.Text.Json.JsonSerializer.Serialize(connect, CapacitorJsonContext.Default.DaemonConnect);

        await Assert.That(connect.ResumingAgents).IsEquivalentTo(claims);
        await Assert.That(json).Contains(
            "\"resuming_agents\":[{\"agent_id\":\"agent-1\",\"predecessor_instance_id\":\"instance-old\",\"session_id\":\"session-1\"}]");
    }

    [Test]
    public async Task SendRawInputCommand_serializes_with_snake_case_names() {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new SendRawInputCommand("a-1", "Gw==", Guid.Parse("00000000-0000-0000-0000-000000000001")),
            CapacitorJsonContext.Default.SendRawInputCommand);

        await Assert.That(json).IsEqualTo("{\"agent_id\":\"a-1\",\"data\":\"Gw==\",\"dispatch_id\":\"00000000-0000-0000-0000-000000000001\"}");
    }
}
