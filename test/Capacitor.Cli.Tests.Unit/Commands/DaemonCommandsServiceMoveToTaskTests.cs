using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;
using Capacitor.Cli.Tests.Unit.Services;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Removing kcap-service gives the daemon it ran back a Scheduled Task, written as an install writes one.</summary>
[NotInParallel]
internal sealed class DaemonCommandsServiceMoveToTaskTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    readonly FakeServicePipe _pipe = new();
    readonly FakeServiceEnrollmentStore _store = new();
    readonly FakeTaskManager _tasks = new();

    WindowsServiceManager Manager() =>
        new(Config.Root, _pipe, _store, _tasks, stopDaemon: _ => true, writeFile: (_, _, _) => { });

    DaemonServiceCommands Commands(WindowsServiceManager manager, string id = "laptop") =>
        new(Daemons.Store, Config.Root, Resolutions.None(Config.Root), manager, id, Home, TimeProvider.System);

    /// The enrollment is the one under whatever name the user has, not the name this invocation resolved
    [Test]
    public async Task The_task_carries_the_enrollments_name_profile_and_agent_cap() {
        var daemon = Config.CreateFile("kcap-daemon.exe");
        _store.Current = new ServiceEnrollment("laptop", daemon, "work", 3, null);
        var manager = Manager();
        using var console = ConsoleOutput.StartCapture();

        var exit = await Commands(manager, id: "other").MoveDaemonToTaskAsync(manager);

        await Assert.That(exit).IsEqualTo(0);
        var spec = _tasks.Specs.Single();
        await Assert.That(spec.ServiceId).IsEqualTo("laptop");
        await Assert.That(spec.DaemonBinaryPath).IsEqualTo(daemon);
        await Assert.That(spec.LogPath).IsEqualTo(Config.Root.Path("daemon-laptop.log"));
        await Assert.That(spec.Environment["KCAP_PROFILE"]).IsEqualTo("work");
        await Assert.That(spec.Environment["KCAP_DAEMON_SUPERVISED"]).IsEqualTo("laptop");
        await Assert.That(spec.ExtraArgs).IsEquivalentTo(["--max-agents", "3"], CollectionOrdering.Matching);
        await Assert.That(console.GetCapturedOutput())
            .Contains("Moved the daemon 'laptop' from kcap-service to a Scheduled Task, which runs it while you are signed in.");
    }

    [Test]
    public async Task Nothing_enrolled_is_nothing_to_move() {
        var manager = Manager();

        var exit = await Commands(manager).MoveDaemonToTaskAsync(manager);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_tasks.Calls).IsEmpty();
        await Assert.That(_pipe.Verbs).IsEmpty();
    }

    /// The elevated removal must not run: the service is still the daemon's only supervisor
    [Test]
    public async Task A_move_that_fails_says_the_service_was_not_changed() {
        _store.Current = new ServiceEnrollment("laptop", Config.CreateFile("kcap-daemon.exe"), null, null, null);
        _tasks.RefuseInstall = true;
        var manager = Manager();
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(manager).MoveDaemonToTaskAsync(manager);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(console.GetCapturedError()).Contains("The Windows service was not changed.");
        await Assert.That(_store.Current).IsNotNull();
    }
}
