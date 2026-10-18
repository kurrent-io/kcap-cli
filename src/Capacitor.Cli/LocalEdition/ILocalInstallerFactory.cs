namespace Capacitor.Cli.LocalEdition;

interface ILocalInstallerFactory {
    /// <summary>An installer whose recoveries report to <paramref name="progress"/>, as its install does.</summary>
    LocalInstaller Create(IProgress<LocalInstallEvent> progress);
}
