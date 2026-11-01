using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Resume is offered only where it can work: a vendor that reloads a session by its id, a directory that exists and
/// the daemon may work in, and the store the session was recorded under, resolved the way restart recovery resolves
/// it. An unknown store, a removed account or a store that moved since is never resumed into.
/// </summary>
public class AgentOrchestratorResumeInPlaceProbeTests {
    const string Session = "0123456789abcdef0123456789abcdef";

    [TempDir] public required TempDir Tmp { get; init; }

    readonly SpyHostedAgentLauncher _claude = new("claude", cliPath: "spy-claude") { SupportsResume = true };

    string ClaudeDefault => field ??= AccountDirectory.Normalize(Tmp.CreateDir(".claude"));
    string Repo          => field ??= ResolvedDir("repo");

    string ResolvedDir(string name) {
        Tmp.CreateDir(name);

        return Tmp.GetResolvedPath(name);
    }

    AccountStore Store => field ??= new AccountStore(Tmp.PathTo("accounts"));

    string Register(string id, string directory) {
        var normalized = AccountDirectory.Normalize(directory);
        Store.Mutate(r => (r with { Accounts = [.. r.Accounts, new VendorAccount(id, HarnessId.Claude, normalized, id, DateTimeOffset.UnixEpoch)] }, 0));

        return normalized;
    }

    sealed class LoadableAcp(string vendor) : IHostedAgentRuntimeFactory {
        public string CliPath            => "";
        public string Vendor             { get; } = vendor;
        public bool   SupportsUnattended => true;

        public bool IsAvailable() => true;

        public bool SupportsRestartResume(string? runtimeTransport) => true;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) =>
            throw new NotSupportedException("not exercised by this test");
    }

    AgentOrchestrator Build(string? allowedRepo = null) =>
        AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher> { [_claude.Vendor] = _claude },
            allowedRepoPath: allowedRepo ?? Repo,
            extraRuntimeFactories: [new LoadableAcp("cursor")],
            configure: c => {
                c.Accounts           = Store;
                c.DefaultDirectories = new Dictionary<HarnessId, string> { [HarnessId.Claude] = ClaudeDefault };
            });

    [Test]
    public async Task A_claude_session_in_its_default_store_is_resumable() {
        await using var orch = Build();

        await Assert.That(await orch.InPlaceResumeRefusalAsync("claude", Session, Repo, null, ClaudeDefault)).IsNull();
    }

    [Test]
    public async Task A_claude_session_under_a_registered_account_is_resumable() {
        var work = Register("acct-work", Tmp.CreateDir("claude-work"));
        await using var orch = Build();

        await Assert.That(await orch.InPlaceResumeRefusalAsync("claude", Session, Repo, "acct-work", work)).IsNull();
    }

    [Test]
    public async Task A_removed_account_is_refused() {
        var work = AccountDirectory.Normalize(Tmp.CreateDir("claude-work"));
        await using var orch = Build();

        await Assert.That(await orch.InPlaceResumeRefusalAsync("claude", Session, Repo, "acct-gone", work))
            .IsEqualTo(ResumeInPlaceRefusals.StoreChanged);
    }

    [Test]
    public async Task An_unregistered_store_that_is_no_longer_the_default_is_refused() {
        var old = AccountDirectory.Normalize(Tmp.CreateDir("claude-old"));
        await using var orch = Build();

        await Assert.That(await orch.InPlaceResumeRefusalAsync("claude", Session, Repo, null, old))
            .IsEqualTo(ResumeInPlaceRefusals.StoreChanged);
    }

    [Test]
    public async Task A_store_whose_link_was_retargeted_is_refused() {
        var target = Tmp.CreateDir("claude-target");
        var link   = Tmp.PathTo("claude-link");
        Directory.CreateSymbolicLink(link, target);
        Register("acct-link", link);
        await using var orch = Build();

        await Assert.That(await orch.InPlaceResumeRefusalAsync("claude", Session, Repo, "acct-link", link))
            .IsEqualTo(ResumeInPlaceRefusals.StoreChanged);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    public async Task An_unknown_store_is_refused(string? configDir) {
        await using var orch = Build();

        await Assert.That(await orch.InPlaceResumeRefusalAsync("claude", Session, Repo, null, configDir))
            .IsEqualTo(ResumeInPlaceRefusals.StoreChanged);
    }

    [Test]
    public async Task A_missing_directory_is_refused() {
        await using var orch = Build();

        await Assert.That(await orch.InPlaceResumeRefusalAsync("claude", Session, Path.Combine(Repo, "gone"), null, ClaudeDefault))
            .IsEqualTo(ResumeInPlaceRefusals.DirectoryMissing);
    }

    [Test]
    public async Task A_directory_outside_the_allowed_repositories_is_refused() {
        var elsewhere = ResolvedDir("elsewhere");
        await using var orch = Build();

        await Assert.That(await orch.InPlaceResumeRefusalAsync("claude", Session, elsewhere, null, ClaudeDefault))
            .IsEqualTo(ResumeInPlaceRefusals.DirectoryNotAllowed);
    }

    [Test]
    [Arguments("claude", "not-a-guid")]
    [Arguments("cursor", "senc1abcdefghijklmnopqrstuvwxyz2")]
    [Arguments("gemini", "sess-1")]
    [Arguments("codex", Session)]
    public async Task A_session_without_a_native_resume_here_is_refused(string vendor, string session) {
        await using var orch = Build();

        await Assert.That(await orch.InPlaceResumeRefusalAsync(vendor, session, Repo, null, ClaudeDefault))
            .IsEqualTo(ResumeInPlaceRefusals.UnsupportedVendor);
    }

    [Test]
    public async Task An_acp_session_recorded_with_no_account_is_resumable_in_its_root() {
        await using var orch = Build();
        var root = Directory.CreateDirectory(orch.HarnessesForTest.Of<CursorHarness>().Paths.CursorDir).FullName;

        await Assert.That(await orch.InPlaceResumeRefusalAsync("cursor", "sess-1", Repo, null, AccountDirectory.Normalize(root))).IsNull();
        await Assert.That(await orch.InPlaceResumeRefusalAsync("cursor", "sess-1", Repo, null, ClaudeDefault))
            .IsEqualTo(ResumeInPlaceRefusals.StoreChanged);
    }
}
