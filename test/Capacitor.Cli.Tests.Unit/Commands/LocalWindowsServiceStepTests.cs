using Capacitor.Cli.Commands;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>What <c>kcap setup --local --windows-service</c> does ahead of the install. Nothing here raises a real
/// administrator prompt.</summary>
public class LocalWindowsServiceStepTests {
    readonly RecordingProgress<LocalInstallEvent> _progress = new();
    readonly List<string> _said = [];
    int _elevations;
    int _migrations;

    LocalWindowsServiceStep Step(Func<bool> serves, bool carried = true, int exit = 0) =>
        new(serves, () => carried, () => {
            _elevations++;
            return exit;
        }, () => {
            _migrations++;
            return Task.FromResult<string?>("moved");
        });

    Task RunAsync(LocalWindowsServiceStep step) => step.RunAsync(_progress, _said.Add);

    [Test]
    public async Task A_service_that_already_runs_servers_raises_no_prompt() {
        await RunAsync(Step(() => true));

        await Assert.That(_elevations).IsEqualTo(0);
        await Assert.That(_migrations).IsEqualTo(1);
        await Assert.That(_progress.Reports).IsEmpty();
        await Assert.That(_said).IsEquivalentTo(["moved"]);
    }

    [Test]
    public async Task A_missing_service_is_installed_then_the_server_moves_to_it() {
        var installed = false;

        await RunAsync(new LocalWindowsServiceStep(() => installed, () => true, () => {
            _elevations++;
            installed = true;
            return 0;
        }, () => {
            _migrations++;
            return Task.FromResult<string?>(null);
        }));

        await Assert.That(_elevations).IsEqualTo(1);
        await Assert.That(_migrations).IsEqualTo(1);
        await Assert.That(_progress.Reports).IsEmpty();
    }

    /// <summary>The install goes on under the task, so a decline is an event, never a failure.</summary>
    [Test]
    public async Task A_declined_prompt_reports_the_decline_and_moves_nothing() {
        await RunAsync(Step(() => false, exit: 1223));

        await Assert.That(_progress.Reports.Single()).IsEqualTo(new LocalInstallEvent.WindowsServiceDeclined());
        await Assert.That(_migrations).IsEqualTo(0);
    }

    [Test]
    public async Task A_kcap_without_the_service_binary_reports_it_unavailable_without_a_prompt() {
        await RunAsync(Step(() => false, carried: false));

        await Assert.That(_elevations).IsEqualTo(0);
        await Assert.That(_progress.Reports.Single()).IsTypeOf<LocalInstallEvent.WindowsServiceUnavailable>();
        await Assert.That(_migrations).IsEqualTo(0);
    }

    [Test]
    public async Task A_failed_install_reports_its_exit_code() {
        await RunAsync(Step(() => false, exit: 5));

        await Assert.That(((LocalInstallEvent.WindowsServiceUnavailable)_progress.Reports.Single()).Message).Contains("exit 5");
        await Assert.That(_migrations).IsEqualTo(0);
    }

    [Test]
    public async Task A_service_that_still_cannot_run_servers_after_its_install_is_unavailable() {
        await RunAsync(Step(() => false));

        await Assert.That(_elevations).IsEqualTo(1);
        await Assert.That(_progress.Reports.Single()).IsTypeOf<LocalInstallEvent.WindowsServiceUnavailable>();
        await Assert.That(_migrations).IsEqualTo(0);
    }
}
