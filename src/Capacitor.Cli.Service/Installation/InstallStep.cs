namespace Capacitor.Cli.Service.Installation;

public enum InstallStep {
    StopService,
    PrepareDirectory,
    CopyBinary,
    CreateService,
    ConfigureService,
    StartService,
}
