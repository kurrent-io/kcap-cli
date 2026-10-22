using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

[ParallelLimiter<SubprocessLimit>]
public class AgentOrchestratorAccountTests {
    const string WorkId  = "c1a0de01-work";
    const string CodexId = "c0dec001-codex";

    [TempDir] public required TempDir Tmp { get; init; }

    string ClaudeDefault => Tmp.CreateDir(".claude");
    string CodexDefault  => Tmp.CreateDir(".codex");

    AccountStore Store => field ??= new AccountStore(Tmp.PathTo("accounts"));

    string Register(string id, HarnessId vendor, string directory, string label) {
        var normalized = AccountDirectory.Normalize(directory);
        Store.Mutate(r => (r with { Accounts = [.. r.Accounts, new VendorAccount(id, vendor, normalized, label, DateTimeOffset.UnixEpoch)] }, 0));

        return normalized;
    }

    string RefusalFor(string accountId) =>
        AccountResolution.Resolve(Store.TryLoad(), HarnessId.Claude, accountId, ClaudeDefault).Refusal!;

    AgentOrchestrator Build(CaptureServerConnection server, SpyPtyProcessFactory pty, SpyHostedAgentLauncher launcher, string? allowedRepo = null) =>
        AgentOrchestratorHarness.BuildOrchestrator(
            server, pty, new Dictionary<string, IHostedAgentLauncher> { ["claude"] = launcher },
            allowedRepoPath: allowedRepo,
            configure: c => {
                c.Accounts           = Store;
                c.DefaultDirectories = new Dictionary<HarnessId, string> { [HarnessId.Claude] = ClaudeDefault, [HarnessId.Codex] = CodexDefault };
            });

    static SpyHostedAgentLauncher Spy() => new("claude", cliPath: "spy-claude");

    static SpyPtyProcessFactory LivePty() => new(new OneChunkThenBlockPtyProcess());

    static LaunchAgentCommand Launch(string agentId, string repoPath, string? accountId) => new(
        AgentId: agentId, Prompt: "hi", Model: "opus", Effort: null, RepoPath: repoPath,
        Tools: null, AttachmentIds: null, Vendor: "claude", AccountId: accountId);

    [Test]
    public async Task Null_account_id_launches_under_the_daemon_default_directory() {
        using var repo     = GitRepo.CreateWithCommit();
        var       launcher = Spy();
        await using var orch = Build(new CaptureServerConnection(), LivePty(), launcher, repo);

        await orch.HandleLaunchAgentForTest(Launch("acct-default", repo, accountId: null));

        var expected = new ResolvedAccount(ClaudeDefault, null, ".claude");
        await Assert.That(launcher.LastContext!.Account).IsEqualTo(expected);
        await Assert.That(orch.GetAgentForTest("acct-default")!.Account).IsEqualTo(expected);
    }

    [Test]
    public async Task Registered_account_id_launches_under_its_directory_with_its_label() {
        var work = Register(WorkId, HarnessId.Claude, Tmp.CreateDir(".claude-work"), "Work");
        using var repo     = GitRepo.CreateWithCommit();
        var       launcher = Spy();
        await using var orch = Build(new CaptureServerConnection(), LivePty(), launcher, repo);

        await orch.HandleLaunchAgentForTest(Launch("acct-work", repo, WorkId));

        var expected = new ResolvedAccount(work, WorkId, "Work");
        await Assert.That(launcher.LastContext!.Account).IsEqualTo(expected);
        await Assert.That(orch.GetAgentForTest("acct-work")!.Account).IsEqualTo(expected);
    }

    [Test]
    public async Task Unknown_account_id_is_refused_with_account_unavailable_and_spawns_nothing() {
        using var repo     = GitRepo.CreateWithCommit();
        var       server   = new CaptureServerConnection();
        var       pty      = new SpyPtyProcessFactory();
        var       launcher = Spy();
        await using var orch = Build(server, pty, launcher, repo);

        await orch.HandleLaunchAgentForTest(Launch("acct-unknown", repo, "no-such-account"));

        await Assert.That(server.LaunchFailedCalls).Count().IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].AgentId).IsEqualTo("acct-unknown");
        await Assert.That(server.LaunchFailedCalls[0].Reason).StartsWith("account_unavailable: ");
        await Assert.That(server.LaunchFailedCalls[0].Reason).IsEqualTo(RefusalFor("no-such-account"));
        await Assert.That(launcher.PrepareCalls).IsEqualTo(0);
        await Assert.That(pty.SpawnCalls).IsEqualTo(0);
        await Assert.That(orch.GetAgentForTest("acct-unknown")).IsNull();
        await Assert.That(Directory.Exists(repo.PathTo(".capacitor", "worktrees"))).IsFalse();
    }

    [Test]
    public async Task Account_id_of_another_vendor_is_refused() {
        Register(CodexId, HarnessId.Codex, Tmp.CreateDir(".codex-work"), "Codex work");
        using var repo     = GitRepo.CreateWithCommit();
        var       server   = new CaptureServerConnection();
        var       pty      = new SpyPtyProcessFactory();
        await using var orch = Build(server, pty, Spy(), repo);

        await orch.HandleLaunchAgentForTest(Launch("acct-codex", repo, CodexId));

        await Assert.That(server.LaunchFailedCalls).Count().IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].Reason).StartsWith("account_unavailable: ");
        await Assert.That(server.LaunchFailedCalls[0].Reason).IsEqualTo(RefusalFor(CodexId));
        await Assert.That(pty.SpawnCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Registered_account_whose_directory_is_gone_is_refused() {
        var work = Register(WorkId, HarnessId.Claude, Tmp.CreateDir(".claude-work"), "Work");
        Directory.Delete(work);
        using var repo     = GitRepo.CreateWithCommit();
        var       server   = new CaptureServerConnection();
        var       pty      = new SpyPtyProcessFactory();
        await using var orch = Build(server, pty, Spy(), repo);

        await orch.HandleLaunchAgentForTest(Launch("acct-gone", repo, WorkId));

        await Assert.That(server.LaunchFailedCalls).Count().IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].Reason).StartsWith("account_unavailable: ");
        await Assert.That(server.LaunchFailedCalls[0].Reason).Contains(work);
        await Assert.That(pty.SpawnCalls).IsEqualTo(0);
        await Assert.That(Directory.Exists(repo.PathTo(".capacitor", "worktrees"))).IsFalse();
    }

    [Test]
    public async Task Spawn_frame_account_id_resolves_like_a_server_launch() {
        var work     = Register(WorkId, HarnessId.Claude, Tmp.CreateDir(".claude-work"), "Work");
        var cwd      = Tmp.CreateDir("cwd");
        var pty      = LivePty();
        var launcher = Spy();
        await using var orch = Build(new CaptureServerConnection(), pty, launcher);

        await SpawnAsync(orch, cwd, WorkId);

        var expected = new ResolvedAccount(work, WorkId, "Work");
        await Assert.That(launcher.LastContext!.Account).IsEqualTo(expected);
        await Assert.That(orch.GetAgentForTest(launcher.LastContext.AgentId)!.Account).IsEqualTo(expected);

        var refused = await SpawnAsync(orch, cwd, "no-such-account");

        await Assert.That(refused.Single(f => f.Type == FrameType.Error).Text).IsEqualTo(RefusalFor("no-such-account"));
        await Assert.That(refused.Any(f => f.Type is FrameType.Attached or FrameType.AttachedReadOnly)).IsFalse();
        await Assert.That(pty.SpawnCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Malformed_registry_refuses_an_explicit_id_and_defaults_a_null_one() {
        Register(WorkId, HarnessId.Claude, Tmp.CreateDir(".claude-work"), "Work");
        File.WriteAllText(Store.RegistryPath, "{ not a registry");
        using var repo     = GitRepo.CreateWithCommit();
        var       server   = new CaptureServerConnection();
        var       launcher = Spy();
        await using var orch = Build(server, LivePty(), launcher, repo);

        await orch.HandleLaunchAgentForTest(Launch("acct-malformed-id", repo, WorkId));
        await orch.HandleLaunchAgentForTest(Launch("acct-malformed-default", repo, accountId: null));

        await Assert.That(server.LaunchFailedCalls).Count().IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].AgentId).IsEqualTo("acct-malformed-id");
        await Assert.That(server.LaunchFailedCalls[0].Reason).IsEqualTo(RefusalFor(WorkId));
        await Assert.That(launcher.PrepareCalls).IsEqualTo(1);
        await Assert.That(launcher.LastContext!.Account).IsEqualTo(new ResolvedAccount(ClaudeDefault, null, ".claude"));
    }

    static async Task<List<LocalFrame>> SpawnAsync(AgentOrchestrator orch, string cwd, string accountId) {
        var readBuf = new MemoryStream();
        await FrameCodec.WriteAsync(readBuf, LocalFrame.Detach(), default);
        readBuf.Position = 0;
        using var client = new DuplexTestStream(readBuf, new MemoryStream());

        await orch.HandleLocalSpawnAsync(
            FrameCodec.Spawn("claude", WorkLocation.BorrowedCwd, isPrivate: true, cwd, [], 80, 24, accountId: accountId), client, default);

        client.WrittenStream.Position = 0;
        var frames = new List<LocalFrame>();
        while (await FrameCodec.ReadAsync(client.WrittenStream, default) is { } f) frames.Add(f);

        return frames;
    }
}
