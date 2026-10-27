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
}
