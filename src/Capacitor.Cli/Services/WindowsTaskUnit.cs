using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

/// <summary>Pure rendering + command vectors for a per-user Windows logon Scheduled Task.</summary>
static class WindowsTaskUnit {
    const string Prefix = "kcap-daemon-";

    public static string TaskName(string id) => Prefix + id;

    public static string WrapperPath(string id) => PathHelpers.ConfigPath($"daemon-service-{id}.cmd");

    /// <summary>.cmd wrapper: set the captured env, then exec the daemon (no Environment element in Task XML).</summary>
    public static string Wrapper(ServiceSpec spec) {
        var sb = new StringBuilder();
        sb.Append("@echo off\r\n");
        foreach (var (k, v) in spec.Environment)
            sb.Append($"set \"{ServiceText.CmdValue(k)}={ServiceText.CmdValue(v)}\"\r\n");
        var args = string.Join(' ',
            new[] { "--name", spec.ServiceId, "--log-file", Quote(spec.LogPath) }
                .Concat(spec.ExtraArgs.Select(QuoteIfNeeded)));
        sb.Append($"{Quote(ServiceText.CmdValue(spec.DaemonBinaryPath))} {args}\r\n");
        return sb.ToString();
    }

    static string Quote(string s) => $"\"{s}\"";
    static string QuoteIfNeeded(string s) => s.Contains(' ') ? Quote(s) : s;

    public static string TaskXml(ServiceSpec spec, string wrapperPath) =>
        $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>kcap daemon ({ServiceText.Xml(spec.ServiceId)})</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger><Enabled>true</Enabled></LogonTrigger>
          </Triggers>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <StartWhenAvailable>true</StartWhenAvailable>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <RestartOnFailure><Interval>PT1M</Interval><Count>999</Count></RestartOnFailure>
          </Settings>
          <Actions>
            <Exec>
              <Command>cmd.exe</Command>
              <Arguments>/c "{ServiceText.Xml(wrapperPath)}"</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    public static string? IdFromTaskName(string taskName) =>
        taskName.StartsWith(Prefix, StringComparison.Ordinal) ? taskName[Prefix.Length..] : null;

    // ── command vectors (schtasks) ──
    public static string[] CreateArgs(string id, string xmlPath) => ["/Create", "/TN", TaskName(id), "/XML", xmlPath, "/F"];
    public static string[] DeleteArgs(string id)                 => ["/Delete", "/TN", TaskName(id), "/F"];
    public static string[] RunArgs(string id)                    => ["/Run", "/TN", TaskName(id)];
    public static string[] EndArgs(string id)                    => ["/End", "/TN", TaskName(id)];
    public static string[] QueryArgs(string id)                  => ["/Query", "/TN", TaskName(id), "/FO", "LIST"];

    public static ServiceState StatusFromQuery(int exitCode, string stdout) {
        if (exitCode != 0) return ServiceState.NotInstalled;
        return stdout.Contains("Running", StringComparison.OrdinalIgnoreCase)
            ? ServiceState.Running
            : ServiceState.Installed;
    }
}
