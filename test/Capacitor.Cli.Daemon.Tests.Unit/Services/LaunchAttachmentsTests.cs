using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The launch path's attachment lane: a prompt whose files cannot be delivered never starts an
/// agent, and a batch no launch completed leaves nothing on disk.
/// </summary>
// Serialized against itself: concurrent stub-server starts under the assembly's full width starve
// each other's startup.
[NotInParallel(nameof(LaunchAttachmentsTests))]
public class LaunchAttachmentsTests : IDisposable {
    [TempDir] public required TempDir Tmp { get; init; }

    readonly WireMockServer _api = WireMockServer.Start();

    public void Dispose() {
        _api.Dispose();
        GC.SuppressFinalize(this);
    }

    static string Id(int n) => new((char)('a' + n), 32);

    void Serve(string id, string name = "f.png") =>
        _api.Given(Request.Create().WithPath($"/api/attachments/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(new byte[] { 1, 2, 3 })
                .WithHeader("Content-Disposition", $"attachment; filename=\"{name}\""));

    AgentOrchestrator Build(
            ServerConnection server, IPtyProcessFactory pty,
            IReadOnlyDictionary<string, IHostedAgentLauncher> launchers,
            IEnumerable<IHostedAgentRuntimeFactory>? factories = null, string? worktreeRoot = null) =>
        AgentOrchestratorHarness.BuildOrchestrator(
            server, pty, launchers, extraRuntimeFactories: factories,
            configure: worktreeRoot is null ? null : c => c.WorktreeRoot = worktreeRoot,
            httpClientFactory: new UrlHttpClientFactory(_api.Url!));

    static LaunchAgentCommand Launch(string agentId, string repoPath, string vendor, string[]? ids) => new(
        AgentId: agentId, Prompt: "goal", Model: "default", Effort: null, RepoPath: repoPath,
        Tools: null, AttachmentIds: ids, Vendor: vendor);

    [Test]
    public async Task Malformed_ids_fail_the_launch_with_attachments_refused_and_start_nothing() {
        using var repo = GitRepo.CreateWithCommit();
        var server = new CaptureServerConnection();
        var pty    = new SpyPtyProcessFactory();
        await using var orch = Build(server, pty, AgentOrchestratorHarness.Launcher("claude"));

        await orch.HandleLaunchAgentForTest(Launch("a1", repo.Path, "claude", ["not-an-id"]));

        await Assert.That(server.LaunchFailedCalls.Single().Reason).StartsWith("attachments_refused:");
        await Assert.That(pty.SpawnCalls).IsEqualTo(0);
        await Assert.That(_api.LogEntries).IsEmpty();
        await Assert.That(orch.GetAgentForTest("a1")).IsNull();
    }

    [Test]
    public async Task Missing_attachment_fails_the_launch_with_attachment_unavailable_and_removes_the_worktree() {
        using var repo = GitRepo.CreateWithCommit();
        var worktreeRoot = Tmp.CreateDir("worktrees");
        var server = new CaptureServerConnection();
        var pty    = new SpyPtyProcessFactory();
        await using var orch = Build(
            server, pty, AgentOrchestratorHarness.Launcher("claude"), worktreeRoot: worktreeRoot);

        await orch.HandleLaunchAgentForTest(Launch("a1", repo.Path, "claude", [Id(0)]));

        await Assert.That(server.LaunchFailedCalls.Single().Reason)
            .StartsWith($"attachment_unavailable: {Id(0)}");
        await Assert.That(pty.SpawnCalls).IsEqualTo(0);
        await Assert.That(Directory.GetDirectories(worktreeRoot)).IsEmpty();
    }

    [Test]
    public async Task Borrowed_cwd_launch_with_ids_and_worktree_placement_fails_and_without_ids_proceeds() {
        using var cwd = GitRepo.CreateWithCommit();
        Serve(Id(0));
        var server  = new CaptureServerConnection();
        var factory = new SpyHostedAgentRuntimeFactory("cursor");
        await using var orch = Build(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(), [factory]);
        var borrowed = Launch("a1", cwd.Path, "cursor", [Id(0)]) with { Borrowed = true, BorrowCwd = cwd.Path };

        await orch.HandleLaunchAgentForTest(borrowed);

        await Assert.That(server.LaunchFailedCalls.Single().Reason)
            .IsEqualTo("attachments_refused: attachments need a daemon-owned worktree");
        await Assert.That(factory.StartCalls).IsEqualTo(0);
        await Assert.That(_api.LogEntries).IsEmpty();

        await orch.HandleLaunchAgentForTest(borrowed with { AgentId = "a2", AttachmentIds = null });

        await Assert.That(factory.StartCalls).IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Successful_fetch_appends_the_trailer_to_the_prompt() {
        using var repo = GitRepo.CreateWithCommit();
        Serve(Id(0));
        var server  = new CaptureServerConnection();
        var factory = new SpyHostedAgentRuntimeFactory("cursor");
        await using var orch = Build(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(), [factory]);

        await orch.HandleLaunchAgentForTest(Launch("a1", repo.Path, "cursor", [Id(0)]));

        var ctx      = factory.LastContext!;
        var attached = Path.Combine(ctx.Worktree.Path, ".attached");
        var batch    = Path.GetFileName(Directory.GetDirectories(attached).Single());

        await Assert.That(ctx.Prompt).IsEqualTo($"goal\n\n[Attached files: .attached/{batch}/f.png]");
        await Assert.That(File.Exists(Path.Combine(attached, batch, "f.png"))).IsTrue();
        await Assert.That(orch.GetAgentForTest("a1")!.Placement).IsEqualTo(AttachmentPlacement.Worktree);
    }

    [Test]
    public async Task Borrowed_request_that_resolves_to_an_owned_snapshot_fetches_into_it() {
        using var cwd = GitRepo.CreateWithCommit();
        Serve(Id(0));
        var server  = new CaptureServerConnection();
        var factory = new SpyHostedAgentRuntimeFactory("cursor") {
            BorrowedReviewRequiresIndependentSnapshot = true
        };
        await using var orch = Build(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(), [factory]);
        var bridge = orch.PermissionBridgeForTest;
        await bridge.StartAsync(CancellationToken.None);

        try {
            await orch.HandleLaunchAgentForTest(
                Launch("a1", cwd.Path, "cursor", [Id(0)]) with { Borrowed = true, BorrowCwd = cwd.Path });

            var ctx      = factory.LastContext!;
            var attached = Path.Combine(ctx.Worktree.Path, ".attached");
            var batch    = Path.GetFileName(Directory.GetDirectories(attached).Single());

            await Assert.That(server.LaunchFailedCalls).IsEmpty();
            await Assert.That(ctx.Work).IsEqualTo(WorkLocation.OwnedWorktree);
            await Assert.That(ctx.Worktree.Path).IsNotEqualTo(cwd.Path);
            await Assert.That(ctx.Prompt).IsEqualTo($"goal\n\n[Attached files: .attached/{batch}/f.png]");
            await Assert.That(Directory.Exists(Path.Combine(cwd.Path, ".attached"))).IsFalse();
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test]
    public async Task Daemon_store_batch_is_kept_by_a_launch_that_publishes() {
        using var repo = GitRepo.CreateWithCommit();
        Serve(Id(0));
        var server  = new CaptureServerConnection();
        var factory = new SpyHostedAgentRuntimeFactory("codex") { Placement = AttachmentPlacement.DaemonStore };
        await using var orch = Build(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(), [factory]);

        await orch.HandleLaunchAgentForTest(Launch("a1", repo.Path, "codex", [Id(0)]));

        var store = orch.AttachmentStore.DirectoryFor("a1");
        var batch = Directory.GetDirectories(store).Single();

        await Assert.That(server.LaunchFailedCalls).IsEmpty();
        await Assert.That(orch.GetAgentForTest("a1")).IsNotNull();
        await Assert.That(factory.LastContext!.Prompt)
            .IsEqualTo($"goal\n\n[Attached files: {Path.Combine(batch, "f.png")}]");
        await Assert.That(File.Exists(Path.Combine(batch, "f.png"))).IsTrue();
    }

    /// <summary>A relaunch under an id a live agent still holds must not take that agent's files with
    /// it when it fails: the lease releases the directory only when no incarnation holds the id.</summary>
    [Test]
    public async Task Failed_relaunch_leaves_the_live_incarnations_store_batch_in_place() {
        using var repo = GitRepo.CreateWithCommit();
        Serve(Id(0));
        var server  = new CaptureServerConnection();
        var factory = new SpyHostedAgentRuntimeFactory("codex") { Placement = AttachmentPlacement.DaemonStore };
        await using var orch = Build(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(), [factory]);

        await orch.HandleLaunchAgentForTest(Launch("a1", repo.Path, "codex", [Id(0)]));

        var live = Directory.GetDirectories(orch.AttachmentStore.DirectoryFor("a1")).Single();

        // The second fetch ends on the unserved id, so this launch never publishes.
        await orch.HandleLaunchAgentForTest(Launch("a1", repo.Path, "codex", [Id(0), Id(1)]));

        await Assert.That(server.LaunchFailedCalls.Single().Reason).StartsWith("attachment_unavailable:");
        await Assert.That(orch.GetAgentForTest("a1")).IsNotNull();
        await Assert.That(File.Exists(Path.Combine(live, "f.png"))).IsTrue();
    }

    [Test]
    public async Task Daemon_store_batch_is_removed_when_start_fails_after_the_fetch() {
        using var repo = GitRepo.CreateWithCommit();
        Serve(Id(0));
        var server  = new CaptureServerConnection();
        var factory = new SpyHostedAgentRuntimeFactory("codex") {
            Placement  = AttachmentPlacement.DaemonStore,
            StartThrow = new InvalidOperationException("runtime refused")
        };
        await using var orch = Build(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(), [factory]);

        await orch.HandleLaunchAgentForTest(Launch("a1", repo.Path, "codex", [Id(0)]));

        var store = orch.AttachmentStore.DirectoryFor("a1");

        await Assert.That(server.LaunchFailedCalls.Single().Reason).IsEqualTo("runtime refused");
        await Assert.That(factory.LastContext!.Prompt).Contains($"[Attached files: {store}/");
        await Assert.That(Directory.Exists(store)).IsFalse();
    }

    [Test]
    public async Task Daemon_store_batch_is_removed_when_registration_fails_after_start() {
        using var repo = GitRepo.CreateWithCommit();
        Serve(Id(0));
        var server  = new CaptureServerConnection { AgentRegisteredFailTimes = 1 };
        var factory = new SpyHostedAgentRuntimeFactory("codex") { Placement = AttachmentPlacement.DaemonStore };
        await using var orch = Build(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(), [factory]);

        await orch.HandleLaunchAgentForTest(Launch("a1", repo.Path, "codex", [Id(0)]));

        var store = orch.AttachmentStore.DirectoryFor("a1");

        await Assert.That(factory.LastContext!.Prompt).Contains($"[Attached files: {store}/");
        await Assert.That(server.LaunchFailedCalls).Count().IsEqualTo(1);
        await Assert.That(orch.GetAgentForTest("a1")).IsNull();
        await Assert.That(Directory.Exists(store)).IsFalse();
    }
}
