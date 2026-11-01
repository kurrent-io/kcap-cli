using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Capacitor.Cli.Services;

/// <summary>Ctrl+Break for every process on another process's console: the one stop a console process cannot opt out
/// of, and the one the .NET host takes as a graceful shutdown.</summary>
[SupportedOSPlatform("windows")]
static unsafe partial class WindowsConsoleBreak {
    const uint CtrlBreakEvent = 1;

    /// <summary>Leaves this process's own console for <paramref name="pid"/>'s, so only a process with nothing more to
    /// write to its console may call it: a helper whose output is redirected, never the command a user is watching.</summary>
    public static string? Send(int pid) {
        FreeConsole();

        if (!AttachConsole((uint)pid)) return $"could not attach to the console of process {pid} (error {Marshal.GetLastPInvokeError()})";

        // The event reaches this process too, and the default handler would end it before it reports.
        if (!SetConsoleCtrlHandler(&Swallow, true)) return $"could not keep the break from ending the sender (error {Marshal.GetLastPInvokeError()})";

        return GenerateConsoleCtrlEvent(CtrlBreakEvent, 0) ? null : $"Ctrl+Break could not be sent (error {Marshal.GetLastPInvokeError()})";
    }

    [UnmanagedCallersOnly]
    static int Swallow(uint ctrlType) => 1;

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(delegate* unmanaged<uint, int> handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}
