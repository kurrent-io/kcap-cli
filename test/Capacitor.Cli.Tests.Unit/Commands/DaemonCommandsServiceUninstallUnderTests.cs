using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// An app's uninstaller removes only the units that run its own daemon copy; a unit pointing
/// anywhere else belongs to another install and must survive.
[NotInParallel]
public class DaemonCommandsServiceUninstallUnderTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Unreadable = RecordingServiceManager.Unreadable;

    DaemonServiceCommands Commands(IServiceManager manager, string id = "unused") =>
        new(Daemons.Store, Config.Root, Resolutions.None(Config.Root), manager, id, Home, TimeProvider.System);

    [Test]
    public async Task Removes_only_units_whose_daemon_is_inside_the_folder() {
        var app = Path.GetFullPath("/apps/kcap/current");
        var manager = new RecordingServiceManager(new() {
            ["laptop"] = Path.Combine(app, "kcap-daemon.exe"),
            ["npm"]    = Path.GetFullPath("/npm/node_modules/@kurrent/kcap/bin/kcap-daemon.exe"),
            ["near"]   = Path.GetFullPath("/apps/kcap/current-old/kcap-daemon.exe"),
            ["blank"]  = null,
        });
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(manager).UninstallUnder(app);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(manager.Uninstalled).IsEquivalentTo(["laptop"]);
    }

    /// <summary>The removed daemon never starts again, so it is asked to end its agents before the unit
    /// goes; the bootout alone would keep them for a successor.</summary>
    [Test]
    public async Task A_running_daemon_is_asked_to_end_its_agents_before_its_unit_is_removed() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        var app = Path.GetFullPath("/apps/kcap/current");
        using var child = DummyProcess.StartSleep(120);
        File.WriteAllText(Daemons.Store.PidPath("laptop"), $"{child.Pid}\n{ProcessStartToken.ForPid(child.Pid)}\n");
        // Set before the stand-in dies: a bootout that found it gone came after the request.
        var asked    = 0;
        var received = FakeControlSocket.AnswerOnce(Daemons.Store, "laptop", LocalFrame.ShutdownAck("stopping"),
            afterReply: () => { Volatile.Write(ref asked, 1); child.Kill(); child.WaitForExit(); });
        var askedFirst = new List<bool>();
        var manager = new RecordingServiceManager(new() { ["laptop"] = Path.Combine(app, "kcap-daemon.exe") }) {
            OnBootout = _ => askedFirst.Add(Volatile.Read(ref asked) == 1),
        };
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(manager).UninstallUnder(app);

        await Assert.That(exit).IsEqualTo(0);
        var request = await received.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(ShutdownIpc.Parse(request!.Text!)).IsEqualTo(new ShutdownRequestDto("end", "final"));
        await Assert.That(askedFirst).IsEquivalentTo([true]);
        await Assert.That(manager.Uninstalled).IsEquivalentTo(["laptop"]);
    }

    [Test]
    public async Task Uninstall_asks_a_running_daemon_to_end_its_agents_first() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        using var child = DummyProcess.StartSleep(120);
        File.WriteAllText(Daemons.Store.PidPath("solo"), $"{child.Pid}\n{ProcessStartToken.ForPid(child.Pid)}\n");
        // Set before the stand-in dies: a bootout that found it gone came after the request.
        var asked    = 0;
        var received = FakeControlSocket.AnswerOnce(Daemons.Store, "solo", LocalFrame.ShutdownAck("stopping"),
            afterReply: () => { Volatile.Write(ref asked, 1); child.Kill(); child.WaitForExit(); });
        var askedFirst = new List<bool>();
        var manager = new RecordingServiceManager(new() { ["solo"] = "/opt/kcap/kcap-daemon" }) {
            OnBootout = _ => askedFirst.Add(Volatile.Read(ref asked) == 1),
        };
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(manager, "solo").Uninstall();

        await Assert.That(exit).IsEqualTo(0);
        var request = await received.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(ShutdownIpc.Parse(request!.Text!)).IsEqualTo(new ShutdownRequestDto("end", "final"));
        await Assert.That(askedFirst).IsEquivalentTo([true]);
        await Assert.That(manager.Uninstalled).IsEquivalentTo(["solo"]);
    }

    /// <summary>A daemon that cannot be reached is removed by the bootout as before.</summary>
    [Test]
    public async Task Uninstall_of_a_daemon_that_does_not_answer_still_removes_its_unit() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        using var child = DummyProcess.StartSleep(120);
        File.WriteAllText(Daemons.Store.PidPath("solo"), $"{child.Pid}\n{ProcessStartToken.ForPid(child.Pid)}\n");
        var manager = new RecordingServiceManager(new() { ["solo"] = "/opt/kcap/kcap-daemon" });
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(manager, "solo").Uninstall();

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(manager.Uninstalled).IsEquivalentTo(["solo"]);
    }

    /// <summary>A daemon that could not be asked to end its agents leaves entries a later start would
    /// resume; the operator meant to end them.</summary>
    [Test]
    public async Task Uninstall_of_a_daemon_that_does_not_answer_marks_its_ledger_interrupted() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        using var child = DummyProcess.StartSleep(120);
        File.WriteAllText(Daemons.Store.PidPath("solo"), $"{child.Pid}\n{ProcessStartToken.ForPid(child.Pid)}\n");
        var ledger = ResumeLedgerStore.For(Daemons.Store, "solo");
        ledger.Write(ResumeLedgerEntries.For("agent-1", ResumeState.Running));
        ledger.Write(ResumeLedgerEntries.For("agent-2", ResumeState.Resuming));
        var manager = new RecordingServiceManager(new() { ["solo"] = "/opt/kcap/kcap-daemon" });
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(manager, "solo").Uninstall();

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(ledger.ReadAll().All(e => e is { State: ResumeState.Interrupted, LastError: "stopped_by_operator" })).IsTrue();
    }

    [Test]
    public async Task UninstallUnder_of_a_daemon_that_does_not_answer_marks_its_ledger_interrupted() {
        Skip.When(OperatingSystem.IsWindows(), "the control socket is a unix socket");
        var app = Path.GetFullPath("/apps/kcap/current");
        using var child = DummyProcess.StartSleep(120);
        File.WriteAllText(Daemons.Store.PidPath("laptop"), $"{child.Pid}\n{ProcessStartToken.ForPid(child.Pid)}\n");
        var ledger = ResumeLedgerStore.For(Daemons.Store, "laptop");
        ledger.Write(ResumeLedgerEntries.For("agent-1", ResumeState.Running));
        var manager = new RecordingServiceManager(new() { ["laptop"] = Path.Combine(app, "kcap-daemon.exe") });
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(manager).UninstallUnder(app);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(ledger.ReadAll().Single() is { State: ResumeState.Interrupted, LastError: "stopped_by_operator" }).IsTrue();
    }

    [Test]
    public async Task A_unit_another_operation_holds_is_left_and_reported() {
        var app = Path.GetFullPath("/apps/kcap/current");
        var manager = new RecordingServiceManager(new() { ["laptop"] = Path.Combine(app, "kcap-daemon.exe") });
        using var held = await ServiceTxnLock.TryAcquireAsync(Daemons.Store, "laptop", TimeSpan.Zero, TimeProvider.System);
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(manager).UninstallUnder(app);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(manager.Uninstalled).IsEmpty();
        await Assert.That(console.GetCapturedError()).Contains("'laptop'");
    }

    [Test]
    public async Task A_unit_repointed_while_the_lock_was_held_is_left_alone() {
        var app = Path.GetFullPath("/apps/kcap/current");
        var binaries = new Dictionary<string, string?> { ["laptop"] = Path.Combine(app, "kcap-daemon.exe") };
        var manager = new RecordingServiceManager(binaries);
        var held = await ServiceTxnLock.TryAcquireAsync(Daemons.Store, "laptop", TimeSpan.Zero, TimeProvider.System);
        using var console = ConsoleOutput.StartFullCapture();

        var run = Commands(manager).UninstallUnder(app);
        while (manager.StatusReads == 0) await Task.Delay(10);
        binaries["laptop"] = Path.GetFullPath("/npm/node_modules/@kurrent/kcap/bin/kcap-daemon.exe");
        held!.Dispose();
        var exit = await run;

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(manager.Uninstalled).IsEmpty();
    }

    [Test]
    public async Task A_unit_that_cannot_be_read_does_not_stop_the_others() {
        var app = Path.GetFullPath("/apps/kcap/current");
        var manager = new RecordingServiceManager(new() {
            ["broken"] = Unreadable,
            ["laptop"] = Path.Combine(app, "kcap-daemon.exe"),
        });
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(manager).UninstallUnder(app);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(manager.Uninstalled).IsEquivalentTo(["laptop"]);
        await Assert.That(console.GetCapturedError()).Contains("'broken'");
    }

    /// <summary>A listing that failed is not an empty one: reporting success would leave the app's task
    /// retrying its deleted daemon at every sign-in.</summary>
    [Test]
    public async Task A_listing_that_fails_is_reported_not_taken_as_no_units() {
        var app = Path.GetFullPath("/apps/kcap/current");
        var manager = new RecordingServiceManager(new() { ["laptop"] = Path.Combine(app, "kcap-daemon.exe") }) { ListingFails = true };
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(manager).UninstallUnder(app);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(manager.Uninstalled).IsEmpty();
        await Assert.That(console.GetCapturedError()).Contains("Could not list the installed services");
    }

    [Test]
    [Arguments(new[] { "--json" }, null)]
    [Arguments(new[] { "--under", "C:/apps/kcap" }, "C:/apps/kcap")]
    [Arguments(new[] { "--under" }, "")]
    [Arguments(new[] { "--under", "" }, "")]
    [Arguments(new[] { "--under", "  " }, "")]
    [Arguments(new[] { "--under", "--name", "foo" }, "")]
    public async Task UnderArg_refuses_a_missing_blank_or_flag_value(string[] rest, string? expected) {
        await Assert.That(DaemonServiceCommands.UnderArg(rest)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("/a/b/kcap-daemon", "/a/b", false, true)]
    [Arguments("/a/b/kcap-daemon", "/a/b/", false, true)]
    [Arguments("/a/bc/kcap-daemon", "/a/b", false, false)]
    [Arguments("/A/B/kcap-daemon", "/a/b", false, false)]
    [Arguments("/A/B/kcap-daemon", "/a/b", true, true)]
    [Arguments("/a/b/../c/kcap-daemon", "/a/b", false, false)]
    [Arguments("/a/kcap-daemon", "/", false, true)]
    public async Task IsUnder_compares_whole_path_segments(string path, string dir, bool ignoreCase, bool expected) {
        await Assert.That(DaemonServiceCommands.IsUnder(Path.GetFullPath(path), dir, ignoreCase)).IsEqualTo(expected);
    }

    /// <summary>Without a user bus nothing can be disabled, so a unit the folder owns must fail the run and
    /// stay, never be skipped as someone else's and reported as success.</summary>
    [Test]
    public async Task UninstallUnder_without_a_reachable_systemd_fails_and_keeps_an_owned_unit() {
        var app  = Path.GetFullPath("/apps/kcap/current");
        var unit = SystemdUnit.UnitPath(Home, "laptop");
        Directory.CreateDirectory(Path.GetDirectoryName(unit)!);
        File.WriteAllText(unit, SystemdUnit.Unit(new ServiceSpec(
            "laptop", Path.Combine(app, "bin", "kcap-daemon"), "/x/daemon.log", new Dictionary<string, string>(), [])));
        var manager = new SystemdServiceManager(Home, runProcess: (_, _) => (1, "", "Failed to connect to bus: No medium found"));
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(manager).UninstallUnder(app);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(File.Exists(unit)).IsTrue();
    }

    /// <summary>Stopping the daemon is the first step of removing its unit; when the supervisor cannot say
    /// anything about that unit, neither step is taken, or the daemon would end up stopped beside a unit
    /// that stays installed.</summary>
    [Test]
    public async Task UninstallUnder_leaves_a_unit_whose_state_is_unknown_and_its_daemon_alone() {
        Skip.When(OperatingSystem.IsWindows(), "the stand-in daemon is identified through /proc");
        var app = Path.GetFullPath("/apps/kcap/current");
        using var child = DummyProcess.StartSleep(120);
        File.WriteAllText(Daemons.Store.PidPath("laptop"), $"{child.Pid}\n{ProcessStartToken.ForPid(child.Pid)}\n");
        var ledger = ResumeLedgerStore.For(Daemons.Store, "laptop");
        ledger.Write(ResumeLedgerEntries.For("agent-1", ResumeState.Running));
        var manager = new RecordingServiceManager(new() { ["laptop"] = Path.Combine(app, "kcap-daemon.exe") });
        manager.Unanswered.Add("laptop");
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(manager).UninstallUnder(app);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(manager.Uninstalled).IsEmpty();
        await Assert.That(ledger.ReadAll().Single().State).IsEqualTo(ResumeState.Running);
    }
}
