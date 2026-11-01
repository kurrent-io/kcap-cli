using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Services;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;
using Capacitor.Cli.Tests.Unit.Services;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

/// <summary>The local server under <c>kcap-service</c>, against a fake pipe and enrollment: the registry contract the
/// service reads, the verbs it is sent, and the task it falls back to.</summary>
internal sealed class LocalServerWindowsServiceTests {
    const string Cli = @"C:\Users\alice\AppData\Local\kcap\current\bin\kcap.exe";

    [TempDir] public required TempDir Tmp { get; init; }

    readonly FakeServicePipe _pipe = new();
    readonly FakeLocalServerEnrollmentStore _store = new();
    readonly List<string> _log = [];
    readonly List<string> _said = [];

    UserHome Home => field ??= NewHome();

    UserHome NewHome() {
        Tmp.CreateDir("home");
        return new(Tmp.GetResolvedPath("home"));
    }

    LocalServerUnit Unit => field ??= LocalServerUnit.ForInstallation(new LocalRoot(Tmp.PathTo("local")), 51234);

    FakeLocalServerService Fallback => field ??= new(_log, Home);

    string EnvironmentFile => LocalServerWindowsService.EnvironmentFilePath(Home);

    LocalServerWindowsService Service(Func<LocalRoot, string?>? findLive = null, IServicePipe? pipe = null) =>
        new(Home, TimeProvider.System, pipe ?? _pipe, _store, Fallback, (path, content, _) => {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }, findLive ?? (_ => null), Cli, _said.Add);

    void Enrolled(bool stopped = false) =>
        _store.Current = new(Unit.ServerBinary, Unit.Root.Directory, Cli, EnvironmentFile, Unit.OutputLog, stopped);

    [Test]
    public async Task Install_writes_the_environment_and_the_enrollment_then_enrolls() {
        Service().Install(Unit);

        await Assert.That(_store.Current).IsEqualTo(new LocalServerEnrollment(
            Unit.ServerBinary, Unit.Root.Directory, Cli, EnvironmentFile, Unit.OutputLog, Stopped: false));
        await Assert.That(File.ReadAllText(EnvironmentFile)).Contains("\"Local__Port\":\"51234\"");
        await Assert.That(_pipe.Verbs).IsEquivalentTo([ServicePipeVerbs.ServerEnroll]);
        await Assert.That(_log).IsEmpty();
    }

    /// <summary>Two supervisors on one root would each start a server on it.</summary>
    [Test]
    public async Task Install_retires_a_task_the_user_still_has_before_it_enrolls() {
        Fallback.Install(Unit);
        _log.Clear();

        Service().Install(Unit);

        await Assert.That(_log).IsEquivalentTo(["uninstall"]);
        await Assert.That(Fallback.IsInstalled(Unit)).IsFalse();
        await Assert.That(_store.Current).IsNotNull();
    }

    [Test]
    public async Task A_refused_enrollment_falls_back_to_the_task_and_says_so() {
        _pipe.Answer = verb => verb == ServicePipeVerbs.ServerEnroll ? new ServicePipeResponse(false, "kcap-service is removing itself") : null;

        Service().Install(Unit);

        await Assert.That(_store.Current).IsNull();
        await Assert.That(File.Exists(EnvironmentFile)).IsFalse();
        await Assert.That(_log).IsEquivalentTo(["install"]);
        await Assert.That(_said.Single()).Contains("kcap-service is removing itself");
        await Assert.That(_said.Single()).Contains("Scheduled Task");
    }

    [Test]
    public async Task A_stopped_move_falls_back_to_a_stopped_task() {
        _pipe.Answer = _ => null;

        Service().Install(Unit, start: false);

        await Assert.That(_log).IsEquivalentTo(["install", "stop"]);
    }

    [Test]
    public async Task Start_clears_the_persisted_stop_then_asks_the_service() {
        Enrolled(stopped: true);

        Service().Start(Unit);

        await Assert.That(_store.Current!.Stopped).IsFalse();
        await Assert.That(_pipe.Verbs).IsEquivalentTo([ServicePipeVerbs.ServerStart]);
    }

    [Test]
    public async Task A_stop_persists_then_waits_until_nothing_of_the_stack_is_alive() {
        Enrolled();
        var probes = 0;

        await Service(_ => ++probes < 3 ? "Postgres still accepts connections" : null).StopAsync(Unit, CancellationToken.None);

        await Assert.That(_store.Current!.Stopped).IsTrue();
        await Assert.That(_pipe.Verbs).IsEquivalentTo([ServicePipeVerbs.ServerStop]);
        await Assert.That(probes).IsEqualTo(3);
    }

    [Test]
    public async Task A_stop_the_service_does_not_answer_fails() {
        Enrolled();
        _pipe.Answer = _ => null;

        var refused = await Assert.ThrowsAsync<LocalLifecycleException>(() => Service().StopAsync(Unit, CancellationToken.None));

        await Assert.That(refused!.Message).Contains("did not answer");
    }

    [Test]
    public async Task Uninstall_unenrolls_then_removes_the_enrollment_and_its_environment() {
        Enrolled();
        Directory.CreateDirectory(Path.GetDirectoryName(EnvironmentFile)!);
        File.WriteAllText(EnvironmentFile, "{}");

        await Service().UninstallAsync(Unit, CancellationToken.None);

        await Assert.That(_pipe.Verbs).IsEquivalentTo([ServicePipeVerbs.ServerUnenroll]);
        await Assert.That(_store.Current).IsNull();
        await Assert.That(File.Exists(EnvironmentFile)).IsFalse();
    }

    /// <summary>The service answers an unenroll only once the server has stopped; an app uninstaller cannot wait that long.</summary>
    [Test]
    public async Task A_quick_uninstall_waits_no_longer_than_its_grace_and_checks_nothing() {
        Enrolled();
        var pipe  = new TimedPipe();
        var grace = TimeSpan.FromSeconds(10);

        await Service(_ => throw new InvalidOperationException("the quick path probed the stack"), pipe).UninstallWithinAsync(Unit, grace, CancellationToken.None);

        await Assert.That(pipe.Sent.Single()).IsEqualTo((ServicePipeVerbs.ServerUnenroll, grace));
        await Assert.That(_store.Current).IsNull();
    }

    sealed class TimedPipe : IServicePipe {
        public List<(string, TimeSpan)> Sent { get; } = [];

        public ServicePipeResponse? Send(string verb, TimeSpan timeout) {
            Sent.Add((verb, timeout));
            return null;
        }
    }

    public static IEnumerable<Func<(ServicePipeStatus Status, UnitActivity Activity)>> States() {
        yield return () => (new ServicePipeStatus("running", Pid: 4242, Launches: 2), new UnitActivity.Running(4242) { Starts = 2 });
        yield return () => (new ServicePipeStatus("relaunching", LastExit: 3, Launches: 2), new UnitActivity.Restarting(3) { Starts = 2 });
        yield return () => (new ServicePipeStatus("failed", LastExit: 1), new UnitActivity.Exited(1) { Starts = 0 });
        yield return () => (new ServicePipeStatus("stopped"), UnitActivity.Stopped.Instance);
        yield return () => (new ServicePipeStatus("session_ended"), UnitActivity.Stopped.Instance);
        yield return () => (new ServicePipeStatus("stopping"), UnitActivity.Stopped.Instance);
    }

    [Test]
    [MethodDataSource(nameof(States))]
    public async Task The_services_state_words_read_as_the_units_activity(ServicePipeStatus status, UnitActivity activity) =>
        await Assert.That(LocalServerWindowsService.Read(status)).IsEqualTo(activity);

    /// <summary>A newer service's word is never taken for a running server.</summary>
    [Test]
    public async Task A_state_word_this_cli_does_not_know_is_unknown() =>
        await Assert.That(LocalServerWindowsService.Read(new ServicePipeStatus("hibernating"))).IsTypeOf<UnitActivity.Unknown>();

    [Test]
    public async Task A_service_that_does_not_answer_leaves_the_activity_unknown() {
        Enrolled();
        _pipe.Answer = _ => null;

        await Assert.That(Service().Activity(Unit)).IsTypeOf<UnitActivity.Unknown>();
    }

    [Test]
    public async Task The_owner_is_the_root_the_enrollment_and_its_environment_name() {
        Service().Install(Unit);

        await Assert.That(Service().Owner(Unit)).IsEqualTo(
            new UnitOwner(new OwnerReading.Root(Unit.Root.Directory), new OwnerReading.Root(Unit.Root.Directory)));
    }

    [Test]
    public async Task Without_an_enrollment_or_a_task_nothing_owns_the_slot() =>
        await Assert.That(Service().Owner(Unit)).IsEqualTo(new UnitOwner(OwnerReading.Absent.Instance, OwnerReading.Absent.Instance));

    /// <summary>Until an install moves it, the task the user already has is the server's supervisor.</summary>
    [Test]
    public async Task Without_an_enrollment_a_remaining_task_gets_every_call() {
        Fallback.Install(Unit);
        _log.Clear();
        var service = Service();

        service.Start(Unit);
        await service.StopAsync(Unit, CancellationToken.None);

        await Assert.That(_log).IsEquivalentTo(["start", "stop"]);
        await Assert.That(service.Owner(Unit)).IsEqualTo(Fallback.Owner(Unit));
        await Assert.That(service.UnitFile(Unit)).IsEqualTo(Fallback.UnitFile(Unit));
        await Assert.That(_pipe.Verbs).IsEmpty();
    }
}
