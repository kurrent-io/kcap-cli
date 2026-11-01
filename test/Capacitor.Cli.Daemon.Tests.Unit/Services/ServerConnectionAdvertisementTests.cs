using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.SignalR.Client;
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

        var connect = conn.BuildDaemonConnect("mac", [], [], null, config.Advertisement);

        await Assert.That(ReferenceEquals(connect.VendorModels, config.VendorModels)).IsTrue();
    }

    [Test]
    public async Task DaemonConnect_before_any_probe_carries_null_vendor_models() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        await using var conn = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);

        await Assert.That(conn.BuildDaemonConnect("mac", [], [], null, config.Advertisement).VendorModels).IsNull();
    }

    [Test]
    public async Task DaemonConnect_without_a_headless_handler_advertises_none() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        await using var conn = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);
        conn.GetHeadlessVendors = () => ["claude"];

        var vendors = conn.BuildDaemonConnect("mac", [], [], null, config.Advertisement).HeadlessVendors;

        await Assert.That(vendors).IsNotNull();
        await Assert.That(vendors!).IsEmpty();
    }

    [Test]
    public async Task DaemonConnect_advertises_the_headless_harnesses_once_wired() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        await using var conn = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);
        conn.RunHeadlessPromptHandler = _ => Task.FromResult(default(HeadlessPromptResponse));
        conn.GetHeadlessVendors       = () => ["claude", "codex"];

        await Assert.That(conn.BuildDaemonConnect("mac", [], [], null, config.Advertisement).HeadlessVendors).IsEquivalentTo(["claude", "codex"]);
    }

    [Test]
    public async Task DaemonConnect_advertises_raw_input_once_a_handler_is_wired() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        await using var conn = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);
        conn.OnSendRawInput += _ => Task.CompletedTask;

        await Assert.That(conn.BuildDaemonConnect("mac", [], [], null, config.Advertisement).SupportsRawInput).IsTrue();
    }

    [Test]
    public async Task DaemonConnect_without_a_raw_input_handler_does_not_advertise_it() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        await using var conn = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);

        await Assert.That(conn.BuildDaemonConnect("mac", [], [], null, config.Advertisement).SupportsRawInput).IsFalse();
    }

    [Test]
    public async Task DaemonConnect_advertises_branch_and_abbreviated_sha_base_refs() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        await using var conn = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);

        var connect = conn.BuildDaemonConnect("mac", [], [], null, config.Advertisement);

        await Assert.That(connect.SupportsBranchBaseRef).IsTrue();
        await Assert.That(connect.SupportsAbbreviatedShaBaseRef).IsTrue();
    }

    [Test]
    public async Task DaemonConnect_advertises_the_permission_presets_the_bridge_enforces() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        await using var conn = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);

        var json = System.Text.Json.JsonSerializer.Serialize(
            conn.BuildDaemonConnect("mac", [], [], null, config.Advertisement), CapacitorJsonContext.Default.DaemonConnect);

        await Assert.That(json).Contains("\"acp_presets\":[\"explore\",\"edit\",\"auto\"]");
    }

    [Test]
    public async Task DaemonConnect_serializes_its_capability_flags_with_snake_case_names() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        await using var conn = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);
        conn.OnSendRawInput += _ => Task.CompletedTask;

        var json = System.Text.Json.JsonSerializer.Serialize(
            conn.BuildDaemonConnect("mac", [], [], null, config.Advertisement), CapacitorJsonContext.Default.DaemonConnect);

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

        var connect = conn.BuildDaemonConnect("mac", [], [], null, config.Advertisement);
        var json    = System.Text.Json.JsonSerializer.Serialize(connect, CapacitorJsonContext.Default.DaemonConnect);

        await Assert.That(connect.ResumingAgents).IsEquivalentTo(claims);
        await Assert.That(json).Contains(
            "\"resuming_agents\":[{\"agent_id\":\"agent-1\",\"predecessor_instance_id\":\"instance-old\",\"session_id\":\"session-1\"}]");
    }

    [Test]
    public async Task Connect_payload_advertises_account_launches_and_carries_the_snapshot() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        await using var conn = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);
        var snapshot = new AccountsSnapshotDto("host-1", 3,
            [new("acc-1", "claude", "/home/u/.claude-work", "key-1", "work", "recording")],
            [new("claude", "/home/u/.claude", "key-0")]);
        conn.GetAccountsSnapshot = () => snapshot;

        var connect = conn.BuildDaemonConnect("mac", [], [], null, config.Advertisement);
        var json    = System.Text.Json.JsonSerializer.Serialize(connect, CapacitorJsonContext.Default.DaemonConnect);

        await Assert.That(connect.SupportsAccountLaunches).IsTrue();
        await Assert.That(connect.Accounts).IsSameReferenceAs(snapshot);
        await Assert.That(json).Contains("\"supports_account_launches\":true");
        await Assert.That(json).Contains("\"accounts\":{\"host_id\":\"host-1\",\"revision\":3,");
    }

    /// <summary>Records each payload instead of sending it.</summary>
    sealed class RecordingConnection(DaemonConfig config) : ServerConnection(
        config, new FixedCredentialSource(), NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance, TimeProvider.System) {
        public List<DaemonConnect> Connects { get; } = [];

        public Exception? InvokeThrows { get; init; }

        internal override HubConnectionState HubState => HubConnectionState.Connected;

        internal override Task InvokeDaemonConnectRawAsync(DaemonConnect connect) {
            Connects.Add(connect);
            return InvokeThrows is { } ex ? Task.FromException(ex) : Task.CompletedTask;
        }
    }

    /// <summary>A refresh commits a newer advertisement while the registration is being assembled: what
    /// the server is sent and what the daemon then routes on as delivered must be the same one.</summary>
    [Test]
    public async Task A_registration_acknowledges_the_advertisement_it_sent() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        config.Advertise(new List<UnattendedVendorCapability>(), CodexTransportResolution.Resolve(1, switchOn: false, () => null));
        var newerCapabilities = new List<UnattendedVendorCapability>();
        var newer = CodexTransportResolution.Resolve(2, switchOn: true, () => "0.146.0");
        await using var conn = new RecordingConnection(config);
        conn.GetLiveAgentIds = () => {
            config.Advertise(newerCapabilities, newer);
            return [];
        };

        await conn.ReRegisterAsync();

        await Assert.That(conn.Connects.Single().UnattendedVendorCapabilities).IsSameReferenceAs(newerCapabilities);
        await Assert.That(config.AcknowledgedCodexResolution).IsSameReferenceAs(newer);
    }

    /// <summary>The server dispatches against an advertisement from the moment it accepts DaemonConnect;
    /// agent recovery failing afterwards does not undo that.</summary>
    [Test]
    public async Task An_accepted_connect_is_acknowledged_even_when_agent_recovery_fails() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        var codex  = CodexTransportResolution.Resolve(1, switchOn: true, () => "0.146.0");
        config.Advertise(new List<UnattendedVendorCapability>(), codex);
        await using var conn = new RecordingConnection(config) {
            ReRegisterAgentsHook = () => throw new InvalidOperationException("agent recovery failed"),
        };

        await Assert.That(conn.ReRegisterAsync).Throws<InvalidOperationException>();

        await Assert.That(config.AcknowledgedCodexResolution).IsSameReferenceAs(codex);
        await Assert.That(config.FailedCodexGeneration).IsEqualTo(0L);
    }

    /// <summary>A refresh commits a newer advertisement while the registration is being assembled and the
    /// send then fails: the failure belongs to what the payload carried, not to the older one.</summary>
    [Test]
    public async Task A_failed_send_marks_the_advertisement_its_payload_carried() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        config.Advertise(new List<UnattendedVendorCapability>(), CodexTransportResolution.Resolve(1, switchOn: false, () => null));
        var newer = CodexTransportResolution.Resolve(2, switchOn: true, () => "0.146.0");
        await using var conn = new RecordingConnection(config) { InvokeThrows = new InvalidOperationException("hub down") };
        conn.GetLiveAgentIds = () => {
            config.Advertise(new List<UnattendedVendorCapability>(), newer);
            return [];
        };

        await Assert.That(conn.ReRegisterAsync).Throws<InvalidOperationException>();

        await Assert.That(config.FailedCodexGeneration).IsEqualTo(newer.Generation);
    }

    /// <summary>A payload that fails to build carried nothing, so a newer advertisement published during
    /// setup is not marked failed; the failure belongs to the snapshot the registration started from.</summary>
    [Test]
    public async Task A_payload_that_fails_to_build_marks_the_starting_advertisement() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        var starting = CodexTransportResolution.Resolve(1, switchOn: true, () => "0.146.0");
        config.Advertise(new List<UnattendedVendorCapability>(), starting);
        await using var conn = new RecordingConnection(config);
        conn.GetLiveAgentIds = () => {
            config.Advertise(new List<UnattendedVendorCapability>(), CodexTransportResolution.Resolve(2, switchOn: false, () => null));
            return [];
        };
        conn.GetResumingAgents = () => throw new InvalidOperationException("resume ledger unreadable");

        await Assert.That(conn.ReRegisterAsync).Throws<InvalidOperationException>();

        await Assert.That(config.FailedCodexGeneration).IsEqualTo(starting.Generation);
        await Assert.That(conn.Connects).IsEmpty();
    }

    [Test]
    public async Task SendRawInputCommand_serializes_with_snake_case_names() {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new SendRawInputCommand("a-1", "Gw==", Guid.Parse("00000000-0000-0000-0000-000000000001")),
            CapacitorJsonContext.Default.SendRawInputCommand);

        await Assert.That(json).IsEqualTo("{\"agent_id\":\"a-1\",\"data\":\"Gw==\",\"dispatch_id\":\"00000000-0000-0000-0000-000000000001\"}");
    }

    /// <summary>A connect takes its live agents, claims and completeness from one recovery snapshot, so a
    /// participant that publishes or fails between two reads can never be missing from both lists while
    /// the connect reports startup complete.</summary>
    [Test]
    public async Task DaemonConnect_takes_live_agents_claims_and_completeness_from_one_snapshot() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        await using var conn = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);
        var claim = new ResumingAgentClaim("reviewer-1", "instance-old", "session-1");
        conn.GetStartupReapComplete = () => true;
        conn.GetResumingAgents      = () => [];
        conn.GetRecoverySnapshot    = () => new RecoverySnapshot(["live-1"], [], [claim], StartupReapComplete: false);

        var connect = conn.BuildDaemonConnect("mac", [], [], null, config.Advertisement, conn.GetRecoverySnapshot());

        await Assert.That(connect.LiveAgentIds).IsEquivalentTo(["live-1"]);
        await Assert.That(connect.ResumingAgents!).IsEquivalentTo([claim]);
        await Assert.That(connect.StartupReapComplete).IsFalse();
    }

    [Test]
    public async Task DaemonConnect_advertises_handoff_launches() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        await using var conn = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);

        await Assert.That(conn.BuildDaemonConnect("mac", [], [], null, config.Advertisement).SupportsHandoffLaunch).IsTrue();
    }

    [Test]
    public async Task DaemonConnect_advertises_in_place_resumes() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        await using var conn = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);

        await Assert.That(conn.BuildDaemonConnect("mac", [], [], null, config.Advertisement).SupportsResumeInPlace).IsTrue();
    }

    [Test]
    public async Task The_begin_takeover_argument_goes_out_snake_case_through_the_hub_options() {
        var options = new System.Text.Json.JsonSerializerOptions();
        ServerConnection.ConfigureHubPayload(options);

        var json = System.Text.Json.JsonSerializer.Serialize(
            new Capacitor.Cli.Core.Handoff.HandoffTakeoverBeginRequest("src", "succ"), options);

        await Assert.That(json).IsEqualTo("""{"source_session_id":"src","successor_session_id":"succ"}""");
    }
}
