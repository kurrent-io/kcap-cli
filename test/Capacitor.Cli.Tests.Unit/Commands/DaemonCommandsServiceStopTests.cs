using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary><c>service stop</c> asks the daemon to shut down before the service manager stops it, and marks
/// the resume ledger interrupted when the daemon did not exit on that request. The "daemon" is a sleep
/// child this test owns, named by a pid file carrying its real start token.</summary>
[NotInParallel]
public class DaemonCommandsServiceStopTests {
    const string Name = "svc";

    [TempHome]        public required TempHome        Home    { get; init; }
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }

    DaemonServiceCommands Commands(IServiceManager manager) =>
        new(Daemons.Store, Config.Root, Resolutions.None(Config.Root), manager, Name, Home, TimeProvider.System);

    DummyProcess StartDaemon() {
        var child = DummyProcess.StartSleep(120);
        File.WriteAllText(Daemons.Store.PidPath(Name), $"{child.Pid}\n{ProcessStartToken.ForPid(child.Pid)}\n");
        return child;
    }

    /// <summary>A daemon that predates the shutdown frame closes the connection without answering: the stop
    /// goes ahead through the service manager, and its agents are marked interrupted by the operator.</summary>
    [Test]
    public async Task A_daemon_that_closes_without_answering_is_stopped_by_the_service_manager() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        using var child = StartDaemon();
        var ledger = ResumeLedgerStore.For(Daemons.Store, Name);
        ledger.Write(ResumeLedgerEntries.For("agent-1", ResumeState.Running));
        var received = FakeControlSocket.AnswerOnce(Daemons.Store, Name, reply: null);
        var manager  = new RecordingServiceManager(new() { [Name] = "/opt/kcap/kcap-daemon" });
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(manager).Stop([]);

        await Assert.That(exit).IsEqualTo(0);
        var request = await received.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(ShutdownIpc.Parse(request!.Text!)).IsEqualTo(new ShutdownRequestDto("end", "final"));
        await Assert.That(manager.Stopped).IsEquivalentTo([Name]);
        await Assert.That(ledger.ReadAll().Single() is { State: ResumeState.Interrupted, LastError: "stopped_by_operator" }).IsTrue();
    }
}
