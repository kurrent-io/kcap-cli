using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Covers the one detached spawn that must pass a handle to its child: the session-end hand-off
/// feeds the continuation through stdin, and an anonymous pipe reaches a child only by being
/// inherited. The spawn therefore cannot refuse inheritance outright the way the others do — it
/// names the single pipe handle instead, and these pin both halves of that: the payload arrives,
/// and nothing else of this process's does.
///
/// Windows-only: handle inheritance is a Windows mechanism, and off Windows the CLOEXEC sweep
/// covers the same ground (<c>ProcessHelpersUnixFdCloexecTests</c>).
/// </summary>
public class ProcessHelpersDetachedStdinTests {
    const uint HANDLE_FLAG_INHERIT = 0x00000001;

    static string Comspec => Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

    [Test]
    public async Task The_payload_reaches_the_childs_stdin() {
        Skip.Unless(OperatingSystem.IsWindows(), "handle inheritance is a Windows mechanism");

        using var tmp  = new TempDir();
        var       sink = tmp.PathTo("payload.txt");

        var psi = new ProcessStartInfo(Comspec) { Arguments = $"/c more > \"{sink}\"" };

        var (pid, standardInput) = ProcessHelpers.StartDetachedWindowsWithStdin(psi);

        try {
            using (var writer = new StreamWriter(standardInput)) {
                writer.Write("""{"hook_event_name":"SessionEnd","session_id":"abc"}""");
            }

            var deadline = DateTime.UtcNow.AddSeconds(10);

            while (DateTime.UtcNow < deadline && (!File.Exists(sink) || new FileInfo(sink).Length == 0)) {
                await Task.Delay(50);
            }

            await Assert.That(File.ReadAllText(sink).Trim())
                        .IsEqualTo("""{"hook_event_name":"SessionEnd","session_id":"abc"}""");
        } finally {
            Kill(pid);
        }
    }

    /// <summary>
    /// The leak this spawn exists to avoid, reproduced directly: an inheritable pipe standing in for
    /// the one a coding agent reads its hook's output from. Once this process drops its own write
    /// end, a read of the other end reports EOF — unless a child is holding a copy, which is exactly
    /// the condition that leaves an agent waiting on a hook that has already returned.
    /// </summary>
    [Test]
    public async Task The_child_inherits_the_payload_pipe_and_nothing_else() {
        Skip.Unless(OperatingSystem.IsWindows(), "handle inheritance is a Windows mechanism");

        var security = new SecurityAttributes {
            nLength              = Marshal.SizeOf<SecurityAttributes>(),
            lpSecurityDescriptor = 0,
            bInheritHandle       = 1
        };

        await Assert.That(CreatePipe(out var probeRead, out var probeWrite, ref security, 0)).IsTrue();

        var probeClosed = false;

        try {
            await Assert.That(GetHandleInformation(probeWrite, out var flags)).IsTrue();
            await Assert.That(flags & HANDLE_FLAG_INHERIT).IsEqualTo(HANDLE_FLAG_INHERIT);

            // `more` with no redirect sits on its stdin, so the child is alive across the probe.
            var psi = new ProcessStartInfo(Comspec) { Arguments = "/c more" };

            var (pid, standardInput) = ProcessHelpers.StartDetachedWindowsWithStdin(psi);

            try {
                CloseHandle(probeWrite);
                probeClosed = true;

                using var probe  = new FileStream(new SafeFileHandle(probeRead, ownsHandle: true), FileAccess.Read);
                var       buffer = new byte[1];
                var       read   = probe.ReadAsync(buffer).AsTask();

                // A read that never settles IS the failure: it means a child is still holding the
                // write end, so the assertion is on completion first and EOF second.
                var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10)));

                await Assert.That(finished == read).IsTrue();
                await Assert.That(await read).IsEqualTo(0);

                // The payload pipe is the one handle that DID cross, so it still works.
                using var writer = new StreamWriter(standardInput);
                writer.Write("x");
            } finally {
                Kill(pid);
            }
        } finally {
            if (!probeClosed) CloseHandle(probeWrite);
        }
    }

    static void Kill(int pid) {
        try {
            using var child = Process.GetProcessById(pid);
            child.Kill(entireProcessTree: true);
        } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes {
        public int  nLength;
        public nint lpSecurityDescriptor;
        public int  bInheritHandle; // BOOL — int keeps the struct blittable
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreatePipe(out nint hReadPipe, out nint hWritePipe, ref SecurityAttributes lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetHandleInformation(nint hObject, out uint lpdwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(nint hObject);
}
