using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>kcap daemon logs</c> must surface the sibling stderr capture (<c>daemon.out.log</c>), not only
/// the primary <c>daemon.log</c>. The pre-host startup breadcrumbs and native fatal messages land in
/// the capture, and a detached start redirects the daemon's fds there — so an operator following the
/// documented command would otherwise never see the diagnostics meant for an early startup stall.
/// </summary>
public class DaemonLogsTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }
    [TempHome]        public required TempHome        Home    { get; init; }

    StringWriter Out { get; } = new();
    StringWriter Err { get; } = new();

    DaemonCommands Sut() => new(
        Daemons.Store, Config.Root, Resolutions.None(Config.Root), Home,
        TestHarnesses.All(), TestBinaries.None, TimeProvider.System, new StandardStreams(Out, Err, TextReader.Null));

    string LogPath          => Config.Root.Path("daemon.log");
    string StderrCapturePath => System.IO.Path.ChangeExtension(LogPath, null) + ".out.log";

    [Test]
    public async Task Logs_surfaces_the_stderr_capture_sibling() {
        await File.WriteAllTextAsync(LogPath, "primary-log-line\n");
        await File.WriteAllTextAsync(StderrCapturePath, "startup: lock acquired\n");


        var exit = await Sut().HandleAsync(["daemon", "logs"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Out.ToString()).Contains("primary-log-line");
        await Assert.That(Out.ToString()).Contains("startup: lock acquired");
        await Assert.That(Err.ToString()).Contains("daemon.out.log");
    }

    [Test]
    public async Task Logs_skips_an_empty_stderr_capture() {
        await File.WriteAllTextAsync(LogPath, "primary-log-line\n");
        await File.WriteAllTextAsync(StderrCapturePath, "");   // a clean run leaves it empty


        var exit = await Sut().HandleAsync(["daemon", "logs"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Err.ToString()).DoesNotContain("daemon.out.log");
    }

    [Test]
    public async Task Logs_reports_nothing_when_neither_file_exists() {

        var exit = await Sut().HandleAsync(["daemon", "logs"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(Err.ToString()).Contains("No log file found");
    }
}
