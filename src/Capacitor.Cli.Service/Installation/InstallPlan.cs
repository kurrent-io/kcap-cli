namespace Capacitor.Cli.Service.Installation;

/// <summary>
/// What <c>kcap-service install</c> does, from what is already on the machine. Replacing the binary
/// stops the service first and only the service: the daemons it supervises keep running and the new
/// binary adopts them. "Current" compares the bytes, so a downgrade replaces the binary just as an
/// upgrade does
/// </summary>
public static class InstallPlan {
    public static IReadOnlyList<InstallStep> For(bool serviceExists, bool binaryCurrent, bool running) {
        if (!serviceExists)
            return [InstallStep.PrepareDirectory, InstallStep.CopyBinary, InstallStep.CreateService, InstallStep.StartService];
        if (!binaryCurrent)
            return [InstallStep.StopService, InstallStep.PrepareDirectory, InstallStep.CopyBinary, InstallStep.ConfigureService, InstallStep.StartService];
        return running ? [] : [InstallStep.ConfigureService, InstallStep.StartService];
    }
}
