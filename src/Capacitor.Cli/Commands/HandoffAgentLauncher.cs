using System.Diagnostics;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>Runs the chosen agent in the foreground with the handoff prompt, pinned to the profile
/// setup saved so every kcap MCP server it spawns talks to the server the import ran against.</summary>
sealed class HandoffAgentLauncher(ConfigRoot config, IProcessStarter starter, TimeProvider? time = null) : IHandoffAgentLauncher {
    static readonly TimeSpan FailureWindow = TimeSpan.FromMilliseconds(2000);

    readonly TimeProvider _time = time ?? TimeProvider.System;

    public HandoffLaunchResult Launch(HandoffLaunchRequest request) {
        if (request.Vendor.Executable is not { } exe)
            return new HandoffLaunchResult(HandoffLaunchStatus.LaunchFailed, null, $"{request.Vendor.Label} is not on the search path");

        var psi = new ProcessStartInfo { FileName = exe, WorkingDirectory = request.WorkingDirectory, UseShellExecute = false };
        foreach (var arg in HandoffLaunchRecipe.All[request.Vendor.Id].Argv(request.Prompt)) psi.ArgumentList.Add(arg);
        psi.Environment[ConfigRoot.ConfigDirEnvVar]  = config.Directory;
        psi.Environment[ProfileOverrides.ProfileVar] = request.ProfileName;
        psi.Environment.Remove(ProfileOverrides.UrlVar);

        Process? process;
        var started = _time.GetTimestamp();
        try {
            process = starter.Start(psi);
        } catch (Exception ex) {
            return new HandoffLaunchResult(HandoffLaunchStatus.LaunchFailed, null, ex.Message);
        }
        if (process is null) return new HandoffLaunchResult(HandoffLaunchStatus.LaunchFailed, null, "the process did not start");

        process.WaitForExit();
        var failedFast = process.ExitCode != 0 && _time.GetElapsedTime(started) < FailureWindow;

        return new HandoffLaunchResult(failedFast ? HandoffLaunchStatus.LaunchFailed : HandoffLaunchStatus.Ran, process.ExitCode,
            failedFast ? $"exit {process.ExitCode}" : null);
    }
}
