namespace Capacitor.Cli.LocalEdition;

interface ILocalInstallerFactory {
    /// <summary>An installer for one run. Its recoveries report to the progress its install is given.</summary>
    LocalInstaller Create();
}
