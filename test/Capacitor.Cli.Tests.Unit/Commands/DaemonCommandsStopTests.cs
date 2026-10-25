using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary><c>daemon stop</c> asks the daemon to shut down with the operator's intent and kills it only
/// when that request is not answered by an exit, marking the resume ledger interrupted on the kill.
/// The "daemon" is a sleep child this test owns, named by a pid file carrying its real start token.</summary>
[ParallelLimiter<SubprocessLimit>]
public class DaemonCommandsStopTests {
    const string Name = "st";

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }
    [TempHome]        public required TempHome        Home    { get; init; }

    StringWriter Out { get; } = new();
    StringWriter Err { get; } = new();

    DaemonCommands Commands(TimeProvider time) =>
        new(Daemons.Store, Config.Root, Resolutions.None(Config.Root), Home,
            TestHarnesses.All(), TestBinaries.None, time, new StandardStreams(Out, Err, TextReader.Null));

    DummyProcess StartDaemon() {
        var child = DummyProcess.StartSleep(120);
        File.WriteAllText(Daemons.Store.PidPath(Name), $"{child.Pid}\n{ProcessStartToken.ForPid(child.Pid)}\n");
        return child;
    }

    ResumeLedgerStore SeedLedger(params (string AgentId, ResumeState State, string? LastError)[] entries) {
        var ledger = ResumeLedgerStore.For(Daemons.Store, Name);
        var stamp  = TimeProvider.System.GetUtcNow();

        foreach (var (agentId, state, lastError) in entries) {
            var launch = new ResumeLaunchDescriptor(
                Prompt: null, Model: null, Effort: null, RepoPath: "/repo", WorktreePath: "/repo/wt",
                Work: WorkLocation.OwnedWorktree, GitRelativeCwd: null, SandboxPolicy: null, ApprovalPolicy: null,
                PermissionPreset: null, McpConfigPath: null, Title: null, IsPrivate: false, Origin: "local",
                RequesterUserId: null, RequesterDisplay: null, ConsentSource: null, ConsentAt: null,
                FlowRunId: null, FlowRole: null);
            ledger.Write(new ResumeLedgerEntry(
                ResumeLedgerEntry.CurrentSchemaVersion, agentId, "daemon-1", stamp, stamp, "claude", "pty",
                null, null, launch, state, 0, lastError, PredecessorInstanceId: null, ResumingAt: null));
        }

        return ledger;
    }

    static ShutdownRequestDto? Request(LocalFrame? frame) =>
        frame is { Type: FrameType.Shutdown, Text: { } text } ? ShutdownIpc.Parse(text) : null;

    [Test]
    public async Task Stop_sends_end_agents_final_and_does_not_kill_when_the_daemon_exits() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        using var child = StartDaemon();
        var ledger   = SeedLedger(("agent-1", ResumeState.Running, null));
        var received = FakeControlSocket.AnswerOnce(Daemons.Store, Name, LocalFrame.ShutdownAck("stopping"),
            afterReply: () => { child.Kill(); child.WaitForExit(); });

        var exit = await Commands(TimeProvider.System).HandleAsync(["daemon", "stop", "--name", Name]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Request(await received.WaitAsync(TimeSpan.FromSeconds(30)))).IsEqualTo(new ShutdownRequestDto("end", "final"));
        await Assert.That(ledger.ReadAll().Single().State).IsEqualTo(ResumeState.Running);
        await Assert.That(Out.ToString()).Contains($"Daemon '{Name}' stopped (PID {child.Pid}).");
    }

    [Test]
    public async Task Stop_keep_agents_sends_keep() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        using var child = StartDaemon();
        var received = FakeControlSocket.AnswerOnce(Daemons.Store, Name, LocalFrame.ShutdownAck("queued"),
            afterReply: () => { child.Kill(); child.WaitForExit(); });

        var exit = await Commands(TimeProvider.System).HandleAsync(["daemon", "stop", "--name", Name, "--keep-agents"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Request(await received.WaitAsync(TimeSpan.FromSeconds(30)))).IsEqualTo(new ShutdownRequestDto("keep", "final"));
    }

    [Test]
    public async Task Stop_falls_back_to_kill_and_marks_the_ledger_interrupted_when_nothing_answers() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        using var child = StartDaemon();
        var identity = PidIdentity.Capture(child.Pid);
        var ledger   = SeedLedger(("agent-1", ResumeState.Running, null), ("agent-2", ResumeState.Resuming, null));

        var exit = await Commands(TimeProvider.System).HandleAsync(["daemon", "stop", "--name", Name]);

        await Assert.That(exit).IsEqualTo(0);
        await PidIdentity.WaitUntilGoneAsync(child.Pid, identity, TimeSpan.FromSeconds(10));
        var entries = ledger.ReadAll();
        await Assert.That(entries.Count).IsEqualTo(2);
        await Assert.That(entries.All(e => e is { State: ResumeState.Interrupted, LastError: "stopped_by_operator" })).IsTrue();
    }

    /// <summary>A daemon that predates the shutdown frame closes the connection without answering; the
    /// stop falls back to the kill at once rather than waiting out the bound.</summary>
    [Test]
    public async Task Stop_falls_back_to_kill_when_the_daemon_closes_without_answering() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        using var child = StartDaemon();
        var identity = PidIdentity.Capture(child.Pid);
        var ledger   = SeedLedger(("agent-1", ResumeState.Running, null));
        var received = FakeControlSocket.AnswerOnce(Daemons.Store, Name, reply: null);

        var exit = await Commands(new FakeTimeProvider()).HandleAsync(["daemon", "stop", "--name", Name]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Request(await received.WaitAsync(TimeSpan.FromSeconds(30)))).IsEqualTo(new ShutdownRequestDto("end", "final"));
        await PidIdentity.WaitUntilGoneAsync(child.Pid, identity, TimeSpan.FromSeconds(10));
        await Assert.That(ledger.ReadAll().Single() is { State: ResumeState.Interrupted, LastError: "stopped_by_operator" }).IsTrue();
    }

    [Test]
    public async Task Stop_falls_back_when_the_daemon_acks_but_never_exits() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        using var child = StartDaemon();
        var identity = PidIdentity.Capture(child.Pid);
        var ledger   = SeedLedger(("agent-1", ResumeState.Running, null));
        var acked    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = FakeControlSocket.AnswerOnce(Daemons.Store, Name, LocalFrame.ShutdownAck("stopping"),
            afterReply: acked.SetResult);
        var time = new FakeTimeProvider();

        var run = Commands(time).HandleAsync(["daemon", "stop", "--name", Name]);
        await acked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(run.IsCompleted).IsFalse();

        for (var steps = 0; !run.IsCompleted; steps++) {
            if (steps > 2000) throw new TimeoutException("the stop never gave up waiting under the fake clock");
            time.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(5);
        }

        await Assert.That(await run).IsEqualTo(0);
        await Assert.That(Request(await received.WaitAsync(TimeSpan.FromSeconds(30)))).IsEqualTo(new ShutdownRequestDto("end", "final"));
        await PidIdentity.WaitUntilGoneAsync(child.Pid, identity, TimeSpan.FromSeconds(10));
        await Assert.That(ledger.ReadAll().Single() is { State: ResumeState.Interrupted, LastError: "stopped_by_operator" }).IsTrue();
    }

    /// <summary>A keep-agents stop that overruns had already saved some agents as resuming; only the
    /// entries still running are killed mid-run. An end request ends them all.</summary>
    [Test]
    [Arguments(true, ResumeState.Resuming)]
    [Arguments(false, ResumeState.Interrupted)]
    public async Task An_overrun_stop_keeps_the_resuming_entries_only_when_it_kept_agents(bool keep, ResumeState resumingAfter) {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        using var child = StartDaemon();
        var identity = PidIdentity.Capture(child.Pid);
        var ledger   = SeedLedger(("running-1", ResumeState.Running, null), ("saved-1", ResumeState.Resuming, null));
        var acked    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = FakeControlSocket.AnswerOnce(Daemons.Store, Name, LocalFrame.ShutdownAck("stopping"), afterReply: acked.SetResult);
        var time = new FakeTimeProvider();

        string[] args = keep ? ["daemon", "stop", "--name", Name, "--keep-agents"] : ["daemon", "stop", "--name", Name];
        var run = Commands(time).HandleAsync(args);
        await acked.Task.WaitAsync(TimeSpan.FromSeconds(30));

        for (var steps = 0; !run.IsCompleted; steps++) {
            if (steps > 2000) throw new TimeoutException("the stop never gave up waiting under the fake clock");
            time.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(5);
        }

        await Assert.That(await run).IsEqualTo(0);
        await PidIdentity.WaitUntilGoneAsync(child.Pid, identity, TimeSpan.FromSeconds(10));
        await Assert.That(ledger.TryRead("running-1")!.State).IsEqualTo(ResumeState.Interrupted);
        await Assert.That(ledger.TryRead("saved-1")!.State).IsEqualTo(resumingAfter);
    }

    /// <summary>The daemon exits between the end of the bound and the fallback, having left its ledger as
    /// it wants it: the fallback must neither signal the pid nor overwrite the ledger.</summary>
    [Test]
    public async Task Stop_leaves_a_daemon_that_exits_just_after_the_bound_alone() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        using var child = StartDaemon();
        var ledger   = SeedLedger(("agent-1", ResumeState.Resuming, null));
        var acked    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = FakeControlSocket.AnswerOnce(Daemons.Store, Name, LocalFrame.ShutdownAck("stopping"),
            afterReply: acked.SetResult);
        var time = new FakeTimeProvider();

        var run = Task.Run(() => Commands(time).HandleAsync(["daemon", "stop", "--name", Name, "--keep-agents"]));
        await acked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(200);

        // The last poll before the bound parks a delay due past it, so the bound fires first.
        time.Advance(DaemonCommands.StopExitWait - TimeSpan.FromMilliseconds(20));
        await Task.Delay(200);
        child.Kill();
        child.WaitForExit();
        time.Advance(TimeSpan.FromMilliseconds(50));

        await Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo(0);
        await Assert.That(Request(await received.WaitAsync(TimeSpan.FromSeconds(30)))).IsEqualTo(new ShutdownRequestDto("keep", "final"));
        await Assert.That(ledger.ReadAll().Single() is { State: ResumeState.Resuming, LastError: null }).IsTrue();
        await Assert.That(Out.ToString()).DoesNotContain("was not running");
    }

    [Test]
    public async Task Status_lists_resuming_and_interrupted_counts() {
        SeedLedger(
            ("agent-1", ResumeState.Resuming, null),
            ("agent-2", ResumeState.Interrupted, "stopped_by_operator"),
            ("agent-3", ResumeState.Interrupted, "runtime_exited"),
            ("agent-4", ResumeState.Running, null));

        var exit = await Commands(TimeProvider.System).HandleAsync(["daemon", "status", "--name", Name]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Out.ToString())
            .Contains("  resuming: 1  interrupted: 2 (runtime_exited, stopped_by_operator)");
    }

    [Test]
    public async Task Status_omits_the_ledger_line_when_nothing_is_resuming_or_interrupted() {

        await Commands(TimeProvider.System).HandleAsync(["daemon", "status", "--name", Name]);

        await Assert.That(Out.ToString()).DoesNotContain("resuming:");
    }
}
