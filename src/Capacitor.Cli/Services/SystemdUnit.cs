using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

/// <summary>Pure rendering + command vectors for a per-user systemd unit (one file per id).</summary>
static class SystemdUnit {
    const string Prefix = "kcap-daemon-";

    public static string UnitName(string id) => $"{Prefix}{id}.service";

    public static string UserUnitDir() =>
        Path.Combine(PathHelpers.HomeDirectory, ".config", "systemd", "user");

    public static string UnitPath(string id) => Path.Combine(UserUnitDir(), UnitName(id));

    public static string Unit(ServiceSpec spec) {
        var sb = new StringBuilder();
        sb.Append("[Unit]\n");
        sb.Append($"Description=kcap daemon ({ServiceText.SystemdValue(spec.ServiceId)})\n");
        sb.Append("After=network-online.target\n");
        sb.Append("Wants=network-online.target\n\n");

        sb.Append("[Service]\n");
        foreach (var (k, v) in spec.Environment)
            sb.Append($"Environment={ServiceText.SystemdValue(k)}={ServiceText.SystemdValue(v)}\n");

        var args = string.Join(' ', new[] { "--name", spec.ServiceId, "--log-file", spec.LogPath }.Concat(spec.ExtraArgs));
        sb.Append($"ExecStart={spec.DaemonBinaryPath} {args}\n");
        sb.Append("Restart=on-failure\n");
        sb.Append("RestartSec=5\n");
        sb.Append("StartLimitIntervalSec=60\n");
        sb.Append("StartLimitBurst=5\n\n");

        sb.Append("[Install]\n");
        sb.Append("WantedBy=default.target\n");
        return sb.ToString();
    }

    public static string? IdFromUnitFileName(string fileName) =>
        fileName.StartsWith(Prefix, StringComparison.Ordinal) && fileName.EndsWith(".service", StringComparison.Ordinal)
            ? fileName[Prefix.Length..^".service".Length]
            : null;

    // ── command vectors ──
    public static string[] DaemonReloadArgs()       => ["--user", "daemon-reload"];
    public static string[] EnableArgs(string id)    => ["--user", "enable", UnitName(id)];
    public static string[] DisableNowArgs(string id)=> ["--user", "disable", "--now", UnitName(id)];
    public static string[] StartArgs(string id)     => ["--user", "start", UnitName(id)];
    public static string[] RestartArgs(string id)   => ["--user", "restart", UnitName(id)];
    public static string[] StopArgs(string id)      => ["--user", "stop", UnitName(id)];
    public static string[] IsActiveArgs(string id)  => ["--user", "is-active", UnitName(id)];
    public static string[] IsEnabledArgs(string id) => ["--user", "is-enabled", UnitName(id)];

    public static ServiceState StatusFrom(string activeOut, int enabledExit) {
        if (activeOut.Trim().Equals("active", StringComparison.OrdinalIgnoreCase)) return ServiceState.Running;
        return enabledExit == 0 ? ServiceState.Installed : ServiceState.NotInstalled;
    }
}
