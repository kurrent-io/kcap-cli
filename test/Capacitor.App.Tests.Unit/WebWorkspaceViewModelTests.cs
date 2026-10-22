using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class WebWorkspaceViewModelTests {
    [Test]
    [Arguments("http://localhost:5050")]
    [Arguments("https://team.example/workspace")]
    public Task Opens_the_configured_workspace(string url) => AvaloniaSession.WithImmediateRxScheduler(async () => {
        var opener = new RecordingOpener();
        using var vm = new WebWorkspaceViewModel(url, opener, _ => { }, CancellationToken.None);
        await vm.OpenCommand.Execute().ToTask();
        await Assert.That(opener.Opened.Single()).IsEqualTo(url);
        await Assert.That(vm.Label).IsEqualTo("Open dashboard");
    });

    [Test]
    [Arguments(null)]
    [Arguments("file:///tmp/server")]
    [Arguments("not a URL")]
    public Task No_workspace_hides_and_disables_the_action(string? url) => AvaloniaSession.WithImmediateRxScheduler(async () => {
        using var vm = new WebWorkspaceViewModel(url, new RecordingOpener(), _ => { }, CancellationToken.None);
        await Assert.That(vm.IsVisible).IsFalse();
        await Assert.That(await vm.OpenCommand.CanExecute.FirstAsync()).IsFalse();
    });

    [Test]
    public Task Local_start_finishes_before_opening_and_shares_the_recovery_gate() => AvaloniaSession.WithImmediateRxScheduler(async () => {
        using var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Retrying));
        var started = new TaskCompletionSource<ProcessResult>();
        var starts = 0;
        var reconnected = false;
        using var recovery = new LocalServerRecoveryViewModel(status, true,
            _ => { starts++; return started.Task; }, () => { reconnected = true; return Task.CompletedTask; }, CancellationToken.None);
        var opener = new RecordingOpener();
        using var vm = new WebWorkspaceViewModel("http://localhost:5050", opener, _ => { }, CancellationToken.None, recovery);
        await Assert.That(vm.Label).IsEqualTo("Start server and open dashboard");
        var open = vm.OpenCommand.Execute().ToTask();
        await Assert.That(recovery.IsStarting).IsTrue();
        await Assert.That(vm.Label).IsEqualTo("Starting server…");
        await Assert.That(opener.Opened).IsEmpty();
        await recovery.StartCommand.Execute().ToTask();
        await Assert.That(starts).IsEqualTo(1);
        started.SetResult(new ProcessResult(0, "", "", false));
        await open;
        await Assert.That(reconnected).IsTrue();
        await Assert.That(opener.Opened.Single()).IsEqualTo("http://localhost:5050");
    });

    [Test]
    public Task Failed_local_start_does_not_open_a_broken_page() => AvaloniaSession.WithImmediateRxScheduler(async () => {
        using var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Retrying));
        using var recovery = new LocalServerRecoveryViewModel(status, true,
            _ => Task.FromResult(new ProcessResult(1, "", "start failed", false)),
            () => Task.CompletedTask, CancellationToken.None);
        var opener = new RecordingOpener();
        var notifications = new List<string>();
        using var vm = new WebWorkspaceViewModel("http://localhost:5050", opener, notifications.Add, CancellationToken.None, recovery);
        await vm.OpenCommand.Execute().ToTask();
        await Assert.That(opener.Opened).IsEmpty();
        await Assert.That(recovery.Message!).Contains("start failed");
        await Assert.That(notifications.Single()).Contains("start failed");
    });

    [Test]
    public Task Local_upgrade_disables_start_until_the_stack_is_available() => AvaloniaSession.WithImmediateRxScheduler(async () => {
        using var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Retrying));
        using var busy = new BehaviorSubject<bool>(true);
        using var recovery = new LocalServerRecoveryViewModel(status, true,
            _ => Task.FromResult(new ProcessResult(0, "", "", false)), () => Task.CompletedTask, CancellationToken.None, busy);
        using var vm = new WebWorkspaceViewModel("http://localhost:5050", new RecordingOpener(), _ => { }, CancellationToken.None, recovery);
        await Assert.That(await vm.OpenCommand.CanExecute.FirstAsync()).IsFalse();
        await Assert.That(vm.Label).IsEqualTo("Open dashboard");
        busy.OnNext(false);
        await Assert.That(await vm.OpenCommand.CanExecute.FirstAsync()).IsTrue();
        await Assert.That(vm.Label).IsEqualTo("Start server and open dashboard");
    });

    [Test]
    public Task Cloud_workspace_opens_with_its_server_lane_disconnected() => AvaloniaSession.WithImmediateRxScheduler(async () => {
        using var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Retrying));
        var starts = 0;
        using var recovery = new LocalServerRecoveryViewModel(status, false,
            _ => { starts++; return Task.FromResult(new ProcessResult(0, "", "", false)); },
            () => Task.CompletedTask, CancellationToken.None);
        var opener = new RecordingOpener();
        using var vm = new WebWorkspaceViewModel("https://team.example", opener, _ => { }, CancellationToken.None, recovery);
        await vm.OpenCommand.Execute().ToTask();
        await Assert.That(starts).IsEqualTo(0);
        await Assert.That(opener.Opened.Single()).IsEqualTo("https://team.example");
    });
    [Test]
    public Task Failed_reconnect_reports_the_partial_failure_without_opening_the_dashboard() => AvaloniaSession.WithImmediateRxScheduler(async () => {
        using var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Retrying));
        using var recovery = new LocalServerRecoveryViewModel(status, true,
            _ => Task.FromResult(new ProcessResult(0, "", "", false)),
            () => throw new IOException("reconnect failed"), CancellationToken.None);
        var opener = new RecordingOpener();
        var notifications = new List<string>();
        using var vm = new WebWorkspaceViewModel("http://localhost:5050", opener, notifications.Add, CancellationToken.None, recovery);
        await vm.OpenCommand.Execute().ToTask();
        await Assert.That(opener.Opened).IsEmpty();
        await Assert.That(recovery.Message!).Contains("reconnect failed");
        await Assert.That(notifications.Single()).Contains("The local server started, but reconnecting failed");
    });

}
