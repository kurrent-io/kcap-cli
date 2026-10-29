using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// A resume relaunches under the account its ledger entry recorded, checked against the registry as it
/// is now; an account that can no longer be used interrupts the entry by name, with no continuation.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class AgentOrchestratorResumeAccountTests {
    const string WorkId = "c1a0de01-work";

    [TempDir] public required TempDir Tmp { get; init; }

    readonly SpyHostedAgentLauncher _launcher = new("claude", cliPath: "spy-claude") { SupportsResume = true };
    readonly SpyPtyProcessFactory   _pty      = new(new ScriptedPtyProcess());

    string ClaudeDefault => Tmp.CreateDir(".claude");
    string Repo          => Tmp.CreateDir("repo");

    AccountStore Store => field ??= new AccountStore(Tmp.PathTo("accounts"));

    string Register(string id, string directory, string label) {
        var normalized = AccountDirectory.Normalize(directory);
        Store.Mutate(r => (r with { Accounts = [.. r.Accounts, new VendorAccount(id, HarnessId.Claude, normalized, label, DateTimeOffset.UnixEpoch)] }, 0));

        return normalized;
    }

    AgentOrchestrator Build(string? allowedRepo = null, CaptureServerConnection? server = null) =>
        AgentOrchestratorHarness.BuildOrchestrator(
            server ?? new CaptureServerConnection(), _pty, new Dictionary<string, IHostedAgentLauncher> { [_launcher.Vendor] = _launcher },
            allowedRepoPath: allowedRepo ?? Repo,
            configure: c => {
                c.InstanceId            = "instance-now";
                c.ResumeReadinessWindow = TimeSpan.FromMilliseconds(100);
                c.Accounts              = Store;
                c.DefaultDirectories    = new Dictionary<HarnessId, string> { [HarnessId.Claude] = ClaudeDefault };
            });

    ResumeLedgerEntry Resuming(AgentOrchestrator orch, string agentId, string? directory, string? accountId, string? label) {
        var stamp = TimeProvider.System.GetUtcNow();
        var launch = new ResumeLaunchDescriptor(
            Prompt: "build the thing", Model: "opus", Effort: null, RepoPath: Repo,
            WorktreePath: Tmp.CreateDir($"wt-{agentId}"), Work: WorkLocation.OwnedWorktree,
            GitRelativeCwd: null, SandboxPolicy: null, ApprovalPolicy: null, PermissionPreset: null,
            McpConfigPath: null, Title: "the thing", IsPrivate: false, Origin: "server",
            RequesterUserId: "user-1", RequesterDisplay: "User One", ConsentSource: "default",
            ConsentAt: stamp.ToString("O"), FlowRunId: null, FlowRole: null,
            WorktreeBranch: $"capacitor/custom-{agentId}", WorktreeIsStandalone: false, WorktreeSourceRepo: Repo,
            AccountDirectory: directory, AccountId: accountId, AccountLabel: label);
        var entry = new ResumeLedgerEntry(
            ResumeLedgerEntry.CurrentSchemaVersion, agentId, "daemon-1", stamp, stamp, "claude", "pty",
            $"session-{agentId}", $"vendor-{agentId}", launch, ResumeState.Resuming, Attempts: 0, LastError: null,
            PredecessorInstanceId: "instance-old", ResumingAt: stamp);
        orch.ResumeLedger!.Write(entry);

        return entry;
    }

    static async Task ResumeAsync(AgentOrchestrator orch) {
        orch.LoadResumeLedger();
        await orch.ResumeOnBootAsync(CancellationToken.None);
    }

    /// <summary>Counts the continuations the lane asks for; each one is reported as failed.</summary>
    static Func<int> CountContinuations(AgentOrchestrator orch) {
        var continued = 0;
        orch.ContinueEntry = (_, _, _) => {
            Interlocked.Increment(ref continued);
            return Task.FromResult(ResumeOutcome.Interrupted);
        };

        return () => Volatile.Read(ref continued);
    }

    static (string? Directory, string? Id, string? Label) Recorded(ResumeLedgerEntry entry) =>
        (entry.Launch.AccountDirectory, entry.Launch.AccountId, entry.Launch.AccountLabel);

    static LaunchAgentCommand Launch(string agentId, string repoPath, string? accountId) => new(
        AgentId: agentId, Prompt: "hi", Model: "opus", Effort: null, RepoPath: repoPath,
        Tools: null, AttachmentIds: null, Vendor: "claude", AccountId: accountId);

    /// <summary>A default launch records its directory too: a resume after the default moves needs it.</summary>
    [Test]
    public async Task Ledger_entry_records_the_resolved_account() {
        var work = Register(WorkId, Tmp.CreateDir(".claude-work"), "Work");
        using var repo = GitRepo.CreateWithCommit();
        await using var orch = Build(repo);
        var atStart = new Dictionary<string, ResumeLedgerEntry?>();
        _launcher.OnPrepare = ctx => atStart[ctx.AgentId] = orch.ResumeLedger!.TryRead(ctx.AgentId);

        await orch.HandleLaunchAgentForTest(Launch("ledger-work", repo, WorkId));
        await orch.HandleLaunchAgentForTest(Launch("ledger-default", repo, accountId: null));

        await Assert.That(Recorded(atStart["ledger-work"]!)).IsEqualTo((work, WorkId, "Work"));
        await Assert.That(Recorded(orch.ResumeLedger!.TryRead("ledger-work")!)).IsEqualTo((work, WorkId, "Work"));
        await Assert.That(Recorded(atStart["ledger-default"]!)).IsEqualTo((AccountDirectory.Normalize(ClaudeDefault), (string?)null, ".claude"));
        await Assert.That(Recorded(orch.ResumeLedger!.TryRead("ledger-default")!)).IsEqualTo((AccountDirectory.Normalize(ClaudeDefault), (string?)null, ".claude"));
    }

    [Test]
    public async Task Resume_relaunches_under_the_ledger_directory_after_the_default_changed() {
        var before = Tmp.CreateDir(".claude-before");
        await using var orch = Build();
        Resuming(orch, "resume-moved", before, accountId: null, ".claude-before");

        await ResumeAsync(orch);

        var expected = new ResolvedAccount(AccountDirectory.Normalize(before), null, ".claude-before");
        await Assert.That(_launcher.LastContext!.Account).IsEqualTo(expected);
        await Assert.That(orch.GetAgentForTest("resume-moved")!.Account).IsEqualTo(expected);
        await Assert.That(_pty.LastEnv!["CLAUDE_CONFIG_DIR"]).IsEqualTo(AccountDirectory.Normalize(before));
        await Assert.That(orch.ResumeLedger!.TryRead("resume-moved")!.State).IsEqualTo(ResumeState.Running);
    }

    [Test]
    public async Task Resume_of_a_removed_registered_account_fails_with_account_unavailable_and_skips_continue() {
        var work = Register(WorkId, Tmp.CreateDir(".claude-work"), "Work");
        Store.Mutate(r => (r with { Accounts = [] }, 0));
        var server = new CaptureServerConnection();
        await using var orch = Build(server: server);
        var entry     = Resuming(orch, "resume-removed", work, WorkId, "Work");
        var continued = CountContinuations(orch);
        var refusal   = AccountResolution.ResolveForResume(Store.TryLoad(), HarnessId.Claude, WorkId, work, ClaudeDefault).Refusal!;

        await ResumeAsync(orch);

        var after = orch.ResumeLedger!.TryRead("resume-removed")!;
        await Assert.That(after.State).IsEqualTo(ResumeState.Interrupted);
        await Assert.That(after.LastError).StartsWith("account_unavailable: ");
        await Assert.That(after.LastError).IsEqualTo(refusal);
        await Assert.That(after.Attempts).IsEqualTo(1);
        await Assert.That(continued()).IsEqualTo(0);
        await Assert.That(_launcher.PrepareCalls).IsEqualTo(0);
        await Assert.That(_pty.SpawnCalls).IsEqualTo(0);
        await Assert.That(Directory.Exists(entry.Launch.WorktreePath)).IsTrue();
        await Assert.That(server.ShutdownReports.Select(r => (r.AgentId, r.Status, r.Reason)))
            .Contains(("resume-removed", "Failed", $"resume_failed:{refusal}"));
        await Assert.That(orch.ResumingClaims()).IsEmpty();
    }

    /// <summary>With no vendor session the lane goes straight to the continuation, so the account check has
    /// to come first there too.</summary>
    [Test]
    public async Task Resume_without_a_vendor_session_of_a_removed_registered_account_fails_with_account_unavailable() {
        var work = Register(WorkId, Tmp.CreateDir(".claude-work"), "Work");
        Store.Mutate(r => (r with { Accounts = [] }, 0));
        var server = new CaptureServerConnection();
        await using var orch = Build(server: server);
        var entry = Resuming(orch, "resume-sessionless", work, WorkId, "Work");
        orch.ResumeLedger!.Write(entry with { VendorSession = null });
        var continued = CountContinuations(orch);
        var refusal   = AccountResolution.ResolveForResume(Store.TryLoad(), HarnessId.Claude, WorkId, work, ClaudeDefault).Refusal!;

        await ResumeAsync(orch);

        var after = orch.ResumeLedger!.TryRead("resume-sessionless")!;
        await Assert.That(after.State).IsEqualTo(ResumeState.Interrupted);
        await Assert.That(after.LastError).StartsWith("account_unavailable: ");
        await Assert.That(after.LastError).IsEqualTo(refusal);
        await Assert.That(after.Attempts).IsEqualTo(1);
        await Assert.That(continued()).IsEqualTo(0);
        await Assert.That(_pty.SpawnCalls).IsEqualTo(0);
        await Assert.That(server.ShutdownReports.Select(r => (r.AgentId, r.Status, r.Reason)))
            .Contains(("resume-sessionless", "Failed", $"resume_failed:{refusal}"));
    }

    /// <summary>The resume leaves the registry as it found it: an unregistered default stays unregistered.</summary>
    [Test]
    public async Task Resume_of_an_unregistered_default_uses_its_stored_directory() {
        Register(WorkId, Tmp.CreateDir(".claude-work"), "Work");
        var personal = Tmp.CreateDir(".claude-personal");
        await using var orch = Build();
        Resuming(orch, "resume-personal", personal, accountId: null, ".claude-personal");

        await ResumeAsync(orch);

        var expected = new ResolvedAccount(AccountDirectory.Normalize(personal), null, ".claude-personal");
        await Assert.That(_launcher.LastContext!.Account).IsEqualTo(expected);
        await Assert.That(orch.GetAgentForTest("resume-personal")!.Account).IsEqualTo(expected);
        await Assert.That(Recorded(orch.ResumeLedger!.TryRead("resume-personal")!)).IsEqualTo((personal, (string?)null, ".claude-personal"));
        await Assert.That(Store.TryLoad()!.Accounts.Select(a => a.Id)).IsEquivalentTo([WorkId]);
    }

    [Test]
    public async Task Resume_of_an_unregistered_default_whose_directory_is_gone_fails_named() {
        var gone = Tmp.PathTo(".claude-gone");
        await using var orch = Build();
        Resuming(orch, "resume-gone", gone, accountId: null, ".claude-gone");
        var continued = CountContinuations(orch);

        await ResumeAsync(orch);

        var after = orch.ResumeLedger!.TryRead("resume-gone")!;
        await Assert.That(after.State).IsEqualTo(ResumeState.Interrupted);
        await Assert.That(after.LastError).StartsWith("account_unavailable: ");
        await Assert.That(after.LastError).Contains(gone);
        await Assert.That(continued()).IsEqualTo(0);
        await Assert.That(_pty.SpawnCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Legacy_entry_without_account_fields_resumes_under_the_current_default() {
        await using var orch = Build();
        Resuming(orch, "resume-legacy", directory: null, accountId: null, label: null);

        await ResumeAsync(orch);

        var expected = new ResolvedAccount(AccountDirectory.Normalize(ClaudeDefault), null, ".claude");
        await Assert.That(_launcher.LastContext!.Account).IsEqualTo(expected);
        await Assert.That(orch.GetAgentForTest("resume-legacy")!.Account).IsEqualTo(expected);
        await Assert.That(orch.ResumeLedger!.TryRead("resume-legacy")!.State).IsEqualTo(ResumeState.Running);
    }
}
