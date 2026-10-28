using System.Diagnostics;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>
/// Whether Muse's sandbox can actually confine a reviewer on this host. Muse's own
/// <c>sandboxAvailability</c> reports <c>available</c> in a container where bubblewrap cannot create
/// user namespaces, so on Linux this runs <c>bwrap --unshare-all --ro-bind / / true</c> once and caches the
/// answer. The root bind is required: without it bwrap starts on an empty root where <c>true</c> does not
/// resolve, and the probe fails on every host. macOS uses Seatbelt, which needs no probe.
/// </summary>
/// <param name="probe">Replaces the real check; production passes null.</param>
internal sealed class MuseSandboxProbe(Func<bool>? probe = null) {
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    internal static readonly IReadOnlyList<string> BubblewrapArguments = ["--unshare-all", "--ro-bind", "/", "/", "true"];

    readonly Lazy<bool> _available = new(() => FailClosed(probe ?? RunDefault), LazyThreadSafetyMode.ExecutionAndPublication);

    public bool Available => _available.Value;

    /// <summary>A throwing probe would be cached by the <see cref="Lazy{T}"/> and rethrown from every
    /// capability read, so any exception is an unavailable sandbox.</summary>
    static bool FailClosed(Func<bool> check) {
        try {
            return check();
        } catch (Exception) {
            return false;
        }
    }

    static bool RunDefault() => !OperatingSystem.IsLinux() || RunBubblewrap();

    static bool RunBubblewrap() {
        try {
            using var process = Process.Start(new ProcessStartInfo("bwrap", BubblewrapArguments) {
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            });

            if (process is null) return false;

            process.StandardInput.Close();

            if (process.WaitForExit(ProbeTimeout)) return process.ExitCode == 0;

            ProcessTree.Kill(process);
            return false;
        } catch (Exception) {
            return false;
        }
    }
}
