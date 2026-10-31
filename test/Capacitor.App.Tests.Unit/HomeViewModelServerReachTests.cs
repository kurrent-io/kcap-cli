using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// A server that gives no answer is told apart from a rejected credential: the launcher names the outage and
/// offers Retry, never Sign in, however long it lasts; a refused credential still asks for a sign-in.
[NotInParallel("AvaloniaSession")]
public class HomeViewModelServerReachTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string AppServer = "http://localhost:9999";

    sealed class NoLaunch : ILaunchClient {
        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) =>
            Task.FromResult(new LaunchOutcome(false, null, "unused"));
    }

    HomeViewModel Make(FakeDaemonClientService daemon, FakeServerLane lane, TimeProvider time) =>
        new(daemon, new AppStateStore(Tmp.PathTo("app-state.json")), new NoLaunch(), () => Task.FromResult<string[]>([]), time,
            laneStatus: lane.Status, appServerUrl: AppServer);

    static void Attach(FakeDaemonClientService daemon, string connection) {
        daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: connection, serverUrl: AppServer));
        daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
    }

    [Test]
    public async Task An_unanswered_server_is_named_with_a_retry_and_no_sign_in_even_past_the_catch_up_bound() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            var time = new FakeTimeProvider();
            using var vm = Make(daemon, lane, time);
            var retries = 0;
            vm.AttachServerRetry(() => { retries++; return Task.CompletedTask; });

            Attach(daemon, "reconnecting");
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying, "Connection refused", Failure: ConnectionFailure.Refused));

            var notice = HomeViewModel.ServerUnreachableNotice(ConnectionFailure.Refused);
            await Assert.That(vm.ConnectionNotice).IsEqualTo(notice);
            await Assert.That(notice).Contains("connection refused").And.DoesNotContain("Sign in");
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.ServerRetryVisible).IsTrue();
            await Assert.That(vm.BannerActionsVisible).IsTrue();
            await Assert.That(vm.BannerBusy).IsFalse();

            time.Advance(HomeViewModel.CatchUpLimit);
            Attach(daemon, "disconnected");

            await Assert.That(vm.ConnectionNotice).IsEqualTo(notice);
            await Assert.That(vm.SignInVisible).IsFalse();

            await vm.RetryServerCommand.Execute();
            await Assert.That(retries).IsEqualTo(1);

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            Attach(daemon, "connected");

            await Assert.That(vm.ConnectionNotice).IsNull();
            await Assert.That(vm.ServerRetryVisible).IsFalse();
        });
    }

    /// A lane that keeps retrying on something it cannot classify says so after the bound, still without Sign in.
    [Test]
    public async Task A_lane_retrying_past_the_bound_says_it_cannot_connect_instead_of_asking_for_a_sign_in() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            var time = new FakeTimeProvider();
            using var vm = Make(daemon, lane, time);
            vm.AttachServerRetry(() => Task.CompletedTask);

            Attach(daemon, "reconnecting");
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);

            time.Advance(HomeViewModel.CatchUpLimit);

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ServerRetryingNotice);
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.ServerRetryVisible).IsTrue();
        });
    }

    /// A rejected credential parks the lane signed out, and that alone still asks for a sign-in.
    [Test]
    public async Task A_rejected_credential_still_asks_for_a_sign_in() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = Make(daemon, lane, TimeProvider.System);
            vm.AttachServerRetry(() => Task.CompletedTask);

            Attach(daemon, "disconnected");
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.SignedOut));

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.SignInExpiredNotice);
            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.ServerRetryVisible).IsFalse();
        });
    }

    /// This machine's local server speaks through its own banner, so the outage line stays out of the launcher's.
    [Test]
    public async Task A_managed_local_server_leaves_the_outage_to_its_own_banner() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = Make(daemon, lane, TimeProvider.System);
            using var local = new LocalServerRecoveryViewModel(
                lane.Status, isLocalServer: true, _ => Task.FromResult(new ProcessResult(0, "", "", false)),
                () => Task.CompletedTask, CancellationToken.None);
            vm.AttachLocalServerRecovery(local);
            vm.AttachServerRetry(() => Task.CompletedTask);

            Attach(daemon, "reconnecting");
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying, "Connection refused", Failure: ConnectionFailure.Refused));

            await Assert.That(local.Visible).IsTrue();
            await Assert.That(vm.ConnectionNotice).IsNull();
            await Assert.That(vm.ServerRetryVisible).IsFalse();
        });
    }

    [Test]
    [Arguments(ServerLaneState.Retrying, false)]
    [Arguments(ServerLaneState.Connected, true)]
    [Arguments(ServerLaneState.Dormant, true)]
    public async Task Sign_in_follows_a_lost_session_only_while_the_lane_is_not_retrying(ServerLaneState lane, bool offered) {
        await Assert.That(HomeViewModel.SignInOffered(localServer: false, awaiting: false, expired: false, timedOut: true, foreign: false,
            LaunchAvailability.ServerDisconnected, lane)).IsEqualTo(offered);
        await Assert.That(HomeViewModel.SignInOffered(localServer: false, awaiting: false, expired: true, timedOut: false, foreign: false,
            LaunchAvailability.ServerDisconnected, ServerLaneState.Retrying)).IsTrue();
    }
}
