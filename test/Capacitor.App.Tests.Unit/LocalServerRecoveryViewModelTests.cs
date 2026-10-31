using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Windows.Input;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class LocalServerRecoveryViewModelTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly ProcessResult Started = new(0, "Local server is running.\n", "", false);

    sealed class Recorder {
        public int Starts;
        public int Reconnects;
        public Func<CancellationToken, Task<ProcessResult>> Next = _ => Task.FromResult(Started);

        public Task<ProcessResult> Start(CancellationToken ct) {
            Interlocked.Increment(ref Starts);
            return Next(ct);
        }

        public Task Reconnect() {
            Interlocked.Increment(ref Reconnects);
            return Task.CompletedTask;
        }
    }

    static LocalServerRecoveryViewModel Build(
            BehaviorSubject<ServerLaneStatus> status, bool isLocalServer, Recorder recorder) =>
        new(status, isLocalServer, recorder.Start, recorder.Reconnect, CancellationToken.None);

    /// The banner reads only the server lane: nothing about the daemon is an input, so a healthy
    /// daemon cannot hide it. A Connecting between retries keeps it up rather than flickering.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_retrying_local_server_offers_start_whatever_the_daemon_says() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Connecting));
            using var vm = Build(status, isLocalServer: true, new Recorder());

            await Assert.That(vm.Visible).IsFalse();
            status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));
            await Assert.That(vm.Visible).IsTrue();
            status.OnNext(new(ServerLaneState.Connecting));
            await Assert.That(vm.Visible).IsTrue();
            status.OnNext(new(ServerLaneState.Connected));
            await Assert.That(vm.Visible).IsFalse();
        });
    }

    /// A failed start says so until the server comes back, however it comes back; a later stop is then a plain stop.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_server_that_comes_back_clears_the_last_start_failure() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Retrying, "Connection refused"));
            var recorder = new Recorder { Next = _ => Task.FromResult(new ProcessResult(1, "", "start_failed: the server did not start\n", false)) };
            using var vm = Build(status, isLocalServer: true, recorder);

            await vm.StartCommand.Execute().ToTask();
            await Assert.That(vm.Message).IsNotNull();

            status.OnNext(new(ServerLaneState.Connected));
            await Assert.That(vm.Message).IsNull();
        });
    }

    [Test]
    [Arguments("exit")]
    [Arguments("start")]
    [Arguments("reconnect")]
    [NotInParallel("AvaloniaSession")]
    public async Task Recovery_during_a_start_prevents_a_late_failure_from_returning(string failure) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Retrying));
            var start = new TaskCompletionSource<ProcessResult>();
            var reconnect = new TaskCompletionSource();
            using var vm = new LocalServerRecoveryViewModel(status, true,
                _ => failure == "reconnect" ? Task.FromResult(Started) : start.Task,
                () => reconnect.Task, CancellationToken.None);
            var run = vm.StartAsync();
            status.OnNext(new(ServerLaneState.Connected));
            status.OnNext(new(ServerLaneState.Retrying));

            if (failure == "exit") start.SetResult(new ProcessResult(1, "", "late start failure", false));
            else if (failure == "start") start.SetException(new InvalidOperationException("late start failure"));
            else reconnect.SetException(new InvalidOperationException("late reconnect failure"));
            await run;

            await Assert.That(vm.Message).IsNull();
            await Assert.That(vm.Visible).IsTrue();
        });
    }

    /// An upgrade stops the server on purpose, and a Start then would only meet its lock; the server
    /// still reads as down, so the generic lost-connection notice stays quiet.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_hides_while_an_upgrade_runs_and_the_server_still_reads_down() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Retrying, "Connection refused"));
            var busy = new BehaviorSubject<bool>(true);
            using var vm = new LocalServerRecoveryViewModel(
                status, isLocalServer: true, new Recorder().Start, new Recorder().Reconnect, CancellationToken.None, busy);

            await Assert.That(vm.Visible).IsFalse();
            await Assert.That(vm.ServerDown).IsTrue();

            busy.OnNext(false);

            await Assert.That(vm.Visible).IsTrue();
            await Assert.That(vm.ServerDown).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_retrying_cloud_server_never_offers_start() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Retrying, "Connection refused"));
            using var vm = Build(status, isLocalServer: false, new Recorder());

            await Assert.That(vm.Visible).IsFalse();
            status.OnNext(new(ServerLaneState.Connecting));
            status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));
            await Assert.That(vm.Visible).IsFalse();
        });
    }

    /// The CLI's own readiness budget is ten minutes; the view model must add no limit of its own.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_start_longer_than_a_minute_succeeds_and_reconnects() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var time = new FakeTimeProvider();
            var recorder = new Recorder {
                Next = async ct => {
                    await Task.Delay(TimeSpan.FromSeconds(90), time, ct);
                    return Started;
                },
            };
            var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Retrying));
            using var vm = Build(status, isLocalServer: true, recorder);

            var run = vm.StartCommand.Execute().ToTask();
            time.Advance(TimeSpan.FromSeconds(61));
            await Assert.That(recorder.Reconnects).IsEqualTo(0);
            time.Advance(TimeSpan.FromSeconds(29));
            await run;

            await Assert.That(recorder.Reconnects).IsEqualTo(1);
            await Assert.That(vm.Message).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(1, false)]
    [Arguments(-1, true)]
    public async Task A_failed_or_contended_start_shows_the_cli_output_and_can_be_pressed_again(int exitCode, bool timedOut) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var recorder = new Recorder {
                Next = _ => Task.FromResult(new ProcessResult(exitCode, "Starting the local server...\n", "lifecycle_busy: another kcap local command is running\n", timedOut)),
            };
            var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Retrying));
            using var vm = Build(status, isLocalServer: true, recorder);

            await vm.StartCommand.Execute().ToTask();

            await Assert.That(vm.Message).Contains("lifecycle_busy: another kcap local command is running");
            await Assert.That(vm.Message).Contains("Starting the local server...");
            await Assert.That(recorder.Reconnects).IsEqualTo(0);
            await Assert.That(vm.Visible).IsTrue();

            recorder.Next = _ => Task.FromResult(Started);
            await vm.StartCommand.Execute().ToTask();

            await Assert.That(recorder.Starts).IsEqualTo(2);
            await Assert.That(recorder.Reconnects).IsEqualTo(1);
            await Assert.That(vm.Message).IsNull();
        });
    }

    /// Through ICommand, the path a bound button takes: ReactiveCommand's own executing gate only
    /// disables the button, so the view model has to refuse the second run itself.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_second_press_while_starting_does_nothing() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var release = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var recorder = new Recorder { Next = _ => release.Task };
            var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Retrying));
            using var vm = Build(status, isLocalServer: true, recorder);

            var run = vm.StartCommand.Execute().ToTask();
            ((ICommand)vm.StartCommand).Execute(null);
            await vm.StartCommand.Execute().ToTask();
            release.SetResult(Started);
            await run;

            await Assert.That(recorder.Starts).IsEqualTo(1);
            await Assert.That(recorder.Reconnects).IsEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_dashboard_start_disables_the_banner_command_until_it_finishes() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var release = new TaskCompletionSource<ProcessResult>();
            var recorder = new Recorder { Next = _ => release.Task };
            using var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Retrying));
            using var vm = Build(status, isLocalServer: true, recorder);
            var command = (ICommand)vm.StartCommand;

            await Assert.That(command.CanExecute(null)).IsTrue();
            var run = vm.StartAsync();
            await Assert.That(vm.IsStarting).IsTrue();
            await Assert.That(command.CanExecute(null)).IsFalse();
            await Assert.That(await vm.StartAsync()).IsFalse();

            release.SetResult(Started);
            await Assert.That(await run).IsTrue();
            await Assert.That(vm.IsStarting).IsFalse();
            await Assert.That(command.CanExecute(null)).IsTrue();
            await Assert.That(recorder.Starts).IsEqualTo(1);
        });
    }

    /// A throw out of the reconnect must not escape the command, where nothing observes it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_reconnect_after_a_start_becomes_a_message() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Retrying));
            using var vm = new LocalServerRecoveryViewModel(
                status, isLocalServer: true, _ => Task.FromResult(Started),
                () => throw new ObjectDisposedException("lane"), CancellationToken.None);

            await Assert.That(await vm.StartAsync()).IsFalse();
            await Assert.That(vm.Message).Contains("reconnecting failed");
        });
    }

    static LocalServerRecoveryViewModel WithCondition(
            BehaviorSubject<ServerLaneStatus> status, Func<LocalServerCondition> read, Recorder recorder, bool canStart = true) =>
        WithReading(status, () => new(read()), recorder, canStart);

    static LocalServerRecoveryViewModel WithReading(
            BehaviorSubject<ServerLaneStatus> status, Func<LocalServerReading> read, Recorder recorder, bool canStart = true) =>
        new(status, isLocalServer: true, recorder.Start, recorder.Reconnect, CancellationToken.None,
            readCondition: _ => Task.FromResult(read()), canStart: canStart,
            readScheduler: System.Reactive.Concurrency.ImmediateScheduler.Instance);

    /// <summary>A port another program holds is no stopped server: the banner names the holder, offers no Start that
    /// could only fail, and Start invoked anyway runs nothing.</summary>
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_taken_port_names_its_holder_and_is_never_offered_start() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Connecting));
            var recorder = new Recorder();
            using var vm = WithReading(status,
                () => new(LocalServerCondition.PortTaken, 9473, new PortHolder(4242, "python3"), "a web server that calls itself SimpleHTTP/0.6"),
                recorder);

            status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));

            await Assert.That(vm.Visible).IsTrue();
            await Assert.That(vm.StartOffered).IsFalse();
            await Assert.That(vm.Headline).IsEqualTo("Another program holds the local server's port");
            await Assert.That(vm.Body!).Contains("Port 9473 is held by python3 (pid 4242)").And.Contains("kcap never stops a program it did not start");
            await Assert.That(vm.Body!).DoesNotContain("shows which process it is");
            await Assert.That(((ICommand)vm.StartCommand).CanExecute(null)).IsFalse();
            await Assert.That(await vm.StartAsync()).IsFalse();
            await Assert.That(recorder.Starts).IsEqualTo(0);
        });
    }

    /// <summary>When the OS cannot name the holder, its own answer names it, and the banner gives the command
    /// <c>kcap local status</c> gives to find the process.</summary>
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_taken_port_the_os_cannot_name_is_named_by_its_answer() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Connecting));
            using var vm = WithReading(status,
                () => new(LocalServerCondition.PortTaken, 9473, Answer: "a web server that calls itself nginx/1.25.3"), new Recorder());

            status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));

            await Assert.That(vm.Body!).Contains("Port 9473 is held by a web server that calls itself nginx/1.25.3")
                .And.Contains($"{PortHolder.LookupCommand(9473)} shows which process it is.");
        });
    }

    /// <summary>Freeing the port takes the conflict away on the next retry: the same banner offers Start again.</summary>
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_freed_port_offers_start_again_on_the_next_retry() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Connecting));
            var reading = new LocalServerReading(LocalServerCondition.PortTaken, 9473, new PortHolder(1, "nginx"));
            using var vm = WithReading(status, () => reading, new Recorder());

            status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));
            await Assert.That(vm.StartOffered).IsFalse();

            reading = new(LocalServerCondition.Installed, 9473);
            status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));

            await Assert.That(vm.StartOffered).IsTrue();
            await Assert.That(vm.Headline).IsEqualTo("The local server isn't running");
        });
    }

    /// <summary>A read that fails is evidence of nothing, so it offers nothing.</summary>
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_read_offers_no_start() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Connecting));
            using var vm = WithReading(status, () => throw new IOException("disk gone"), new Recorder());

            status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));

            await Assert.That(vm.Condition).IsEqualTo(LocalServerCondition.Unreadable);
            await Assert.That(vm.StartOffered).IsFalse();
        });
    }

    /// An uninstalled, purged or never-installed server still reads as down, but a start cannot bring it back:
    /// the banner says what the files say and offers nothing to click, and Start invoked anyway runs nothing.
    [Test]
    [Arguments(LocalServerCondition.Uninstalled, "The local server was uninstalled")]
    [Arguments(LocalServerCondition.Purged, "The local server was removed")]
    [Arguments(LocalServerCondition.NotInstalled, "The local server was removed")]
    [Arguments(LocalServerCondition.FilesMissing, "The local server needs attention")]
    [Arguments(LocalServerCondition.Interrupted, "The local server needs attention")]
    [Arguments(LocalServerCondition.Unreadable, "The local server needs attention")]
    [NotInParallel("AvaloniaSession")]
    public async Task A_gone_installation_is_explained_and_never_offered_start(LocalServerCondition condition, string headline) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Connecting));
            var recorder = new Recorder();
            using var vm = WithCondition(status, () => condition, recorder);

            status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));

            await Assert.That(vm.Visible).IsTrue();
            await Assert.That(vm.Condition).IsEqualTo(condition);
            await Assert.That(vm.StartOffered).IsFalse();
            await Assert.That(vm.Headline).IsEqualTo(headline);
            await Assert.That(vm.Body).IsEqualTo(condition.Explanation);
            await Assert.That(((ICommand)vm.StartCommand).CanExecute(null)).IsFalse();
            await Assert.That(await vm.StartAsync()).IsFalse();
            await Assert.That(recorder.Starts).IsEqualTo(0);
        });
    }

    /// Another kcap's restart passes through stopped: no Start then, and Start comes back once the files say it is
    /// installed and nothing holds it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_waits_out_another_commands_change_and_returns_when_it_settles() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Connecting));
            var condition = LocalServerCondition.Changing;
            using var vm = WithCondition(status, () => condition, new Recorder());

            status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));
            await Assert.That(vm.StartOffered).IsFalse();
            await Assert.That(vm.Headline).IsEqualTo("The local server is being changed");

            condition = LocalServerCondition.Installed;
            status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));

            await Assert.That(vm.StartOffered).IsTrue();
            await Assert.That(vm.Headline).IsEqualTo("The local server isn't running");
            await Assert.That(vm.Body).IsEqualTo("Start it to launch agents and open the dashboard.");
        });
    }

    /// Until the files have been read nothing is known, so nothing is offered.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_is_not_offered_before_the_installation_has_been_read() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Connected));
            using var vm = WithCondition(status, () => LocalServerCondition.Installed, new Recorder());

            await Assert.That(vm.Condition).IsNull();
            await Assert.That(vm.StartOffered).IsFalse();
            await Assert.That(await vm.StartAsync()).IsFalse();
        });
    }

    /// Where kcap local start does not run, an installed server is explained rather than offered a Start that fails.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_platform_without_kcap_local_start_offers_no_start() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Connecting));
            using var vm = WithCondition(status, () => LocalServerCondition.Installed, new Recorder(), canStart: false);

            status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));

            await Assert.That(vm.Visible).IsTrue();
            await Assert.That(vm.StartOffered).IsFalse();
            await Assert.That(vm.Body!).Contains("can't start it on this computer");
        });
    }

    /// A start that fails (an uninstall landed under it, say) reads the files again, so the banner stops offering it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_start_rereads_the_installation() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Connecting));
            var condition = LocalServerCondition.Installed;
            var recorder = new Recorder {
                Next = _ => {
                    condition = LocalServerCondition.Uninstalled;
                    return Task.FromResult(new ProcessResult(1, "", "files are missing\n", false));
                },
            };
            using var vm = WithCondition(status, () => condition, recorder);
            status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));

            await Assert.That(await vm.StartAsync()).IsFalse();

            await Assert.That(vm.Condition).IsEqualTo(LocalServerCondition.Uninstalled);
            await Assert.That(vm.StartOffered).IsFalse();
        });
    }

    [Test]
    public async Task Sign_in_is_never_requested_for_a_local_server() {
        var root = new LocalRoot(Tmp.CreateDir("local"));
        LocalStateFiles.PublishRegistry(root, InstallRegistry.Empty.WithActive(
            new("0123456789abcdef0123456789abcdef", 5108, "http://localhost:5108", "1.0.0")));
        Action openSignIn = () => { };

        object? local = App.SignInRequestFor(App.IsLocalServer("http://127.0.0.1:5108", root), openSignIn);
        object? cloud = App.SignInRequestFor(App.IsLocalServer("https://acme.kcap.ai", root), openSignIn);

        await Assert.That(local).IsNull();
        await Assert.That(cloud).IsSameReferenceAs(openSignIn);
    }
}
