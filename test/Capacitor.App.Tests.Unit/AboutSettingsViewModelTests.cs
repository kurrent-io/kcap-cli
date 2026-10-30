using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class AboutSettingsViewModelTests {
    /// The report a bug carries names every installed piece, the daemon without build metadata and
    /// only while attached: the service keeps its last snapshot after a disconnect.
    [Test]
    public Task Versions_and_the_report_follow_the_daemon_and_the_command_line() => AvaloniaSession.RunOnUiAsync(async () => {
        var service = new FakeDaemonClientService();
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(version: "1.4.0+abc123"));
        using var vm = new AboutSettingsViewModel(service, _ => Task.FromResult<string?>("1.5.0+def"));
        await vm.LoadAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await Assert.That(vm.DaemonVersion).IsEqualTo("1.4.0");
        await Assert.That(vm.CliVersion).IsEqualTo("1.5.0");
        await Assert.That(vm.VersionReport).Contains("Daemon: 1.4.0");
        await Assert.That(vm.VersionReport).Contains("Command line: 1.5.0");
        await Assert.That(vm.VersionReport).Contains($"Desktop app: {AppVersion.Display}");

        service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await Assert.That(vm.VersionReport).Contains("Daemon: not connected");
    });

    /// The rail shows no healthy status, so About is where each component's state stays readable:
    /// the daemon and its name, its server connection, and its hosted agents against the limit.
    [Test]
    public Task Statuses_follow_the_attach_and_say_what_they_count() => AvaloniaSession.RunOnUiAsync(async () => {
        var service = new FakeDaemonClientService();
        using var vm = new AboutSettingsViewModel(service, _ => Task.FromResult<string?>(null));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await Assert.That(vm.DaemonStatus).IsEqualTo("connecting");
        await Assert.That(vm.ServerConnection).IsNull();
        await Assert.That(vm.RunningAgents).IsNull();

        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(daemon: "work-mac", serverUrl: "https://kcap.example",
            connection: "reconnecting", active: 0, max: 5));
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await Assert.That(vm.DaemonStatus).IsEqualTo("connected · work-mac");
        await Assert.That(vm.ServerConnection).IsEqualTo("reconnecting · https://kcap.example");
        await Assert.That(vm.RunningAgents).IsEqualTo("0 running · limit 5");

        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(active: 2, max: 0));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await Assert.That(vm.RunningAgents).IsEqualTo("2 running · no limit");

        service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await Assert.That(vm.DaemonStatus).IsEqualTo("not running");
        await Assert.That(vm.ServerConnection).IsNull().Because("a retained snapshot says nothing about a daemon we cannot reach");
        await Assert.That(vm.RunningAgents).IsNull();
    });

    [Test]
    public Task Beta_switch_starts_from_the_updater_and_saves_each_change() => AvaloniaSession.RunOnUiAsync(async () => {
        var saved = new List<bool>();
        using var vm = new AboutSettingsViewModel(new FakeDaemonClientService(), _ => Task.FromResult<string?>(null),
            updateMenu: System.Reactive.Linq.Observable.Return(new Capacitor.App.Services.Update.UpdateMenuItem(true, "Check for Updates…")),
            followsBetas: () => true, setBetaUpdates: v => { saved.Add(v); return Task.CompletedTask; });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await Assert.That(vm.BetaUpdates).IsTrue();
        await Assert.That(vm.BetaUpdatesVisible).IsTrue();

        vm.BetaUpdates = false;
        vm.BetaUpdates = false;

        await Assert.That(saved).IsEquivalentTo([false]);
    });
}
