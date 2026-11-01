using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Services;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;
using Capacitor.Cli.Tests.Unit.Services;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

/// <summary>The local server follows <c>kcap-service</c> in and out, ending every move with exactly one supervisor and
/// a stopped server still stopped.</summary>
public class LocalServerMigrationTests {
    const int Port = 47391;

    [TempDir] public required TempDir Tmp { get; init; }

    readonly FakeServicePipe _pipe = new();
    readonly FakeLocalServerEnrollmentStore _store = new();
    readonly List<string> _log = [];

    UserHome Home => field ??= NewHome();

    LocalRoot Root => field ??= LocalRoot.Resolve(Tmp.PathTo("local"), Home);

    LocalServerUnit Unit => LocalServerUnit.ForInstallation(Root, Port);

    FakeLocalServerService Scheduled => field ??= new(_log, Home);

    LocalServerWindowsService Service => field ??= new(Home, TimeProvider.System, _pipe, _store, Scheduled, (path, content, _) => {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }, _ => null, @"C:\kcap\kcap.exe", _ => { });

    UserHome NewHome() {
        Tmp.CreateDir("home");
        return new(Tmp.GetResolvedPath("home"));
    }

    void Installed() {
        Root.EnsureCreated();
        LocalStateFiles.PublishRegistry(Root,
            InstallRegistry.Empty.WithActive(new("0123456789abcdef0123456789abcdef", Port, $"http://localhost:{Port}", "1.2.3")));
    }

    Task<string?> ToServiceAsync(bool serviceRunsServers = true) =>
        LocalServerMigration.ToServiceAsync(Root, Home, TimeProvider.System, Scheduled, Service, _store, () => serviceRunsServers, CancellationToken.None);

    Task<string?> ToTaskAsync() => LocalServerMigration.ToTaskAsync(Home, TimeProvider.System, Scheduled, Service, _store, CancellationToken.None);

    [Test]
    public async Task A_server_its_task_runs_moves_to_the_service_running() {
        Installed();
        Scheduled.Install(Unit);

        var said = await ToServiceAsync();

        await Assert.That(said).Contains("Moved");
        await Assert.That(Scheduled.IsInstalled(Unit)).IsFalse();
        await Assert.That(_store.Current!.Stopped).IsFalse();
        await Assert.That(_pipe.Verbs).Contains(ServicePipeVerbs.ServerEnroll);
    }

    [Test]
    public async Task A_stopped_server_moves_to_the_service_stopped() {
        Installed();
        Scheduled.Install(Unit);
        await Scheduled.StopAsync(Unit, CancellationToken.None);

        await ToServiceAsync();

        await Assert.That(_store.Current!.Stopped).IsTrue();
    }

    [Test]
    public async Task A_service_that_cannot_run_servers_leaves_the_server_under_its_task() {
        Installed();
        Scheduled.Install(Unit);

        var said = await ToServiceAsync(serviceRunsServers: false);

        await Assert.That(said).Contains("stays a Scheduled Task");
        await Assert.That(Scheduled.IsInstalled(Unit)).IsTrue();
        await Assert.That(_store.Current).IsNull();
    }

    [Test]
    public async Task Without_a_task_there_is_nothing_to_move() {
        Installed();

        await Assert.That(await ToServiceAsync()).IsNull();
        await Assert.That(_pipe.Verbs).IsEmpty();
    }

    /// <summary>The service keeps the server only while it exists; removing it first would leave no supervisor.</summary>
    [Test]
    public async Task Before_the_service_goes_its_server_moves_back_to_a_task() {
        Installed();
        Service.Install(Unit);
        _log.Clear();

        var said = await ToTaskAsync();

        await Assert.That(said).Contains("Scheduled Task");
        await Assert.That(_store.Current).IsNull();
        await Assert.That(_pipe.Verbs).Contains(ServicePipeVerbs.ServerUnenroll);
        await Assert.That(_log).IsEquivalentTo(["install"]);
    }

    [Test]
    public async Task A_server_stopped_under_the_service_stays_stopped_under_its_task() {
        Installed();
        Service.Install(Unit, start: false);
        _log.Clear();

        await ToTaskAsync();

        await Assert.That(_log).IsEquivalentTo(["install", "stop"]);
    }

    [Test]
    public async Task A_server_the_service_cannot_stop_stays_with_the_service_as_it_was() {
        Installed();
        Service.Install(Unit);
        _pipe.Answer = _ => null;
        _log.Clear();

        await Assert.ThrowsAsync<LocalLifecycleException>(ToTaskAsync);

        await Assert.That(_store.Current!.Stopped).IsFalse();
        await Assert.That(_log).IsEmpty();
    }

    [Test]
    public async Task Without_an_enrollment_there_is_nothing_to_move_back() =>
        await Assert.That(await ToTaskAsync()).IsNull();
}
