using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

sealed class LocalStackFactory(LocalRoot root, UserHome home, TimeProvider time, IHttpClientFactory http, IOutgoingRecorders recorders, ConfigRoot config,
    LocalBundleClient bundles)
    : ILocalStackFactory {
    public LocalRoot Root => root;

    public LocalStack Create() {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
            throw new LocalInstallFailure(LocalInstallFailureCode.UnsupportedPlatform, "The local edition runs on Apple silicon Macs and x64 Windows for now.");

        return new(root, home, LocalServerServices.ForCurrentOs(home, time), http.CreateClient(CapacitorClients.LocalInstallation), time, recorders, config);
    }

    public LocalBundleStager Stager() => new(root, bundles, LocalBundleManifestKey.Load(), LocalPlatform.Current());
}
