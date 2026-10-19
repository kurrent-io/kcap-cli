using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

sealed class LocalInstallerFactory(
    LocalRoot root, ConfigRoot config, UserHome home, TimeProvider time, IHttpClientFactory http, LocalBundleClient bundles,
    IOutgoingRecorders recorders)
    : ILocalInstallerFactory {
    public LocalInstaller Create() {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            throw new LocalInstallFailure(LocalInstallFailureCode.UnsupportedPlatform, "The local edition runs on macOS for now.");

        var service = LocalServerServices.ForCurrentOs(home, time);
        var local   = http.CreateClient(CapacitorClients.LocalInstallation);

        var stack   = new LocalStack(root, home, service, local, time, recorders, config);

        return new(root, config, home, service, bundles, local, LocalBundleManifestKey.Load(), LocalPlatform.Current(), time,
            installer => [
                new LocalSetupRecovery(service, home, local, time, installer.Progress, installer.RolledForward),
                // Setup may be writing JSON events to stdout, which stopping a daemon would print into.
                new LocalPurgeRecovery(service, home, recorders, config, mayStop: false, force: false, installer.Progress),
                new LocalUpgradeRecovery(stack, installer.Progress),
            ]) {
            Ollama = new OllamaClient(http.CreateClient(CapacitorClients.Ollama), OllamaClient.DefaultUrl, time),
        };
    }
}
