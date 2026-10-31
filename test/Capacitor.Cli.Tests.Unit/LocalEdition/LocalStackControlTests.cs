using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

[ExcludeOn(OS.Windows)]
public class LocalStackControlTests {
    [TempDir] public required TempDir Tmp { get; init; }

    readonly List<string> _log = [];
    readonly List<string> _said = [];

    readonly List<PortHolder> _holders = [];

    UserHome Home => field ??= NewHome();

    LocalRoot Root => field ??= LocalRoot.Resolve(Tmp.PathTo("r"), Home);

    FakeLocalServerService Service => field ??= new(_log, Home);

    FakeLocalServer Server => field ??= new(_log, Root);

    LocalServerUnit Unit => LocalServerUnit.ForInstallation(Root, Active.Port);

    static readonly LocalInstallation Active = new("abc123", 9473, "http://localhost:9473", "1.2.3");

    UserHome NewHome() {
        Tmp.CreateDir("home");
        return new(Tmp.GetResolvedPath("home"));
    }

    LocalStackControl Control(TimeProvider? time = null) =>
        new(new LocalStack(Root, Home, Service, new HttpClient(Server), time ?? TimeProvider.System, new FakeOutgoingRecorders([]), new ConfigRoot(Tmp.PathTo("cfg"))), _ => _holders.FirstOrDefault());

    void Publish() {
        Root.EnsureCreated();
        LocalStateFiles.PublishRegistry(Root, InstallRegistry.Empty.WithActive(Active));

        Tmp.CreateFile(["r", "bin", "current", "kcap-server"]);
    }

    Task<string> Start(TimeProvider? time = null) => Control(time).StartAsync(new RecordingProgress<LocalInstallEvent>(), _said.Add, CancellationToken.None);

    Task<string> Stop() => Control().StopAsync(new RecordingProgress<LocalInstallEvent>(), _said.Add, CancellationToken.None);

    Task<string> Restart() => Control().RestartAsync(new RecordingProgress<LocalInstallEvent>(), _said.Add, CancellationToken.None);

    [Test]
    public async Task Start_waits_through_an_online_cutover_and_its_supervised_restart() {
        Publish();
        var time = new FakeTimeProvider();
        var pending = true;
        Server.Override = path => path == "/healthz/ready"
            ? new(System.Net.HttpStatusCode.OK) { Content = new StringContent(pending ? "{\"updatePending\":true}" : "{\"updatePending\":false}") }
            : null;
        var start = Start(time);
        try {
            await Assert.That(start.IsCompleted).IsFalse();
            for (var boot = 1; boot <= 4; boot++) {
                Service.ActivityOverride = new UnitActivity.Restarting(10) { Starts = boot };
                time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(20);
                await Assert.That(start.IsCompleted).IsFalse();
                Service.ActivityOverride = new UnitActivity.Running(100 + boot) { Starts = boot };
                time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(20);
                await Assert.That(start.IsCompleted).IsFalse();
            }
            pending = false;
            Service.ActivityOverride = new UnitActivity.Running(123);
            while (!start.IsCompleted) {
                time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(1);
            }
            await Assert.That(await start).IsEqualTo("Ready on http://localhost:9473");
        } finally {
            pending = false;
            time.Advance(LocalServerReadiness.StartBudget);
        }
    }

    [Test]
    public async Task Start_without_an_installation_sends_the_user_to_setup() {
        var failure = await Assert.ThrowsAsync<LocalInstallFailure>(() => Start());

        await Assert.That(failure!.Message).Contains("kcap setup --local");
        await Assert.That(_log).IsEmpty();
    }

    [Test]
    public async Task Start_installs_a_missing_unit_and_waits_for_the_lift() {
        Publish();

        var done = await Start();

        await Assert.That(done).IsEqualTo("Ready on http://localhost:9473");
        await Assert.That(_log).IsEquivalentTo(["install", "GET /healthz/ready", "GET /auth/config"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Start_starts_this_roots_stopped_unit() {
        Publish();
        Service.Install(Unit);
        await Service.StopAsync(Unit, CancellationToken.None);
        _log.Clear();

        await Start();

        await Assert.That(_log[0]).IsEqualTo("start");
    }

    [Test]
    public async Task Start_leaves_another_roots_unit_alone() {
        Publish();
        var other = Tmp.PathTo("elsewhere");
        Service.OwnerOverride = new(new OwnerReading.Root(other), new OwnerReading.Root(other));

        var failure = await Assert.ThrowsAsync<LocalInstallFailure>(() => Start());

        await Assert.That(failure!.Message).Contains("not provably this installation's").And.Contains(other);
        await Assert.That(_log).IsEmpty();
    }

    [Test]
    public async Task Start_gives_up_at_once_on_a_taken_port() {
        Publish();
        Server.Override = _ => throw new HttpRequestException("Connection refused");
        _holders.Add(new(1, "nginx"));

        var failure = await Assert.ThrowsAsync<LocalInstallFailure>(() => Start(new FakeTimeProvider()));

        await Assert.That(failure!.Message).Contains("Port 9473 is taken by nginx (pid 1)");
    }

    /// <summary>The OS names no holder here, as it cannot for another user's process; the 404 alone is enough,
    /// since the kcap server's health route never returns one.</summary>
    [Test]
    public async Task Start_gives_up_at_once_on_a_foreign_answer_the_os_cannot_name() {
        Publish();
        Server.Override = path => path == "/healthz/ready" ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) : null;

        var failure = await Assert.ThrowsAsync<LocalInstallFailure>(() => Start(new FakeTimeProvider()));

        await Assert.That(failure!.Message).Contains("Port 9473 answers as a web server that answers HTTP 404, not as the kcap server");
        await Assert.That(_log).DoesNotContain("stop");
    }

    /// <summary>The server's own host answers 404 until the health route is mapped; a start waits through it rather
    /// than calling the server a stranger.</summary>
    [Test]
    public async Task Start_waits_through_its_own_hosts_answers_before_the_health_route() {
        Publish();
        var time   = new FakeTimeProvider();
        var mapped = false;
        Server.Override = path => {
            if (path != "/healthz/ready" || mapped) return null;

            var response = new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            response.Headers.TryAddWithoutValidation("Server", "Kestrel");
            return response;
        };

        var start = Start(time);
        try {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20);
            await Assert.That(start.IsCompleted).IsFalse();

            mapped = true;
            while (!start.IsCompleted) {
                time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(1);
            }

            await Assert.That(await start).IsEqualTo("Ready on http://localhost:9473");
        } finally {
            mapped = true;
            time.Advance(LocalServerReadiness.StartBudget);
        }
    }

    [Test]
    public async Task Start_gives_up_at_once_on_a_server_that_exited_for_good() {
        Publish();
        Server.Override = _ => throw new HttpRequestException("Connection refused");
        Service.ActivityOverride = new UnitActivity.Exited(1);

        var failure = await Assert.ThrowsAsync<LocalInstallFailure>(() => Start(new FakeTimeProvider()));

        await Assert.That(failure!.Message).Contains("exited with code 1");
    }

    [Test]
    public async Task Start_gives_up_on_a_server_that_keeps_exiting() {
        Publish();
        var time = new FakeTimeProvider();
        var pid  = 100;
        Service.ActivityOverride = new UnitActivity.Running(pid);
        Server.OnRequest = path => {
            if (path != "/healthz/ready") return;

            Service.ActivityOverride = Service.ActivityOverride is UnitActivity.Running ? new UnitActivity.Restarting(3) : new UnitActivity.Running(++pid);
        };
        Server.Override = _ => throw new HttpRequestException("Connection refused");

        var start = Start(time);
        while (!start.IsCompleted) {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1);
        }

        var failure = await Assert.ThrowsAsync<LocalInstallFailure>(() => start);

        await Assert.That(failure!.Message).Contains("keeps exiting with code 3");
        await Assert.That(time.GetUtcNow() - time.Start).IsLessThan(TimeSpan.FromMinutes(1));
    }

    [Test]
    public async Task Start_counts_exits_it_never_saw_running() {
        Publish();
        var time   = new FakeTimeProvider();
        var starts = 1;
        Service.ActivityOverride = new UnitActivity.Restarting(3) { Starts = starts };
        Server.OnRequest = path => {
            if (path == "/healthz/ready") Service.ActivityOverride = new UnitActivity.Restarting(3) { Starts = ++starts };
        };
        Server.Override = _ => throw new HttpRequestException("Connection refused");

        var start = Start(time);
        while (!start.IsCompleted) {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1);
        }

        var failure = await Assert.ThrowsAsync<LocalInstallFailure>(() => start);

        await Assert.That(failure!.Message).Contains("keeps exiting with code 3");
    }

    [Test]
    public async Task Start_does_not_take_a_ready_answer_from_another_listener() {
        Publish();
        _holders.Add(new(1, "kcap-server"));

        var failure = await Assert.ThrowsAsync<LocalInstallFailure>(() => Start());

        await Assert.That(failure!.Message).Contains("Port 9473 is taken by kcap-server (pid 1)");
    }

    [Test]
    public async Task Start_rereads_the_process_before_calling_the_listener_foreign() {
        Publish();
        _holders.Add(new(42, "kcap-server"));
        Service.Activities.Enqueue(new UnitActivity.Restarting(null));
        Service.Activities.Enqueue(new UnitActivity.Running(42));
        Service.ActivityOverride = new UnitActivity.Running(42);

        await Assert.That(await Start()).IsEqualTo("Ready on http://localhost:9473");
    }

    [Test]
    public async Task Start_gives_up_on_a_server_that_stopped_after_running() {
        Publish();
        Server.Override = _ => throw new HttpRequestException("Connection refused");
        Service.Activities.Enqueue(new UnitActivity.Running(7));
        Service.ActivityOverride = UnitActivity.Stopped.Instance;

        var time  = new FakeTimeProvider();
        var start = Start(time);
        while (!start.IsCompleted) {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1);
        }

        var failure = await Assert.ThrowsAsync<LocalInstallFailure>(() => start);

        await Assert.That(failure!.Message).Contains("stopped and will not be started again");
    }

    [Test]
    public async Task Start_does_not_call_a_listener_foreign_while_the_process_cannot_be_read() {
        Publish();
        _holders.Add(new(42, "kcap-server"));
        Service.Activities.Enqueue(new UnitActivity.Unknown("launchctl print failed"));
        Service.ActivityOverride = new UnitActivity.Running(42);
        var time = new FakeTimeProvider();

        var start = Start(time);
        while (!start.IsCompleted) {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1);
        }

        await Assert.That(await start).IsEqualTo("Ready on http://localhost:9473");
    }

    [Test]
    public async Task Start_refuses_a_server_that_does_not_answer_as_this_installation() {
        Publish();
        Server.WhileAllowed = FakeAuthConfig.Forbidden;

        var failure = await Assert.ThrowsAsync<LocalInstallFailure>(() => Start());

        await Assert.That(failure!.Message).Contains("does not answer as this installation");
    }

    [Test]
    public async Task Stop_stops_this_roots_unit() {
        Publish();
        Service.Install(Unit);
        _log.Clear();

        var done = await Stop();

        await Assert.That(done).StartsWith("Stopped.");
        await Assert.That(_log).IsEquivalentTo(["stop"]);
    }

    [Test]
    public async Task Stop_leaves_another_roots_unit_alone() {
        Publish();
        var other = Tmp.PathTo("elsewhere");
        Service.OwnerOverride = new(new OwnerReading.Root(other), OwnerReading.Absent.Instance);

        await Assert.ThrowsAsync<LocalInstallFailure>(Stop);

        await Assert.That(_log).IsEmpty();
    }

    [Test]
    public async Task Stop_with_no_unit_has_nothing_to_stop() {
        Publish();

        await Assert.That(await Stop()).StartsWith("Not running");
        await Assert.That(_log).IsEmpty();
    }

    [Test]
    public async Task Restart_stops_then_starts() {
        Publish();
        Service.Install(Unit);
        _log.Clear();

        await Restart();

        await Assert.That(_log.Take(2)).IsEquivalentTo(["stop", "start"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }
}
