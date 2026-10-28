using System.Globalization;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

[ParallelLimiter<SubprocessLimit>]
public class AgentOrchestratorAccountTests {
    const string WorkId  = "c1a0de01-work";
    const string CodexId = "c0dec001-codex";

    const string AccountSession     = "0a0a0a0a-1111-4111-8111-000000000001";
    const string EnvironmentSession = "0e0e0e0e-2222-4222-8222-000000000002";

    [TempDir] public required TempDir Tmp { get; init; }

    UserHome? _daemonHome;

    /// <summary>The home the daemon's environment layouts hang off; set by <see cref="Build"/>.</summary>
    UserHome DaemonHome => _daemonHome!;

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

    AgentOrchestrator Build(CaptureServerConnection server, SpyPtyProcessFactory pty, SpyHostedAgentLauncher launcher, string? allowedRepo = null,
            AccountReadingSink? readings = null) =>
        AgentOrchestratorHarness.BuildOrchestrator(
            server, pty, new Dictionary<string, IHostedAgentLauncher> { [launcher.Vendor] = launcher },
            allowedRepoPath: allowedRepo,
            readings: readings,
            configure: c => {
                c.Accounts           = Store;
                c.DefaultDirectories = new Dictionary<HarnessId, string> { [HarnessId.Claude] = ClaudeDefault, [HarnessId.Codex] = CodexDefault };
                _daemonHome          = c.Home;
            });

    static SpyHostedAgentLauncher Spy(string vendor = "claude") => new(vendor, cliPath: $"spy-{vendor}");

    static SpyPtyProcessFactory LivePty() => new(new OneChunkThenBlockPtyProcess());

    static LaunchAgentCommand Launch(string agentId, string repoPath, string? accountId, string vendor = "claude", string model = "opus") => new(
        AgentId: agentId, Prompt: "hi", Model: model, Effort: null, RepoPath: repoPath,
        Tools: null, AttachmentIds: null, Vendor: vendor, AccountId: accountId);

    [Test]
    public async Task Null_account_id_launches_under_the_daemon_default_directory() {
        using var repo     = GitRepo.CreateWithCommit();
        var       launcher = Spy();
        await using var orch = Build(new CaptureServerConnection(), LivePty(), launcher, repo);

        await orch.HandleLaunchAgentForTest(Launch("acct-default", repo, accountId: null));

        var expected = new ResolvedAccount(AccountDirectory.Normalize(ClaudeDefault), null, ".claude");
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
    public async Task Explicit_account_id_for_a_vendor_without_accounts_is_refused() {
        Register(WorkId, HarnessId.Claude, Tmp.CreateDir(".claude-work"), "Work");
        using var repo     = GitRepo.CreateWithCommit();
        var       server   = new CaptureServerConnection();
        var       pty      = new SpyPtyProcessFactory();
        var       launcher = Spy("cursor");
        await using var orch = Build(server, pty, launcher, repo);

        await orch.HandleLaunchAgentForTest(Launch("acct-cursor", repo, WorkId, vendor: "cursor"));

        await Assert.That(server.LaunchFailedCalls).Count().IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].AgentId).IsEqualTo("acct-cursor");
        await Assert.That(server.LaunchFailedCalls[0].Reason).IsEqualTo("account_unavailable: cursor has no accounts");
        await Assert.That(launcher.PrepareCalls).IsEqualTo(0);
        await Assert.That(pty.SpawnCalls).IsEqualTo(0);
        await Assert.That(orch.GetAgentForTest("acct-cursor")).IsNull();
    }

    [Test]
    public async Task Explicit_account_id_on_a_review_flow_launch_is_refused() {
        Register(WorkId, HarnessId.Claude, Tmp.CreateDir(".claude-work"), "Work");
        using var repo     = GitRepo.CreateWithCommit();
        var       server   = new CaptureServerConnection();
        var       pty      = new SpyPtyProcessFactory();
        var       launcher = Spy();
        await using var orch = Build(server, pty, launcher, repo);

        await orch.HandleLaunchAgentForTest(Launch("acct-reviewer", repo, WorkId) with { Kind = LaunchKind.ReviewFlow });

        await Assert.That(server.LaunchFailedCalls).Count().IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].AgentId).IsEqualTo("acct-reviewer");
        await Assert.That(server.LaunchFailedCalls[0].Reason).IsEqualTo("account_unavailable: a review-flow launch runs on the default account");
        await Assert.That(launcher.PrepareCalls).IsEqualTo(0);
        await Assert.That(pty.SpawnCalls).IsEqualTo(0);
        await Assert.That(orch.GetAgentForTest("acct-reviewer")).IsNull();
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

    /// <summary>The local spawn hands its child the account's directory, and drops the variable for the
    /// built-in one rather than naming it.</summary>
    [Test]
    public async Task Spawn_frame_launch_hands_the_child_its_account_directory() {
        const string PersonalId = "c1a0de02-personal";
        var work = Register(WorkId, HarnessId.Claude, Tmp.CreateDir(".claude-work"), "Work");
        var cwd  = Tmp.CreateDir("cwd");
        var pty  = new SpyPtyProcessFactory();
        await using var orch = Build(new CaptureServerConnection(), pty, Spy());
        Register(PersonalId, HarnessId.Claude, new TempDirHandle(DaemonHome.Path).CreateDir(".claude"), "Personal");

        await SpawnAsync(orch, cwd, WorkId);

        await Assert.That(pty.LastEnv!["CLAUDE_CONFIG_DIR"]).IsEqualTo(work);
        await Assert.That(pty.LastRemoveEnv ?? []).DoesNotContain("CLAUDE_CONFIG_DIR");

        await SpawnAsync(orch, cwd, PersonalId);

        await Assert.That(pty.SpawnCalls).IsEqualTo(2);
        await Assert.That(pty.LastEnv!.ContainsKey("CLAUDE_CONFIG_DIR")).IsFalse();
        await Assert.That(pty.LastRemoveEnv!).Contains("CLAUDE_CONFIG_DIR");
    }

    [Test]
    public async Task Status_dto_names_the_resolved_account_directory_id_and_label() {
        var work = Register(WorkId, HarnessId.Claude, Tmp.CreateDir(".claude-work"), "Work");
        using var repo = GitRepo.CreateWithCommit();
        await using var orch = Build(new CaptureServerConnection(), LivePty(), Spy(), repo);

        await orch.HandleLaunchAgentForTest(Launch("acct-status", repo, WorkId));

        var row = orch.SnapshotAgentsForStatus().Single(a => a.Id == "acct-status");
        await Assert.That(row.AccountDirectory).IsEqualTo(work);
        await Assert.That(row.AccountId).IsEqualTo(WorkId);
        await Assert.That(row.AccountLabel).IsEqualTo("Work");
    }

    [Test]
    public async Task Registration_and_live_agent_report_carry_the_resolved_account() {
        var work   = Register(WorkId, HarnessId.Claude, Tmp.CreateDir(".claude-work"), "Work");
        var server = new CaptureServerConnection();
        using var repo = GitRepo.CreateWithCommit();
        await using var orch = Build(server, LivePty(), Spy(), repo);

        await orch.HandleLaunchAgentForTest(Launch("acct-report", repo, WorkId));

        await Assert.That(server.AgentRegisteredAccounts).IsEquivalentTo([("acct-report", (string?)work, (string?)WorkId, (string?)"Work")]);

        var live = orch.BuildLiveAgents().Single(a => a.Id == "acct-report");
        await Assert.That(live.AccountDirectory).IsEqualTo(work);
        await Assert.That(live.AccountId).IsEqualTo(WorkId);
        await Assert.That(live.AccountLabel).IsEqualTo("Work");
    }

    /// <summary>A reconnect re-registers the agent, and the server has no launch to read the account from.</summary>
    [Test]
    public async Task Reconnect_registration_carries_the_resolved_account() {
        var work   = Register(WorkId, HarnessId.Claude, Tmp.CreateDir(".claude-work"), "Work");
        var server = new CaptureServerConnection();
        using var repo = GitRepo.CreateWithCommit();
        await using var orch = Build(server, LivePty(), Spy(), repo);
        await orch.HandleLaunchAgentForTest(Launch("acct-rereg", repo, WorkId));

        await orch.ReRegisterAgentsForTestAsync();

        var expected = ("acct-rereg", (string?)work, (string?)WorkId, (string?)"Work");
        await Assert.That(server.AgentRegisteredAccounts).IsEquivalentTo([expected, expected]);
    }

    [Test]
    public async Task Unregistered_default_reports_the_directory_and_a_null_id() {
        var physical = AccountDirectory.Normalize(ClaudeDefault);
        var server   = new CaptureServerConnection();
        using var repo = GitRepo.CreateWithCommit();
        await using var orch = Build(server, LivePty(), Spy(), repo);

        await orch.HandleLaunchAgentForTest(Launch("acct-unregistered", repo, accountId: null));

        var row = orch.SnapshotAgentsForStatus().Single(a => a.Id == "acct-unregistered");
        await Assert.That(row.AccountDirectory).IsEqualTo(physical);
        await Assert.That(row.AccountId).IsNull();
        await Assert.That(row.AccountLabel).IsEqualTo(".claude");
        await Assert.That(server.AgentRegisteredAccounts).IsEquivalentTo([("acct-unregistered", (string?)physical, (string?)null, (string?)".claude")]);

        var live = orch.BuildLiveAgents().Single(a => a.Id == "acct-unregistered");
        await Assert.That(live.AccountDirectory).IsEqualTo(physical);
        await Assert.That(live.AccountId).IsNull();
        await Assert.That(live.AccountLabel).IsEqualTo(".claude");
    }

    [Test]
    public async Task Vendor_without_accounts_reports_no_account() {
        var server = new CaptureServerConnection();
        using var repo = GitRepo.CreateWithCommit();
        await using var orch = Build(server, LivePty(), Spy("gemini"), repo);

        await orch.HandleLaunchAgentForTest(Launch("acct-none", repo, accountId: null, vendor: "gemini"));

        var row = orch.SnapshotAgentsForStatus().Single(a => a.Id == "acct-none");
        await Assert.That((row.AccountDirectory, row.AccountId, row.AccountLabel)).IsEqualTo(((string?)null, (string?)null, (string?)null));
        await Assert.That(server.AgentRegisteredAccounts).IsEquivalentTo([("acct-none", (string?)null, (string?)null, (string?)null)]);

        var live = orch.BuildLiveAgents().Single(a => a.Id == "acct-none");
        await Assert.That((live.AccountDirectory, live.AccountId, live.AccountLabel)).IsEqualTo(((string?)null, (string?)null, (string?)null));
    }

    /// <summary>A resuming or interrupted agent has no process, so its row reads the account its ledger entry recorded.</summary>
    [Test]
    public async Task Ledger_only_status_row_reports_the_account_its_entry_recorded() {
        var work = Register(WorkId, HarnessId.Claude, Tmp.CreateDir(".claude-work"), "Work");
        await using var orch = Build(new CaptureServerConnection(), LivePty(), Spy());
        var entry = ResumeLedgerEntries.For("acct-ledger", ResumeState.Interrupted);
        orch.ResumeLedger!.Write(entry with { Launch = entry.Launch with { AccountDirectory = work, AccountId = WorkId, AccountLabel = "Work" } });

        var row = orch.SnapshotAgentsForStatus().Single(a => a.Id == "acct-ledger");

        await Assert.That(row.AccountDirectory).IsEqualTo(work);
        await Assert.That(row.AccountId).IsEqualTo(WorkId);
        await Assert.That(row.AccountLabel).IsEqualTo("Work");
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
        await Assert.That(launcher.LastContext!.Account).IsEqualTo(new ResolvedAccount(AccountDirectory.Normalize(ClaudeDefault), null, ".claude"));
    }

    /// <summary>Both layouts hold a transcript for the agent's worktree, so the one detection links
    /// says which layout it searched.</summary>
    [Test]
    public async Task Codex_session_detection_searches_the_account_sessions_directory() {
        var work = Register(CodexId, HarnessId.Codex, Tmp.CreateDir(".codex-work"), "Codex work");
        using var repo = GitRepo.CreateWithCommit();
        await using var orch = Build(new CaptureServerConnection(), LivePty(), Spy("codex"), repo);

        await orch.HandleLaunchAgentForTest(Launch("acct-codex-session", repo, CodexId, vendor: "codex"));

        var agent    = orch.GetAgentForTest("acct-codex-session")!;
        var worktree = agent.Worktree.Path;
        StageRollout(new CodexPaths(DaemonHome, null).Sessions, EnvironmentSession, worktree);
        var rollout = StageRollout(AccountLayouts.Codex(DaemonHome, work).Sessions, AccountSession, worktree);

        await WaitHarness.PollUntilAsync(() => agent.TranscriptPath is not null);

        await Assert.That(agent.SessionId).IsEqualTo(Dashless(AccountSession));
        await Assert.That(agent.TranscriptPath).IsEqualTo(rollout);
    }

    /// <inheritdoc cref="Codex_session_detection_searches_the_account_sessions_directory"/>
    [Test]
    public async Task Claude_session_detection_searches_the_account_projects_directory() {
        var work = Register(WorkId, HarnessId.Claude, Tmp.CreateDir(".claude-work"), "Work");
        using var repo = GitRepo.CreateWithCommit();
        await using var orch = Build(new CaptureServerConnection(), LivePty(), Spy(), repo);

        await orch.HandleLaunchAgentForTest(Launch("acct-claude-session", repo, WorkId));

        // Both account layouts hold the reserved vendor ID; the discovered path identifies the chosen account.
        var agent    = orch.GetAgentForTest("acct-claude-session")!;
        var worktree = agent.Worktree.Path;
        StageTranscript(new ClaudePaths(DaemonHome, null).ProjectDir(worktree), agent.VendorSession!, worktree);
        var transcript = StageTranscript(AccountLayouts.Claude(DaemonHome, work).ProjectDir(worktree), agent.VendorSession!, worktree);

        await WaitHarness.PollUntilAsync(() => agent.TranscriptPath is not null);

        await Assert.That(agent.TranscriptPath).IsEqualTo(transcript);
    }

    [Test]
    public async Task Codex_resolved_model_report_reads_the_account_config_toml() {
        var work = Tmp.CreateDir(".codex-work");
        Register(CodexId, HarnessId.Codex, work, "Codex work");
        using var repo   = GitRepo.CreateWithCommit();
        var       server = new CaptureServerConnection();
        await using var orch = Build(server, LivePty(), Spy("codex"), repo);
        new TempDirHandle(new CodexPaths(DaemonHome, null).Home).CreateFile("config.toml", "model = \"gpt-environment\"\n");
        work.CreateFile("config.toml", "model = \"gpt-account\"\n");

        await orch.HandleLaunchAgentForTest(Launch("acct-codex-model", repo, CodexId, vendor: "codex", model: "default"));

        await Assert.That(server.ReportAgentResolvedModelCalls).IsEquivalentTo([("acct-codex-model", "gpt-account")]);
    }

    static string Dashless(string sessionId) => sessionId.Replace("-", "");

    static string StageTranscript(string projectDir, string sessionId, string cwd) =>
        new TempDirHandle(projectDir).CreateFile($"{sessionId}.jsonl", new JsonObject { ["type"] = "user", ["cwd"] = cwd }.ToJsonString() + "\n");

    // Codex names the day folders and the rollout by local time.
    static string StageRollout(string sessionsRoot, string sessionId, string cwd) {
        var now  = TimeProvider.System.GetLocalNow();
        var meta = new JsonObject { ["type"] = "session_meta", ["payload"] = new JsonObject { ["id"] = sessionId, ["cwd"] = cwd } };

        return new TempDirHandle(sessionsRoot).CreateFile(
            [Stamp("yyyy"), Stamp("MM"), Stamp("dd"), $"rollout-{Stamp("yyyy-MM-dd")}T{Stamp("HH-mm-ss")}-{sessionId}.jsonl"],
            meta.ToJsonString() + "\n");

        string Stamp(string format) => now.ToString(format, CultureInfo.InvariantCulture);
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

    /// <summary>The menu blocks the account the agent launched under, with the unknown constraint: the
    /// scrape names no window, so no reset lifts it.</summary>
    [Test]
    public async Task Claude_limit_menu_blocks_the_agents_account() {
        Register(WorkId, HarnessId.Claude, Tmp.CreateDir(".claude-work"), "Work");
        using var repo = GitRepo.CreateWithCommit();
        var pty = new ScriptedPtyProcess();
        Store.Readings.ProducerBudget = TimeSpan.FromSeconds(60);
        var recorded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sink = new AccountReadingSink(Store, TimeProvider.System, NullLogger<AccountReadingSink>.Instance) {
            Processed = (_, written) => recorded.TrySetResult(written),
        };
        await using var orch = Build(new CaptureServerConnection(), new SpyPtyProcessFactory(pty), Spy(), repo, sink);

        await orch.HandleLaunchAgentForTest(Launch("acct-limit", repo, WorkId));
        pty.Emit(
            "You've hit your session limit · resets 3:10pm\n\n" +
            "What do you want to do?\n\n" +
            "1. Stop and wait for limit to reset\n" +
            "2. Wait here, then continue automatically shortly\n");

        await Assert.That(await recorded.Task.WaitAsync(TimeSpan.FromSeconds(60))).IsTrue();
        var file = Store.Readings.LoadAll()[WorkId];
        await Assert.That(file.Reading.Blocked?.BlockedAt).IsNotNull();
        await Assert.That(orch.GetAgentForTest("acct-limit")!.UsageLimit).IsNotNull();
        await Assert.That(file.Reading.Blocked!.Constraints.Select(c => c.ResetsAt)).IsEquivalentTo(new DateTimeOffset?[] { null });
        await Assert.That(Store.Readings.LoadAll().Keys).IsEquivalentTo([WorkId]);
    }
}
