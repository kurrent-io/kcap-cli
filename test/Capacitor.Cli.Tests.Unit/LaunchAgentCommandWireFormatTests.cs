using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Locks in the SignalR JSON wire format for <see cref="LaunchAgentCommand"/>:
/// the server (Kurrent.Capacitor) configures its SignalR JSON protocol with
/// <c>JsonStringEnumConverter(JsonNamingPolicy.CamelCase)</c> and snake_case
/// property names, so <see cref="LaunchKind"/> rides the wire as a camelCase
/// string. The daemon's <see cref="CapacitorJsonContext"/> must be able to
/// parse that — historically it couldn't, and the SignalR client silently
/// dropped every <c>LaunchAgent</c> invocation (DEV-1665).
/// </summary>
public class LaunchAgentCommandWireFormatTests {
    /// <summary>
    /// Mirrors src/Kurrent.Capacitor/Program.cs SignalR <c>AddJsonProtocol</c>
    /// configuration. If the server changes its options, update this here too —
    /// the test is meaningless if it doesn't match production.
    /// </summary>
    static readonly JsonSerializerOptions ServerWireOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters           = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Test]
    public async Task DaemonDeserialises_DefaultKind() {
        var cmd = new LaunchAgentCommand(
            AgentId:       "abc12345",
            Prompt:        null,
            Model:         "opus",
            Effort:        null,
            RepoPath:      "/tmp/repo",
            Tools:         null,
            AttachmentIds: null,
            Vendor:        "claude"
        );

        var wire   = JsonSerializer.Serialize(cmd, ServerWireOptions);
        var parsed = JsonSerializer.Deserialize(wire, CapacitorJsonContext.Default.LaunchAgentCommand);

        await Assert.That(parsed.Kind).IsEqualTo(LaunchKind.Default);
        await Assert.That(parsed.AgentId).IsEqualTo("abc12345");
    }

    [Test]
    public async Task DaemonDeserialises_ReviewKind_WithReviewInfo() {
        var cmd = new LaunchAgentCommand(
            AgentId:       "def67890",
            Prompt:        null,
            Model:         "opus",
            Effort:        null,
            RepoPath:      "/tmp/repo",
            Tools:         null,
            AttachmentIds: null,
            Vendor:        "claude",
            Kind:          LaunchKind.Review,
            Review:        new ReviewLaunchInfo("kurrent-io", "kcap", 42)
        );

        var wire   = JsonSerializer.Serialize(cmd, ServerWireOptions);
        var parsed = JsonSerializer.Deserialize(wire, CapacitorJsonContext.Default.LaunchAgentCommand);

        await Assert.That(parsed.Kind).IsEqualTo(LaunchKind.Review);
        await Assert.That(parsed.Review).IsNotNull();
        await Assert.That(parsed.Review!.Value.Owner).IsEqualTo("kurrent-io");
        await Assert.That(parsed.Review!.Value.Repo).IsEqualTo("kcap");
        await Assert.That(parsed.Review!.Value.PrNumber).IsEqualTo(42);
    }

    [Test]
    public async Task ServerWire_EncodesKindAsCamelCaseString() {
        var cmd = new LaunchAgentCommand(
            AgentId:       "x",
            Prompt:        null,
            Model:         "opus",
            Effort:        null,
            RepoPath:      "/r",
            Tools:         null,
            AttachmentIds: null,
            Vendor:        "claude",
            Kind:          LaunchKind.Review
        );

        var wire = JsonSerializer.Serialize(cmd, ServerWireOptions);

        await Assert.That(wire).Contains("\"kind\":\"review\"");
    }

    [Test]
    public async Task DaemonDeserialises_SyncFromRepoRoot_WhenPresent() {
        // AI-1163: a mirror-requester review-flow launch carries the requester's repo root so the
        // daemon can sync its live working tree into the reviewer worktree before spawning.
        var cmd = new LaunchAgentCommand(
            AgentId:          "sync1234",
            Prompt:           null,
            Model:            "default",
            Effort:           null,
            RepoPath:         "/tmp/repo",
            Tools:            null,
            AttachmentIds:    null,
            Vendor:           "codex",
            Kind:             LaunchKind.ReviewFlow,
            SyncFromRepoRoot: "/home/me/dev/kcap"
        );

        var wire   = JsonSerializer.Serialize(cmd, ServerWireOptions);
        var parsed = JsonSerializer.Deserialize(wire, CapacitorJsonContext.Default.LaunchAgentCommand);

        await Assert.That(wire).Contains("\"sync_from_repo_root\":\"/home/me/dev/kcap\"");
        await Assert.That(parsed.SyncFromRepoRoot).IsEqualTo("/home/me/dev/kcap");
        await Assert.That(parsed.Kind).IsEqualTo(LaunchKind.ReviewFlow);
    }

    [Test]
    public async Task DaemonDeserialises_SyncFromRepoRoot_DefaultsToNull_WhenAbsent() {
        // Version skew: an older server that predates AI-1163 omits the field entirely. The daemon
        // must still bind the command (positional SignalR binding) and default the field to null —
        // i.e. no launch-time sync — rather than failing to invoke LaunchAgent (cf. DEV-1665).
        const string legacyWire =
            """
            {"agent_id":"legacy01","prompt":null,"model":"opus","effort":null,"repo_path":"/tmp/repo","tools":null,"attachment_ids":null,"vendor":"claude","kind":"reviewFlow"}
            """;

        var parsed = JsonSerializer.Deserialize(legacyWire, CapacitorJsonContext.Default.LaunchAgentCommand);

        await Assert.That(parsed.SyncFromRepoRoot).IsNull();
        await Assert.That(parsed.Kind).IsEqualTo(LaunchKind.ReviewFlow);
        await Assert.That(parsed.RepoPath).IsEqualTo("/tmp/repo");
    }

    [Test]
    public async Task Vendor_field_round_trips_through_json_serializer() {
        var cmd = new LaunchAgentCommand(
            AgentId:       "agent-1",
            Prompt:        null,
            Model:         "claude-sonnet-4-6",
            Effort:        null,
            RepoPath:      "/tmp/repo",
            Tools:         null,
            AttachmentIds: null,
            Vendor:        "codex"
        );

        var json = JsonSerializer.Serialize(cmd, CapacitorJsonContext.Default.LaunchAgentCommand);
        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.LaunchAgentCommand);

        await Assert.That(back.Vendor).IsEqualTo("codex");
    }

    [Test]
    public async Task AgentRunStarted_vendor_serialises_into_json_body() {
        var evt = new AgentRunStarted(
            Prompt: "do a thing",
            Model: "claude-sonnet-4-6",
            Effort: null,
            RepoPath: "/tmp/repo",
            WorktreePath: "/tmp/wt",
            Vendor: "codex"
        );

        var json = JsonSerializer.Serialize(evt, CapacitorJsonContext.Default.AgentRunStarted);
        await Assert.That(json).Contains("codex");
        await Assert.That(json.ToLowerInvariant()).Contains("\"vendor\"");
    }
}
