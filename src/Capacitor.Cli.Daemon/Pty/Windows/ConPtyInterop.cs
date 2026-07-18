using System.Runtime.InteropServices;

namespace Capacitor.Cli.Daemon.Pty.Windows;

internal static partial class ConPtyInterop
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int ResizePseudoConsole(IntPtr hPC, COORD size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEXW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue,
        IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PeekNamedPipe(IntPtr hNamedPipe, IntPtr lpBuffer, uint nBufferSize,
        IntPtr lpBytesRead, out uint lpTotalBytesAvail, IntPtr lpBytesLeftThisMessage);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    internal const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    internal const uint CREATE_UNICODE_ENVIRONMENT   = 0x00000400;
    internal const int  STARTF_USESTDHANDLES         = 0x00000100;
    internal const uint STILL_ACTIVE                 = 259;

    internal static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    internal struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFOW
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetInformationJobObject(
        IntPtr hJob, int JobObjectInformationClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    // JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation
    internal const int JobObjectExtendedLimitInformation = 9;

    // JOBOBJECT_BASIC_LIMIT_INFORMATION.LimitFlags: kill every process in the job the instant
    // the LAST handle to the job object closes (clean dispose, crash, task-manager kill — all
    // of them). Deliberately the ONLY flag we set: no UI-restriction flags (those would block
    // job nesting when the daemon itself already runs inside an enclosing job), no breakaway
    // flags (so a descendant literally cannot CreateProcess its way out of the job).
    internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    // PROC_THREAD_ATTRIBUTE_JOB_LIST — grows the existing 1-entry attribute list
    // (PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE) to 2 entries. The value is a POINTER TO AN ARRAY
    // of job handles (we pass an array of exactly one).
    internal static readonly IntPtr PROC_THREAD_ATTRIBUTE_JOB_LIST = 0x0002000D;

    // Used only by the breakaway-denial test: a child that tries this must fail.
    internal const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION {
        public long  PerProcessUserTimeLimit;
        public long  PerJobUserTimeLimit;
        public uint  LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint  ActiveProcessLimit;
        public nuint Affinity;
        public uint  PriorityClass;
        public uint  SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS                       IoInfo;
        public nuint                              ProcessMemoryLimit;
        public nuint                              JobMemoryLimit;
        public nuint                              PeakProcessMemoryUsed;
        public nuint                              PeakJobMemoryUsed;
    }
}
