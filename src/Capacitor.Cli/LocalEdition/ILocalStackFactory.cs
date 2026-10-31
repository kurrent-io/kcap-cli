using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

interface ILocalStackFactory {
    /// <summary>The root <see cref="Create"/> uses, known even where it throws: a server another build
    /// installed there still has data to keep.</summary>
    LocalRoot Root { get; }

    /// <summary>Throws <see cref="LocalInstallFailure"/> on a platform the local edition does not run on.</summary>
    LocalStack Create();

    /// <summary>Stages bundles into the root <see cref="Create"/> uses.</summary>
    LocalBundleStager Stager();
}
