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

    /// A throw out of the reconnect must not escape the command, where nothing observes it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_reconnect_after_a_start_becomes_a_message() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Retrying));
            using var vm = new LocalServerRecoveryViewModel(
                status, isLocalServer: true, _ => Task.FromResult(Started),
                () => throw new ObjectDisposedException("lane"), CancellationToken.None);

            await vm.StartCommand.Execute().ToTask();

            await Assert.That(vm.Message).Contains("reconnecting failed");
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
