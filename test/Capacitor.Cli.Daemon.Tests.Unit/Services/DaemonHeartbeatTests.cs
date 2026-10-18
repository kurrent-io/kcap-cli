using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class DaemonHeartbeatTests {
    static readonly DateTimeOffset Start = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Beat_creates_the_file_and_LastBeat_reads_its_time() {
        using var tmp  = new TempDaemonStore();
        var       time = new FakeTimeProvider(Start);
        var       beat = Create(tmp, time);

        beat.Beat();

        await Assert.That(DaemonHeartbeat.LastBeat(tmp.Store, "hb")).IsEqualTo(Start);

        time.Advance(DaemonHeartbeat.Interval);
        beat.Beat();

        await Assert.That(DaemonHeartbeat.LastBeat(tmp.Store, "hb")).IsEqualTo(Start + DaemonHeartbeat.Interval);
    }

    [Test]
    public async Task LastBeat_is_null_without_a_file() {
        using var tmp = new TempDaemonStore();

        await Assert.That(DaemonHeartbeat.LastBeat(tmp.Store, "hb")).IsNull();
    }

    [Test]
    public async Task Loop_does_not_beat_before_the_first_interval() {
        using var tmp  = new TempDaemonStore();
        var       time = new FakeTimeProvider(Start);
        using var beat  = Create(tmp, time);
        using var armed = Armed(beat);

        await beat.StartAsync(CancellationToken.None);
        await Assert.That(await armed.WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();
        time.Advance(DaemonHeartbeat.Interval - TimeSpan.FromSeconds(1));
        await Task.Delay(100);

        await Assert.That(DaemonHeartbeat.LastBeat(tmp.Store, "hb")).IsNull();

        await beat.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Loop_beats_once_per_interval() {
        using var tmp  = new TempDaemonStore();
        var       time = new FakeTimeProvider(Start);
        using var beat  = Create(tmp, time);
        using var armed = Armed(beat);

        await beat.StartAsync(CancellationToken.None);
        await Assert.That(await armed.WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();

        time.Advance(DaemonHeartbeat.Interval);
        await WaitForBeat(tmp, Start + DaemonHeartbeat.Interval);

        await Assert.That(await armed.WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();
        time.Advance(DaemonHeartbeat.Interval);
        await WaitForBeat(tmp, Start + DaemonHeartbeat.Interval * 2);

        await beat.StopAsync(CancellationToken.None);
    }

    /// <summary>Released each time the loop is about to await its interval.</summary>
    static SemaphoreSlim Armed(DaemonHeartbeat beat) {
        var armed = new SemaphoreSlim(0);
        beat.OnArmedForTest = () => armed.Release();

        return armed;
    }

    static DaemonHeartbeat Create(TempDaemonStore tmp, FakeTimeProvider time) =>
        new(new DaemonConfig { Name = "hb", Store = tmp.Store }, time, NullLogger<DaemonHeartbeat>.Instance);

    static async Task WaitForBeat(TempDaemonStore tmp, DateTimeOffset expected) {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DaemonHeartbeat.LastBeat(tmp.Store, "hb") != expected && DateTime.UtcNow < deadline) await Task.Delay(10);

        await Assert.That(DaemonHeartbeat.LastBeat(tmp.Store, "hb")).IsEqualTo(expected);
    }
}
