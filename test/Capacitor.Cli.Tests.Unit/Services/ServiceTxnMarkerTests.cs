using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class ServiceTxnMarkerTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    static TxnMarker M(string phase = "captured") => new(1, "install", phase, "absent|nounit||pid=", "no-unit", null);

    [Test]
    public async Task Roundtrip_and_phase_update() {
        ServiceTxnMarker.Write(Daemons.Store, "a", M());
        await Assert.That(ServiceTxnMarker.Read(Daemons.Store, "a")!.Phase).IsEqualTo("captured");
        ServiceTxnMarker.Write(Daemons.Store, "a", M("written") with { PlistFingerprint = ServiceTxnMarker.Fingerprint("<plist/>") });
        await Assert.That(ServiceTxnMarker.Read(Daemons.Store, "a")!.Phase).IsEqualTo("written");
        ServiceTxnMarker.Delete(Daemons.Store, "a");
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, "a")).IsFalse();
    }

    [Test]
    public async Task Corrupt_marker_reads_null() {
        File.WriteAllText(Daemons.Store.ServiceTxnPath("a"), "{not json");
        await Assert.That(ServiceTxnMarker.Read(Daemons.Store, "a")).IsNull();
    }

    [Test]
    public async Task Missing_marker_reads_null_and_exists_is_false() {
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, "missing")).IsFalse();
        await Assert.That(ServiceTxnMarker.Read(Daemons.Store, "missing")).IsNull();
    }

    [Test]
    public async Task Marker_path_is_under_the_daemons_directory() {
        await Assert.That(Daemons.Store.ServiceTxnPath("a"))
            .IsEqualTo(Path.Combine(Daemons.Directory, "a.service-txn"));
    }

    [Test]
    public async Task Fingerprint_is_stable_hex() {
        var fp1 = ServiceTxnMarker.Fingerprint("<plist/>");
        var fp2 = ServiceTxnMarker.Fingerprint("<plist/>");
        await Assert.That(fp1).IsEqualTo(fp2);
        await Assert.That(fp1.Length).IsEqualTo(64);
        await Assert.That(fp1).Matches("^[0-9a-f]{64}$");
    }

    [Test]
    public async Task Delete_of_missing_marker_does_not_throw() {
        ServiceTxnMarker.Delete(Daemons.Store, "never-written");
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, "never-written")).IsFalse();
    }

    /// <summary>The barrier reports whether it fired, and it fires on a real directory — which is
    /// what makes the false below a genuine failure signal rather than the only answer it has.</summary>
    [Test]
    public async Task Directory_barrier_fires_on_a_real_directory() {
        if (OperatingSystem.IsWindows()) return; // no portable directory fsync there
        await Assert.That(ServiceTxnMarker.FlushDirectory(Daemons.Directory)).IsTrue();
    }

    [Test]
    public async Task Directory_barrier_reports_failure_rather_than_throwing() {
        await Assert.That(ServiceTxnMarker.FlushDirectory(Path.Combine(Daemons.Directory, "absent"))).IsFalse();
    }

    /// <summary>A barrier that cannot fire must not fail the delete it follows: the marker going away
    /// is the caller's contract, the fsync behind it is hardening.</summary>
    [Test]
    public async Task Delete_survives_a_barrier_that_cannot_fire() {
        var store = new DaemonStore(Path.Combine(Daemons.Directory, "absent"));
        ServiceTxnMarker.Delete(store, "a");
        await Assert.That(ServiceTxnMarker.Exists(store, "a")).IsFalse();
    }

    // File.Delete on a path that is actually a directory throws (UnauthorizedAccessException on
    // every platform .NET runs Delete on) — this is what the try/catch in Delete swallows.
    [Test]
    public async Task Delete_swallows_the_exception_when_the_path_cannot_be_deleted_as_a_file() {
        var path = Daemons.Store.ServiceTxnPath("a");
        Directory.CreateDirectory(path);
        ServiceTxnMarker.Delete(Daemons.Store, "a");
        await Assert.That(Directory.Exists(path)).IsTrue();
    }
}
