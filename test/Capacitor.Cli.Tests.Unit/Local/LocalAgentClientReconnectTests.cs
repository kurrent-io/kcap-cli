using System.Net.Sockets;
using System.Text;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Local;

namespace Capacitor.Cli.Tests.Unit.Local;

/// <summary>An attached terminal survives a daemon restart: the dropped socket is retried by agent id
/// until the resumed agent answers, or until its ledger entry says it will not come back.</summary>
[NotInParallel]
public class LocalAgentClientReconnectTests {
    const string AgentId = "0123456789abcdef0123456789abcdef";
    const string Daemon  = "d";

    [TempDaemonPaths("rc")] public required TempDaemonStore Daemons { get; init; }

    string SocketPath => Daemons.Store.SocketPath(Daemon);

    void SeedLedger(ResumeState state, string? lastError = null) =>
        ResumeLedgerStore.For(Daemons.Store, Daemon).Write(ResumeLedgerEntries.For(AgentId, state) with { LastError = lastError });

    AttachReconnect Reconnect() =>
        new(AgentId, TimeSpan.FromSeconds(20), () => ResumeLedgerStore.FindByAgentId(Daemons.Store, AgentId)) {
            RetryGap = TimeSpan.FromMilliseconds(50)
        };

    Socket Listen() {
        Daemons.Store.EnsureDirectory();
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        listener.Listen(4);

        return listener;
    }

    static Task<int> Attach(string socketPath, FakeAgentTerminal terminal, AttachReconnect reconnect) =>
        LocalAgentClient.RunAsync(
            socketPath, new LocalFrame(FrameType.Attach) { Text = AgentId }, TimeProvider.System, terminal, reconnect, CancellationToken.None);

    /// <summary>Accepts one connection, reads its opening frame, writes the replies and closes it.</summary>
    static async Task<LocalFrame?> AnswerAsync(Socket listener, params LocalFrame[] replies) {
        using var connection = await listener.AcceptAsync();
        await using var stream = new NetworkStream(connection, ownsSocket: false);
        var opening = await FrameCodec.ReadAsync(stream, CancellationToken.None);
        foreach (var reply in replies) await FrameCodec.WriteAsync(stream, reply, CancellationToken.None);

        return opening;
    }

    [Test]
    public async Task Attach_reconnects_after_a_dropped_socket_and_replays() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        SeedLedger(ResumeState.Resuming);
        using var listener = Listen();
        using var terminal = new FakeAgentTerminal();
        using var console  = ConsoleOutput.StartErrorCapture("\n");
        var replay = "earlier output\r\n── kcap daemon restarted; session resumed ──\r\n";

        var run = Attach(SocketPath, terminal, Reconnect());

        await AnswerAsync(listener, FrameCodec.Attached(AgentId, Encoding.UTF8.GetBytes("before the restart\r\n")));
        var notYet = await AnswerAsync(listener, LocalFrame.Error($"no such agent {AgentId}"));
        var again  = await AnswerAsync(listener, FrameCodec.Attached(AgentId, Encoding.UTF8.GetBytes(replay)), LocalFrame.Exited(0));

        var exit = await run.WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(notYet?.Type).IsEqualTo(FrameType.Attach);
        await Assert.That(again?.Type).IsEqualTo(FrameType.Attach);
        await Assert.That(again?.Text).IsEqualTo(AgentId);
        await Assert.That(terminal.Output).IsEqualTo("before the restart\r\n" + replay);
        var waiting = console.GetCapturedError().Split('\n').Count(l => l.Contains($"daemon restarting; waiting for {AgentId} to resume…"));
        await Assert.That(waiting).IsEqualTo(1);
    }

    [Test]
    public async Task Attach_exits_with_the_reason_when_the_entry_is_interrupted() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        SeedLedger(ResumeState.Interrupted, "worktree_missing");
        using var terminal = new FakeAgentTerminal();
        using var console  = ConsoleOutput.StartErrorCapture("\n");
        Task<int> run;

        using (var listener = Listen()) {
            run = Attach(SocketPath, terminal, Reconnect());
            await AnswerAsync(listener, FrameCodec.Attached(AgentId, []));
        }

        var exit = await run.WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(console.GetCapturedError()).Contains("worktree_missing");
        await Assert.That(console.GetCapturedError()).Contains($"kcap agent resume {AgentId}");
        await Assert.That(console.GetCapturedError()).DoesNotContain("daemon restarting");
    }

    [Test]
    public async Task Attaching_to_an_agent_already_interrupted_prints_the_reason() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        SeedLedger(ResumeState.Interrupted, "too_old");
        using var listener = Listen();
        using var terminal = new FakeAgentTerminal();
        using var console  = ConsoleOutput.StartErrorCapture("\n");

        var run = Attach(SocketPath, terminal, Reconnect());
        await AnswerAsync(listener, LocalFrame.Error($"no such agent {AgentId}"));

        var exit = await run.WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(console.GetCapturedError()).Contains("too_old");
        await Assert.That(console.GetCapturedError()).Contains($"kcap agent resume {AgentId}");
        await Assert.That(console.GetCapturedError()).DoesNotContain("no such agent");
        await Assert.That(console.GetCapturedError()).DoesNotContain("daemon restarting");
    }

    [Test]
    public async Task A_first_refusal_while_the_agent_resumes_waits_for_it_and_attaches() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        SeedLedger(ResumeState.Resuming);
        using var listener = Listen();
        using var terminal = new FakeAgentTerminal();
        using var console  = ConsoleOutput.StartErrorCapture("\n");

        var run = Attach(SocketPath, terminal, Reconnect());

        await AnswerAsync(listener, LocalFrame.Error($"no such agent {AgentId}"));
        var again = AnswerAsync(listener, FrameCodec.Attached(AgentId, Encoding.UTF8.GetBytes("resumed\r\n")), LocalFrame.Exited(0));

        var exit = await run.WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That((await again.WaitAsync(TimeSpan.FromSeconds(5)))?.Type).IsEqualTo(FrameType.Attach);
        await Assert.That(terminal.Output).IsEqualTo("resumed\r\n");
        await Assert.That(console.GetCapturedError()).DoesNotContain("no such agent");
    }

    /// <summary>Only the daemon's own not-published refusal is ridden out; any other first-attach error is
    /// final, or a live agent with no terminal would be retried for the whole window.</summary>
    [Test]
    public async Task A_first_refusal_for_another_reason_is_printed_and_ends_the_attach() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        SeedLedger(ResumeState.Running);
        using var listener = Listen();
        using var terminal = new FakeAgentTerminal();
        using var console  = ConsoleOutput.StartErrorCapture("\n");

        var run = Attach(SocketPath, terminal, Reconnect());
        await AnswerAsync(listener, LocalFrame.Error($"{AgentId} has no terminal to attach to"));

        var exit = await run.WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(console.GetCapturedError()).Contains("has no terminal to attach to");
        await Assert.That(console.GetCapturedError()).DoesNotContain("daemon restarting");
    }

    /// <summary>The daemon can be gone when the attach opens, between its exit and its successor's socket.</summary>
    [Test]
    public async Task An_opening_connect_refused_while_the_agent_resumes_waits_for_the_daemon() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        SeedLedger(ResumeState.Resuming);
        using var terminal = new FakeAgentTerminal();
        using var console  = ConsoleOutput.StartErrorCapture("\n");

        var run = Attach(SocketPath, terminal, Reconnect());
        await Task.Delay(200);
        using var listener = Listen();
        var opened = await AnswerAsync(listener, FrameCodec.Attached(AgentId, Encoding.UTF8.GetBytes("resumed\r\n")), LocalFrame.Exited(0));

        var exit = await run.WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(opened?.Type).IsEqualTo(FrameType.Attach);
        await Assert.That(terminal.Output).IsEqualTo("resumed\r\n");
        await Assert.That(console.GetCapturedError()).Contains($"daemon restarting; waiting for {AgentId} to resume…");
    }

    [Test]
    public async Task A_dropped_socket_without_a_ledger_entry_is_a_lost_connection() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        using var terminal = new FakeAgentTerminal();
        using var console  = ConsoleOutput.StartErrorCapture("\n");
        Task<int> run;

        using (var listener = Listen()) {
            run = Attach(SocketPath, terminal, Reconnect());
            await AnswerAsync(listener, FrameCodec.Attached(AgentId, []));
        }

        var exit = await run.WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(console.GetCapturedError()).Contains("lost connection to the daemon");
        await Assert.That(console.GetCapturedError()).DoesNotContain("daemon restarting");
    }
}
