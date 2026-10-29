using System.Diagnostics;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>Spawns the detached remainder import the way <c>DaemonCommands.StartDetached</c> spawns
/// the daemon: streams redirected and closed by the parent, handles not inherited, a short readiness
/// wait deciding between running and dead on arrival.</summary>
sealed class BackgroundImportSpawner(ConfigRoot config, IProcessStarter starter) : IBackgroundImportSpawner {
    static readonly TimeSpan Readiness = TimeSpan.FromMilliseconds(1500);

    public BackgroundImportLaunch Spawn(BackgroundImportRequest request) {
        var logPath = config.Path($"import-{request.RunId}.log");
        try {
            using (OwnerOnlyFile.CreateNew(logPath)) { }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return new BackgroundImportLaunch(BackgroundImportStatus.Failed, logPath, null, $"could not create {logPath}: {ex.Message}");
        }

        var psi = BuildStartInfo(config, request, logPath);

        Process? process;
        try {
            ProcessHelpers.PreventInheritedHandles();
            process = starter.Start(psi);
        } catch (Exception ex) {
            return new BackgroundImportLaunch(BackgroundImportStatus.Failed, logPath, null, ex.Message);
        }
        if (process is null) return new BackgroundImportLaunch(BackgroundImportStatus.Failed, logPath, null, "the process did not start");

        process.StandardInput.Close();
        process.StandardOutput.Close();
        process.StandardError.Close();

        if (!process.WaitForExit((int)Readiness.TotalMilliseconds))
            return new BackgroundImportLaunch(BackgroundImportStatus.Running, logPath, null, null);

        return process.ExitCode == 0
            ? new BackgroundImportLaunch(BackgroundImportStatus.ExitedZero, logPath, 0, null)
            : new BackgroundImportLaunch(BackgroundImportStatus.Failed, logPath, process.ExitCode, $"exit {process.ExitCode}");
    }

    /// <summary>A plan request runs <c>import --yes</c> under <see cref="ImportPlan.EnvVar"/>; any other
    /// imports everything.</summary>
    internal static ProcessStartInfo BuildStartInfo(ConfigRoot config, BackgroundImportRequest request, string logPath) {
        var psi = new ProcessStartInfo {
            FileName               = Environment.ProcessPath ?? "kcap",
            WorkingDirectory       = request.WorkingDirectory,
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        string[] argv = request.PlanPath is null ? ["import", "--all", "--yes", "--skip-title"] : ["import", "--yes"];
        foreach (var arg in argv) psi.ArgumentList.Add(arg);

        // A URL override outranks the profile pin and resolves to no profile, so it must not travel:
        // the child's capture lists and visibility come from the profile setup just saved.
        psi.Environment[ConfigRoot.ConfigDirEnvVar]        = config.Directory;
        psi.Environment[ProfileOverrides.ProfileVar]       = request.ProfileName;
        psi.Environment.Remove(ProfileOverrides.UrlVar);
        psi.Environment[DetachedImportLog.EnvVar]           = logPath;
        psi.Environment[DetachedImportLog.VisibilityEnvVar] = request.DefaultVisibility;

        if (request.PlanPath is { } plan) psi.Environment[ImportPlan.EnvVar] = plan;
        else psi.Environment.Remove(ImportPlan.EnvVar);

        return psi;
    }
}
