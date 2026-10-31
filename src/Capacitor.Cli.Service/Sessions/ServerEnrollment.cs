using System.Diagnostics.CodeAnalysis;
using Capacitor.Cli.Service.Supervision;

namespace Capacitor.Cli.Service.Sessions;

/// <summary>A user's opt-in for their local edition server, read from their own hive. Like the daemon's, it is
/// the user's to choose: the server and the CLI it names only ever run as that user</summary>
/// <param name="Root">The local edition's root; a server running from under its <c>bin</c> is this user's</param>
/// <param name="CliPath">The <c>kcap.exe</c> that delivers the server's graceful stop</param>
public sealed record ServerEnrollment(string ServerPath, string Root, string CliPath, string LogFile, string? EnvironmentFile = null)
    : ISupervisedEnrollment {
    public const string ServerFileName = "kcap-server.exe";
    public const string CliFileName    = "kcap.exe";

    /// <summary>What the CLI writes after a deliberate stop; the refusal that keeps the server down</summary>
    public const string StoppedRefusal = "stopped";

    public const string ProcessName    = "kcap-server";

    public string Name => "kcap-local";

    /// <summary>Null when the values cannot describe a server, or the user stopped it, with the reason</summary>
    /// <param name="stopped">The <c>Stopped</c> DWORD; non-zero holds the server down across sign-ins and
    /// service restarts</param>
    public static ServerEnrollment? Parse(
            string? serverPath, string? root, string? cliPath, string? logFile, string? environmentFile, object? stopped,
            out string? refusal) {
        refusal = null;
        if (stopped is int flag && flag != 0) {
            refusal = StoppedRefusal;
            return null;
        }
        if (!IsLocalFile(serverPath, ServerFileName)) {
            refusal = $"the server path is not an absolute local path to {ServerFileName}";
            return null;
        }
        if (!LocalPath.IsAbsolute(root)) {
            refusal = "the local edition root is not an absolute local path";
            return null;
        }
        if (!IsLocalFile(cliPath, CliFileName)) {
            refusal = $"the CLI path is not an absolute local path to {CliFileName}";
            return null;
        }
        if (!LocalPath.IsAbsolute(logFile)) {
            refusal = "the log file is not an absolute local path";
            return null;
        }
        if (!string.IsNullOrWhiteSpace(environmentFile) && !LocalPath.IsAbsolute(environmentFile)) {
            refusal = "the environment file is not an absolute local path";
            return null;
        }
        return new ServerEnrollment(serverPath, root, cliPath, logFile,
            string.IsNullOrWhiteSpace(environmentFile) ? null : environmentFile);
    }

    static bool IsLocalFile([NotNullWhen(true)] string? path, string fileName) =>
        LocalPath.IsAbsolute(path) && string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase);
}
