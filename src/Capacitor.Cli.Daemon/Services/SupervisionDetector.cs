namespace Capacitor.Cli.Daemon.Services;

public enum SupervisionMode { Supervised, Detached, Foreground }

/// <summary>
/// Classifies how this daemon process was launched, choosing the restart strategy.
/// All "supervised" signals are name-bound and non-inheritable so an inherited env
/// var (e.g. a marker leaking into a daemon-spawned agent) can't misclassify a
/// different-name daemon: the marker must equal our own sanitized name; systemd
/// requires our unit's cgroup AND SYSTEMD_EXEC_PID == our PID (a child has a
/// different PID); launchd requires XPC_SERVICE_NAME to equal our exact label.
/// (INVOCATION_ID is intentionally NOT used — it is inherited by children.)
/// </summary>
public static class SupervisionDetector {
    // Must match LaunchdUnit.LabelPrefix and SystemdUnit.Prefix in Capacitor.Cli.
    const string LaunchdLabelPrefix = "io.kurrent.kcap.daemon.";
    const string SystemdUnitPrefix  = "kcap-daemon-";

    public static SupervisionMode Detect(
            IReadOnlyDictionary<string, string> env,
            string                              sanitizedName,
            bool                                hasLogFile,
            string?                             cgroupContents,
            int                                 processId) {

        // 1. Authoritative, name-specific marker.
        if (env.TryGetValue("KCAP_DAEMON_SUPERVISED", out var marker) && marker == sanitizedName)
            return SupervisionMode.Supervised;

        // 2. systemd: our unit's cgroup AND direct-launch proof (non-inheritable).
        var inOurCgroup = cgroupContents is not null
                       && cgroupContents.Contains($"{SystemdUnitPrefix}{sanitizedName}.service", StringComparison.Ordinal);
        var execPidMatches = env.TryGetValue("SYSTEMD_EXEC_PID", out var execPid)
                          && execPid == processId.ToString();
        if (inOurCgroup && execPidMatches) return SupervisionMode.Supervised;

        // 3. launchd: exact label match.
        if (env.TryGetValue("XPC_SERVICE_NAME", out var label)
         && label == $"{LaunchdLabelPrefix}{sanitizedName}")
            return SupervisionMode.Supervised;

        // 4. Not supervised: detached if it logs to a file (CLI -d adds --log-file),
        //    otherwise an interactive foreground run.
        return hasLogFile ? SupervisionMode.Detached : SupervisionMode.Foreground;
    }

    /// <summary>Production entry: reads the real environment and /proc/self/cgroup.</summary>
    public static SupervisionMode DetectCurrent(string sanitizedName, bool hasLogFile) {
        var env = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(e => e.Value is not null)
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.Ordinal);

        string? cgroup = null;
        try { if (File.Exists("/proc/self/cgroup")) cgroup = File.ReadAllText("/proc/self/cgroup"); }
        catch { /* not Linux / unreadable — leave null */ }

        return Detect(env, sanitizedName, hasLogFile, cgroup, Environment.ProcessId);
    }
}
