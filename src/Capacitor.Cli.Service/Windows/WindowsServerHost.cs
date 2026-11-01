using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Capacitor.Cli.Service.Sessions;
using Capacitor.Cli.Service.Supervision;
using Microsoft.Win32;
using static Capacitor.Cli.Service.Native.Win32;

namespace Capacitor.Cli.Service.Windows;

/// <summary>
/// The local edition server supervisor's Windows. As with the daemon, the enrollment, the binaries, the
/// environment file and the log are opened while impersonating the user, and the server and the CLI that
/// stops it run with the user's token
/// </summary>
[SupportedOSPlatform("windows")]
sealed unsafe class WindowsServerHost : IDaemonHost<ServerEnrollment> {
    const int MaxPathChars = 32768;

    public IReadOnlyList<int> ActiveSessions() => WtsSessions.Active();

    public SessionUser? UserOf(int sessionId) => WtsSessions.UserOf(sessionId);

    public ServerEnrollment? EnrollmentOf(SessionUser user, out string? refusal) {
        using var token = UserToken.ForSession(user.SessionId);
        if (token is null) {
            refusal = "the session has ended";
            return null;
        }

        var (enrollment, reason) = token.Impersonate<(ServerEnrollment?, string?)>(() => {
            using var key = Registry.Users.OpenSubKey($@"{user.Sid}\{ServiceIdentity.ServerEnrollmentKey}");
            if (key is null) return ((ServerEnrollment?)null, "no local server enrollment");
            var parsed = ServerEnrollment.Parse(
                key.GetValue("ServerPath") as string, key.GetValue("Root") as string, key.GetValue("CliPath") as string,
                key.GetValue("LogFile") as string, key.GetValue("EnvironmentFile") as string, key.GetValue("Stopped"),
                out var why);
            if (parsed is null) return (null, why);
            return File.Exists(parsed.ServerPath) ? (parsed, null) : (null, $"{parsed.ServerPath} does not exist");
        });
        refusal = reason;
        return enrollment;
    }

    public bool HasEnrollment(SessionUser user) => EnrollmentKey.Exists(user, ServiceIdentity.ServerEnrollmentKey);

    /// <summary>The image path Windows reports has the <c>bin\current</c> junction resolved, so the server is
    /// matched by lying anywhere under <c>bin</c>, itself resolved as the user</summary>
    public IRunningDaemon? FindRunning(SessionUser user, ServerEnrollment enrollment) {
        using var token = UserToken.ForSession(user.SessionId);
        if (token is null) return null;
        var bin = token.Impersonate(() => FinalPath(Path.Combine(enrollment.Root, "bin")));
        if (bin is null) return null;
        var within = bin.TrimEnd('\\') + @"\";

        foreach (var process in Process.GetProcessesByName(ServerEnrollment.ProcessName)) {
            using (process) {
                int session;
                try {
                    session = process.SessionId;
                } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) {
                    continue;
                }
                if (session != user.SessionId) continue;

                var handle = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE, false, process.Id);
                if (handle == IntPtr.Zero) continue;
                if (ImagePath(handle) is { } image && image.StartsWith(within, StringComparison.OrdinalIgnoreCase))
                    return UserProcess.Adopt(process.Id, handle, ServiceIdentity.ServerJobName(user.Sid));
                CloseHandle(handle);
            }
        }
        return null;
    }

    public IRunningDaemon Launch(SessionUser user, ServerEnrollment enrollment) {
        using var token = UserToken.ForSession(user.SessionId) ?? throw new InvalidOperationException("the session has ended");
        var plan = PlanFor(token, enrollment);
        return UserProcess.LaunchInJob(token, ServiceIdentity.ServerJobName(user.Sid), plan.CommandLine, plan.EnvironmentBlock(),
            plan.WorkingDirectory, plan.LogPath);
    }

    /// <summary>The CLI reports a failed break only on its standard error, which goes to the server's log</summary>
    public void RequestStop(SessionUser user, ServerEnrollment enrollment, int pid) {
        using var token = UserToken.ForSession(user.SessionId) ?? throw new InvalidOperationException("the session has ended");
        if (!token.Impersonate(() => File.Exists(enrollment.CliPath))) throw new InvalidOperationException($"{enrollment.CliPath} does not exist");

        var plan = PlanFor(token, enrollment);
        UserProcess.Run(token, ServerLaunchPlan.StopCommandLine(enrollment, pid), plan.EnvironmentBlock(), user.ProfileDirectory, plan.LogPath);
    }

    static ServerLaunchPlan PlanFor(UserToken token, ServerEnrollment enrollment) {
        var captured = enrollment.EnvironmentFile is { } file ? token.Impersonate(() => UserProcess.ReadCaptured(file)) : null;
        return ServerLaunchPlan.For(enrollment, token.Environment(), captured);
    }

    /// <summary>Called while impersonating. Null when the directory cannot be opened</summary>
    static string? FinalPath(string directory) {
        var handle = CreateFileW(directory, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, null,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle == new IntPtr(-1)) return null;
        string path;
        try {
            var buffer = new char[MaxPathChars];
            fixed (char* p = buffer) {
                var length = GetFinalPathNameByHandleW(handle, p, (uint)buffer.Length, 0);
                if (length == 0 || length >= buffer.Length) return null;
                path = new string(p, 0, (int)length);
            }
        } finally {
            CloseHandle(handle);
        }

        const string uncPrefix = @"\\?\UNC\";
        const string longPrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.Ordinal)) return @"\\" + path[uncPrefix.Length..];
        return path.StartsWith(longPrefix, StringComparison.Ordinal) ? path[longPrefix.Length..] : path;
    }

    static string? ImagePath(IntPtr process) {
        var buffer = new char[MaxPathChars];
        var size = buffer.Length;
        fixed (char* p = buffer) {
            return QueryFullProcessImageNameW(process, 0, p, ref size) ? new string(p, 0, size) : null;
        }
    }
}
