using System.Runtime.Versioning;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap setup --local --windows-service</c>, ahead of the install: installs <c>kcap-service</c> behind the
/// administrator prompt when it is missing or cannot run the local server, then moves a server a Scheduled Task runs
/// to it. A declined prompt or a build without the service is not a failure: the install carries on under the task.
/// </summary>
/// <param name="servesLocalServer">The service is registered and advertises every server verb.</param>
/// <param name="carried">This kcap ships <c>kcap-service.exe</c>, or the machine already has it.</param>
/// <param name="elevate"><c>kcap-service.exe install</c> behind the prompt; its exit code.</param>
/// <param name="migrate">Moves a task's server to the service; what to tell the user, if anything.</param>
sealed class LocalWindowsServiceStep(Func<bool> servesLocalServer, Func<bool> carried, Func<int> elevate, Func<Task<string?>> migrate) {
    /// <summary>Win32 ERROR_CANCELLED, which a declined UAC prompt reports.</summary>
    const int DeclinedExit = 1223;

    /// <summary>A service started a moment ago may not have opened its pipe yet.</summary>
    static readonly TimeSpan ServiceWait = TimeSpan.FromSeconds(10);

    [SupportedOSPlatform("windows")]
    public static LocalWindowsServiceStep ForCurrentProcess(LocalRoot root, UserHome home, TimeProvider time) {
        var pipe = new WindowsServicePipeClient(time);

        return new(
            () => WindowsServiceBinaries.Registered() && ServicePipeVerbs.ServesLocalServer(pipe, ServiceWait),
            () => WindowsServiceBinaries.Bundled() is not null || File.Exists(WindowsServiceBinaries.Installed),
            () => WindowsServiceElevation.Run("install"),
            () => LocalServerMigration.ToServiceAsync(root, home, time));
    }

    public async Task RunAsync(IProgress<LocalInstallEvent> progress, Action<string> say) {
        if (!servesLocalServer()) {
            if (!carried()) {
                progress.Report(new LocalInstallEvent.WindowsServiceUnavailable("This kcap install does not carry kcap-service.exe; the desktop app installs the Windows service."));
                return;
            }

            switch (elevate()) {
                case 0:
                    break;
                case DeclinedExit:
                    progress.Report(new LocalInstallEvent.WindowsServiceDeclined());
                    return;
                case var exit:
                    progress.Report(new LocalInstallEvent.WindowsServiceUnavailable($"kcap-service install failed (exit {exit})."));
                    return;
            }

            if (!servesLocalServer()) {
                progress.Report(new LocalInstallEvent.WindowsServiceUnavailable("kcap-service is installed but does not answer as one that runs the local server."));
                return;
            }
        }

        if (await migrate() is { } moved) say(moved);
    }
}
