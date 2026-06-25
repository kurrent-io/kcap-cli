using System.Text;
using Capacitor.Cli.Daemon.Pty.Unix;

namespace Capacitor.Cli.Tests.Integration.Daemon;

[NotInParallel("KCAP_DAEMON_SUPERVISED")] // mutates a process-wide env var
public class PtyEnvScrubTests {
    [Test]
    public async Task Spawned_child_does_not_see_supervision_marker() {
        if (OperatingSystem.IsWindows()) return; // Unix PTY (forkpty) path only

        Environment.SetEnvironmentVariable("KCAP_DAEMON_SUPERVISED", "laptop");
        try {
            await using var pty = UnixPtyProcess.Spawn("/bin/sh", ["-c", "printf 'MARK=[%s]' \"$KCAP_DAEMON_SUPERVISED\""], "/tmp");
            var sb  = new StringBuilder();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await foreach (var chunk in pty.ReadOutputAsync(cts.Token)) {
                sb.Append(Encoding.UTF8.GetString(chunk));
                if (sb.ToString().Contains("MARK=")) break;
            }

            await Assert.That(sb.ToString()).Contains("MARK=[]");
        } finally {
            Environment.SetEnvironmentVariable("KCAP_DAEMON_SUPERVISED", null);
        }
    }
}
