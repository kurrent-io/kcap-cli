using System.Text.Json;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

/// <summary>
/// The additive daemon self-report DTOs round-trip through the source-gen
/// <see cref="CapacitorJsonContext"/>, and trailing fields on existing wire types stay
/// backward-compatible (JSON without them still deserializes).
/// </summary>
public class DaemonStatusDtoTests {
    [Test]
    public async Task DaemonStatusReport_roundtrips_through_source_gen_context() {
        var report = new DaemonStatusReport(
            ActiveCount: 2,
            LiveAgents:  [new LiveAgentInfo("a1", "ReviewFlow", DateTimeOffset.UtcNow, "flow-1", "reviewer")],
            Quarantined: [new QuarantinedAgentInfo("a2", "Default", DateTimeOffset.UtcNow)]);

        var json = JsonSerializer.Serialize(report, CapacitorJsonContext.Default.DaemonStatusReport);
        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.DaemonStatusReport);

        await Assert.That(back.ActiveCount).IsEqualTo(2);
        await Assert.That(back.LiveAgents).Count().IsEqualTo(1);
        await Assert.That(back.LiveAgents[0].FlowRunId).IsEqualTo("flow-1");
        await Assert.That(back.LiveAgents[0].FlowRole).IsEqualTo("reviewer");
        await Assert.That(back.Quarantined[0].Id).IsEqualTo("a2");
    }

    [Test]
    public async Task LaunchAgentCommand_without_flow_fields_roundtrips_with_nulls() {
        // An old server builds the command without the new trailing FlowRunId/FlowRole (they default
        // to null). STJ source-gen binds by name, so the additive trailing params can't break the
        // wire: a command serialized without them deserializes back with the required fields intact
        // and the new fields null.
        var cmd = new LaunchAgentCommand(
            AgentId: "a1", Prompt: null, Model: "default", Effort: null,
            RepoPath: "/r", Tools: null, AttachmentIds: null, Vendor: "codex");

        var json = JsonSerializer.Serialize(cmd, CapacitorJsonContext.Default.LaunchAgentCommand);
        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.LaunchAgentCommand);

        await Assert.That(back.AgentId).IsEqualTo("a1");
        await Assert.That(back.Vendor).IsEqualTo("codex");
        await Assert.That(back.FlowRunId).IsNull();
        await Assert.That(back.FlowRole).IsNull();
    }

    [Test]
    public async Task DaemonConnect_old_json_without_live_agents_still_deserializes() {
        const string oldJson = """
                               {"name":"tony","platform":"macOS","repoPaths":[],"maxAgents":5,"liveAgentIds":[]}
                               """;

        var connect = JsonSerializer.Deserialize(oldJson, CapacitorJsonContext.Default.DaemonConnect);

        await Assert.That(connect.Name).IsEqualTo("tony");
        await Assert.That(connect.LiveAgents).IsNull();
    }

    [Test]
    public async Task Supported_vendors_round_trips() {
        var dto = new DaemonStatusDto(
            new DaemonInfoDto("kcap-dev", "1.2.3", "https://example.test", "connected", 4, 1,
                Pid: 42, InstanceId: "abc", SupportedVendors: ["claude", "cursor"]),
            []);

        var json = JsonSerializer.Serialize(dto, StatusIpcJsonContext.Default.DaemonStatusDto);
        var back = JsonSerializer.Deserialize(json, StatusIpcJsonContext.Default.DaemonStatusDto)!;

        await Assert.That(back.Daemon.SupportedVendors).IsEquivalentTo(new[] { "claude", "cursor" });
    }

    [Test]
    public async Task Snapshot_without_supported_vendors_deserializes_as_null() {
        const string json = """
            {"daemon":{"name":"kcap-dev","version":"1","server_url":"u","connection":"connected",
            "max_agents":4,"active_agents":0,"pid":1,"instance_id":"i"},"agents":[]}
            """;

        var back = JsonSerializer.Deserialize(json, StatusIpcJsonContext.Default.DaemonStatusDto)!;

        await Assert.That(back.Daemon.SupportedVendors).IsNull();
    }

    [Test]
    public async Task DaemonConnect_serializes_vendor_models_as_snake_case_and_round_trips() {
        var connect = new DaemonConnect("d", "mac", [], 1, [],
            VendorModels: new Dictionary<string, VendorModelOption[]>(StringComparer.Ordinal) {
                ["pi"] = [new("anthropic/claude-opus-5", "Claude Opus 5 · anthropic")],
                ["kiro"] = [],
            });

        var json = JsonSerializer.Serialize(connect, CapacitorJsonContext.Default.DaemonConnect);
        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.DaemonConnect);

        await Assert.That(json).Contains("\"vendor_models\"");
        await Assert.That(back.VendorModels!["pi"][0].Value).IsEqualTo("anthropic/claude-opus-5");
        await Assert.That(back.VendorModels["kiro"]).IsEmpty();
    }

    [Test]
    public async Task DaemonConnect_without_vendor_models_deserializes_to_null() {
        var json = JsonSerializer.Serialize(new DaemonConnect("d", "mac", [], 1, []), CapacitorJsonContext.Default.DaemonConnect);
        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.DaemonConnect);
        await Assert.That(back.VendorModels).IsNull();
    }

    [Test]
    public async Task DaemonInfoDto_vendor_models_round_trips_through_status_context() {
        var dto = new DaemonStatusDto(
            new DaemonInfoDto("d", "1", "http://s", "connected", 1, 0,
                VendorModels: new Dictionary<string, VendorModelOption[]>(StringComparer.Ordinal) { ["pi"] = [] }),
            []);

        var json = JsonSerializer.Serialize(dto, StatusIpcJsonContext.Default.DaemonStatusDto);
        var back = JsonSerializer.Deserialize(json, StatusIpcJsonContext.Default.DaemonStatusDto)!;

        await Assert.That(json).Contains("\"vendor_models\"");
        await Assert.That(back.Daemon.VendorModels!["pi"]).IsEmpty();
    }

    [Test]
    public async Task DaemonInfoDto_from_an_older_daemon_has_null_vendor_models() {
        var json = """{"daemon":{"name":"d","version":"1","server_url":"http://s","connection":"connected","max_agents":1,"active_agents":0},"agents":[]}""";
        var back = JsonSerializer.Deserialize(json, StatusIpcJsonContext.Default.DaemonStatusDto)!;
        await Assert.That(back.Daemon.VendorModels).IsNull();
    }

    static AccountsSnapshotDto Snapshot() => new(
        "host-1", 3,
        [new AccountViewDto("a1b2", "claude", "/Users/x/.claude-work", "/users/x/.claude-work", "Work", "recording")],
        [new DefaultDirectoryDto("claude", "/Users/x/.claude", "/users/x/.claude")]);

    [Test]
    public async Task DaemonConnect_carries_account_launch_support_the_snapshot_and_per_account_models() {
        var connect = new DaemonConnect("d", "mac", [], 1, [],
            SupportsAccountLaunches: true,
            Accounts: Snapshot(),
            VendorModelsByAccount: new Dictionary<string, VendorModelOption[]>(StringComparer.Ordinal) {
                ["a1b2"] = [new("opus", "Opus")],
            });

        var json = JsonSerializer.Serialize(connect, CapacitorJsonContext.Default.DaemonConnect);
        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.DaemonConnect);

        await Assert.That(json).Contains("\"supports_account_launches\":true");
        await Assert.That(json).Contains("\"vendor_models_by_account\":{\"a1b2\":[");
        await Assert.That(back.SupportsAccountLaunches).IsTrue();
        await Assert.That(back.Accounts!.HostId).IsEqualTo("host-1");
        await Assert.That(back.Accounts.Accounts[0].Id).IsEqualTo("a1b2");
        await Assert.That(back.Accounts.Defaults[0].Vendor).IsEqualTo("claude");
        await Assert.That(back.VendorModelsByAccount!["a1b2"][0].Value).IsEqualTo("opus");
    }

    [Test]
    public async Task DaemonConnect_from_an_older_daemon_reads_the_account_members_as_absent() {
        var json = JsonSerializer.Serialize(new DaemonConnect("d", "mac", [], 1, []), CapacitorJsonContext.Default.DaemonConnect);
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            json, ",\"(supports_account_launches|accounts|vendor_models_by_account)\":(true|false|null)", "");

        var back = JsonSerializer.Deserialize(stripped, CapacitorJsonContext.Default.DaemonConnect);

        await Assert.That(stripped).DoesNotContain("supports_account_launches");
        await Assert.That(back.SupportsAccountLaunches).IsFalse();
        await Assert.That(back.Accounts).IsNull();
        await Assert.That(back.VendorModelsByAccount).IsNull();
    }

    [Test]
    public async Task DaemonInfoDto_vendor_models_by_account_round_trips_through_status_context() {
        var dto = new DaemonStatusDto(
            new DaemonInfoDto("d", "1", "http://s", "connected", 1, 0,
                VendorModelsByAccount: new Dictionary<string, VendorModelOption[]>(StringComparer.Ordinal) { ["a1b2"] = [] }),
            []);

        var json = JsonSerializer.Serialize(dto, StatusIpcJsonContext.Default.DaemonStatusDto);
        var back = JsonSerializer.Deserialize(json, StatusIpcJsonContext.Default.DaemonStatusDto)!;

        await Assert.That(json).Contains("\"vendor_models_by_account\":{\"a1b2\":[]}");
        await Assert.That(back.Daemon.VendorModelsByAccount!["a1b2"]).IsEmpty();
    }

    [Test]
    public async Task Registration_and_live_agent_entries_carry_the_account_members() {
        var registered = new AgentRegistered("a1", null, null, null, null,
            AccountDirectory: "/Users/x/.claude-work", AccountId: "a1b2", AccountLabel: "Work");
        var live = new LiveAgentInfo("a1", "Default", DateTimeOffset.UnixEpoch,
            AccountDirectory: "/Users/x/.claude-work", AccountId: "a1b2", AccountLabel: "Work");

        var registeredBack = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(registered, CapacitorJsonContext.Default.AgentRegistered), CapacitorJsonContext.Default.AgentRegistered);
        var liveBack = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(live, CapacitorJsonContext.Default.LiveAgentInfo), CapacitorJsonContext.Default.LiveAgentInfo);

        await Assert.That(registeredBack).IsEqualTo(registered);
        await Assert.That(liveBack).IsEqualTo(live);
    }
}
