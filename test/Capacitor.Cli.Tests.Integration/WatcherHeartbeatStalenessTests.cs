using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Covers the hook-side staleness probe: <c>WatcherManager.IsWatcherAlive</c>
/// now requires BOTH a live PID and a non-stale heartbeat, and <c>EnsureWatcherRunning</c>
/// reaps + respawns a wedged (alive-but-stale) watcher under the cross-platform spawn lock so
/// two concurrent hooks racing the same key can't double-spawn. Mirrors <see cref="WatcherLifecycleTests"/> —
/// uses <c>KCAP_WATCHER_DIR</c> — but never lets <c>EnsureWatcherRunning</c> launch a real
/// child process: the "wedged watcher" is a real, disposable dummy process (so <c>KillWatcher</c>
/// can genuinely terminate it) while the respawn goes to a substituted spawner, so the test stays
/// fast and deterministic.
/// </summary>
[NotInParallel]
public class WatcherHeartbeatStalenessTests {
    static readonly TempDir Tmp = new();
    static readonly TempDir Transcripts = new();
    static string TempDir => Tmp.Path;

    // KCAP_WATCHER_DIR is pinned below and wins over the root, so nothing here reads it — it points
    // at the same temp directory anyway, so a lapse in that precedence cannot escape into the
    // developer's own config.
    static readonly ConfigRoot Root = new(Tmp.Path);

    // One manager over that root and the URL these spawns target — the two values production hands
    // it, so a watcher here can never point at a second server. The directory is named rather than
    // read back off the environment, which this class's own static initialiser would race.
    static readonly ProfileContext Profiles = Resolutions.At("http://localhost:0", Root);
    static readonly WatcherPaths   Paths    = new(TempDir);

    static readonly WatcherManager Watchers =
        TestWatchers.In(Paths, Root, Profiles, new FixedCapacitorHttpClient());

    static WatcherManager Managing(IWatcherSpawner spawner) =>
        new(Root, Profiles, new FixedCapacitorHttpClient(), SystemProcessStarter.Instance, Paths, spawner, TimeProvider.System);

    static string? _previousWatcherDir;

    [Before(Class)]
    public static void SetUp() {
        _previousWatcherDir = Environment.GetEnvironmentVariable("KCAP_WATCHER_DIR");
        Environment.SetEnvironmentVariable("KCAP_WATCHER_DIR", TempDir);
    }

    [After(Class)]
    public static void TearDown() {
        Environment.SetEnvironmentVariable("KCAP_WATCHER_DIR", _previousWatcherDir);
        Tmp.Dispose();
        Transcripts.Dispose();
    }

    static (string key, string transcriptPath, string pidFile) NewKey(string prefix) {
        var key            = $"{prefix}-{Guid.NewGuid():N}";
        var transcriptPath = Transcripts.CreateFile($"{key}.jsonl");

        return (key, transcriptPath, Path.Combine(TempDir, $"{key}.pid"));
    }

    /// <summary>Starts a real, harmless long-lived process to stand in for a wedged watcher's PID —
    /// same pattern as <c>ProcessHelpersTests</c> — so <c>KillWatcher</c> can genuinely terminate it.</summary>
    static Process StartDummyProcess() {
        var psi = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new ProcessStartInfo("ping", "-n 60 127.0.0.1")
            : new ProcessStartInfo("/bin/sleep", "60");

        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError  = true;
        psi.UseShellExecute        = false;

        var process = new Process { StartInfo = psi };
        process.Start();

        return process;
    }

    static void WriteStaleWatcherFiles(string key, string pidFile, int pid) {
        var longAgo = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);
        File.WriteAllText(pidFile, pid.ToString(CultureInfo.InvariantCulture));
        WatcherHeartbeat.Touch(Path.Combine(TempDir, $"{key}.started"), longAgo);
        WatcherHeartbeat.Touch(Paths.HeartbeatFile(key), longAgo);
    }

    [Test]
    public async Task IsWatcherAlive_PidAliveButHeartbeatStale_IsFalse() {
        var (key, _, pidFile) = NewKey("wedged");
        using var dummy = StartDummyProcess();

        try {
            WriteStaleWatcherFiles(key, pidFile, dummy.Id);

            await Assert.That(Watchers.IsWatcherAlive(key)).IsFalse();
        } finally {
            try { dummy.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task IsWatcherAlive_PidAliveAndHeartbeatFresh_IsTrue() {
        var (key, _, pidFile) = NewKey("healthy");
        using var dummy = StartDummyProcess();

        try {
            File.WriteAllText(pidFile, dummy.Id.ToString(CultureInfo.InvariantCulture));
            var now = DateTimeOffset.UtcNow;
            WatcherHeartbeat.Touch(Path.Combine(TempDir, $"{key}.started"), now - TimeSpan.FromMinutes(5));
            WatcherHeartbeat.Touch(Paths.HeartbeatFile(key), now);

            await Assert.That(Watchers.IsWatcherAlive(key)).IsTrue();
        } finally {
            try { dummy.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task IsWatcherAlive_WithinStartupGrace_IsTrueEvenWithNoHeartbeatYet() {
        var (key, _, pidFile) = NewKey("fresh-start");
        using var dummy = StartDummyProcess();

        try {
            // Just spawned: started marker is fresh, no heartbeat written yet.
            File.WriteAllText(pidFile, dummy.Id.ToString(CultureInfo.InvariantCulture));
            WatcherHeartbeat.Touch(Path.Combine(TempDir, $"{key}.started"), DateTimeOffset.UtcNow);

            await Assert.That(Watchers.IsWatcherAlive(key)).IsTrue();
        } finally {
            try { dummy.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task EnsureWatcherRunning_WedgedWatcher_ReapsAndRespawnsExactlyOnce() {
        var (key, transcriptPath, pidFile) = NewKey("reap");
        using var dummy = StartDummyProcess();

        var spawnCount   = 0;
        var firstEntered = new TaskCompletionSource();
        var release      = new TaskCompletionSource();

        var spawner = new FakeWatcherSpawner(async request => {
            Interlocked.Increment(ref spawnCount);
            firstEntered.TrySetResult();
            await release.Task; // held open until the test says both attempts have raced

            // Simulate a successful respawn: fresh pid (this test process — always alive)
            // + fresh heartbeat/started markers, so a losing concurrent caller's re-check
            // under the lock sees a healthy watcher and skips.
            File.WriteAllText(Paths.PidFile(request.Key), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            var now = DateTimeOffset.UtcNow;
            WatcherHeartbeat.Touch(Paths.StartedFile(request.Key), now);
            WatcherHeartbeat.Touch(Paths.HeartbeatFile(request.Key), now);
        });

        var watchers = Managing(spawner);

        try {
            WriteStaleWatcherFiles(key, pidFile, dummy.Id);

            // First caller acquires the spawn lock, kills the dummy, and blocks inside the
            // fake spawn until we release it — holding the lock open for the whole window.
            var first = watchers.EnsureWatcherRunning(key, transcriptPath, agentId: null);
            await firstEntered.Task;

            // Second caller races the SAME stale key while the first still holds the lock:
            // it must find the lock contended and return without spawning.
            var second = watchers.EnsureWatcherRunning(key, transcriptPath, agentId: null);
            await second;

            await Assert.That(spawnCount).IsEqualTo(1);

            release.TrySetResult();
            await first;

            await Assert.That(spawnCount).IsEqualTo(1);
            await Assert.That(watchers.IsWatcherAlive(key)).IsTrue();
        } finally {
            release.TrySetResult();
            try { dummy.Kill(entireProcessTree: true); } catch { /* best effort — likely already dead */ }
        }
    }

    [Test]
    public async Task KillWatcher_RemovesHeartbeatAndStartedFiles() {
        // The sidecar files must not leak per-session the way the pid file never did.
        var (key, _, pidFile) = NewKey("kill-cleanup");
        using var dummy = StartDummyProcess();

        var heartbeat = Paths.HeartbeatFile(key);
        var started   = Path.Combine(TempDir, $"{key}.started");

        try {
            File.WriteAllText(pidFile, dummy.Id.ToString(CultureInfo.InvariantCulture));
            WatcherHeartbeat.Touch(started, DateTimeOffset.UtcNow);
            WatcherHeartbeat.Touch(heartbeat, DateTimeOffset.UtcNow);

            await Watchers.KillWatcher(key);

            await Assert.That(File.Exists(pidFile)).IsFalse();
            await Assert.That(File.Exists(heartbeat)).IsFalse();
            await Assert.That(File.Exists(started)).IsFalse();
        } finally {
            try { dummy.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task PurgeAuxiliaryFiles_RemovesHeartbeatStartedAndSpawnlock() {
        // Cleanup is the one place spawn locks are swept (KillWatcher intentionally leaves them
        // to avoid the unlink-race). Verify all three auxiliary kinds are removed, including orphans.
        var key       = $"purge-{Guid.NewGuid():N}";
        var heartbeat = Paths.HeartbeatFile(key);
        var started   = Path.Combine(TempDir, $"{key}.started");
        var spawnlock = Path.Combine(TempDir, $"{key}.spawnlock");

        WatcherHeartbeat.Touch(heartbeat, DateTimeOffset.UtcNow);
        WatcherHeartbeat.Touch(started, DateTimeOffset.UtcNow);
        File.WriteAllText(spawnlock, "");

        var removed = Watchers.PurgeAuxiliaryFiles();

        await Assert.That(removed).IsGreaterThanOrEqualTo(3);
        await Assert.That(File.Exists(heartbeat)).IsFalse();
        await Assert.That(File.Exists(started)).IsFalse();
        await Assert.That(File.Exists(spawnlock)).IsFalse();
    }

    [Test]
    public async Task EnsureWatcherRunning_HealthyWatcher_DoesNotReap() {
        var (key, transcriptPath, pidFile) = NewKey("no-reap");
        using var dummy = StartDummyProcess();

        var spawner = new FakeWatcherSpawner();

        try {
            File.WriteAllText(pidFile, dummy.Id.ToString(CultureInfo.InvariantCulture));
            var now = DateTimeOffset.UtcNow;
            WatcherHeartbeat.Touch(Path.Combine(TempDir, $"{key}.started"), now - TimeSpan.FromMinutes(5));
            WatcherHeartbeat.Touch(Paths.HeartbeatFile(key), now);

            await Managing(spawner).EnsureWatcherRunning(key, transcriptPath, agentId: null);

            await Assert.That(spawner.Spawned).IsEmpty();
            await Assert.That(dummy.HasExited).IsFalse();
        } finally {
            try { dummy.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
    }
}
