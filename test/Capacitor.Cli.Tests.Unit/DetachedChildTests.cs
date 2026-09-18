using System.Diagnostics;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The detached start hands its caller a child that owns both its stdin pipe and its own process
/// handle, so disposing it has to release both — and must not take the child process with it, which
/// is the whole point of spawning detached. <see cref="DetachedChild.Terminate"/> is the one path
/// that does kill it, and it goes through the retained handle rather than the child's reusable pid.
/// </summary>
public class DetachedChildTests {
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

    static void Kill(int pid) {
        try {
            using var running = Process.GetProcessById(pid);
            running.Kill(entireProcessTree: true);
        } catch { }
    }

    [Test]
    public async Task Disposing_releases_the_handle_and_leaves_the_child_running() {
        var process  = Process.Start(IgnoresItsStdin())!;
        var pid      = process.Id;
        var identity = PidIdentity.Capture(pid);

        try {
            // Touching StandardInput is what marks the pipe caller-owned, so Process.Close leaves it
            // alone and the returned child is its only owner.
            var child = DetachedChild.ForProcess(process, process.StandardInput.BaseStream);

            child.StandardInput.Write("payload"u8);
            child.Dispose();

            await Assert.That(IsReleased(process)).IsTrue();
            await Assert.That(PidIdentity.IsGone(pid, identity)).IsFalse();
        } finally {
            Kill(pid);
        }
    }

    /// <summary>
    /// Disposing has to reach the child as EOF, or the continuation waits forever on a payload that
    /// is already complete. `cat` and `more` both read until EOF and then exit, so the child going
    /// away is the delivery proof — and it does not depend on how a platform reports a disposed
    /// stream's state.
    /// </summary>
    [Test]
    public async Task Disposing_delivers_eof_to_the_child() {
        using var tmp  = new TempDir();
        var       sink = tmp.PathTo("payload.txt");

        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", $"/c more > \"{sink}\"")
            : new ProcessStartInfo("/bin/sh", $"-c \"cat > '{sink}'\"");

        psi.UseShellExecute       = false;
        psi.CreateNoWindow        = true;
        psi.RedirectStandardInput = true;

        var process  = Process.Start(psi)!;
        var pid      = process.Id;
        var identity = PidIdentity.Capture(pid);

        try {
            var child = DetachedChild.ForProcess(process, process.StandardInput.BaseStream);

            using (var payload = new StreamWriter(child.StandardInput)) {
                payload.Write("hook-payload");
            }

            child.Dispose();

            await PidIdentity.WaitUntilGoneAsync(pid, identity, TimeSpan.FromSeconds(15));
            await Assert.That(File.ReadAllText(sink).Trim()).IsEqualTo("hook-payload");
        } finally {
            Kill(pid);
        }
    }

    /// <summary>
    /// The exceptional path the <c>finally</c> exists for: a pipe close can fail with buffered bytes
    /// and a child that already exited, and the process handle must still be released.
    /// </summary>
    [Test]
    public async Task The_handle_is_released_even_when_closing_the_pipe_throws() {
        var process = Process.Start(IgnoresItsStdin())!;
        var pid     = process.Id;
        var pipe    = new CountingStream { ThrowOnDispose = true };

        try {
            var child = DetachedChild.ForProcess(process, pipe);

            Assert.Throws<IOException>(child.Dispose);

            await Assert.That(pipe.SyncDisposals).IsEqualTo(1);
            await Assert.That(IsReleased(process)).IsTrue();
        } finally {
            Kill(pid);
        }
    }

    /// <summary>A second close must not run the release again.</summary>
    [Test]
    public async Task Repeated_disposal_releases_exactly_once() {
        var process = Process.Start(IgnoresItsStdin())!;
        var pid     = process.Id;
        var pipe    = new CountingStream();

        try {
            var child = DetachedChild.ForProcess(process, pipe);

            child.Dispose();
            child.Dispose();

            await Assert.That(pipe.SyncDisposals).IsEqualTo(1);
        } finally {
            Kill(pid);
        }
    }

    /// <summary>
    /// Terminate is the deliberate kill, and it goes through the handle the child was created with.
    /// Resolving the pid again instead would race a child that has already exited, whose number the
    /// OS is free to have handed to something else.
    /// </summary>
    [Test]
    public async Task Terminate_kills_the_child() {
        var process  = Process.Start(IgnoresItsStdin())!;
        var pid      = process.Id;
        var identity = PidIdentity.Capture(pid);

        try {
            var child = DetachedChild.ForProcess(process, process.StandardInput.BaseStream);

            child.Terminate();

            await PidIdentity.WaitUntilGoneAsync(pid, identity, TimeSpan.FromSeconds(15));

            child.Dispose();
        } finally {
            Kill(pid);
        }
    }

    /// <summary>
    /// Counts how disposal reached the wrapped pipe, and can fail the way a pipe to a departed child
    /// does. A real <see cref="FileStream"/> reports neither, and tolerates repeated closes silently,
    /// so it cannot tell a released-once child from a released-twice one.
    /// </summary>
    sealed class CountingStream : Stream {
        public int  SyncDisposals  { get; private set; }
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
    }
}
