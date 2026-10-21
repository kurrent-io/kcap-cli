using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.LocalEdition;

/// <summary>Pure rendering and command vectors for the local server's LaunchAgent.</summary>
static class LocalServerLaunchdUnit {
    /// <summary>How long launchd waits after SIGTERM before SIGKILL. The server stops Postgres on the way
    /// out, and a kill before that leaves a postmaster for the next start to reconcile.</summary>
    public const int ExitTimeoutSeconds = 90;

    public static string PlistPath(UserHome home, LocalServerUnit unit) =>
        Path.Combine(home.Path, "Library", "LaunchAgents", unit.Label + ".plist");

    public static string Plist(LocalServerUnit unit) {
        var sb = new StringBuilder();
        sb.Append("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>

            """);
        sb.Append($"  <key>Label</key><string>{Guarded("the service label", unit.Label)}</string>\n");
        sb.Append($"  <key>ProgramArguments</key><array><string>{Guarded("the server path", unit.ServerBinary)}</string></array>\n");

        sb.Append("  <key>EnvironmentVariables</key><dict>\n");
        foreach (var (k, v) in unit.Environment) {
            ServiceText.RequireValidEnvName(k);
            sb.Append($"    <key>{ServiceText.Xml(k)}</key><string>{Guarded(k, v)}</string>\n");
        }
        sb.Append("  </dict>\n");

        sb.Append("  <key>RunAtLoad</key><true/>\n");
        // Crash-only: the server exits nonzero when a store fails, and the whole stack restarts.
        sb.Append("  <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>\n");
        // Standard, because Adaptive and Background let macOS hold the job at background priority under load.
        sb.Append("  <key>ProcessType</key><string>Standard</string>\n");
        sb.Append($"  <key>ExitTimeOut</key><integer>{ExitTimeoutSeconds}</integer>\n");
        var log = Guarded("the server log path", unit.OutputLog);
        sb.Append($"  <key>StandardOutPath</key><string>{log}</string>\n");
        sb.Append($"  <key>StandardErrorPath</key><string>{log}</string>\n");
        sb.Append("</dict>\n</plist>\n");

        return sb.ToString();
    }

    static string Guarded(string what, string value) {
        ServiceText.RequireXmlRepresentableValue(what, value);
        return ServiceText.Xml(value);
    }

    static string Target(int uid, LocalServerUnit unit) => $"gui/{uid}/{unit.Label}";

    public static string[] BootstrapArgs(int uid, string plistPath)     => ["bootstrap", $"gui/{uid}", plistPath];
    public static string[] BootoutArgs(int uid, LocalServerUnit unit)   => ["bootout", Target(uid, unit)];
    public static string[] KickstartArgs(int uid, LocalServerUnit unit) => ["kickstart", Target(uid, unit)];
    public static string[] PrintArgs(int uid, LocalServerUnit unit)     => ["print", Target(uid, unit)];

    /// <summary>Persisted by launchd across logins: a disabled label does not load from its plist, so a stopped
    /// stack stays stopped through a reboot.</summary>
    public static string[] DisableArgs(int uid, LocalServerUnit unit) => ["disable", Target(uid, unit)];
    public static string[] EnableArgs(int uid, LocalServerUnit unit)  => ["enable", Target(uid, unit)];
}
