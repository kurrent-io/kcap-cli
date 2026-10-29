using System.Globalization;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.LocalEdition;

/// <summary>A process listening where the local server must: on loopback or on every address.</summary>
sealed record PortHolder(int Pid, string Command) {
    /// <summary>Asks <c>lsof</c>; null when nothing listens there or the answer cannot be had.</summary>
    public static PortHolder? Find(int port) {
        try {
            var (exit, stdout, _) = ServiceProcess.Run("lsof", "-nP", $"-iTCP:{port.ToString(CultureInfo.InvariantCulture)}", "-sTCP:LISTEN", "-Fpcn");
            return exit == 0 ? Parse(stdout) : null;
        } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) {
            return null;
        }
    }

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
}
