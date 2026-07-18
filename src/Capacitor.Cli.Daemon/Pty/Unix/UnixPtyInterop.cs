using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Capacitor.Cli.Daemon.Pty.Unix;

internal static partial class UnixPtyInterop {
    static readonly bool IsMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    // forkpty: glibc 2.34+ consolidated libutil into libc, and Ubuntu 24.04 ships only
    // libutil.so.1 (no unversioned libutil.so), so DllImport("libutil") fails to resolve.
    // libc works on Linux (glibc 2.34+) and on macOS (libSystem re-exports forkpty).
    [LibraryImport("libc", EntryPoint = "forkpty", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int forkpty(out int master, IntPtr name, IntPtr termp, ref WinSize winp);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int execvp(string file, string?[] argv);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int chdir(string path);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int setenv(string name, string value, int overwrite);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int unsetenv(string name);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int kill(int pid, int sig);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int waitpid(int pid, out int status, int options);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint read(int fd, byte[] buf, nint count);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint write(int fd, byte[] buf, nint count);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int poll(ref PollFd fds, uint nfds, int timeout);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ioctl(int fd, ulong request, ref WinSize ws);

    [LibraryImport("libc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void _exit(int status);

    // On macOS ARM64, ioctl is variadic and P/Invoke doesn't work correctly.
    // Use the native shim if available.
    [LibraryImport("libpty_shim", EntryPoint = "pty_set_winsize", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int pty_set_winsize_shim(int fd, ushort rows, ushort cols);

    [LibraryImport("libpty_shim", EntryPoint = "pty_probe_execveat")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int pty_probe_execveat();

    // argv/envp are NULL-terminated string arrays; a `string?[]` with a trailing `null` element
    // marshals correctly via LibraryImport's array marshaller (mirrors the existing execvp import).
    [LibraryImport("libpty_shim", EntryPoint = "pty_preflight", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int pty_preflight(
        string exeAbsPath, string?[] origArgv, string?[] envp, int execveatSupported, out IntPtr outPlan);

    [LibraryImport("libpty_shim", EntryPoint = "pty_plan_contained")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int pty_plan_contained(IntPtr plan);

    [LibraryImport("libpty_shim", EntryPoint = "pty_plan_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void pty_plan_free(ref IntPtr plan);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int pipe(int[] fds);

    // forkpty + child sequence + error-pipe handshake (L1-shim(b)). `plan` is the opaque
    // pty_exec_plan* from pty_preflight; envp/cwd cross the boundary the same way argv does
    // for pty_preflight — a NULL-terminated `string?[]`/UTF-8 string, no length prefix.
    [LibraryImport("libpty_shim", EntryPoint = "pty_spawn", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int pty_spawn(
        IntPtr plan, string?[] envp, string cwd, ushort rows, ushort cols,
        int expectedParent, int cancelFd, out PtySpawnResult result);

    // macOS-only export (see pty_shim.h) — no compile-time guard needed on the C# side, since
    // LibraryImport resolution is lazy/per-call; callers must gate with OperatingSystem.IsMacOS().
    [LibraryImport("libpty_shim", EntryPoint = "pty_capture_mac_identity", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int pty_capture_mac_identity(int pid, byte* out_, nuint outlen);

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct PtySpawnResult {
        public int  Pid;
        public int  MasterFd;
        public int  ErrNo;
        public int  FailedStep;
        public fixed byte StartIdentity[128];

        public readonly string StartIdentityString {
            get {
                fixed (byte* p = StartIdentity) {
                    var len = 0;
                    while (len < 128 && p[len] != 0) len++;
                    return System.Text.Encoding.UTF8.GetString(p, len);
                }
            }
        }
    }

    public static void SetWinSize(int fd, ushort rows, ushort cols) {
        if (IsMacOS && RuntimeInformation.OSArchitecture == Architecture.Arm64) {
            // Use C shim on macOS ARM64 (ioctl variadic ABI issue)
            pty_set_winsize_shim(fd, rows, cols);
        } else {
            // Direct ioctl on Linux x64 (works fine)
            var ws = new WinSize { ws_row = rows, ws_col = cols };

            const ulong TIOCSWINSZ = 0x5414; // Linux value
            ioctl(fd, TIOCSWINSZ, ref ws);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WinSize {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd {
        public int   fd;
        public short events;
        public short revents;
    }

    public const short POLLIN   = 0x01;
    public const short POLLHUP  = 0x10;
    public const short POLLERR  = 0x08;
    public const int   WNOHANG  = 1;
    public const int   SIGTERM = 15;
    public const int   SIGKILL = 9;
    public const int   SIGINT  = 2;
}
