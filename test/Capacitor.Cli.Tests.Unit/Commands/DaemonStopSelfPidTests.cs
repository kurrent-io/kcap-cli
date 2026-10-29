using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>daemon stop</c> must refuse to kill a PID that is this very process. A pid file naming the
/// test runner, with its real start token, passes every identity check, so only the self-reference
/// guard stands between it and <c>Process.Kill(entireProcessTree: true)</c> on the runner itself.
/// </summary>
public class DaemonStopSelfPidTests {
    [TempDaemonPaths]  public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]   public required TempConfigRoot  Config  { get; init; }
    [TempHome]         public required TempHome        Home    { get; init; }

    [Test]
    public async Task Stop_refuses_to_kill_a_pid_file_naming_the_current_process() {
        // Written directly, not through a held DaemonLock: a held lock leaves the pid file unreadable,
        // and the stop would refuse down that branch without ever reaching the kill.
        var token = ProcessStartToken.ForPid(Environment.ProcessId);

        await File.WriteAllTextAsync(
            Daemons.Store.PidPath("self"), $"{Environment.ProcessId}\n{token}\n");

        var stderr = new StringWriter();
        var exit = await new DaemonCommands(
                Daemons.Store, Config.Root, Resolutions.None(Config.Root), Home,
                TestHarnesses.All(), TestBinaries.None, TimeProvider.System,
                new StandardStreams(TextWriter.Null, stderr, TextReader.Null))
            .HandleAsync(["daemon", "stop", "--name", "self", "--yes"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(stderr.ToString()).Contains("refusing to stop it");
    }
}
