using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class AgentCommandAccountTests {
    const string Server     = "https://kcap.example";
    const string DaemonName = "acct";
    const string WorkId     = "c1a0de01c1a0de01c1a0de01c1a0de01";
    const string PlayId     = "c1a0de02c1a0de02c1a0de02c1a0de02";
    const string AgentId    = "0123456789abcdef0123456789abcdef";

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }
    [TempHome]        public required TempHome        Home    { get; init; }

    AccountStore Accounts => AccountStore.Beside(Daemons.Store);

    AgentCommand Command(TextWriter stderr) =>
        new(Daemons.Store, Accounts, Config.Root, Resolutions.None(Config.Root), Home, TestHarnesses.All(),
            TestBinaries.None, new WorkingDirectory(Daemons.Root), TimeProvider.System, new StandardStreams(TextWriter.Null, stderr, TextReader.Null));

    Task<int> Start(StringWriter stderr, params string[] args) =>
        Command(stderr).HandleAsync(["agent", "start", .. args, "--daemon", DaemonName], Server);

    string Register(string id, HarnessId vendor, string name) {
        var directory = AccountDirectory.Normalize(Home.CreateDir(name));
        Accounts.Mutate(r => (r with { Accounts = [.. r.Accounts, new VendorAccount(id, vendor, directory, name, DateTimeOffset.UnixEpoch)] }, 0));

        return directory;
    }

    static LocalFrame HelloReply(params string[] capabilities) =>
        LocalFrame.HelloJson(FrameType.HelloReply,
            JsonSerializer.Serialize(new HelloReplyDto(1, "9.9.9", DaemonName, [.. capabilities]), HelloIpcJsonContext.Default.HelloReplyDto));

    // The first connection is the reachability check, which dials and hangs up without a frame.
    Task<IReadOnlyList<LocalFrame?>> Daemon(params LocalFrame[] replies) =>
        FakeControlSocket.AnswerInOrder(Daemons.Store, DaemonName, [null, .. replies]);

    [Test]
    public async Task Account_prefix_resolves_and_the_spawn_frame_carries_the_id() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        Register(WorkId, HarnessId.Claude, ".claude-work");
        Register(PlayId, HarnessId.Claude, ".claude-play");
        Register("c0de0000c0de0000c0de0000c0de0000", HarnessId.Codex, ".codex-work");
        var daemon = Daemon(HelloReply("accounts/1"), FrameCodec.Attached(AgentId, []));
        var stderr = new StringWriter();

        var exit = await Start(stderr, "claude", "--account", "c1a0de02", "-d");

        var frames = await daemon.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(exit).IsEqualTo(0).Because(stderr.ToString());
        await Assert.That(frames.Select(f => f?.Type)).IsEquivalentTo(new FrameType?[] { null, FrameType.Hello, FrameType.Spawn });
        await Assert.That(FrameCodec.Spawn(frames[2]!).accountId).IsEqualTo(PlayId);
    }

    [Test]
    public async Task Tilde_directory_resolves_to_the_registered_account() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        Register(WorkId, HarnessId.Claude, ".claude-work");
        var daemon = Daemon(HelloReply("accounts/1"), FrameCodec.Attached(AgentId, []));
        var stderr = new StringWriter();

        var exit = await Start(stderr, "claude", "--account", "~/.claude-work", "-d");

        var frames = await daemon.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(exit).IsEqualTo(0).Because(stderr.ToString());
        await Assert.That(FrameCodec.Spawn(frames[2]!).accountId).IsEqualTo(WorkId);
    }

    [Test]
    public async Task Ambiguous_prefix_is_refused_listing_both_candidates_and_nothing_is_sent() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        Register(WorkId, HarnessId.Claude, ".claude-work");
        Register(PlayId, HarnessId.Claude, ".claude-play");
        using var daemon = new ControlSocketTripwire(Daemons.Store, DaemonName);
        var stderr = new StringWriter();

        var exit = await Start(stderr, "claude", "--account", "c1a0", "-d");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(stderr.ToString()).Contains(WorkId).And.Contains(PlayId);
        await Assert.That(daemon.WasDialed).IsFalse();
    }

    [Test]
    public async Task Unregistered_directory_is_refused_with_the_add_remedy() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        Register(WorkId, HarnessId.Claude, ".claude-work");
        var stranger = Home.CreateDir(".claude-stranger").Path;
        using var daemon = new ControlSocketTripwire(Daemons.Store, DaemonName);
        var stderr = new StringWriter();

        var exit = await Start(stderr, "claude", "--account", stranger, "-d");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(stderr.ToString()).Contains(stranger).And.Contains("kcap accounts add");
        await Assert.That(daemon.WasDialed).IsFalse();
    }

    [Test]
    public async Task Daemon_without_accounts_capability_is_refused_before_spawn() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        Register(WorkId, HarnessId.Claude, ".claude-work");
        var daemon = Daemon(HelloReply("status/1"));
        var stderr = new StringWriter();

        var exit = await Start(stderr, "claude", "--account", "c1a0de01", "-d");

        var frames = await daemon.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(stderr.ToString()).Contains("this daemon does not support choosing an account; update kcap on this machine");
        await Assert.That(frames.Select(f => f?.Type)).IsEquivalentTo(new FrameType?[] { null, FrameType.Hello });
    }

    [Test]
    public async Task A_vendor_without_accounts_is_refused_and_nothing_is_sent() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        using var daemon = new ControlSocketTripwire(Daemons.Store, DaemonName);
        var stderr = new StringWriter();

        var exit = await Start(stderr, "cursor", "--account", "c1a0de01", "-d");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(stderr.ToString()).Contains("accounts apply to claude and codex only");
        await Assert.That(daemon.WasDialed).IsFalse();
    }

    [Test]
    public async Task An_unreadable_registry_is_refused_naming_its_directory() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        new TempDirHandle(Daemons.Root).CreateFile("accounts/accounts.json", "{ not json");
        using var daemon = new ControlSocketTripwire(Daemons.Store, DaemonName);
        var stderr = new StringWriter();

        var exit = await Start(stderr, "claude", "--account", "c1a0de01", "-d");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(stderr.ToString()).Contains(Accounts.Directory);
        await Assert.That(daemon.WasDialed).IsFalse();
    }

    [Test]
    public async Task Start_without_account_sends_no_hello_and_no_account() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        var daemon = Daemon(FrameCodec.Attached(AgentId, []));
        var stderr = new StringWriter();

        var exit = await Start(stderr, "claude", "-d");

        var frames = await daemon.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(exit).IsEqualTo(0).Because(stderr.ToString());
        await Assert.That(frames.Select(f => f?.Type)).IsEquivalentTo(new FrameType?[] { null, FrameType.Spawn });
        await Assert.That(FrameCodec.Spawn(frames[1]!).accountId).IsNull();
    }
}
