using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Capacitor.Cli;

static partial class ProcessHelpers {
    const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    const uint CREATE_NO_WINDOW           = 0x08000000;
    const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    const int  STARTF_USESTDHANDLES         = 0x00000100;

    const nint PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;

    /// <summary>
    /// Starts a detached child that inherits NO handle from this process, returning its pid.
    /// Throws <see cref="Win32Exception"/> if the spawn is refused. Windows only — callers keep the
    /// <see cref="System.Diagnostics.Process"/> path everywhere else.
    /// </summary>
    /// <remarks>
    /// <see cref="PreventInheritedHandles"/> cannot close this leak. It clears
    /// HANDLE_FLAG_INHERIT on the three handles <c>GetStdHandle</c> reports, but a coding
    /// agent invokes a hook holding more inheritable handles than those three — further
    /// copies of its own pipes, under values this process cannot enumerate — and
    /// <c>CreateProcess</c> inherits every one of them. Measured against Codex: a watcher
    /// spawned from a hook held the agent's stdout pipe open for the watcher's whole
    /// lifetime, so the agent's read of the hook's stdout never reached EOF and it abandoned
    /// SessionStart and Stop at its hook timeout, ~33s apiece, on every turn. The std-handle
    /// clear verifiably applies and does not help; dropping the stream redirection does not
    /// help either, because <see cref="System.Diagnostics.Process"/> asks for inheritance
    /// regardless and offers no way to decline it. Hence <c>CreateProcess</c> directly.
    ///
    /// <para>The child is left with null standard handles, which is what a detached worker
    /// wants: it logs to its own file and must never write onto the agent's console.</para>
    /// </remarks>
    internal static unsafe int? StartDetachedWindows(ProcessStartInfo startInfo) {
        if (!OperatingSystem.IsWindows()) {
            return null;
        }

        var commandLine = Terminated(BuildCommandLine(startInfo));
        var environment = Terminated(BuildEnvironmentBlock(startInfo.Environment));

        var workingDirectory = startInfo.WorkingDirectory is { Length: > 0 } directory ? directory : null;
        var startup          = new StartupInfoW { cb = sizeof(StartupInfoW) };
        var created          = default(ProcessInformation);

        fixed (char* commandLinePtr = commandLine)
        fixed (char* environmentPtr = environment) {
            if (!CreateProcessW(
                    startInfo.FileName, commandLinePtr, 0, 0, bInheritHandles: false,
                    CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT, environmentPtr,
                    workingDirectory, &startup, &created)) {
                // Read the error before any other interop call can overwrite it. Callers log
                // inside a catch-all, so this exception is the only route by which the OS's
                // reason — bad image, missing working directory, access denied — reaches them.
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }

        CloseHandle(created.hProcess);
        CloseHandle(created.hThread);

        return created.dwProcessId;
    }

    /// <summary>
    /// Starts a detached child holding exactly ONE of this process's handles — the read end of a
    /// fresh pipe, wired to its stdin — and returns its pid with the writing end. Throws
    /// <see cref="Win32Exception"/> if the spawn is refused. Windows only.
    /// </summary>
    /// <remarks>
    /// A caller that must hand the child a payload cannot use <see cref="StartDetachedWindows"/>:
    /// an anonymous pipe reaches a child only by being inherited, and that spawn inherits nothing.
    /// <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c> is the seam between the two — it narrows inheritance
    /// to a named set, so the pipe crosses while the agent's own handles, the ones that keep its
    /// read of the hook's output from reaching EOF, do not.
    /// </remarks>
    internal static unsafe (int Pid, Stream StandardInput) StartDetachedWindowsWithStdin(ProcessStartInfo startInfo) {
        var security = new SecurityAttributes {
            nLength        = sizeof(SecurityAttributes),
            bInheritHandle = 1
        };

        if (!CreatePipe(out var childEnd, out var parentEnd, &security, 0)) {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var attributeList = nint.Zero;

        try {
            // CreatePipe made BOTH ends inheritable. A child holding the writing end too would
            // keep the pipe from ever reaching EOF, so its own copy is withdrawn here.
            if (!SetHandleInformation(parentEnd, HANDLE_FLAG_INHERIT, 0)) {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var size = nuint.Zero;
            InitializeProcThreadAttributeList(nint.Zero, 1, 0, ref size); // sizing call: fails by design
            attributeList = Marshal.AllocHGlobal((nint)size);

            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size)) {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var inheritable = childEnd;

            if (!UpdateProcThreadAttribute(attributeList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                                           &inheritable, (nuint)sizeof(nint), nint.Zero, nint.Zero)) {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var commandLine      = Terminated(BuildCommandLine(startInfo));
            var environment      = Terminated(BuildEnvironmentBlock(startInfo.Environment));
            var workingDirectory = startInfo.WorkingDirectory is { Length: > 0 } directory ? directory : null;

            var startup = new StartupInfoExW {
                StartupInfo = {
                    cb        = sizeof(StartupInfoExW),
                    dwFlags   = STARTF_USESTDHANDLES,
                    hStdInput = childEnd
                },
                lpAttributeList = attributeList
            };

            var created = default(ProcessInformation);

            fixed (char* commandLinePtr = commandLine)
            fixed (char* environmentPtr = environment) {
                // bInheritHandles must be true for the attribute list to be consulted at all; the
                // list is what keeps that from meaning "every inheritable handle".
                if (!CreateProcessW(
                        startInfo.FileName, commandLinePtr, 0, 0, bInheritHandles: true,
                        EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
                        environmentPtr, workingDirectory, (StartupInfoW*)&startup, &created)) {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }

            CloseHandle(created.hProcess);
            CloseHandle(created.hThread);

            var standardInput = new FileStream(new SafeFileHandle(parentEnd, ownsHandle: true), FileAccess.Write);
            parentEnd = nint.Zero; // the stream owns it now

            return (created.dwProcessId, standardInput);
        } finally {
            if (attributeList != nint.Zero) {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            // This process's copy of the child's end goes either way: while it stays open the
            // child's stdin has a second writer and never sees EOF.
            CloseHandle(childEnd);

            if (parentEnd != nint.Zero) {
                CloseHandle(parentEnd);
            }
        }
    }

    static char[] Terminated(string value) {
        var buffer = new char[value.Length + 1];
        value.CopyTo(buffer);

        return buffer;
    }

    /// <summary>
    /// argv[0] followed by the caller's arguments. A <see cref="ProcessStartInfo"/> carries
    /// its arguments EITHER pre-joined in <see cref="ProcessStartInfo.Arguments"/> — already
    /// quoted by whoever built it — or as discrete
    /// <see cref="ProcessStartInfo.ArgumentList"/> entries that still need quoting.
    /// </summary>
    static string BuildCommandLine(ProcessStartInfo startInfo) {
        var commandLine = new StringBuilder();
        AppendQuoted(commandLine, startInfo.FileName);

        if (!string.IsNullOrEmpty(startInfo.Arguments)) {
            return commandLine.Append(' ').Append(startInfo.Arguments).ToString();
        }

        foreach (var argument in startInfo.ArgumentList) {
            AppendQuoted(commandLine.Append(' '), argument);
        }

        return commandLine.ToString();
    }

    /// <summary>
    /// Quotes one argument the way <c>CommandLineToArgvW</c> parses it back: a run of
    /// backslashes is doubled only when it precedes a quote (or the closing quote), and an
    /// embedded quote is escaped. Whoever consumes the command line splits it with those
    /// rules, so an argument holding spaces or quotes must be written to match them.
    /// </summary>
    static void AppendQuoted(StringBuilder commandLine, string argument) {
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0) {
            commandLine.Append(argument);

            return;
        }

        commandLine.Append('"');

        for (var index = 0; index < argument.Length; index++) {
            var backslashes = 0;

            while (index < argument.Length && argument[index] == '\\') {
                backslashes++;
                index++;
            }

            if (index == argument.Length) {
                commandLine.Append('\\', backslashes * 2);

                break;
            }

            if (argument[index] == '"') {
                commandLine.Append('\\', backslashes * 2 + 1).Append('"');
            } else {
                commandLine.Append('\\', backslashes).Append(argument[index]);
            }
        }

        commandLine.Append('"');
    }

    /// <summary>
    /// The CREATE_UNICODE_ENVIRONMENT block: NAME=VALUE runs separated by NULs, the whole
    /// terminated by one more. <see cref="ProcessStartInfo.Environment"/> starts out as a
    /// copy of this process's environment, so a caller's additions are layered over it and
    /// nothing is dropped by passing an explicit block.
    /// </summary>
    static string BuildEnvironmentBlock(IDictionary<string, string?> environment) {
        var block = new StringBuilder();

        foreach (var (name, value) in environment.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)) {
            if (string.IsNullOrEmpty(name)) {
                continue;
            }

            block.Append(name).Append('=').Append(value).Append('\0');
        }

        return block.Append('\0').ToString();
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true,
                   StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessW(
        string?             lpApplicationName,
        char*               lpCommandLine,
        nint                lpProcessAttributes,
        nint                lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint                dwCreationFlags,
        char*               lpEnvironment,
        string?             lpCurrentDirectory,
        StartupInfoW*       lpStartupInfo,
        ProcessInformation* lpProcessInformation);

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfoW {
        public int   cb;
        public nint  lpReserved;
        public nint  lpDesktop;
        public nint  lpTitle;
        public int   dwX;
        public int   dwY;
        public int   dwXSize;
        public int   dwYSize;
        public int   dwXCountChars;
        public int   dwYCountChars;
        public int   dwFillAttribute;
        public int   dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint  lpReserved2;
        public nint  hStdInput;
        public nint  hStdOutput;
        public nint  hStdError;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreatePipe(
        out nint hReadPipe, out nint hWritePipe, SecurityAttributes* lpPipeAttributes, uint nSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(
        nint lpAttributeList, int dwAttributeCount, int dwFlags, ref nuint lpSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool UpdateProcThreadAttribute(
        nint lpAttributeList, uint dwFlags, nint attribute, void* lpValue, nuint cbSize,
        nint lpPreviousValue, nint lpReturnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint lpAttributeList);

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes {
        public int  nLength;
        public nint lpSecurityDescriptor;
        public int  bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfoExW {
        public StartupInfoW StartupInfo;
        public nint         lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation {
        public nint hProcess;
        public nint hThread;
        public int  dwProcessId;
        public int  dwThreadId;
    }
}
