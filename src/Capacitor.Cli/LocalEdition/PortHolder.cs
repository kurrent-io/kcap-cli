using System.Globalization;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.LocalEdition;

/// <summary>The process listening on a loopback TCP port.</summary>
sealed record PortHolder(int Pid, string Command) {
    /// <summary>Asks <c>lsof</c>; null when nothing listens or the answer cannot be had.</summary>
    public static PortHolder? Find(int port) {
        try {
            var (exit, stdout, _) = ServiceProcess.Run("lsof", "-nP", $"-iTCP:{port.ToString(CultureInfo.InvariantCulture)}", "-sTCP:LISTEN", "-Fpc");
            return exit == 0 ? Parse(stdout) : null;
        } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) {
            return null;
        }
    }

    /// <summary>The first process in <c>lsof -F pc</c> output: a <c>p&lt;pid&gt;</c> line, then <c>c&lt;command&gt;</c>.</summary>
    public static PortHolder? Parse(string output) {
        int? pid = null;

        foreach (var raw in output.Split('\n')) {
            var line = raw.TrimEnd('\r');
            if (line.Length < 2) continue;

            switch (line[0]) {
                case 'p' when pid is null && int.TryParse(line.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var p):
                    pid = p;
                    break;
                case 'c' when pid is { } owner:
                    return new(owner, line[1..]);
            }
        }

        return pid is { } only ? new(only, "an unnamed process") : null;
    }
}
