using Capacitor.Cli.Service.Logging;
using Capacitor.Cli.Service.Sessions;
using Capacitor.Cli.Service.Supervision;
using Microsoft.Extensions.Time.Testing;
using DaemonSupervisor = Capacitor.Cli.Service.Supervision.Supervisor<Capacitor.Cli.Service.Sessions.Enrollment>;

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

    static readonly TimeSpan StopWait = TimeSpan.FromSeconds(15);

    DaemonSupervisor NewSupervisor() => new(_host, _time, _log, StopWait);

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
        _time.Advance(DaemonSupervisor.RelaunchPause - TimeSpan.FromMilliseconds(1));
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
        _time.Advance(DaemonSupervisor.RelaunchPause * 4);
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
        _time.Advance(DaemonSupervisor.RelaunchPause * 4);
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
        _time.Advance(StopWait);
        var status = await stopping;

        await Assert.That(_host.Launched[0].Terminations).IsEqualTo(1);
        await Assert.That(status.State).IsEqualTo(DaemonState.Stopped);
        _time.Advance(DaemonSupervisor.RelaunchPause * 4);
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
        _time.Advance(DaemonSupervisor.RelaunchPause * 4);
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
        var supervisor = new DaemonSupervisor(_host, _time, _log, StopWait, supervised.Add);

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

    /// A service restart detaches one supervisor and starts another, which finds the daemon still running: it has to
    /// supervise it as one it launched, not only report it
    [Test]
    public async Task A_daemon_adopted_across_a_service_restart_is_relaunched_when_it_crashes() {
        _host.SignIn(1, Alice);
        var before = NewSupervisor();
        before.Start();
        var survivor = _host.Launched[0];
        before.Detach();
        _host.Running = survivor;
        var after = NewSupervisor();

        after.Start();
        var adopted = after.Status(Alice);
        survivor.Exit(-1);
        await WaitUntilAsync(() => after.Status(Alice).State == DaemonState.Relaunching, "the relaunching state");
        _time.Advance(DaemonSupervisor.RelaunchPause);

        await WaitUntilAsync(() => _host.LaunchCount == 2, "the relaunch");
        await Assert.That(adopted.Adopted).IsTrue();
        await Assert.That(adopted.Pid).IsEqualTo(survivor.Pid);
        await Assert.That(after.Status(Alice).State).IsEqualTo(DaemonState.Running);
    }

    [Test]
    public async Task A_stop_asks_an_adopted_daemon_to_stop_and_ends_it_once_the_wait_runs_out() {
        _host.SignIn(1, Alice);
        var adopted = new FakeRunningDaemon(777);
        _host.Running = adopted;
        var supervisor = NewSupervisor();
        supervisor.Start();

        var stopping = supervisor.StopAsync(Alice);
        await WaitUntilAsync(() => _host.StopRequests.Count == 1, "the stop request");
        _time.Advance(StopWait);
        var status = await stopping;
        _time.Advance(DaemonSupervisor.RelaunchPause * 4);
        await Task.Delay(50);

        await Assert.That(_host.StopPids).IsEquivalentTo([777]);
        await Assert.That(adopted.Terminations).IsEqualTo(1);
        await Assert.That(status.State).IsEqualTo(DaemonState.Stopped);
        await Assert.That(_host.LaunchCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_detached_supervisor_leaves_exits_alone() {
        _host.SignIn(1, Alice);
        var supervisor = NewSupervisor();
        supervisor.Start();

        supervisor.Detach();
        _host.Launched[0].Exit(1);
        _time.Advance(DaemonSupervisor.RelaunchPause * 4);
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
        var supervisor = new DaemonSupervisor(failing, _time, _log, StopWait);

        var status = supervisor.Start(Alice);

        await Assert.That(status.State).IsEqualTo(DaemonState.Failed);
        await Assert.That(supervisor.Status(Alice).Error).IsEqualTo("CreateProcessAsUser failed");
    }

    /// The local server is given a longer window than the daemon: the daemon's must not end it early
    [Test]
    public async Task Stop_waits_the_graceful_window_the_supervisor_was_given() {
        _host.SignIn(1, Alice);
        var wait = TimeSpan.FromSeconds(100);
        var supervisor = new DaemonSupervisor(_host, _time, _log, wait);
        supervisor.Start();

        var stopping = supervisor.StopAsync(Alice);
        await WaitUntilAsync(() => _host.StopRequests.Count == 1, "the stop request");
        _time.Advance(StopWait);
        await Task.Delay(50);
        await Assert.That(stopping.IsCompleted).IsFalse();
        await Assert.That(_host.Launched[0].Terminations).IsEqualTo(0);

        _time.Advance(wait - StopWait);
        var status = await stopping;

        await Assert.That(_host.Launched[0].Terminations).IsEqualTo(1);
        await Assert.That(status.State).IsEqualTo(DaemonState.Stopped);
    }

    [Test]
    public async Task The_stop_request_names_the_running_process() {
        _host.SignIn(1, Alice);
        _host.StopRequestEndsDaemon = true;
        var supervisor = NewSupervisor();
        supervisor.Start();

        await supervisor.StopAsync(Alice);

        await Assert.That(_host.StopPids).IsEquivalentTo([_host.Launched[0].Pid]);
    }

    [Test]
    public async Task An_enrollment_that_holds_its_process_down_launches_nothing_even_on_logon() {
        _host.SignIn(1, Alice);
        _host.Enrollments.Remove(Alice);
        _host.Refusals[Alice] = "stopped";
        var supervisor = NewSupervisor();

        supervisor.Start();
        supervisor.OnLogon(1);
        var status = supervisor.Start(Alice);

        await Assert.That(_host.LaunchCount).IsEqualTo(0);
        await Assert.That(status.State).IsEqualTo(DaemonState.NotEnrolled);
        await Assert.That(status.Error).IsEqualTo("stopped");
        await Assert.That(supervisor.HasEnrollment(Alice)).IsTrue();
    }

    [Test]
    public async Task Whether_a_user_is_enrolled_cannot_be_told_without_their_session() {
        _host.SignIn(1, Alice);
        _host.Enrollments.Remove(Alice);
        var supervisor = NewSupervisor();

        await Assert.That(supervisor.HasEnrollment(Alice)).IsFalse();
        await Assert.That(supervisor.HasEnrollment(Bob)).IsNull();
    }

    sealed class ThrowingHost(FakeDaemonHost inner) : IDaemonHost<Enrollment> {
        public IReadOnlyList<int> ActiveSessions() => inner.ActiveSessions();
        public SessionUser? UserOf(int sessionId) => inner.UserOf(sessionId);
        public Enrollment? EnrollmentOf(SessionUser user, out string? refusal) => inner.EnrollmentOf(user, out refusal);
        public bool HasEnrollment(SessionUser user) => inner.HasEnrollment(user);
        public IRunningDaemon? FindRunning(SessionUser user, Enrollment enrollment) => null;
        public IRunningDaemon Launch(SessionUser user, Enrollment enrollment) =>
            throw new InvalidOperationException("CreateProcessAsUser failed");
        public void RequestStop(SessionUser user, Enrollment enrollment, int pid) { }
    }
}
