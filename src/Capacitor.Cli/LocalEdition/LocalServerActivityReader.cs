using System.Globalization;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.LocalEdition;

/// <summary>Parses what launchd's and systemd's output say about the server's process.</summary>
static class LocalServerActivityReader {
    /// <summary>The plist keeps a job alive only after a failure, so a clean exit is final.</summary>
    public static UnitActivity FromPrint(int exitCode, string stdout, string stderr) {
        switch (LaunchdUnit.ClassifyPrint(exitCode, stdout, stderr)) {
            case LabelProbe.Absent:  return UnitActivity.Stopped.Instance;
            case LabelProbe.Unknown: return new UnitActivity.Unknown($"launchctl print failed (exit {exitCode}): {LaunchdUnit.ToolError(stderr)}");
        }

        var runs = Field(stdout, "runs = ");

        if (LaunchdUnit.PidFromPrint(stdout) is { } pid) return new UnitActivity.Running(pid) { Starts = runs };

        var last = LastExitCode(stdout);

        return last == 0 ? new UnitActivity.Exited(0) { Starts = runs } : new UnitActivity.Restarting(last) { Starts = runs };
    }

    static int? Field(string stdout, string prefix) {
        foreach (var raw in stdout.Split('\n')) {
            var line = raw.Trim();
            if (line.StartsWith(prefix, StringComparison.Ordinal)
             && int.TryParse(line[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                return n;
        }

        return null;
    }

    /// <summary>"last exit code = 1" or "last exit code = (never exited)"; the code may be followed by its name.</summary>
    static int? LastExitCode(string stdout) {
        const string prefix = "last exit code = ";

        foreach (var raw in stdout.Split('\n')) {
            var line = raw.Trim();
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var value = line[prefix.Length..];
            var end   = value.IndexOfAny([':', ' ']);

            return int.TryParse(end < 0 ? value : value[..end], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code)
                ? code
                : null;
        }

        return null;
    }

    public static UnitActivity FromShow(int exitCode, string stdout, string stderr) {
        if (exitCode != 0) return new UnitActivity.Unknown($"systemctl show failed (exit {exitCode}): {stderr.Trim()}");

        var props = stdout.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.Ordinal);

        int? pid   = Int(props, "MainPID") is > 0 and var p ? p : null;
        var status = Int(props, "ExecMainStatus");

        UnitActivity activity = props.GetValueOrDefault("ActiveState") switch {
            "active" or "deactivating" when pid is { } live => new UnitActivity.Running(live),
            "activating" when props.GetValueOrDefault("SubState") == "auto-restart" => new UnitActivity.Restarting(status),
            "activating" when pid is { } starting => new UnitActivity.Running(starting),
            "activating"                          => new UnitActivity.Restarting(status),
            "failed"                              => new UnitActivity.Exited(status),
            "inactive"                            => UnitActivity.Stopped.Instance,
            var other => new UnitActivity.Unknown($"systemd reports the unit {other ?? "with no state"}"),
        };

        return activity with { Starts = Int(props, "NRestarts") };
    }

    static int? Int(Dictionary<string, string> props, string key) =>
        props.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;
}
