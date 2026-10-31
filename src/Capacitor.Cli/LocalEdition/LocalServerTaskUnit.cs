using System.Globalization;
using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.LocalEdition;

/// <summary>Pure rendering, command vectors and output parsing for the local server's per-user logon Scheduled Task.
/// Task XML has no environment, so the task runs a <c>.cmd</c> wrapper that sets it and relaunches the server after
/// a failure.</summary>
static class LocalServerTaskUnit {
    const string Rerun = "`kcap setup --local`";

    /// <summary>Normal priority: a task's default is below normal, and Postgres inherits the server's class.</summary>
    const int Priority = 5;

    /// <summary>Last Result while the action is running (<c>SCHED_S_TASK_RUNNING</c>), in schtasks' decimal.</summary>
    public const int RunningResult = 0x41301;

    /// <summary>Last Result of a task that has never run (<c>SCHED_S_TASK_HAS_NOT_RUN</c>).</summary>
    public const int NeverRunResult = 0x41303;

    /// <summary>Last Result of a task ended by <c>/End</c> (<c>SCHED_S_TASK_TERMINATED</c>).</summary>
    public const int TerminatedResult = 0x41306;

    /// <summary>Beside the unit lock: the task is per user, so its wrapper is too.</summary>
    public static string WrapperPath(UserHome home, LocalServerUnit unit) =>
        Path.Combine(home.Path, ".config", "kcap", unit.TaskName + ".cmd");

    public static string XmlPath(UserHome home, LocalServerUnit unit) => Path.ChangeExtension(WrapperPath(home, unit), ".task.xml");

    /// <summary>Present from a stop until the next start. Ending the task ends only its conhost; the wrapper's cmd lives
    /// on and would relaunch the server, so the wrapper checks this before every launch.</summary>
    public static string StopMarkerPath(UserHome home, LocalServerUnit unit) => Path.ChangeExtension(WrapperPath(home, unit), ".stop");

    public static string Wrapper(LocalServerUnit unit, string stopMarker) {
        var sb = new StringBuilder();
        sb.Append("@echo off\r\n");
        // cmd decodes the file line by line in the console code page; both lines above the first non-ASCII one are ASCII.
        sb.Append("chcp 65001 >nul\r\n");
        sb.Append("setlocal DisableDelayedExpansion\r\n");

        foreach (var (k, v) in unit.Environment) {
            WindowsTaskUnit.RequireRepresentable(k, v, Rerun);
            sb.Append($"set \"{ServiceText.CmdValue(k)}={ServiceText.CmdValue(v)}\"\r\n");
        }

        // A variable named ERRORLEVEL would shadow cmd's dynamic exit code.
        sb.Append("set \"ERRORLEVEL=\"\r\n");
        sb.Append(":run\r\n");
        sb.Append($"if exist {WindowsTaskUnit.ExecValue("the stop marker path", stopMarker, Rerun)} exit /b 0\r\n");
        var server = WindowsTaskUnit.ExecValue("the server path", unit.ServerBinary, Rerun);
        var log    = WindowsTaskUnit.ExecValue("the server log path", unit.OutputLog, Rerun);
        sb.Append($"{server} >>{log} 2>&1\r\n");
        // Crash-only, as on launchd and systemd: a clean exit is a stop, and anything else relaunches the stack.
        sb.Append("if %ERRORLEVEL% EQU 0 exit /b 0\r\n");
        sb.Append($"call \"%SystemRoot%\\System32\\PING.EXE\" -n {WindowsTaskUnit.RelaunchPauseSeconds + 1} 127.0.0.1 >nul\r\n");
        sb.Append("goto run\r\n");

        return sb.ToString();
    }

    public static string TaskXml(LocalServerUnit unit, string wrapperPath, string? userId = null) =>
        WindowsTaskUnit.TaskXml("kcap local server", wrapperPath, userId, Priority,
            "The wrapper sits under the user profile, so the local edition cannot register its task for this account.");

    public static string[] CreateArgs(LocalServerUnit unit, string xmlPath) => ["/Create", "/TN", unit.TaskName, "/XML", xmlPath, "/F"];
    public static string[] DeleteArgs(LocalServerUnit unit)                 => ["/Delete", "/TN", unit.TaskName, "/F"];
    public static string[] RunArgs(LocalServerUnit unit)                    => ["/Run", "/TN", unit.TaskName];
    public static string[] EndArgs(LocalServerUnit unit)                    => ["/End", "/TN", unit.TaskName];

    /// <summary>Persisted with the task: a disabled task does not start at the next logon, so a stopped stack stays
    /// stopped through a reboot.</summary>
    public static string[] DisableArgs(LocalServerUnit unit) => ["/Change", "/TN", unit.TaskName, "/DISABLE"];
    public static string[] EnableArgs(LocalServerUnit unit)  => ["/Change", "/TN", unit.TaskName, "/ENABLE"];

    public static string[] QueryArgs(LocalServerUnit unit)    => ["/Query", "/TN", unit.TaskName, "/V", "/FO", "CSV", "/NH"];
    public static string[] QueryXmlArgs(LocalServerUnit unit) => ["/Query", "/TN", unit.TaskName, "/XML"];
    public static string[] ListArgs()                         => ["/Query", "/FO", "CSV", "/NH"];

    /// <summary>Whether a task listing names this task at the root of the library. Task names are not localized.</summary>
    public static bool Listed(string listing, LocalServerUnit unit) =>
        listing.Split('\n').Select(l => CsvFields(l.Trim()).FirstOrDefault())
            .Any(name => string.Equals(name, @"\" + unit.TaskName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The verbose query's Last Result. The Status column beside it is localized; this is a number.</summary>
    public static int? LastResult(string query) {
        var row = query.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        var fields = row is null ? [] : CsvFields(row);

        return fields.Count > 6 && int.TryParse(fields[6], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    /// <summary>schtasks' CSV: every field quoted, a quote inside one doubled.</summary>
    static List<string> CsvFields(string line) {
        var fields = new List<string>();
        var field  = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++) {
            var c = line[i];

            if (quoted && c == '"' && i + 1 < line.Length && line[i + 1] == '"') {
                field.Append('"');
                i++;
            } else if (c == '"') {
                quoted = !quoted;
            } else if (c == ',' && !quoted) {
                fields.Add(field.ToString());
                field.Clear();
            } else {
                field.Append(c);
            }
        }

        if (line.Length > 0) fields.Add(field.ToString());

        return fields;
    }
}
