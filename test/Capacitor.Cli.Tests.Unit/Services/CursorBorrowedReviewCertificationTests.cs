using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class CursorBorrowedReviewCertificationTests {
    [Test]
    public async Task BundleDigest_IgnoresTransientRunningDirectory() {
        var root = Directory.CreateTempSubdirectory("cursor-certification-");
        try {
            File.WriteAllText(Path.Combine(root.FullName, "cursor-agent"), "artifact");
            var before = CursorBorrowedReviewCertification.ComputeBundleDigest(root.FullName);
            var running = Directory.CreateDirectory(Path.Combine(root.FullName, ".running"));
            File.WriteAllText(Path.Combine(running.FullName, "12345"), "");

            var after = CursorBorrowedReviewCertification.ComputeBundleDigest(root.FullName);

            await Assert.That(after).IsEqualTo(before);
        } finally {
            try { root.Delete(recursive: true); } catch { }
        }
    }
}
