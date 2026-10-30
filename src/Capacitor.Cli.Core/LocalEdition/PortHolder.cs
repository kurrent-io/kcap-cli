using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>A process listening where the local server must: on loopback or on every address.</summary>
public sealed record PortHolder(int Pid, string Command) {
    /// <summary>What the local server's process is called, on every platform.</summary>
    public const string ServerProcessName = "kcap-server";

    /// <summary>Asks the OS's TCP table on Windows and <c>lsof</c> elsewhere; null when nothing listens there or the
    /// answer cannot be had. Without root, <c>lsof</c> does not see another user's process.</summary>
    public static PortHolder? Find(int port) {
        try {
            if (OperatingSystem.IsWindows())
                return WindowsTcpListeners.Owner(port) is { } pid ? new(pid, ProcessName(pid)) : null;

            var (exit, stdout) = Run("lsof", "-nP", $"-iTCP:{port.ToString(CultureInfo.InvariantCulture)}", "-sTCP:LISTEN", "-Fpcn");
            return exit == 0 ? Parse(stdout) : null;
        } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) {
            return null;
        }
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

    static (int ExitCode, string StdOut) Run(string file, params string[] args) {
        var psi = new ProcessStartInfo {
            FileName               = file,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {file}");
        var stdout = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout);
    }
}
