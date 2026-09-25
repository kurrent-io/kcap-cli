using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Capacitor.Cli.Local;

/// <summary>
/// The Windows console counterpart of termios raw mode. Keystrokes arrive as VT sequences with no
/// line editing, echo or Ctrl-C handling, so Ctrl-C reaches the agent's ConPTY as <c>0x03</c> and
/// arrow keys as escape sequences; output is interpreted as VT, which is what an agent's PTY
/// stream is. Both code pages are UTF-8 for the session, since the frames carry UTF-8 bytes.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsConsoleRawMode {
    const int  StdInputHandle  = -10;
    const int  StdOutputHandle = -11;
    const uint Utf8CodePage    = 65001;

    const uint EnableProcessedInput        = 0x0001;
    const uint EnableLineInput             = 0x0002;
    const uint EnableEchoInput             = 0x0004;
    const uint EnableVirtualTerminalInput  = 0x0200;
    const uint EnableProcessedOutput       = 0x0001;
    const uint EnableVirtualTerminalOutput = 0x0004;
    const uint DisableNewlineAutoReturn    = 0x0008;

    static nint Input  => GetStdHandle(StdInputHandle);
    static nint Output => GetStdHandle(StdOutputHandle);

    public static IDisposable Enable() {
        var input = Input;
        if (!GetConsoleMode(input, out var inMode)) return new Restore(null, null, 0, 0); // not a console

        var output  = Output;
        var outMode = GetConsoleMode(output, out var m) ? m : (uint?)null;
        var inputCp  = GetConsoleCP();
        var outputCp = GetConsoleOutputCP();

        SetConsoleMode(input, (inMode & ~(EnableProcessedInput | EnableLineInput | EnableEchoInput)) | EnableVirtualTerminalInput);
        if (outMode is { } o) SetConsoleMode(output, o | EnableProcessedOutput | EnableVirtualTerminalOutput | DisableNewlineAutoReturn);
        SetConsoleCP(Utf8CodePage);
        SetConsoleOutputCP(Utf8CodePage);

        return new Restore(inMode, outMode, inputCp, outputCp);
    }

    public static unsafe int ReadStdin(byte[] buf) {
        fixed (byte* p = buf) {
            return ReadFile(Input, p, (uint)buf.Length, out var read, 0) ? (int)read : 0;
        }
    }

    public static unsafe void WriteStdout(byte[] data, int length) {
        var output = Output;
        var off = 0;
        fixed (byte* p = data) {
            while (off < length) {
                if (!WriteFile(output, p + off, (uint)(length - off), out var written, 0) || written == 0) break;
                off += (int)written;
            }
        }
    }

    sealed class Restore(uint? inMode, uint? outMode, uint inputCp, uint outputCp) : IDisposable {
        bool _done;

        public void Dispose() {
            if (_done || inMode is null) return;
            _done = true;

            SetConsoleMode(Input, inMode.Value);
            if (outMode is { } o) SetConsoleMode(Output, o);
            if (inputCp != 0) SetConsoleCP(inputCp);
            if (outputCp != 0) SetConsoleOutputCP(outputCp);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint handle, out uint mode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(nint handle, uint mode);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetConsoleCP();

    [LibraryImport("kernel32.dll")]
    private static partial uint GetConsoleOutputCP();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCP(uint codePage);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleOutputCP(uint codePage);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReadFile(nint handle, byte* buffer, uint count, out uint read, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool WriteFile(nint handle, byte* buffer, uint count, out uint written, nint overlapped);
}
