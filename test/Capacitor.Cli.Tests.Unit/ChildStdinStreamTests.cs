using System.Diagnostics;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The detached start hands its caller a stream and no way to reach the <see cref="Process"/>
/// wrapper behind it, so closing the stream has to be what releases that wrapper — and must not
/// take the child with it, which is the whole point of spawning detached.
/// </summary>
public class ChildStdinStreamTests {
    /// <summary>
    /// The child must outlive stdin EOF for a survival assertion to mean anything: one that exits
    /// when its input closes would be racing the assertion rather than answering it.
    /// </summary>
    static ProcessStartInfo IgnoresItsStdin() {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
            : new ProcessStartInfo("/bin/sh", "-c \"sleep 30\"");

        psi.UseShellExecute        = false;
        psi.CreateNoWindow         = true;
        psi.RedirectStandardInput  = true;
        psi.RedirectStandardOutput = true;

        return psi;
    }

    static bool IsReleased(Process owner) {
        try {
            _ = owner.Handle;

            return false;
        } catch (InvalidOperationException) {
            return true; // the wrapper has let go of the process it was associated with
        }
    }

    [Test]
    public async Task Closing_the_stream_releases_the_wrapper_and_leaves_the_child_running() {
        var child    = Process.Start(IgnoresItsStdin())!;
        var pid      = child.Id;
        var identity = PidIdentity.Capture(pid);

        try {
            // The production relationship: touching StandardInput is what marks the pipe
            // caller-owned, so Process.Close leaves it alone and this stream is its only owner.
            var pipe   = child.StandardInput.BaseStream;
            var stream = new ChildStdinStream(pipe, child);

            stream.Write("payload"u8);
            stream.Dispose();

            await Assert.That(pipe.CanWrite).IsFalse();
            await Assert.That(IsReleased(child)).IsTrue();
            await Assert.That(PidIdentity.IsGone(pid, identity)).IsFalse();
        } finally {
            Kill(pid);
        }
    }

    /// <summary>
    /// The exceptional path the <c>finally</c> exists for: a pipe close can fail with buffered bytes
    /// and a child that already exited, and the process handle must still be released.
    /// </summary>
    [Test]
    public async Task The_owner_is_released_even_when_closing_the_pipe_throws() {
        var child = Process.Start(IgnoresItsStdin())!;
        var pid   = child.Id;
        var pipe  = new CountingStream { ThrowOnDispose = true };

        try {
            var stream = new ChildStdinStream(pipe, child);

            Assert.Throws<IOException>(stream.Dispose);

            await Assert.That(pipe.SyncDisposals).IsEqualTo(1);
            await Assert.That(IsReleased(child)).IsTrue();
        } finally {
            Kill(pid);
        }
    }

    /// <summary>
    /// Pins the disposal gate. <c>base.DisposeAsync()</c> routes back through
    /// <c>Dispose(bool)</c>, so without the gate an async close would release the pipe a second
    /// time down the synchronous path.
    /// </summary>
    [Test]
    public async Task An_async_close_does_not_release_a_second_time_through_the_sync_path() {
        var child = Process.Start(IgnoresItsStdin())!;
        var pid   = child.Id;
        var pipe  = new CountingStream();

        try {
            var stream = new ChildStdinStream(pipe, child);

            await stream.DisposeAsync();

            await Assert.That(pipe.AsyncDisposals).IsEqualTo(1);
            await Assert.That(pipe.SyncDisposals).IsEqualTo(0);
            await Assert.That(IsReleased(child)).IsTrue();
        } finally {
            Kill(pid);
        }
    }

    /// <summary>Repeated and mixed closes release exactly once, in whichever form came first.</summary>
    [Test]
    public async Task Mixed_and_repeated_disposal_releases_exactly_once() {
        var child = Process.Start(IgnoresItsStdin())!;
        var pid   = child.Id;
        var pipe  = new CountingStream();

        try {
            var stream = new ChildStdinStream(pipe, child);

            stream.Dispose();
            await stream.DisposeAsync();
            stream.Dispose();

            await Assert.That(pipe.SyncDisposals).IsEqualTo(1);
            await Assert.That(pipe.AsyncDisposals).IsEqualTo(0);
        } finally {
            Kill(pid);
        }
    }

    static void Kill(int pid) {
        try {
            using var running = Process.GetProcessById(pid);
            running.Kill(entireProcessTree: true);
        } catch { }
    }

    /// <summary>
    /// Counts how each disposal form reached the wrapped pipe, and can fail the way a pipe to a
    /// departed child does. A real <see cref="FileStream"/> reports neither, and tolerates repeated
    /// closes silently, so it cannot tell a released-once wrapper from a released-twice one.
    /// </summary>
    sealed class CountingStream : Stream {
        public int  SyncDisposals  { get; private set; }
        public int  AsyncDisposals { get; private set; }
        public bool ThrowOnDispose { get; init; }

        public override bool CanRead  => false;
        public override bool CanSeek  => false;
        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override void Write(byte[] buffer, int offset, int count) { }

        public override int  Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin)       => throw new NotSupportedException();
        public override void SetLength(long value)                      => throw new NotSupportedException();

        protected override void Dispose(bool disposing) {
            if (disposing) {
                SyncDisposals++;

                if (ThrowOnDispose) throw new IOException("the pipe is gone");
            }

            base.Dispose(disposing);
        }

        // The double deliberately does NOT route back through Dispose the way Stream's own
        // DisposeAsync does: the counts are how a test tells which path ran, and a round trip here
        // would report the double's own dispatch as the subject's.
#pragma warning disable CA2215
        public override ValueTask DisposeAsync() {
#pragma warning restore CA2215
            AsyncDisposals++;

            return ThrowOnDispose
                ? ValueTask.FromException(new IOException("the pipe is gone"))
                : ValueTask.CompletedTask;
        }
    }
}
