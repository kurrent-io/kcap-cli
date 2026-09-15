using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The attachment-bearing composer frame. Every refusal is one SendTextAck the composer can word,
/// and a refused frame reaches neither the fetcher nor the runtime.
/// </summary>
// Serialized against itself: concurrent stub-server starts under the assembly's full width starve
// each other's startup.
[NotInParallel(nameof(LocalSendTextWithAttachmentsTests))]
public class LocalSendTextWithAttachmentsTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() {
        _server.Dispose();
        GC.SuppressFinalize(this);
    }

    AgentOrchestrator Build() =>
        AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            httpClientFactory: new UrlHttpClientFactory(_server.Url!));

    void Serve(string id, string name = "f.png") =>
        _server.Given(Request.Create().WithPath($"/api/attachments/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(new byte[] { 1, 2, 3 })
                .WithHeader("Content-Disposition", $"attachment; filename=\"{name}\""));

    static async Task<SendTextAckDto> Send(AgentOrchestrator orch, string payload) {
        using var ms = new MemoryStream();
        await orch.HandleLocalSendTextWithAttachmentsAsync(payload, ms, CancellationToken.None);
        ms.Position = 0;
        var frame = await FrameCodec.ReadAsync(ms, CancellationToken.None);
        await Assert.That(frame!.Type).IsEqualTo(FrameType.SendTextAck);

        return JsonSerializer.Deserialize(frame.Text, InputIpcJsonContext.Default.SendTextAckDto)!;
    }

    static string Payload(string agentId, string text, string[] ids) =>
        JsonSerializer.Serialize(
            new SendTextWithAttachmentsDto(agentId, text, ids), InputIpcJsonContext.Default.SendTextWithAttachmentsDto);

    static string Id(int n) => new((char)('a' + n), 32);

    [Test]
    [Arguments("not json")]
    [Arguments("{}")]
    [Arguments("""{"agent_id":"a1","text":"hi"}""")]
    public async Task Payloads_without_all_three_members_ack_malformed(string payload) {
        await using var orch = Build();

        await Assert.That((await Send(orch, payload)).Reason).IsEqualTo(SendTextReasons.Malformed);
    }

    /// <summary>This frame exists to carry attachments: an empty list is a client that should have
    /// sent the plain frame, not a text-only delivery in disguise.</summary>
    [Test]
    public async Task Empty_id_list_on_this_frame_is_malformed() {
        await using var orch = Build();

        var ack = await Send(orch, Payload("a1", "hi", []));

        await Assert.That(ack.Reason).IsEqualTo(SendTextReasons.Malformed);
    }

    [Test]
    public async Task Over_count_malformed_and_duplicate_ids_are_attachments_refused_before_the_core() {
        await using var orch = Build();
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "a1", new FakeAcpRuntime());

        await Assert.That((await Send(orch, Payload("a1", "hi", [.. Enumerable.Range(0, 11).Select(Id)]))).Reason)
            .IsEqualTo(SendTextReasons.AttachmentsRefused);
        await Assert.That((await Send(orch, Payload("a1", "hi", ["nope"]))).Reason)
            .IsEqualTo(SendTextReasons.AttachmentsRefused);

        var dup = await Send(orch, Payload("a1", "hi", [Id(0), Id(0).ToUpperInvariant()]));

        await Assert.That(dup.Reason).IsEqualTo(SendTextReasons.AttachmentsRefused);
        await Assert.That(dup.Error).IsEqualTo("duplicate attachment id");
        await Assert.That(((FakeAcpRuntime)agent.Runtime).SentInputs).IsEmpty();
    }

    /// <summary>A borrowed cwd is the user's own checkout: a vendor that would have its files written
    /// there is refused, while one the daemon places outside every cwd is served.</summary>
    [Test]
    public async Task Borrowed_cwd_with_worktree_placement_is_refused_and_a_daemon_store_agent_is_served() {
        await using var orch = Build();
        Serve(Id(0));
        AgentOrchestratorHarness.SeedBorrowedAcpAgent(orch, "b1", new FakeAcpRuntime());

        var refused = await Send(orch, Payload("b1", "hi", [Id(0)]));

        await Assert.That(refused.Reason).IsEqualTo(SendTextReasons.AttachmentsRefused);
        await Assert.That(refused.Error).IsEqualTo("attachments need a daemon-owned worktree");

        var codex = AgentOrchestratorHarness.SeedBorrowedAcpAgent(
            orch, "c1", new FakeAcpRuntime(), placement: AttachmentPlacement.DaemonStore);

        var served = await Send(orch, Payload("c1", "hi", [Id(0)]));

        await Assert.That(served.Ok).IsTrue();
        await Assert.That(((FakeAcpRuntime)codex.Runtime).SentInputs.Single()).Contains(AttachmentTrailer.Prefix);
    }

    [Test]
    public async Task Quit_command_with_attachments_is_refused_without_stopping() {
        await using var orch = Build();
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "a1", new FakeAcpRuntime());

        var ack = await Send(orch, Payload("a1", "/quit", [Id(0)]));

        await Assert.That(ack.Reason).IsEqualTo(SendTextReasons.AttachmentsRefused);
        await Assert.That(ack.Error).IsEqualTo("a quit command takes no attachments");
        await Assert.That(agent.Status).IsEqualTo("Running");
        await Assert.That(agent.Runtime.HasExited).IsFalse();
    }

    /// <summary>The checks the plain frame already owns still fire on this one: they live in the
    /// core both handlers share, not in either handler's own preamble.</summary>
    [Test]
    public async Task The_plain_frames_own_refusals_still_apply() {
        await using var orch = Build();

        await Assert.That((await Send(orch, Payload("a1", "hi", [Id(0)]))).Reason).IsEqualTo(SendTextReasons.NoSuchAgent);

        AgentOrchestratorHarness.SeedAcpAgent(orch, "rev", new FakeAcpRuntime(), kind: LaunchKind.Review);
        await Assert.That((await Send(orch, Payload("rev", "hi", [Id(0)]))).Reason).IsEqualTo(SendTextReasons.ProtectedKind);

        AgentOrchestratorHarness.SeedAcpAgent(orch, "starting", new FakeAcpRuntime(), status: "Starting");
        await Assert.That((await Send(orch, Payload("starting", "hi", [Id(0)]))).Reason).IsEqualTo(SendTextReasons.NotRunning);

        AgentOrchestratorHarness.SeedAcpAgent(orch, "a1", new FakeAcpRuntime());
        await Assert.That((await Send(orch, Payload("a1", "  ", [Id(0)]))).Reason).IsEqualTo(SendTextReasons.TextEmpty);
    }
}
