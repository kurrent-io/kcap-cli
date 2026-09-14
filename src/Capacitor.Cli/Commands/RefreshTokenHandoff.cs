using System.Diagnostics;
using Capacitor.Cli.Core;

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

    /// <summary>Best effort and silent: a failure to spawn leaves things exactly as they were.</summary>
    public static void Spawn(ConfigRoot config) {
        try {
            ProcessHelpers.PreventInheritedHandles();
            WatcherManager.StartProcess(BuildStartInfo(config))?.Dispose();
        } catch {
            // The abandoned refresh may still complete on its own; nothing here can improve on that.
        }
    }

    internal static ProcessStartInfo BuildStartInfo(ConfigRoot config) {
        var psi = new ProcessStartInfo(Environment.ProcessPath ?? "kcap") {
            RedirectStandardInput  = false,
            RedirectStandardOutput = false,
            RedirectStandardError  = false,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        psi.ArgumentList.Add(Command);
        psi.ArgumentList.Add(DetachedFlag);

        // The child must read the token the hook read, whatever else it resolves for itself.
        psi.Environment[ConfigRoot.ConfigDirEnvVar] = config.Directory;

        return psi;
    }

    /// <summary>The detached process's own setup: nothing on the console, and out of the terminal's
    /// session so a closing window cannot SIGHUP it mid-replay.</summary>
    public static void EnterDetached() {
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        ProcessHelpers.DetachFromControllingTerminal();
    }
}
