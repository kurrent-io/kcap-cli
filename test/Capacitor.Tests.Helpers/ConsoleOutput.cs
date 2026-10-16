using System.Text;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// Captures what the code under test writes to <see cref="Console.Out"/> / <see cref="Console.Error"/>
/// and restores the originals on dispose.
/// <para>
/// Console is process-global, so one capture overlapping another silently swallows the other's
/// output — the hazard TUnit0055 warns about. Every caller must be <c>[NotInParallel]</c> with NO
/// group: a named group only serialises within itself, and captures elsewhere in the suite would
/// still overlap. That is enforced here rather than trusted — an overlapping capture throws instead
/// of quietly corrupting both readings.
/// </para>
/// </summary>
public sealed class ConsoleOutput : IDisposable {
    static int Active;

    readonly TextWriter?    _originalOut;
    readonly TextWriter?    _originalError;
    readonly CapturedWriter _out;
    readonly CapturedWriter _error;

    bool _disposed;

    ConsoleOutput(bool stdout, bool stderr, string? newLine) {
        // Callers that assert on exact output pass "\n": StringWriter otherwise inherits
        // Environment.NewLine, which is "\r\n" on Windows and would only ever fail there.
        _out   = new CapturedWriter(newLine);
        _error = new CapturedWriter(newLine);

        if (Interlocked.Exchange(ref Active, 1) == 1)
            throw new InvalidOperationException(
                "A Console capture is already active. Console is process-global, so mark the test " +
                "[NotInParallel] with no group so it cannot overlap another capture.");

        // The only Console.Set* calls in the suite, which is the point: one place to reason about,
        // guarded above, restored below.
#pragma warning disable TUnit0055
        if (stdout) {
            _originalOut = Console.Out;
            Console.SetOut(_out);
        }

        if (stderr) {
            _originalError = Console.Error;
            Console.SetError(_error);
        }
#pragma warning restore TUnit0055
    }

    /// <summary>Captures stdout only.</summary>
    public static ConsoleOutput StartCapture(string? newLine = null) => new(stdout: true, stderr: false, newLine);

    /// <summary>Captures stderr only.</summary>
    public static ConsoleOutput StartErrorCapture(string? newLine = null) => new(stdout: false, stderr: true, newLine);

    /// <summary>Captures both streams, kept separate.</summary>
    public static ConsoleOutput StartFullCapture(string? newLine = null) => new(stdout: true, stderr: true, newLine);

    /// <summary>Readable both during the capture and after disposal.</summary>
    public string GetCapturedOutput() => _out.Snapshot();

    /// <inheritdoc cref="GetCapturedOutput"/>
    public string GetCapturedError() => _error.Snapshot();

    public void Dispose() {
        if (_disposed)
            return;

        _disposed = true;

#pragma warning disable TUnit0055
        if (_originalOut is not null)
            Console.SetOut(_originalOut);

        if (_originalError is not null)
            Console.SetError(_originalError);
#pragma warning restore TUnit0055

        Interlocked.Exchange(ref Active, 0);
    }

    // StringBuilder.ToString walks its chunks while a concurrent append links a new one and throws
    // ArgumentOutOfRangeException (chunkLength). Every write and every snapshot take _gate.
    sealed class CapturedWriter : TextWriter {
        readonly StringWriter _inner;
        readonly object       _gate = new();

        public CapturedWriter(string? newLine) {
            _inner = newLine is null ? new StringWriter() : new StringWriter { NewLine = newLine };
            // Parameterless WriteLine() reads this writer's newline, not the inner one's.
            if (newLine is not null)
                NewLine = newLine;
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value) {
            lock (_gate) _inner.Write(value);
        }

        public override void Write(string? value) {
            lock (_gate) _inner.Write(value);
        }

        public override void Write(char[] buffer, int index, int count) {
            lock (_gate) _inner.Write(buffer, index, count);
        }

        public override void Write(ReadOnlySpan<char> buffer) {
            lock (_gate) _inner.Write(buffer);
        }

        public override void WriteLine(string? value) {
            lock (_gate) _inner.WriteLine(value);
        }

        public string Snapshot() {
            lock (_gate) return _inner.ToString();
        }
    }
}
