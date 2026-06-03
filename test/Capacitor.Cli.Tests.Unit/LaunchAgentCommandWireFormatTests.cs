using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Locks in the SignalR JSON wire format for <see cref="LaunchAgentCommand"/>:
/// the server (Kurrent.Capacitor) configures its SignalR JSON protocol with
/// <c>JsonStringEnumConverter(JsonNamingPolicy.CamelCase)</c> and snake_case
/// property names, so <see cref="LaunchKind"/> rides the wire as a camelCase
/// string. The daemon's <see cref="Capacitor.Cli.Core.KapacitorJsonContext"/> must be able to
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
        var parsed = JsonSerializer.Deserialize(wire, KapacitorJsonContext.Default.LaunchAgentCommand);

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
            Review:        new ReviewLaunchInfo("kurrent-io", "kapacitor", 42)
        );

        var wire   = JsonSerializer.Serialize(cmd, ServerWireOptions);
        var parsed = JsonSerializer.Deserialize(wire, KapacitorJsonContext.Default.LaunchAgentCommand);

        await Assert.That(parsed.Kind).IsEqualTo(LaunchKind.Review);
        await Assert.That(parsed.Review).IsNotNull();
        await Assert.That(parsed.Review!.Value.Owner).IsEqualTo("kurrent-io");
        await Assert.That(parsed.Review!.Value.Repo).IsEqualTo("kapacitor");
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

        var json = JsonSerializer.Serialize(cmd, KapacitorJsonContext.Default.LaunchAgentCommand);
        var back = JsonSerializer.Deserialize(json, KapacitorJsonContext.Default.LaunchAgentCommand);

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

        var json = JsonSerializer.Serialize(evt, KapacitorJsonContext.Default.AgentRunStarted);
        await Assert.That(json).Contains("codex");
        await Assert.That(json.ToLowerInvariant()).Contains("\"vendor\"");
    }
}
