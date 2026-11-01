using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Capacitor.Cli.Service.Sessions;
using Capacitor.Cli.Service.Supervision;
using Microsoft.Win32;
using static Capacitor.Cli.Service.Native.Win32;

namespace Capacitor.Cli.Service.Windows;

/// <summary>
/// The daemon supervisor's Windows. Everything a user controls — their enrollment, the daemon and CLI binaries,
/// the log directory — is opened while impersonating them, and everything started runs with their token,
/// so SYSTEM never reads, writes or runs a file on a user's behalf that the user could not
/// </summary>
[SupportedOSPlatform("windows")]
sealed class WindowsDaemonHost : IDaemonHost<Enrollment> {
    public IReadOnlyList<int> ActiveSessions() => WtsSessions.Active();

    public SessionUser? UserOf(int sessionId) => WtsSessions.UserOf(sessionId);

    public Enrollment? EnrollmentOf(SessionUser user, out string? refusal) {
        using var token = UserToken.ForSession(user.SessionId);
        if (token is null) {
            refusal = "the session has ended";
            return null;
        }

        var (enrollment, reason) = token.Impersonate<(Enrollment?, string?)>(() => {
            using var key = Registry.Users.OpenSubKey($@"{user.Sid}\{ServiceIdentity.EnrollmentKey}");
            if (key is null) return ((Enrollment?)null, "not enrolled");
            var parsed = Enrollment.Parse(
                key.GetValue("Name") as string, key.GetValue("DaemonPath") as string,
                key.GetValue("Profile") as string, key.GetValue("MaxAgents"), out var why,
                key.GetValue("EnvironmentFile") as string);
            if (parsed is null) return (null, why);
            return File.Exists(parsed.DaemonPath) ? (parsed, null) : (null, $"{parsed.DaemonPath} does not exist");
        });
        refusal = reason;
        return enrollment;
    }

    public bool HasEnrollment(SessionUser user) => EnrollmentKey.Exists(user, ServiceIdentity.EnrollmentKey);

    public IRunningDaemon? FindRunning(SessionUser user, Enrollment enrollment) {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Enrollment.DaemonFileName))) {
            using (process) {
                string? path;
                try {
                    path = process.MainModule?.FileName;
                } catch (Win32Exception) {
                    continue;
                }
                if (process.SessionId != user.SessionId || !string.Equals(path, enrollment.DaemonPath, StringComparison.OrdinalIgnoreCase)) continue;

                var handle = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE, false, process.Id);
                if (handle == IntPtr.Zero) continue;
                return UserProcess.Adopt(process.Id, handle, ServiceIdentity.JobName(user.Sid));
            }
        }
        return null;
    }

    public IRunningDaemon Launch(SessionUser user, Enrollment enrollment) {
        using var token = UserToken.ForSession(user.SessionId) ?? throw new InvalidOperationException("the session has ended");
        var plan = PlanFor(token, user, enrollment);
        return UserProcess.LaunchInJob(token, ServiceIdentity.JobName(user.Sid), plan.CommandLine, plan.EnvironmentBlock(),
            user.ProfileDirectory, plan.StderrPath);
    }

    public void RequestStop(SessionUser user, Enrollment enrollment, int pid) {
        using var token = UserToken.ForSession(user.SessionId) ?? throw new InvalidOperationException("the session has ended");
        if (!token.Impersonate(() => File.Exists(enrollment.CliPath))) throw new InvalidOperationException($"{enrollment.CliPath} does not exist");

        // The daemon's own environment, so a captured HOME or KCAP_DAEMONS_DIR points the CLI at its socket
        var block = PlanFor(token, user, enrollment).EnvironmentBlock();
        UserProcess.Run(token, DaemonLaunchPlan.StopCommandLine(enrollment), block, user.ProfileDirectory);
    }

    static DaemonLaunchPlan PlanFor(UserToken token, SessionUser user, Enrollment enrollment) {
        var captured = enrollment.EnvironmentFile is { } file ? token.Impersonate(() => UserProcess.ReadCaptured(file)) : null;
        return DaemonLaunchPlan.For(enrollment, user.ProfileDirectory, token.Environment(), captured);
    }
}
