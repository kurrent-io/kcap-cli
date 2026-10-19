using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.LocalEdition;

/// <summary>Pure rendering and command vectors for the local server's systemd user unit.</summary>
static class LocalServerSystemdUnit {
    /// <summary>How long systemd waits after SIGTERM before SIGKILL, for the same reason as on launchd.</summary>
    public const int StopTimeoutSeconds = 90;

    public static string UnitPath(UserHome home, LocalServerUnit unit) =>
        Path.Combine(SystemdUnit.UserUnitDir(home), unit.SystemdName);

    public static string Unit(LocalServerUnit unit) {
        var sb = new StringBuilder();
        sb.Append("[Unit]\n");
        sb.Append("Description=kcap local server\n");
        sb.Append("StartLimitIntervalSec=300\n");
        sb.Append("StartLimitBurst=5\n\n");

        sb.Append("[Service]\n");
        foreach (var (k, v) in unit.Environment) {
            ServiceText.RequireValidEnvName(k);
            ServiceText.RequireNoControlCharacters(k, v);
            sb.Append($"Environment={SystemdUnit.EnvAssignment(k, ServiceText.SystemdValue(v))}\n");
        }

        sb.Append($"ExecStart={SystemdUnit.QuoteArg(unit.ServerBinary)}\n");
        // The rest of the line is the path, spaces included, with specifiers expanded.
        ServiceText.RequireNoControlCharacters("the server log path", unit.OutputLog);
        var log = ServiceText.SystemdValue(unit.OutputLog);
        sb.Append($"StandardOutput=append:{log}\n");
        sb.Append($"StandardError=append:{log}\n");
        sb.Append("Restart=on-failure\n");
        sb.Append("RestartSec=5\n");
        // SIGTERM reaches the server alone, which stops Postgres itself; whatever is left at the timeout is killed.
        sb.Append("KillMode=mixed\n");
        sb.Append($"TimeoutStopSec={StopTimeoutSeconds}\n\n");

        sb.Append("[Install]\n");
        sb.Append("WantedBy=default.target\n");

        return sb.ToString();
    }

    public static string[] DaemonReloadArgs()                    => ["--user", "daemon-reload"];
    public static string[] EnableArgs(LocalServerUnit unit)      => ["--user", "enable", unit.SystemdName];
    public static string[] EnableNowArgs(LocalServerUnit unit)   => ["--user", "enable", "--now", unit.SystemdName];
    public static string[] StopArgs(LocalServerUnit unit)        => ["--user", "stop", unit.SystemdName];
    public static string[] ResetFailedArgs(LocalServerUnit unit) => ["--user", "reset-failed", unit.SystemdName];
    public static string[] DisableNowArgs(LocalServerUnit unit)  => ["--user", "disable", "--now", unit.SystemdName];
    public static string[] RestartArgs(LocalServerUnit unit)     => ["--user", "restart", unit.SystemdName];
    public static string[] ShowArgs(LocalServerUnit unit)        => ["--user", "show", "-p", "LoadState", "-p", "ActiveState", "-p", "Environment", unit.SystemdName];
    public static string[] ActivityArgs(LocalServerUnit unit)    =>
        ["--user", "show", "-p", "ActiveState", "-p", "SubState", "-p", "MainPID", "-p", "ExecMainStatus", "-p", "NRestarts", unit.SystemdName];
    public static string[] IsActiveArgs(LocalServerUnit unit)    => ["--user", "is-active", unit.SystemdName];
}
