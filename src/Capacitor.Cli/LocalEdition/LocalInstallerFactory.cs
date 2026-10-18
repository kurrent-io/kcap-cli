using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

sealed class LocalInstallerFactory(
    LocalRoot root, ConfigRoot config, UserHome home, TimeProvider time, IHttpClientFactory http, LocalBundleClient bundles)
    : ILocalInstallerFactory {
    public LocalInstaller Create() {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            throw new LocalInstallFailure(LocalInstallFailureCode.UnsupportedPlatform, "The local edition runs on macOS for now.");

        var service = LocalServerServices.ForCurrentOs(home, time);
        var local   = http.CreateClient(CapacitorClients.LocalInstallation);

        return new(root, config, home, service, bundles, local, LocalBundleManifestKey.Load(), LocalPlatform.Current(), time,
            installer => [
                new LocalSetupRecovery(service, home, local, time, installer.Progress, installer.RolledForward),
                new LocalPurgeRecovery(service, home, installer.Progress),
            ]);
    }
}
