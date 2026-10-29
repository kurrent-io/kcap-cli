using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Capacitor.Cli.Service.Sessions;
using Capacitor.Cli.Service.Supervision;
using Microsoft.Win32;
using static Capacitor.Cli.Service.Native.Win32;

namespace Capacitor.Cli.Service.Windows;

/// <summary>
/// The supervisor's Windows. Everything a user controls — their enrollment, the daemon and CLI binaries,
/// the log directory — is opened while impersonating them, and everything started runs with their token,
/// so SYSTEM never reads, writes or runs a file on a user's behalf that the user could not
/// </summary>
[SupportedOSPlatform("windows")]
sealed unsafe class WindowsDaemonHost : IDaemonHost {
    public IReadOnlyList<int> ActiveSessions() {
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var sessions, out var count)) throw Failure("WTSEnumerateSessions");
        try {
            var ids = new List<int>(count);
            for (var i = 0; i < count; i++)
                if (sessions[i].State is WTSActive or WTSDisconnected) ids.Add(sessions[i].SessionId);
            return ids;
        } finally {
            WTSFreeMemory(sessions);
        }
    }

    public SessionUser? UserOf(int sessionId) {
        using var token = UserToken.ForSession(sessionId);
        return token is null ? null : new SessionUser(sessionId, token.Sid(), token.ProfileDirectory());
    }

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
                var job = OpenJobObjectW(JOB_OBJECT_ALL_ACCESS, false, ServiceIdentity.JobName(user.Sid));
                // Any user can create a job under that name; one the daemon is not in is not the service's
                if (job != IntPtr.Zero && !(IsProcessInJob(handle, job, out var member) && member)) {
                    CloseHandle(job);
                    job = IntPtr.Zero;
                }
                return new Win32RunningDaemon(process.Id, handle, job);
            }
        }
        return null;
    }

    public IRunningDaemon Launch(SessionUser user, Enrollment enrollment) {
        using var token = UserToken.ForSession(user.SessionId) ?? throw new InvalidOperationException("the session has ended");
        var plan = PlanFor(token, user, enrollment);

        var job = CreateJobObjectW(IntPtr.Zero, ServiceIdentity.JobName(user.Sid));
        // Any user can create a job under that name first and keep a handle that ends this one's daemon. Such
        // a job is left alone for an unnamed one, which a later run of the service cannot adopt.
        if (job != IntPtr.Zero && Marshal.GetLastPInvokeError() == ERROR_ALREADY_EXISTS) {
            CloseHandle(job);
            job = CreateJobObjectW(IntPtr.Zero, null);
        }
        if (job == IntPtr.Zero) throw Failure("CreateJobObject");

        var stderr = IntPtr.Zero;
        try {
            stderr = token.Impersonate(() => OpenInheritableAppend(plan.StderrPath));
            var info = Start(token, plan.CommandLine, plan.EnvironmentBlock(), user.ProfileDirectory, stderr);
            if (!AssignProcessToJobObject(job, info.hProcess)) {
                TerminateProcess(info.hProcess, 1);
                CloseHandle(info.hThread);
                CloseHandle(info.hProcess);
                throw Failure("AssignProcessToJobObject");
            }
            // A job's name lives only while a handle to it is open, not while processes are in it. A
            // query-only handle held by the daemon keeps the name alive across a service restart, so the
            // next run can adopt the daemon with its job.
            DuplicateHandle(GetCurrentProcess(), job, info.hProcess, out _, JOB_OBJECT_QUERY, false, 0);
            ResumeThread(info.hThread);
            CloseHandle(info.hThread);
            return new Win32RunningDaemon(info.dwProcessId, info.hProcess, job);
        } catch {
            CloseHandle(job);
            throw;
        } finally {
            if (stderr != IntPtr.Zero) CloseHandle(stderr);
        }
    }

    public void RequestStop(SessionUser user, Enrollment enrollment) {
        using var token = UserToken.ForSession(user.SessionId) ?? throw new InvalidOperationException("the session has ended");
        if (!token.Impersonate(() => File.Exists(enrollment.CliPath))) throw new InvalidOperationException($"{enrollment.CliPath} does not exist");

        // The daemon's own environment, so a captured HOME or KCAP_DAEMONS_DIR points the CLI at its socket
        var block = PlanFor(token, user, enrollment).EnvironmentBlock();
        var info = Start(token, DaemonLaunchPlan.StopCommandLine(enrollment), block, user.ProfileDirectory, IntPtr.Zero);
        ResumeThread(info.hThread);
        CloseHandle(info.hThread);
        CloseHandle(info.hProcess);
    }

    static DaemonLaunchPlan PlanFor(UserToken token, SessionUser user, Enrollment enrollment) {
        var captured = enrollment.EnvironmentFile is { } file ? token.Impersonate(() => ReadCaptured(file)) : null;
        return DaemonLaunchPlan.For(enrollment, user.ProfileDirectory, token.Environment(), captured);
    }

    /// <summary>Suspended, with no window, and inheriting nothing but <paramref name="stdio"/> when one is given</summary>
    static PROCESS_INFORMATION Start(UserToken token, string commandLine, string environmentBlock, string directory, IntPtr stdio) {
        var desktop = Marshal.StringToHGlobalUni(@"winsta0\default");
        var env = Marshal.StringToHGlobalUni(environmentBlock);
        var attributes = IntPtr.Zero;
        try {
            var startup = new STARTUPINFOEX();
            startup.StartupInfo.cb = sizeof(STARTUPINFOEX);
            startup.StartupInfo.lpDesktop = desktop;
            var flags = CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT;

            if (stdio != IntPtr.Zero) {
                nint size = 0;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                attributes = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw Failure("InitializeProcThreadAttributeList");
                var handles = stackalloc IntPtr[1];
                handles[0] = stdio;
                if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, sizeof(IntPtr), IntPtr.Zero, IntPtr.Zero))
                    throw Failure("UpdateProcThreadAttribute");
                startup.lpAttributeList = attributes;
                startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
                startup.StartupInfo.hStdOutput = stdio;
                startup.StartupInfo.hStdError = stdio;
            }

            PROCESS_INFORMATION info;
            fixed (char* cmd = commandLine + "\0") {
                if (!CreateProcessAsUserW(token.Handle, null, cmd, IntPtr.Zero, IntPtr.Zero, stdio != IntPtr.Zero,
                        flags, env, directory, &startup, &info))
                    throw Failure("CreateProcessAsUser");
            }
            return info;
        } finally {
            if (attributes != IntPtr.Zero) {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            Marshal.FreeHGlobal(env);
            Marshal.FreeHGlobal(desktop);
        }
    }

    /// <summary>Called while impersonating. A missing or oversized file launches with the logon
    /// environment alone rather than not at all</summary>
    static IReadOnlyDictionary<string, string>? ReadCaptured(string path) {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > CapturedEnvironment.MaxBytes) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return CapturedEnvironment.Parse(reader.ReadToEnd());
    }

    /// <summary>Called while impersonating: the directory and file are the user's, created as the user</summary>
    static IntPtr OpenInheritableAppend(string path) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var security = new SECURITY_ATTRIBUTES { nLength = sizeof(SECURITY_ATTRIBUTES), bInheritHandle = 1 };
        var handle = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, &security, OPEN_ALWAYS, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1)) throw Failure($"CreateFile({path})");
        return handle;
    }
}
