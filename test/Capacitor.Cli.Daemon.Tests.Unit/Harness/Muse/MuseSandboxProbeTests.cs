using Capacitor.Cli.Daemon.Harness.Muse;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Muse;

public class MuseSandboxProbeTests {
    /// <summary>A <see cref="Lazy{T}"/> caches a thrown exception and rethrows it on every read, so a
    /// throwing probe would turn each capability check into a crash instead of a refusal.</summary>
    [Test]
    public async Task A_probe_that_throws_reads_as_unavailable_on_every_read() {
        var calls = 0;
        var probe = new MuseSandboxProbe(() => {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("bwrap exploded");
        });

        await Assert.That(probe.Available).IsFalse();
        await Assert.That(probe.Available).IsFalse();
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(1);
    }

    [Test]
    public async Task A_probe_answer_is_cached() {
        var calls = 0;
        var probe = new MuseSandboxProbe(() => {
            Interlocked.Increment(ref calls);
            return true;
        });

        await Assert.That(probe.Available).IsTrue();
        await Assert.That(probe.Available).IsTrue();
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(1);
    }

    /// <summary>Without a root bind bwrap starts on an empty root where <c>true</c> does not resolve,
    /// so the probe would report no sandbox on every host, including ones where user namespaces work.</summary>
    [Test]
    public async Task The_bubblewrap_probe_binds_the_root_so_true_resolves() {
        await Assert.That(MuseSandboxProbe.BubblewrapArguments)
            .IsEquivalentTo(["--unshare-all", "--ro-bind", "/", "/", "true"]);
    }
}
