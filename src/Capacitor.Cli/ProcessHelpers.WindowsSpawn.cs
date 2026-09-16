using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Capacitor.Cli;

static partial class ProcessHelpers {
    const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    const uint CREATE_NO_WINDOW           = 0x08000000;

    /// <summary>
    /// Starts a detached child that inherits NO handle from this process, returning its pid
    /// (null if the spawn failed). Windows only — callers keep the
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
                return null;
            }
        }

        CloseHandle(created.hProcess);
        CloseHandle(created.hThread);

        return created.dwProcessId;
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

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation {
        public nint hProcess;
        public nint hThread;
        public int  dwProcessId;
        public int  dwThreadId;
    }
}
