using System.Diagnostics;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The detached start hands its caller a stream and no way to reach the <see cref="Process"/>
/// wrapper behind it, so closing the stream has to be what releases that wrapper — and must not
/// take the child with it, which is the whole point of spawning detached.
/// </summary>
public class ChildStdinStreamTests {
    [Test]
    public async Task Closing_the_stream_releases_the_wrapper_and_leaves_the_child_running() {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/c more")
            : new ProcessStartInfo("/bin/sh", "-c \"sleep 30\"");

        psi.UseShellExecute = false;
        psi.CreateNoWindow  = true;
        psi.RedirectStandardInput = true;

        var child    = Process.Start(psi)!;
        var pid      = child.Id;
        var identity = PidIdentity.Capture(pid);
        var pipe     = new MemoryStream();

        try {
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
}
