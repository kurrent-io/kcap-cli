using System.Text.Json;
using Capacitor.Cli.Service.Control;
using Capacitor.Cli.Service.Logging;
using Capacitor.Cli.Service.Supervision;
using Capacitor.Cli.Service.Tests.Unit.Supervision;
using Microsoft.Extensions.Time.Testing;
using DaemonSupervisor = Capacitor.Cli.Service.Supervision.Supervisor<Capacitor.Cli.Service.Sessions.Enrollment>;
using ServerSupervisor = Capacitor.Cli.Service.Supervision.Supervisor<Capacitor.Cli.Service.Sessions.ServerEnrollment>;

namespace Capacitor.Cli.Service.Tests.Unit.Control;

public class ControlDispatcherTests {
    const string Alice = "S-1-5-21-1-1001";
    const string Bob   = "S-1-5-21-1-1002";

    sealed class NullLog : IServiceLog {
        public void Write(string line) { }
    }

    static readonly TimeSpan StopWait = TimeSpan.FromSeconds(15);

    readonly FakeDaemonHost _host = new();
    readonly FakeServerHost _serverHost;
    readonly FakeEnrollmentIndex _index = new();
    int _lastUserGone;

    public ControlDispatcherTests() => _serverHost = new FakeServerHost(_host.Sessions);

    ControlDispatcher NewDispatcher(out DaemonSupervisor supervisor, bool removalStarts = true) {
        supervisor = new DaemonSupervisor(_host, new FakeTimeProvider(), new NullLog(), StopWait);
        var servers = new ServerSupervisor(_serverHost, new FakeTimeProvider(), new NullLog(), StopWait);
        return new ControlDispatcher(supervisor, servers, _index, () => {
            _lastUserGone++;
            return removalStarts;
        }, "1.5.0", new NullLog());
    }

    static ControlResponse Parse(string line) => JsonSerializer.Deserialize(line, ControlJsonContext.Default.ControlResponse)!;

    [Test]
    public async Task Capabilities_list_exactly_the_verbs_the_dispatcher_handles() {
        var dispatcher = NewDispatcher(out _);

        var response = Parse(await dispatcher.HandleAsync(Alice, """{"verb":"capabilities"}""", CancellationToken.None));

        await Assert.That(response.Capabilities!.Verbs).IsEquivalentTo([
            "capabilities", "enroll", "server-enroll", "server-start", "server-status", "server-stop", "server-unenroll",
            "start", "status", "stop", "unenroll",
        ]);
        await Assert.That(response.Capabilities.Protocol).IsEqualTo(ControlDispatcher.Protocol);
        await Assert.That(response.Capabilities.Version).IsEqualTo("1.5.0");
    }

    [Test]
    public async Task A_caller_sees_and_stops_only_their_own_daemon() {
        _host.SignIn(1, Alice);
        _host.SignIn(2, Bob);
        _host.StopRequestEndsDaemon = true;
        var dispatcher = NewDispatcher(out var supervisor);
        supervisor.Start();

        var stop = Parse(await dispatcher.HandleAsync(Bob, """{"verb":"stop","sid":"S-1-5-21-1-1001"}""", CancellationToken.None));

        await Assert.That(stop.Status!.State).IsEqualTo("stopped");
        await Assert.That(_host.StopRequests).IsEquivalentTo([Bob]);
        await Assert.That(supervisor.Status(Alice).State).IsEqualTo(DaemonState.Running);
    }

    [Test]
    public async Task Enroll_indexes_the_user_once_their_daemon_runs() {
        _host.SignIn(1, Alice);
        var dispatcher = NewDispatcher(out _);

        var response = Parse(await dispatcher.HandleAsync(Alice, """{"verb":"enroll"}""", CancellationToken.None));

        await Assert.That(response.Ok).IsTrue();
        await Assert.That(response.Status!.State).IsEqualTo("running");
        await Assert.That(_index.Sids).Contains(Alice);
    }

    [Test]
    public async Task Enroll_without_an_enrollment_is_refused_and_not_indexed() {
        _host.SignIn(1, Alice);
        _host.Enrollments.Remove(Alice);
        var dispatcher = NewDispatcher(out _);

        var response = Parse(await dispatcher.HandleAsync(Alice, """{"verb":"enroll"}""", CancellationToken.None));

        await Assert.That(response.Ok).IsFalse();
        await Assert.That(response.Error).IsEqualTo("no enrollment");
        await Assert.That(_index.IsEmpty).IsTrue();
    }

    [Test]
    public async Task The_last_unenroll_removes_the_service_and_an_earlier_one_does_not() {
        _host.SignIn(1, Alice);
        _host.SignIn(2, Bob);
        _host.StopRequestEndsDaemon = true;
        var dispatcher = NewDispatcher(out _);
        await dispatcher.HandleAsync(Alice, """{"verb":"enroll"}""", CancellationToken.None);
        await dispatcher.HandleAsync(Bob, """{"verb":"enroll"}""", CancellationToken.None);

        await dispatcher.HandleAsync(Alice, """{"verb":"unenroll"}""", CancellationToken.None);
        await Assert.That(_lastUserGone).IsEqualTo(0);

        await dispatcher.HandleAsync(Bob, """{"verb":"unenroll"}""", CancellationToken.None);
        await Assert.That(_lastUserGone).IsEqualTo(1);
    }

    /// An app uninstall has several callers unenroll the same user: the removal starts once, and every one is answered
    [Test]
    public async Task Unenrolls_after_the_last_user_left_are_answered_without_removing_the_service_again() {
        _host.SignIn(1, Alice);
        _host.StopRequestEndsDaemon = true;
        var dispatcher = NewDispatcher(out _);
        await dispatcher.HandleAsync(Alice, """{"verb":"enroll"}""", CancellationToken.None);

        var replies = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            dispatcher.HandleAsync(Alice, """{"verb":"unenroll"}""", CancellationToken.None)));

        await Assert.That(replies.Select(Parse).All(r => r.Ok)).IsTrue();
        await Assert.That(_lastUserGone).IsEqualTo(1);
    }

    [Test]
    public async Task An_enroll_while_the_service_removes_itself_is_refused() {
        _host.SignIn(1, Alice);
        _host.StopRequestEndsDaemon = true;
        var dispatcher = NewDispatcher(out _);
        await dispatcher.HandleAsync(Alice, """{"verb":"enroll"}""", CancellationToken.None);
        await dispatcher.HandleAsync(Alice, """{"verb":"unenroll"}""", CancellationToken.None);

        var response = Parse(await dispatcher.HandleAsync(Alice, """{"verb":"enroll"}""", CancellationToken.None));

        await Assert.That(response.Ok).IsFalse();
        await Assert.That(response.Error).IsEqualTo(ControlDispatcher.LeavingError);
        await Assert.That(_index.IsEmpty).IsTrue();
    }

    [Test]
    public async Task A_removal_that_could_not_start_is_tried_again_by_the_next_unenroll() {
        _host.SignIn(1, Alice);
        _host.StopRequestEndsDaemon = true;
        var dispatcher = NewDispatcher(out _, removalStarts: false);
        await dispatcher.HandleAsync(Alice, """{"verb":"enroll"}""", CancellationToken.None);

        await dispatcher.HandleAsync(Alice, """{"verb":"unenroll"}""", CancellationToken.None);
        await dispatcher.HandleAsync(Alice, """{"verb":"unenroll"}""", CancellationToken.None);

        await Assert.That(_lastUserGone).IsEqualTo(2);
    }

    [Test]
    public async Task A_departed_users_entry_does_not_keep_the_service_installed() {
        _host.SignIn(1, Alice);
        _host.StopRequestEndsDaemon = true;
        _index.Add(Bob);
        _index.Departed.Add(Bob);
        var dispatcher = NewDispatcher(out _);
        await dispatcher.HandleAsync(Alice, """{"verb":"enroll"}""", CancellationToken.None);

        await dispatcher.HandleAsync(Alice, """{"verb":"unenroll"}""", CancellationToken.None);

        await Assert.That(_lastUserGone).IsEqualTo(1);
    }

    [Test]
    public async Task Enroll_indexes_the_user_before_their_daemon_starts() {
        _host.SignIn(1, Alice);
        var indexedAtLaunch = false;
        _host.BeforeLaunch = _ => indexedAtLaunch = _index.Sids.Contains(Alice);
        var dispatcher = NewDispatcher(out _);

        await dispatcher.HandleAsync(Alice, """{"verb":"enroll"}""", CancellationToken.None);

        await Assert.That(indexedAtLaunch).IsTrue();
    }

    [Test]
    public async Task A_refused_enroll_keeps_a_user_who_was_already_indexed() {
        _host.SignIn(1, Alice);
        _host.Enrollments.Remove(Alice);
        _index.Add(Alice);
        var dispatcher = NewDispatcher(out _);

        var response = Parse(await dispatcher.HandleAsync(Alice, """{"verb":"enroll"}""", CancellationToken.None));

        await Assert.That(response.Ok).IsFalse();
        await Assert.That(_index.Sids).Contains(Alice);
    }

    /// The service has dropped the user whatever state their daemon was in; a failure here would leave their
    /// enrollment behind for a retry the service may no longer be there to answer
    [Test]
    public async Task Unenroll_succeeds_for_a_user_whose_daemon_failed_to_launch() {
        _host.SignIn(1, Alice);
        _host.LaunchFails = true;
        var dispatcher = NewDispatcher(out _);
        await dispatcher.HandleAsync(Alice, """{"verb":"enroll"}""", CancellationToken.None);

        var response = Parse(await dispatcher.HandleAsync(Alice, """{"verb":"unenroll"}""", CancellationToken.None));

        await Assert.That(response.Ok).IsTrue();
        await Assert.That(response.Status!.State).IsEqualTo("stopped");
        await Assert.That(_lastUserGone).IsEqualTo(1);
    }

    [Test]
    public async Task A_supervised_user_is_indexed_until_the_service_starts_removing_itself() {
        _host.SignIn(1, Alice);
        _host.StopRequestEndsDaemon = true;
        var dispatcher = NewDispatcher(out _);

        dispatcher.Supervised(Bob);
        await Assert.That(_index.Sids).Contains(Bob);

        _index.Remove(Bob);
        await dispatcher.HandleAsync(Alice, """{"verb":"enroll"}""", CancellationToken.None);
        await dispatcher.HandleAsync(Alice, """{"verb":"unenroll"}""", CancellationToken.None);
        dispatcher.Supervised(Bob);

        await Assert.That(_index.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Server_verbs_act_on_the_server_and_leave_the_daemon_alone() {
        _host.SignIn(1, Alice);
        _serverHost.Enroll(Alice);
        _serverHost.StopRequestEndsDaemon = true;
        var dispatcher = NewDispatcher(out var daemons);

        var enroll = Parse(await dispatcher.HandleAsync(Alice, """{"verb":"server-enroll"}""", CancellationToken.None));
        var status = Parse(await dispatcher.HandleAsync(Alice, """{"verb":"server-status"}""", CancellationToken.None));
        var stop   = Parse(await dispatcher.HandleAsync(Alice, """{"verb":"server-stop"}""", CancellationToken.None));
        var start  = Parse(await dispatcher.HandleAsync(Alice, """{"verb":"server-start"}""", CancellationToken.None));

        await Assert.That(enroll.Ok).IsTrue();
        await Assert.That(status.Status!.State).IsEqualTo("running");
        await Assert.That(status.Status.Name).IsEqualTo("kcap-local");
        await Assert.That(stop.Status!.State).IsEqualTo("stopped");
        await Assert.That(start.Status!.State).IsEqualTo("running");
        await Assert.That(_serverHost.StopRequests).IsEquivalentTo([Alice]);
        await Assert.That(_serverHost.LaunchCount).IsEqualTo(2);
        await Assert.That(_host.LaunchCount).IsEqualTo(0);
        await Assert.That(daemons.Status(Alice).State).IsEqualTo(DaemonState.NoSession);
        await Assert.That(_index.Sids).Contains(Alice);
    }

    [Test]
    public async Task A_stopped_server_is_not_started_and_says_why() {
        _host.SignIn(1, Alice);
        _serverHost.Refusals[Alice] = "stopped";
        var dispatcher = NewDispatcher(out _);

        var response = Parse(await dispatcher.HandleAsync(Alice, """{"verb":"server-start"}""", CancellationToken.None));

        await Assert.That(response.Ok).IsFalse();
        await Assert.That(response.Error).IsEqualTo("stopped");
        await Assert.That(_serverHost.LaunchCount).IsEqualTo(0);
    }

    [Test]
    public async Task Server_unenroll_keeps_the_user_indexed_while_their_daemon_enrollment_remains() {
        _host.SignIn(1, Alice);
        _serverHost.Enroll(Alice);
        _host.StopRequestEndsDaemon = true;
        _serverHost.StopRequestEndsDaemon = true;
        var dispatcher = NewDispatcher(out _);
        await dispatcher.HandleAsync(Alice, """{"verb":"enroll"}""", CancellationToken.None);
        await dispatcher.HandleAsync(Alice, """{"verb":"server-enroll"}""", CancellationToken.None);

        var response = Parse(await dispatcher.HandleAsync(Alice, """{"verb":"server-unenroll"}""", CancellationToken.None));
        _serverHost.Enrollments.Remove(Alice);

        await Assert.That(response.Ok).IsTrue();
        await Assert.That(_index.Sids).Contains(Alice);
        await Assert.That(_lastUserGone).IsEqualTo(0);

        await dispatcher.HandleAsync(Alice, """{"verb":"unenroll"}""", CancellationToken.None);

        await Assert.That(_index.IsEmpty).IsTrue();
        await Assert.That(_lastUserGone).IsEqualTo(1);
    }

    /// A stopped server launches nothing, yet its enrollment still needs the service
    [Test]
    public async Task Unenroll_keeps_the_user_indexed_while_a_stopped_server_enrollment_remains() {
        _host.SignIn(1, Alice);
        _serverHost.Refusals[Alice] = "stopped";
        _host.StopRequestEndsDaemon = true;
        var dispatcher = NewDispatcher(out _);
        await dispatcher.HandleAsync(Alice, """{"verb":"enroll"}""", CancellationToken.None);

        var response = Parse(await dispatcher.HandleAsync(Alice, """{"verb":"unenroll"}""", CancellationToken.None));

        await Assert.That(response.Ok).IsTrue();
        await Assert.That(_index.Sids).Contains(Alice);
        await Assert.That(_lastUserGone).IsEqualTo(0);
    }

    /// Without a session the other enrollment cannot be read, and removing the service on a guess could strand it
    [Test]
    public async Task A_user_whose_other_enrollment_cannot_be_read_stays_indexed() {
        _index.Add(Bob);
        var dispatcher = NewDispatcher(out _);

        var response = Parse(await dispatcher.HandleAsync(Bob, """{"verb":"server-unenroll"}""", CancellationToken.None));

        await Assert.That(response.Ok).IsTrue();
        await Assert.That(_index.Sids).Contains(Bob);
        await Assert.That(_lastUserGone).IsEqualTo(0);
    }

    [Test]
    [Arguments("""{"verb":"format-c"}""", "unknown verb 'format-c'")]
    [Arguments("not json", "the request is not a JSON object with a verb")]
    public async Task A_bad_request_gets_an_error_response(string line, string error) {
        var dispatcher = NewDispatcher(out _);

        var response = Parse(await dispatcher.HandleAsync(Alice, line, CancellationToken.None));

        await Assert.That(response.Ok).IsFalse();
        await Assert.That(response.Error).IsEqualTo(error);
    }
}
