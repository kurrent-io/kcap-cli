using System.Diagnostics;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The detached start hands its caller a stream and no way to reach the <see cref="Process"/>
/// wrapper behind it, so closing the stream has to be what releases that wrapper — and must not
/// take the child with it, which is the whole point of spawning detached.
/// </summary>
public class ChildStdinStreamTests {
    /// <summary>
    /// The child must outlive stdin EOF for the survival assertion to mean anything: one that exits
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

            // A disposed wrapper has let go of the process it was associated with; a live one
            // hands back a handle here.
            Assert.Throws<InvalidOperationException>(() => _ = child.Handle);

            // The child outlives the stream that fed it.
            await Assert.That(PidIdentity.IsGone(pid, identity)).IsFalse();
        } finally {
            try {
                using var running = Process.GetProcessById(pid);
                running.Kill(entireProcessTree: true);
            } catch { }
        }
    }

    /// <summary>A second close must not run the release again, whichever form it takes.</summary>
    [Test]
    public async Task Disposal_is_idempotent_across_both_forms() {
        var child = Process.Start(IgnoresItsStdin())!;
        var pid   = child.Id;

        try {
            var stream = new ChildStdinStream(child.StandardInput.BaseStream, child);

            stream.Dispose();
            stream.Dispose();
            await stream.DisposeAsync();
        } finally {
            try {
                using var running = Process.GetProcessById(pid);
                running.Kill(entireProcessTree: true);
            } catch { }
        }
    }
}
