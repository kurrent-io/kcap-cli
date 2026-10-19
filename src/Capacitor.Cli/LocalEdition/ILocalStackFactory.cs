namespace Capacitor.Cli.LocalEdition;

interface ILocalStackFactory {
    /// <summary>Throws <see cref="LocalInstallFailure"/> on a platform the local edition does not run on.</summary>
    LocalStack Create();

    /// <summary>Stages bundles into the root <see cref="Create"/> uses.</summary>
    LocalBundleStager Stager();
}
