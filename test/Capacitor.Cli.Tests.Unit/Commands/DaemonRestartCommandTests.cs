using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class DaemonRestartCommandTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }
    [TempHome]        public required TempHome        Home    { get; init; }

    [Test]
    public async Task An_accepted_restart_says_the_agents_resume_on_the_new_daemon() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        var received = FakeControlSocket.AnswerOnce(Daemons.Store, "rs", LocalFrame.RestartAck("restarting"));
        var stdout   = new StringWriter();

        var exit = await new DaemonCommands(
                Daemons.Store, Config.Root, Resolutions.None(Config.Root), Home,
                TestHarnesses.All(), TestBinaries.None, TimeProvider.System,
                new StandardStreams(stdout, TextWriter.Null, TextReader.Null))
            .HandleAsync(["daemon", "restart", "--name", "rs"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That((await received.WaitAsync(TimeSpan.FromSeconds(30)))?.Text).IsEqualTo("now");
        await Assert.That(stdout.ToString().Trim())
            .IsEqualTo("Daemon 'rs': restart accepted; agents are stopped and resumed on the new daemon.");
    }

    [Test]
    public async Task Bare_is_now() =>
        await Assert.That(DaemonCommands.ParseRestartMode([])).IsEqualTo("now");

    [Test]
    public async Task When_idle_flag() =>
        await Assert.That(DaemonCommands.ParseRestartMode(["--when-idle"])).IsEqualTo("when-idle");

    [Test]
    public async Task Force_flag() =>
        await Assert.That(DaemonCommands.ParseRestartMode(["--force"])).IsEqualTo("force");

    [Test]
    public async Task Force_wins_over_when_idle() =>
        await Assert.That(DaemonCommands.ParseRestartMode(["--when-idle", "--force"])).IsEqualTo("force");
}
