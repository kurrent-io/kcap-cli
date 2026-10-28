using Capacitor.Cli.Service.Logging;
using Capacitor.Cli.Service.Sessions;
using Capacitor.Cli.Service.Supervision;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Service.Tests.Unit.Supervision;

public class SupervisorTests {
    const string Alice = "S-1-5-21-1-1001";
    const string Bob   = "S-1-5-21-1-1002";

    sealed class ListLog : IServiceLog {
        public readonly List<string> Lines = [];
        public void Write(string line) { lock (Lines) Lines.Add(line); }
    }

    readonly FakeDaemonHost _host = new();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));
    readonly ListLog _log = new();

    Supervisor NewSupervisor() => new(_host, _time, _log);

    static async Task WaitUntilAsync(Func<bool> condition, string what) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task Start_launches_one_daemon_per_enrolled_signed_in_user() {
        _host.SignIn(1, Alice);
        _host.SignIn(2, Bob);
        _host.Enrollments.Remove(Bob);
        var supervisor = NewSupervisor();

        supervisor.Start();

        await Assert.That(_host.LaunchCount).IsEqualTo(1);
        await Assert.That(supervisor.Status(Alice).State).IsEqualTo(DaemonState.Running);
        await Assert.That(supervisor.Status(Bob).State).IsEqualTo(DaemonState.NoSession);
    }

    [Test]
    public async Task A_crash_relaunches_after_the_pause_and_not_before() {
        _host.SignIn(1, Alice);
        var supervisor = NewSupervisor();
        supervisor.Start();

        _host.Launched[0].Exit(-1);
        await WaitUntilAsync(() => supervisor.Status(Alice).State == DaemonState.Relaunching, "the relaunching state");
        _time.Advance(Supervisor.RelaunchPause - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        await Assert.That(_host.LaunchCount).IsEqualTo(1);

        _time.Advance(TimeSpan.FromMilliseconds(1));

        await WaitUntilAsync(() => _host.LaunchCount == 2, "the relaunch");
        await Assert.That(supervisor.Status(Alice).LastExit).IsEqualTo(-1);
        await Assert.That(supervisor.Status(Alice).Launches).IsEqualTo(2);
    }

    [Test]
    public async Task Exit_zero_is_a_deliberate_stop_and_is_not_relaunched() {
        _host.SignIn(1, Alice);
        var supervisor = NewSupervisor();
        supervisor.Start();

        _host.Launched[0].Exit(0);

        await WaitUntilAsync(() => supervisor.Status(Alice).State == DaemonState.Stopped, "the stopped state");
        _time.Advance(Supervisor.RelaunchPause * 4);
        await Task.Delay(50);
        await Assert.That(_host.LaunchCount).IsEqualTo(1);
    }

    [Test]
    public async Task A_crash_after_logoff_is_not_relaunched() {
        _host.SignIn(1, Alice);
        var supervisor = NewSupervisor();
        supervisor.Start();

        supervisor.OnLogoff(1);
        _host.Launched[0].Exit(1);
        _time.Advance(Supervisor.RelaunchPause * 4);
        await Task.Delay(50);

        await Assert.That(_host.LaunchCount).IsEqualTo(1);
        await Assert.That(supervisor.Status(Alice).State).IsEqualTo(DaemonState.SessionEnded);
    }

    [Test]
    public async Task A_stopped_daemon_stays_stopped_on_unlock_and_starts_on_logon() {
        _host.SignIn(1, Alice);
        _host.StopRequestEndsDaemon = true;
        var supervisor = NewSupervisor();
        supervisor.Start();
        await supervisor.StopAsync(Alice);

        supervisor.OnUnlock(1);
        await Assert.That(_host.LaunchCount).IsEqualTo(1);

        supervisor.OnLogon(1);
        await Assert.That(_host.LaunchCount).IsEqualTo(2);
    }

    [Test]
    public async Task Stop_asks_the_daemon_first_and_does_not_end_the_job_when_it_complies() {
        _host.SignIn(1, Alice);
        _host.StopRequestEndsDaemon = true;
        var supervisor = NewSupervisor();
        supervisor.Start();

        var status = await supervisor.StopAsync(Alice);

        await Assert.That(_host.StopRequests).IsEquivalentTo([Alice]);
        await Assert.That(_host.Launched[0].Terminations).IsEqualTo(0);
        await Assert.That(status.State).IsEqualTo(DaemonState.Stopped);
    }

    [Test]
    public async Task Stop_ends_the_job_once_the_graceful_wait_runs_out() {
        _host.SignIn(1, Alice);
        var supervisor = NewSupervisor();
        supervisor.Start();

        var stopping = supervisor.StopAsync(Alice);
        await WaitUntilAsync(() => _host.StopRequests.Count == 1, "the stop request");
        _time.Advance(Supervisor.GracefulStopWait);
        var status = await stopping;

        await Assert.That(_host.Launched[0].Terminations).IsEqualTo(1);
        await Assert.That(status.State).IsEqualTo(DaemonState.Stopped);
        _time.Advance(Supervisor.RelaunchPause * 4);
        await Task.Delay(50);
        await Assert.That(_host.LaunchCount).IsEqualTo(1);
    }

    [Test]
    public async Task A_stop_during_the_relaunch_pause_cancels_the_relaunch() {
        _host.SignIn(1, Alice);
        var supervisor = NewSupervisor();
        supervisor.Start();
        _host.Launched[0].Exit(-1);
        await WaitUntilAsync(() => supervisor.Status(Alice).State == DaemonState.Relaunching, "the relaunching state");

        var status = await supervisor.StopAsync(Alice);
        _time.Advance(Supervisor.RelaunchPause * 4);
        await Task.Delay(50);

        await Assert.That(status.State).IsEqualTo(DaemonState.Stopped);
        await Assert.That(_host.LaunchCount).IsEqualTo(1);
    }

    [Test]
    public async Task Enrolling_a_changed_enrollment_replaces_the_running_daemon() {
        _host.SignIn(1, Alice);
        _host.StopRequestEndsDaemon = true;
        var supervisor = NewSupervisor();
        supervisor.Start();
        _host.Enrollments[Alice] = _host.Enrollments[Alice] with { Name = "renamed" };

        var status = await supervisor.EnrollAsync(Alice);

        await Assert.That(_host.StopRequests).IsEquivalentTo([Alice]);
        await Assert.That(_host.LaunchCount).IsEqualTo(2);
        await Assert.That(status.Name).IsEqualTo("renamed");
    }

    [Test]
    public async Task Enrolling_the_same_enrollment_leaves_the_running_daemon_alone() {
        _host.SignIn(1, Alice);
        var supervisor = NewSupervisor();
        supervisor.Start();

        var status = await supervisor.EnrollAsync(Alice);

        await Assert.That(_host.StopRequests).IsEmpty();
        await Assert.That(_host.LaunchCount).IsEqualTo(1);
        await Assert.That(status.State).IsEqualTo(DaemonState.Running);
    }

    [Test]
    public async Task Every_launched_or_adopted_user_is_reported_as_supervised() {
        _host.SignIn(1, Alice);
        _host.SignIn(2, Bob);
        _host.Running = new FakeRunningDaemon(777);
        var supervised = new List<string>();
        var supervisor = new Supervisor(_host, _time, _log, supervised.Add);

        supervisor.Start();

        await Assert.That(supervised).IsEquivalentTo([Alice, Bob]);
    }

    [Test]
    public async Task Start_adopts_a_running_daemon_instead_of_launching_a_second() {
        _host.SignIn(1, Alice);
        _host.Running = new FakeRunningDaemon(777, hasJob: false);
        var supervisor = NewSupervisor();

        supervisor.Start();

        var status = supervisor.Status(Alice);
        await Assert.That(_host.LaunchCount).IsEqualTo(0);
        await Assert.That(status.Pid).IsEqualTo(777);
        await Assert.That(status.Adopted).IsTrue();
        await Assert.That(status.HasJob).IsFalse();
    }

    [Test]
    public async Task A_detached_supervisor_leaves_exits_alone() {
        _host.SignIn(1, Alice);
        var supervisor = NewSupervisor();
        supervisor.Start();

        supervisor.Detach();
        _host.Launched[0].Exit(1);
        _time.Advance(Supervisor.RelaunchPause * 4);
        await Task.Delay(50);

        await Assert.That(_host.LaunchCount).IsEqualTo(1);
    }

    [Test]
    public async Task An_explicit_start_reports_why_a_user_cannot_have_a_daemon() {
        _host.SignIn(1, Alice);
        _host.Enrollments.Remove(Alice);
        var supervisor = NewSupervisor();

        var status = supervisor.Start(Alice);

        await Assert.That(status.State).IsEqualTo(DaemonState.NotEnrolled);
        await Assert.That(status.Error).IsEqualTo("no enrollment");
        await Assert.That(supervisor.Start(Bob).State).IsEqualTo(DaemonState.NoSession);
    }

    [Test]
    public async Task A_launch_failure_is_reported_in_the_status() {
        _host.SignIn(1, Alice);
        var failing = new ThrowingHost(_host);
        var supervisor = new Supervisor(failing, _time, _log);

        var status = supervisor.Start(Alice);

        await Assert.That(status.State).IsEqualTo(DaemonState.Failed);
        await Assert.That(supervisor.Status(Alice).Error).IsEqualTo("CreateProcessAsUser failed");
    }

    sealed class ThrowingHost(FakeDaemonHost inner) : IDaemonHost {
        public IReadOnlyList<int> ActiveSessions() => inner.ActiveSessions();
        public SessionUser? UserOf(int sessionId) => inner.UserOf(sessionId);
        public Enrollment? EnrollmentOf(SessionUser user, out string? refusal) => inner.EnrollmentOf(user, out refusal);
        public IRunningDaemon? FindRunning(SessionUser user, Enrollment enrollment) => null;
        public IRunningDaemon Launch(SessionUser user, Enrollment enrollment) =>
            throw new InvalidOperationException("CreateProcessAsUser failed");
        public void RequestStop(SessionUser user, Enrollment enrollment) { }
    }
}
