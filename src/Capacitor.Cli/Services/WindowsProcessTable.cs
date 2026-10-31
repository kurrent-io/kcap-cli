using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Capacitor.Cli.Services;

/// One process in a Toolhelp snapshot: its parent and its image file name.
record WindowsProcessEntry(int ParentPid, string ExeName);

/// A point-in-time map of every process to its parent, from one Toolhelp snapshot.
[SupportedOSPlatform("windows")]
static unsafe partial class WindowsProcessTable {
    public static IReadOnlyDictionary<int, WindowsProcessEntry> Snapshot() {
        var table = new Dictionary<int, WindowsProcessEntry>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle) return table;

        try {
            var entry = new ProcessEntry32W { dwSize = (uint)sizeof(ProcessEntry32W) };
            for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry)) {
                table[(int)entry.th32ProcessID] = new WindowsProcessEntry((int)entry.th32ParentProcessID, new string(entry.szExeFile));
            }
        } finally {
            CloseHandle(snapshot);
        }

        return table;
    }

    /// <summary>The path the process's image was opened by, a link in it unresolved; null when the process is gone or
    /// not this user's to query.</summary>
    public static string? ImagePath(int pid) {
        var process = OpenProcess(QueryLimitedInformation, false, (uint)pid);
        if (process == 0) return null;

        try {
            var buffer = new char[MaxPath];
            var size   = (uint)buffer.Length;
            fixed (char* chars = buffer)
                return QueryFullProcessImageNameW(process, 0, chars, ref size) ? new string(chars, 0, (int)size) : null;
        } finally {
            CloseHandle(process);
        }
    }

    const uint QueryLimitedInformation = 0x1000;
    const int  MaxPath                 = 32768;

    const uint SnapProcess = 0x00000002;
    static readonly nint InvalidHandle = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry32W {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        public fixed char szExeFile[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(nint snapshot, ref ProcessEntry32W entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(nint snapshot, ref ProcessEntry32W entry);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(nint process, uint flags, char* buffer, ref uint size);
}
