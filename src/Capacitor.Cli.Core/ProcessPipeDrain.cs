using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Capacitor.Cli.Core;

/// <summary>
/// Reads a redirected process stream on its own thread, up to a character cap, and keeps reading
/// after that so a full pipe cannot stall the child. A pool-scheduled read can stay queued past the
/// caller's grace window and the bytes already in the pipe are then lost. The loop waits in slices,
/// so a stop is observed while a descendant still holds the write end — closing the reader from
/// another thread deadlocks, because the read holds the reader's lock. A leading UTF-8 BOM is
/// dropped; leaving it on the first token makes a version parser reject the line.
/// </summary>
internal static partial class ProcessPipeDrain {
    public static Task<string> Start(StreamReader reader, int cap, CancellationToken stop) =>
        Task.Factory.StartNew(
            () => Read(reader.BaseStream, cap, stop),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    static string Read(Stream stream, int cap, CancellationToken stop) {
        var kept = new StringBuilder();
        var pendingBom = true;
        try {
            if (OperatingSystem.IsWindows()) ReadBlocking(stream, cap, stop, kept, ref pendingBom);
            else ReadPolled(stream, cap, stop, kept, ref pendingBom);
        } catch {
            // The pipe was torn down. Whatever was already read is still the caller's to parse.
        }
        return kept.ToString();
    }

    [SupportedOSPlatform("windows")]
    static void ReadBlocking(Stream stream, int cap, CancellationToken stop, StringBuilder kept, ref bool pendingBom) {
        var handle = PipeHandle(stream);
        var added = false;
        try {
            handle.DangerousAddRef(ref added);
            var buf = new byte[4096];
            var decoder = Encoding.UTF8.GetDecoder();
            var chars = new char[4096];
            while (!stop.IsCancellationRequested) {
                // A blocking Read would ignore stop until the writer closes. Peek, then read only
                // the bytes already in the pipe.
                if (!PeekNamedPipe(handle, IntPtr.Zero, 0, out _, out var avail, out _)) return;
                if (avail == 0) {
                    if (stop.WaitHandle.WaitOne(50)) return;
                    continue;
                }
                var n = stream.Read(buf, 0, Math.Min(buf.Length, avail));
                if (n == 0) return;
                Append(kept, decoder, buf, n, chars, cap, ref pendingBom);
            }
        } finally {
            if (added) handle.DangerousRelease();
        }
    }

    static void ReadPolled(Stream stream, int cap, CancellationToken stop, StringBuilder kept, ref bool pendingBom) {
        var handle = PipeHandle(stream);
        var added = false;
        try {
            // The raw descriptor is only valid while this ref is held. Dispose of the process can
            // close and recycle it; a later poll would then read some other pipe.
            handle.DangerousAddRef(ref added);
            var fd = (int)handle.DangerousGetHandle();
            var buf = new byte[4096];
            var decoder = Encoding.UTF8.GetDecoder();
            var chars = new char[4096];
            while (!stop.IsCancellationRequested) {
                var pfd = new PollFd { Fd = fd, Events = PollIn };
                int rc;
                do {
                    rc = poll(ref pfd, 1, 50);
                } while (rc < 0 && Marshal.GetLastPInvokeError() == Eintr && !stop.IsCancellationRequested);

                if (stop.IsCancellationRequested) return;
                if (rc < 0) return;
                if (rc == 0) continue;
                if ((pfd.REvents & (PollHup | PollErr | PollNval)) != 0 && (pfd.REvents & PollIn) == 0)
                    return;

                var n = read(fd, buf, buf.Length);
                if (n <= 0) return;
                Append(kept, decoder, buf, (int)n, chars, cap, ref pendingBom);
            }
        } finally {
            if (added) handle.DangerousRelease();
        }
    }

    static void Append(StringBuilder kept, Decoder decoder, byte[] buf, int n, char[] chars, int cap, ref bool pendingBom) {
        var count = decoder.GetChars(buf, 0, n, chars, 0);
        var start = 0;
        if (pendingBom && count > 0) {
            pendingBom = false;
            if (chars[0] == '\uFEFF') start = 1;
        }
        var take = count - start;
        var room = cap - kept.Length;
        if (room > 0 && take > 0) kept.Append(chars, start, Math.Min(take, room));
    }

    static SafeHandle PipeHandle(Stream stream) => stream switch {
        System.IO.Pipes.PipeStream pipe => pipe.SafePipeHandle,
        FileStream fs => fs.SafeFileHandle,
        _ => throw new InvalidOperationException(stream.GetType().FullName)
    };

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekNamedPipe(
        SafeHandle handle, IntPtr buffer, int bufferSize, out int bytesRead, out int bytesAvail, out int bytesLeft);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int poll(ref PollFd fds, uint nfds, int timeout);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint read(int fd, byte[] buf, nint count);

    [StructLayout(LayoutKind.Sequential)]
    struct PollFd {
        public int Fd;
        public short Events;
        public short REvents;
    }

    const short PollIn = 0x01;
    const short PollErr = 0x08;
    const short PollHup = 0x10;
    const short PollNval = 0x20;
    const int Eintr = 4;
}
