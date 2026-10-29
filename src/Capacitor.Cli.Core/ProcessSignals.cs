using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Capacitor.Cli.Core;

public static partial class ProcessSignals {
    const int SIGINT = 2;

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int kill_native(int pid, int sig);

    /// Sends SIGINT to <paramref name="pid"/>. Always false on Windows, where a console child has no equivalent.
    public static bool Interrupt(int pid) => !OperatingSystem.IsWindows() && pid > 1 && kill_native(pid, SIGINT) == 0;
}
