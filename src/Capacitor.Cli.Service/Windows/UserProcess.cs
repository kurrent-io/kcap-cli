using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Capacitor.Cli.Service.Sessions;
using static Capacitor.Cli.Service.Native.Win32;

namespace Capacitor.Cli.Service.Windows;

/// <summary>Starting and adopting processes that run as a signed-in user. Every file a user controls is opened
/// while impersonating them, and every process gets their token</summary>
[SupportedOSPlatform("windows")]
static unsafe class UserProcess {
    /// <summary>Starts a process into a job named <paramref name="jobName"/>, with its standard output and error
    /// appended to <paramref name="outputPath"/></summary>
    public static Win32RunningDaemon LaunchInJob(
            UserToken token, string jobName, string commandLine, string environmentBlock, string directory, string outputPath) {
        var job = CreateJobObjectW(IntPtr.Zero, jobName);
        // Any user can create a job under that name first and keep a handle that ends this one's process. Such
        // a job is left alone for an unnamed one, which a later run of the service cannot adopt.
        if (job != IntPtr.Zero && Marshal.GetLastPInvokeError() == ERROR_ALREADY_EXISTS) {
            CloseHandle(job);
            job = CreateJobObjectW(IntPtr.Zero, null);
        }
        if (job == IntPtr.Zero) throw Failure("CreateJobObject");

        var output = IntPtr.Zero;
        try {
            output = token.Impersonate(() => OpenInheritableAppend(outputPath));
            var info = Start(token, commandLine, environmentBlock, directory, output);
            if (!AssignProcessToJobObject(job, info.hProcess)) {
                TerminateProcess(info.hProcess, 1);
                CloseHandle(info.hThread);
                CloseHandle(info.hProcess);
                throw Failure("AssignProcessToJobObject");
            }
            // A job's name lives only while a handle to it is open, not while processes are in it. A
            // query-only handle held by the process keeps the name alive across a service restart, so the
            // next run can adopt it with its job.
            DuplicateHandle(GetCurrentProcess(), job, info.hProcess, out _, JOB_OBJECT_QUERY, false, 0);
            ResumeThread(info.hThread);
            CloseHandle(info.hThread);
            return new Win32RunningDaemon(info.dwProcessId, info.hProcess, job);
        } catch {
            CloseHandle(job);
            throw;
        } finally {
            if (output != IntPtr.Zero) CloseHandle(output);
        }
    }

    /// <summary>Starts a process and lets it go, its output appended to <paramref name="outputPath"/> when one
    /// is given</summary>
    public static void Run(UserToken token, string commandLine, string environmentBlock, string directory, string? outputPath = null) {
        var output = IntPtr.Zero;
        try {
            if (outputPath is { } path) output = token.Impersonate(() => OpenInheritableAppend(path));
            var info = Start(token, commandLine, environmentBlock, directory, output);
            ResumeThread(info.hThread);
            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
        } finally {
            if (output != IntPtr.Zero) CloseHandle(output);
        }
    }

    /// <summary>Takes ownership of <paramref name="process"/>, with the job named <paramref name="jobName"/> only
    /// when the process is in it</summary>
    public static Win32RunningDaemon Adopt(int pid, IntPtr process, string jobName) {
        var job = OpenJobObjectW(JOB_OBJECT_ALL_ACCESS, false, jobName);
        // Any user can create a job under that name; one the process is not in is not the service's
        if (job != IntPtr.Zero && !(IsProcessInJob(process, job, out var member) && member)) {
            CloseHandle(job);
            job = IntPtr.Zero;
        }
        return new Win32RunningDaemon(pid, process, job);
    }

    /// <summary>Called while impersonating. A missing or oversized file launches with the logon
    /// environment alone rather than not at all</summary>
    public static IReadOnlyDictionary<string, string>? ReadCaptured(string path) {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > CapturedEnvironment.MaxBytes) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return CapturedEnvironment.Parse(reader.ReadToEnd());
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

    /// <summary>Called while impersonating: the directory and file are the user's, created as the user</summary>
    static IntPtr OpenInheritableAppend(string path) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var security = new SECURITY_ATTRIBUTES { nLength = sizeof(SECURITY_ATTRIBUTES), bInheritHandle = 1 };
        var handle = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, &security, OPEN_ALWAYS, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1)) throw Failure($"CreateFile({path})");
        return handle;
    }
}
