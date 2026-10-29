using System.Diagnostics;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Pty.Windows;

namespace Capacitor.Cli.Daemon.Services;

/// What a configured command starts on Windows when the daemon spawns it without a shell.
/// CreateProcess appends only `.exe`, so a bare name never reaches the npm `.cmd` shim that
/// availability found, and the vendor is offered and then fails to launch. An npm shim is started
/// as its own target, so cmd.exe never parses the vendor's arguments.
public static class WindowsLaunchCommand {
    public static ProcessStartInfo Apply(ProcessStartInfo psi, BinaryProbe binaries) {
        if (!OperatingSystem.IsWindows()) return psi;

        var (program, script) = Resolve(psi.FileName, binaries.Resolve, NpmCmdShim.TryRead);
        psi.FileName = program;
        if (script is not null) {
            if (string.IsNullOrEmpty(psi.Arguments)) psi.ArgumentList.Insert(0, script);
            else psi.Arguments = $"\"{script}\" {psi.Arguments}";
        }
        return psi;
    }

    /// The command unchanged when nothing resolves, so CreateProcess still reports it as not found.
    internal static (string Program, string? Script) Resolve(
            string command, Func<string, string?> resolve, Func<string, NpmCmdShim.Target?> readShim) {
        if (resolve(command) is not { } path) return (command, null);
        if (!path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || readShim(path) is not { } shim) return (path, null);
        return (shim.Program == "node" ? resolve("node") ?? "node" : shim.Program, shim.Script);
    }
}
