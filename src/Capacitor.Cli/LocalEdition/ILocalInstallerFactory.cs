namespace Capacitor.Cli.LocalEdition;

interface ILocalInstallerFactory {
    /// <summary>An installer for one run, whose install and recoveries report to <paramref name="progress"/>.</summary>
    LocalInstaller Create(IProgress<LocalInstallEvent> progress);
}
