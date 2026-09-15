using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The delivery core's attachment lane, whichever caller drove it: a refused prompt never reaches
/// the network, a failed fetch is a drop that names the id, and a batch the agent never accepted
/// leaves nothing on disk.
/// </summary>
// Serialized against itself: concurrent stub-server starts under the assembly's full width starve
// each other's startup.
[NotInParallel(nameof(DeliverInputAttachmentsTests))]
public class DeliverInputAttachmentsTests : IDisposable {
    [TempDir] public required TempDir Tmp { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() {
        _server.Dispose();
        GC.SuppressFinalize(this);
    }

    AgentOrchestrator Build(ServerConnection? server = null) =>
        AgentOrchestratorHarness.BuildOrchestrator(
            server ?? new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(),
            httpClientFactory: new UrlHttpClientFactory(_server.Url!));

    void Serve(string id, string name = "f.png") =>
        _server.Given(Request.Create().WithPath($"/api/attachments/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(new byte[] { 1, 2, 3 })
                .WithHeader("Content-Disposition", $"attachment; filename=\"{name}\""));

    static string Id(int n) => new((char)('a' + n), 32);

    static string[] Batches(string worktree) {
        var attached = Path.Combine(worktree, ".attached");

        return Directory.Exists(attached) ? [.. Directory.GetDirectories(attached).Select(Path.GetFileName)!] : [];
    }

    [Test]
    public async Task Protected_kind_with_ids_is_dropped_before_any_fetch_and_text_only_still_flows() {
        await using var orch = Build();
        Serve(Id(0));
        var rt = new FakeAcpRuntime();
        var agent = AgentOrchestratorHarness.SeedAcpAgent(
            orch, "rev", rt, kind: LaunchKind.Review, worktreePath: Tmp.CreateDir("rev"));

        var dropped = await orch.DeliverInputAsync(agent, "round 2", [Id(0)]);

        await Assert.That(dropped.Kind).IsEqualTo(InputDeliveryKind.Dropped);
        await Assert.That(dropped.Reason).IsEqualTo(AgentOrchestrator.SendInputDropReason.DeliveryFailed);
        await Assert.That(dropped.Error).IsEqualTo("attachments are not accepted by a review participant");
        await Assert.That(_server.LogEntries).IsEmpty();

        var delivered = await orch.DeliverInputAsync(agent, "round 2", null);

        await Assert.That(delivered.Kind).IsEqualTo(InputDeliveryKind.Delivered);
        await Assert.That(rt.SentInputs).IsEquivalentTo(new[] { "round 2" });
    }

    /// <summary>The borrowed cwd is the user's own checkout, and a runtime placing files there is
    /// refused on the delivery core too — not only on the local frame, which the server lane never
    /// passes through.</summary>
    [Test]
    public async Task Borrowed_cwd_with_worktree_placement_is_dropped_before_any_fetch() {
        await using var orch = Build();
        Serve(Id(0));
        var rt = new FakeAcpRuntime();
        var agent = AgentOrchestratorHarness.SeedBorrowedAcpAgent(orch, "b1", rt);

        var outcome = await orch.DeliverInputAsync(agent, "hello", [Id(0)]);

        await Assert.That(outcome.Kind).IsEqualTo(InputDeliveryKind.Dropped);
        await Assert.That(outcome.Reason).IsEqualTo(AgentOrchestrator.SendInputDropReason.DeliveryFailed);
        await Assert.That(outcome.Error).IsEqualTo(AttachmentRefusals.NeedsOwnedWorktree);
        await Assert.That(_server.LogEntries).IsEmpty();
        await Assert.That(rt.SentInputs).IsEmpty();
    }

    /// <summary>A TUI-less runtime's quit never reaches a model, so files fetched for it would be
    /// orphaned. The refusal replaces the quit outcome the same text without ids would produce, so
    /// the agent keeps running rather than being stopped on a message that was refused.</summary>
    [Test]
    public async Task Quit_with_ids_on_a_non_pty_runtime_is_dropped_and_the_agent_keeps_running() {
        await using var orch = Build();
        Serve(Id(0));
        var rt = new FakeAcpRuntime();
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "a1", rt, worktreePath: Tmp.CreateDir("quit"));

        var outcome = await orch.DeliverInputAsync(agent, " /quit ", [Id(0)]);

        await Assert.That(outcome.Kind).IsEqualTo(InputDeliveryKind.Dropped);
        await Assert.That(outcome.Reason).IsEqualTo(AgentOrchestrator.SendInputDropReason.DeliveryFailed);
        await Assert.That(outcome.Error).IsEqualTo(AttachmentRefusals.QuitTakesNone);
        await Assert.That(_server.LogEntries).IsEmpty();
        await Assert.That(rt.HasExited).IsFalse();
        await Assert.That(agent.Status).IsEqualTo("Running");

        // The same text without ids is the quit it always was.
        await Assert.That((await orch.DeliverInputAsync(agent, " /quit ", null)).Kind)
            .IsEqualTo(InputDeliveryKind.QuitRequested);
    }

    [Test]
    public async Task Failed_fetch_is_a_delivery_failed_drop_naming_the_id_and_the_runtime_gets_nothing() {
        await using var orch = Build();
        Serve(Id(0));
        var rt = new FakeAcpRuntime();
        var wt = Tmp.CreateDir("wt");
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "a1", rt, worktreePath: wt);

        var outcome = await orch.DeliverInputAsync(agent, "hello", [Id(0), Id(1)]);

        await Assert.That(outcome.Kind).IsEqualTo(InputDeliveryKind.Dropped);
        await Assert.That(outcome.Reason).IsEqualTo(AgentOrchestrator.SendInputDropReason.DeliveryFailed);
        await Assert.That(outcome.Error).Contains(Id(1));
        await Assert.That(rt.SentInputs).IsEmpty();
        await Assert.That(Batches(wt)).IsEmpty();
    }

    [Test]
    public async Task Successful_fetch_delivers_text_blank_line_trailer_with_worktree_relative_paths() {
        await using var orch = Build();
        Serve(Id(0));
        var rt = new FakeAcpRuntime();
        var wt = Tmp.CreateDir("wt");
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "a1", rt, worktreePath: wt);

        var outcome = await orch.DeliverInputAsync(agent, "hello", [Id(0)]);

        await Assert.That(outcome.Kind).IsEqualTo(InputDeliveryKind.Delivered);

        var batch = Batches(wt).Single();

        await Assert.That(rt.SentInputs.Single()).IsEqualTo($"hello\n\n[Attached files: .attached/{batch}/f.png]");
        await Assert.That(File.Exists(Path.Combine(wt, ".attached", batch, "f.png"))).IsTrue();
    }

    /// <summary>The files are the agent's only once the prompt naming them landed. Every refusal
    /// after the download — the late reap claim, a runtime that will not queue the turn, a runtime
    /// whose transport failed — takes the batch back with it.</summary>
    [Test]
    public async Task Refusals_after_the_fetch_roll_the_batch_back() {
        await using var orch = Build();
        Serve(Id(0));

        var lateWt = Tmp.CreateDir("late");
        var late = AgentOrchestratorHarness.SeedAcpAgent(orch, "late", new FakeAcpRuntime(), worktreePath: lateWt);
        orch.SendInputBeforeWriteHookForTest = () => {
            AgentOrchestratorHarness.ClaimReap(late);

            return Task.CompletedTask;
        };

        var claimed = await orch.DeliverInputAsync(late, "hi", [Id(0)]);
        orch.SendInputBeforeWriteHookForTest = null;

        await Assert.That(claimed.Reason).IsEqualTo(AgentOrchestrator.SendInputDropReason.ReaperClaimedLate);
        await Assert.That(Batches(lateWt)).IsEmpty();

        var fullWt = Tmp.CreateDir("full");
        var full = AgentOrchestratorHarness.SeedAcpAgent(
            orch, "full", new FakeAcpRuntime { SendUserInputThrow = new InputNotAdmittedException("queue full") },
            worktreePath: fullWt);

        await Assert.That((await orch.DeliverInputAsync(full, "hi", [Id(0)])).Reason)
            .IsEqualTo(AgentOrchestrator.SendInputDropReason.QueueFull);
        await Assert.That(Batches(fullWt)).IsEmpty();

        var brokenWt = Tmp.CreateDir("broken");
        var broken = AgentOrchestratorHarness.SeedAcpAgent(
            orch, "broken", new FakeAcpRuntime { SendUserInputThrow = new IOException("pipe closed") },
            worktreePath: brokenWt);

        await Assert.That((await orch.DeliverInputAsync(broken, "hi", [Id(0)])).Reason)
            .IsEqualTo(AgentOrchestrator.SendInputDropReason.DeliveryFailed);
        await Assert.That(Batches(brokenWt)).IsEmpty();

        var okWt = Tmp.CreateDir("ok");
        var ok = AgentOrchestratorHarness.SeedAcpAgent(orch, "ok", new FakeAcpRuntime(), worktreePath: okWt);

        await Assert.That((await orch.DeliverInputAsync(ok, "hi", [Id(0)])).Kind).IsEqualTo(InputDeliveryKind.Delivered);
        await Assert.That(Batches(okWt)).HasSingleItem();
    }

    /// <summary>Teardown latches its claim without the delivery gate, so a download admitted while the
    /// agent was live can still be running when cleanup removes the worktree: the batch goes back
    /// rather than recreating a directory nothing will read, and a later send fetches nothing at
    /// all.</summary>
    [Test]
    public async Task A_teardown_that_starts_during_the_fetch_takes_the_batch_back() {
        await using var orch = Build();
        Serve(Id(0));
        var wt = Tmp.CreateDir("torn");
        var rt = new FakeAcpRuntime();
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "a1", rt, worktreePath: wt);

        orch.SendInputBeforeWriteHookForTest = () => {
            AgentOrchestratorHarness.BeginCleanup(agent);

            return Task.CompletedTask;
        };

        var outcome = await orch.DeliverInputAsync(agent, "hi", [Id(0)]);
        orch.SendInputBeforeWriteHookForTest = null;

        await Assert.That(outcome.Kind).IsEqualTo(InputDeliveryKind.Dropped);
        await Assert.That(outcome.Reason).IsEqualTo(AgentOrchestrator.SendInputDropReason.DeliveryFailed);
        await Assert.That(rt.SentInputs).IsEmpty();
        await Assert.That(Batches(wt)).IsEmpty();

        var downloads = _server.LogEntries.Count;
        var afterwards = await orch.DeliverInputAsync(agent, "hi", [Id(0)]);

        await Assert.That(afterwards.Reason).IsEqualTo(AgentOrchestrator.SendInputDropReason.DeliveryFailed);
        await Assert.That(_server.LogEntries.Count).IsEqualTo(downloads);
        await Assert.That(Batches(wt)).IsEmpty();
    }

    /// <summary>What the server is told is the reason token alone: the wording behind it names an
    /// attachment this daemon refused, which is the owner's business and not the dispatcher's.</summary>
    [Test]
    public async Task Server_caller_reports_only_the_reason_token() {
        var server = new CaptureServerConnection();
        await using var orch = Build(server);
        var rt = new FakeAcpRuntime();
        AgentOrchestratorHarness.SeedAcpAgent(orch, "a1", rt, worktreePath: Tmp.CreateDir("wt"));
        var dispatch = Guid.NewGuid();

        await orch.HandleSendInputForTest(new SendInputCommand("a1", "hi", ["nope"], dispatch));

        await Assert.That(server.InputRejections)
            .IsEquivalentTo(new[] { (dispatch, "a1", AgentOrchestrator.SendInputDropReason.DeliveryFailed) });
        await Assert.That(rt.SentInputs).IsEmpty();
        await Assert.That(_server.LogEntries).IsEmpty();
    }
}
