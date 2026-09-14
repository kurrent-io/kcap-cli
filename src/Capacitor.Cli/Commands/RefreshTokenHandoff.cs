using System.Diagnostics;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Hands a token refresh to a detached <c>kcap refresh-token</c> process when a hook abandons its own
/// client creation. A hook lives only as long as its host allows, far less than a WorkOS replay
/// budget, so a rotation it started but cannot finish would lose the rotated pair on exit; the
/// detached process takes the same lock, re-reads the same token, and either finds it fresh or
/// replays it inside WorkOS's window.
/// </summary>
internal static class RefreshTokenHandoff {
    public const string Command      = "refresh-token";
    public const string DetachedFlag = "--detached";

    public static bool IsDetached(string command, string[] args) => command == Command && args.Contains(DetachedFlag);

    /// <summary>Best effort and silent: a failure to spawn leaves things exactly as they were.</summary>
    public static void Spawn(ConfigRoot config, string profile, IProcessStarter starter) {
        try {
            ProcessHelpers.PreventInheritedHandles();

            using var process = starter.Start(BuildStartInfo(config, profile));

            // The child must not hold the host's hook pipes, or a host waiting for EOF waits on the
            // child too — the very lifetime this hand-off exists to escape.
            process?.StandardInput.Close();
            process?.StandardOutput.Close();
            process?.StandardError.Close();
        } catch {
            // The abandoned refresh may still complete on its own; nothing here can improve on that.
        }
    }

    internal static ProcessStartInfo BuildStartInfo(ConfigRoot config, string profile) {
        var psi = new ProcessStartInfo(Environment.ProcessPath ?? "kcap") {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        psi.ArgumentList.Add(Command);
        psi.ArgumentList.Add(DetachedFlag);

        // The child must refresh the very token the hook read: same root, same profile. A URL override
        // outranks the profile pin and resolves to no profile at all, so it does not travel.
        psi.Environment[ConfigRoot.ConfigDirEnvVar]  = config.Directory;
        psi.Environment[ProfileOverrides.ProfileVar] = profile;
        psi.Environment.Remove(ProfileOverrides.UrlVar);

        return psi;
    }

    /// <summary>The detached process's own setup, run before any startup work that could block:
    /// nothing on the console, and out of the terminal's session so a closing window cannot SIGHUP it
    /// mid-replay.</summary>
    public static void EnterDetached() {
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        ProcessHelpers.DetachFromControllingTerminal();
    }
}
