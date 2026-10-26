using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.Services.Update;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class AboutSettingsViewModelTests {
    /// The report a bug carries names every installed piece, the daemon without build metadata.
    [Test]
    public Task Versions_and_the_report_follow_the_daemon_and_the_command_line() => AvaloniaSession.RunOnUiAsync(async () => {
        var service = new FakeDaemonClientService();
        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(version: "1.4.0+abc123"));
        using var vm = new AboutSettingsViewModel(service, _ => Task.FromResult<string?>("1.5.0+def"));
        await vm.LoadAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await Assert.That(vm.DaemonVersion).IsEqualTo("1.4.0");
        await Assert.That(vm.CliVersion).IsEqualTo("1.5.0");
        await Assert.That(vm.VersionReport).Contains("Daemon: 1.4.0");
        await Assert.That(vm.VersionReport).Contains("Command line: 1.5.0");
        await Assert.That(vm.VersionReport).Contains($"Desktop app: {AppVersion.Display}");
    });

    [Test]
    public Task A_missing_command_line_reads_as_not_found() => AvaloniaSession.RunOnUiAsync(async () => {
        using var vm = new AboutSettingsViewModel(new FakeDaemonClientService(), _ => Task.FromResult<string?>(null));
        await vm.LoadAsync();
        await Assert.That(vm.CliVersion).IsEqualTo("not found");
        await Assert.That(vm.VersionReport).Contains("Daemon: not connected");
    });

    [Test]
    public Task The_update_action_follows_the_update_feed() => AvaloniaSession.RunOnUiAsync(async () => {
        var feed = new BehaviorSubject<UpdateMenuItem>(new UpdateMenuItem(false, ""));
        var runs = 0;
        using var vm = new AboutSettingsViewModel(new FakeDaemonClientService(), _ => Task.FromResult<string?>(null),
            updateMenu: feed, updateAction: () => { runs++; return Task.CompletedTask; });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await Assert.That(vm.UpdateVisible).IsFalse();
        feed.OnNext(new UpdateMenuItem(true, "Restart to update to 1.6.0"));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await Assert.That(vm.UpdateVisible).IsTrue();
        await vm.UpdateCommand.Execute().ToTask();
        await Assert.That(runs).IsEqualTo(1);
    });
}
