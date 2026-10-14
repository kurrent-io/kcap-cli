using System.Diagnostics;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>
/// Whether Muse's sandbox can actually confine a reviewer on this host. Muse's own
/// <c>sandboxAvailability</c> reports <c>available</c> in a container where bubblewrap cannot create
/// user namespaces, so on Linux this runs <c>bwrap --unshare-all true</c> once and caches the answer.
/// macOS uses Seatbelt, which needs no probe.
/// </summary>
/// <param name="probe">Replaces the real check; production passes null.</param>
internal sealed class MuseSandboxProbe(Func<bool>? probe = null) {
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    readonly Lazy<bool> _available = new(probe ?? RunDefault, LazyThreadSafetyMode.ExecutionAndPublication);

    public bool Available => _available.Value;

    static bool RunDefault() => !OperatingSystem.IsLinux() || RunBubblewrap();

    static bool RunBubblewrap() {
        try {
            using var process = Process.Start(new ProcessStartInfo("bwrap", ["--unshare-all", "true"]) {
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
        } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) {
            return false;
        }
    }
}
