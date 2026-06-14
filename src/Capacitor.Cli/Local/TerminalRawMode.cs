using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Capacitor.Cli.Local;

/// <summary>
/// Puts the controlling terminal into raw mode so keystrokes — including Ctrl-C as the
/// byte <c>0x03</c> — pass straight through to the attached agent's PTY (where the daemon's
/// line discipline turns it into SIGINT for the agent), and restores the original mode on
/// dispose.
///
/// <para>The platform <c>struct termios</c> layout differs between Linux and macOS (field
/// widths, <c>NCCS</c>, presence of <c>c_line</c>), so we never interpret it: we treat it
/// as an opaque, over-sized blob and let libc's <c>cfmakeraw</c> do the raw transform. We
/// only ever round-trip the blob through <c>tcgetattr</c>/<c>tcsetattr</c>.</para>
/// </summary>
internal static partial class TerminalRawMode {
    const int StdinFd         = 0;
    const int TCSANOW         = 0;
    const int TermiosBlobSize = 128; // > sizeof(struct termios) on Linux (~60) and macOS (~72)

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tcgetattr(int fd, byte[] termios);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tcsetattr(int fd, int optionalActions, byte[] termios);

    [LibraryImport("libc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void cfmakeraw(byte[] termios);

    /// <summary>
    /// Enables raw mode and returns a token whose <see cref="IDisposable.Dispose"/> restores
    /// the original mode. If stdin is not a tty (e.g. piped), returns a no-op token so callers
    /// can always wrap their session in a <c>using</c>.
    /// </summary>
    public static IDisposable Enable() {
        if (OperatingSystem.IsWindows()) return new Noop();

        var original = new byte[TermiosBlobSize];
        if (tcgetattr(StdinFd, original) != 0) return new Noop(); // not a tty

        var raw = (byte[])original.Clone();
        cfmakeraw(raw);

        return tcsetattr(StdinFd, TCSANOW, raw) != 0
            ? new Noop()
            : new Restore(original);
    }

    sealed class Restore(byte[] original) : IDisposable {
        bool _done;

        public void Dispose() {
            if (_done) return;

            _done = true;
            tcsetattr(StdinFd, TCSANOW, original);
        }
    }

    sealed class Noop : IDisposable {
        public void Dispose() { }
    }
}
