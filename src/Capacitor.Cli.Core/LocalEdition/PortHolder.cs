using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>A process listening where the local server must: on loopback or on every address.</summary>
public sealed record PortHolder(int Pid, string Command) {
    /// <summary>What the local server's process is called, on every platform.</summary>
    public const string ServerProcessName = "kcap-server";

    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(3);

    /// <summary>A kill cannot take a process out of an uninterruptible wait, so the caller stops waiting this long
    /// after the timeout whether or not <c>lsof</c> has exited.</summary>
    public static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(1);

    const int OutputLimit = 64 * 1024;

    /// <summary><see cref="FindAsync(int, CancellationToken)"/> for a caller that cannot await.</summary>
    public static PortHolder? Find(int port) => FindAsync(port, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Asks the OS's TCP table on Windows and <c>lsof</c> elsewhere; null when nothing listens there or the
    /// answer cannot be had in time. Without root, <c>lsof</c> does not see another user's process.</summary>
    public static Task<PortHolder?> FindAsync(int port, CancellationToken ct) =>
        FindAsync(new ProcessRunner(TimeProvider.System), TimeProvider.System, port, ct);

    public static async Task<PortHolder?> FindAsync(IProcessRunner runner, TimeProvider time, int port, CancellationToken ct) {
        try {
            if (OperatingSystem.IsWindows())
                return WindowsTcpListeners.Owner(port) is { } pid ? new(pid, ProcessName(pid)) : null;

            var run = runner.RunBoundedAsync("lsof", ["-nP", $"-iTCP:{port.ToString(CultureInfo.InvariantCulture)}", "-sTCP:LISTEN", "-Fpcn"],
                new(Timeout: LookupTimeout, CancelMode: CancelMode.KillTree), OutputLimit, ct);
            _ = run.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

            var result = await run.WaitAsync(LookupTimeout + KillGrace, time, ct).ConfigureAwait(false);
            return result is { ExitCode: 0, TimedOut: false, Oversized: false } ? Parse(result.Stdout) : null;
        } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or TimeoutException) {
            return null;
        }
    }

    /// <summary>The command a user runs to name the process listening on <paramref name="port"/>.</summary>
    public static string LookupCommand(int port) =>
        LookupCommand(port, OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsLinux() ? OSPlatform.Linux : OSPlatform.OSX);

    public static string LookupCommand(int port, OSPlatform os) {
        var p = port.ToString(CultureInfo.InvariantCulture);

        return os == OSPlatform.Windows ? $"netstat -ano -p TCP | findstr :{p}"
             : os == OSPlatform.Linux   ? $"sudo ss -ltnp 'sport = :{p}'"
             : $"sudo lsof -nP -iTCP:{p} -sTCP:LISTEN";
    }

    /// <summary>Named as the server is named. A process can call itself anything, so this only keeps a name from
    /// being reported as a stranger; it never proves the process is this installation's.</summary>
    public bool IsServerProcess =>
        string.Equals(Path.GetFileNameWithoutExtension(Command), ServerProcessName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The first process in <c>lsof -F pcn</c> output with a listener that conflicts with loopback. A process
    /// bound only to another address shares the port with the server.</summary>
    public static PortHolder? Parse(string output) {
        int? pid = null;
        var command = "an unnamed process";

        foreach (var raw in output.Split('\n')) {
            var line = raw.TrimEnd('\r');
            if (line.Length < 2) continue;

            switch (line[0]) {
                case 'p':
                    pid     = int.TryParse(line.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : null;
                    command = "an unnamed process";
                    break;
                case 'c':
                    command = line[1..];
                    break;
                case 'n' when pid is { } owner && ConflictsWithLoopback(line[1..]):
                    return new(owner, command);
            }
        }

        return null;
    }

    static bool ConflictsWithLoopback(string address) =>
        address.StartsWith("127.", StringComparison.Ordinal)
     || address.StartsWith("*:", StringComparison.Ordinal)
     || address.StartsWith("[::1]:", StringComparison.Ordinal)
     || address.StartsWith("[::]:", StringComparison.Ordinal)
     || address.StartsWith("localhost:", StringComparison.Ordinal);

    static string ProcessName(int pid) {
        try {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) {
            return "an unnamed process";
        }
    }
}
