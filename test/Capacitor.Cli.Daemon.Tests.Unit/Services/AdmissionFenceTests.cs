using System.Runtime.Versioning;
using System.Text.Json;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The rename fence: admission and the idle check are one boundary; a held fence ends with its
/// holder, a committed one persists and outlives it until its lease.
/// </summary>
public class AdmissionFenceTests {
    [TempDir] public required TempDir Tmp { get; init; }

    readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    string MarkerPath => Tmp.PathTo("state", "retiring.json");

    AdmissionFence NewFence() => new(MarkerPath, "instance-1", _time, NullLogger<AdmissionFence>.Instance);

    static AdmissionFence.Hold Acquire(AdmissionFence fence) {
        var result = fence.TryAcquire(() => false, out var hold);
        if (result != AdmissionFence.AcquireResult.Acquired) throw new InvalidOperationException($"expected to acquire, got {result}");
        return hold!;
    }

    [Test]
    public async Task Work_in_flight_makes_the_fence_busy_until_it_returns() {
        var fence = NewFence();
        var admission = fence.TryAdmit();

        await Assert.That(admission).IsNotNull();
        await Assert.That(fence.TryAcquire(() => false, out _)).IsEqualTo(AdmissionFence.AcquireResult.Busy);

        admission!.Dispose();
        await Assert.That(fence.TryAcquire(() => false, out _)).IsEqualTo(AdmissionFence.AcquireResult.Acquired);
    }

    [Test]
    public async Task Disposing_an_admission_twice_releases_it_once() {
        var fence = NewFence();
        var first = fence.TryAdmit()!;
        var second = fence.TryAdmit()!;

        first.Dispose();
        first.Dispose();

        await Assert.That(fence.TryAcquire(() => false, out _)).IsEqualTo(AdmissionFence.AcquireResult.Busy);
        second.Dispose();
        await Assert.That(fence.TryAcquire(() => false, out _)).IsEqualTo(AdmissionFence.AcquireResult.Acquired);
    }

    [Test]
    public async Task A_busy_daemon_refuses_the_fence() {
        var fence = NewFence();

        await Assert.That(fence.TryAcquire(() => true, out var hold)).IsEqualTo(AdmissionFence.AcquireResult.Busy);
        await Assert.That(hold).IsNull();
        await Assert.That(fence.TryAdmit()).IsNotNull();
    }

    [Test]
    public async Task A_held_fence_refuses_new_work_and_a_second_fence() {
        var fence = NewFence();
        Acquire(fence);

        await Assert.That(fence.TryAdmit()).IsNull();
        await Assert.That(fence.TryAcquire(() => false, out _)).IsEqualTo(AdmissionFence.AcquireResult.Fenced);
    }

    /// <summary>A committed fence is reported as fenced, not busy: there may be no work at all.</summary>
    [Test]
    public async Task A_committed_fence_reports_fenced_to_a_retry() {
        var fence = NewFence();
        var hold = Acquire(fence);
        hold.Commit();
        hold.Close();

        await Assert.That(fence.TryAcquire(() => false, out _)).IsEqualTo(AdmissionFence.AcquireResult.Fenced);
    }

    [Test]
    public async Task Tracked_work_is_never_refused_and_keeps_the_daemon_busy() {
        var fence = NewFence();
        var tracked = fence.Track();

        await Assert.That(fence.TryAcquire(() => false, out _)).IsEqualTo(AdmissionFence.AcquireResult.Busy);

        tracked.Dispose();
        var hold = Acquire(fence);
        using var duringFence = fence.Track();
        await Assert.That(fence.TryAdmit()).IsNull();
        hold.Close();
    }

    [Test]
    public async Task Closing_a_held_fence_admits_again_and_writes_nothing() {
        var fence = NewFence();
        Acquire(fence).Close();

        await Assert.That(fence.TryAdmit()).IsNotNull();
        await Assert.That(File.Exists(MarkerPath)).IsFalse();
    }

    [Test]
    public async Task Commit_persists_the_marker_before_it_returns() {
        var fence = NewFence();
        var hold = Acquire(fence);

        await Assert.That(hold.Commit()).IsTrue();

        var marker = JsonSerializer.Deserialize(await File.ReadAllTextAsync(MarkerPath), AdmissionFenceJsonContext.Default.RetiringMarker)!;
        await Assert.That(marker.InstanceId).IsEqualTo("instance-1");
        await Assert.That(marker.CommittedAt).IsEqualTo(_time.GetUtcNow());
    }

    [Test]
    public async Task A_committed_fence_outlives_its_connection_until_the_lease_ends() {
        var fence = NewFence();
        var hold = Acquire(fence);
        hold.Commit();
        hold.Close();

        _time.Advance(AdmissionFence.CommitLease - TimeSpan.FromSeconds(1));
        await Assert.That(fence.TryAdmit()).IsNull();

        _time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(fence.TryAdmit()).IsNotNull();
        await Assert.That(File.Exists(MarkerPath)).IsFalse();
    }

    [Test]
    public async Task Abort_after_commit_deletes_the_marker_and_admits_again() {
        var fence = NewFence();
        var hold = Acquire(fence);
        hold.Commit();

        hold.Abort();

        await Assert.That(File.Exists(MarkerPath)).IsFalse();
        await Assert.That(fence.TryAdmit()).IsNotNull();
    }

    [Test]
    public async Task A_released_hold_can_no_longer_commit() {
        var fence = NewFence();
        var hold = Acquire(fence);
        hold.Close();

        await Assert.That(hold.Commit()).IsFalse();
        await Assert.That(File.Exists(MarkerPath)).IsFalse();
        await Assert.That(fence.TryAdmit()).IsNotNull();
    }

    [Test]
    public async Task A_commit_that_cannot_be_persisted_fails_and_stays_held() {
        Tmp.CreateDir("state", "retiring.json"); // a directory where the marker file belongs
        var fence = NewFence();
        var hold = Acquire(fence);

        await Assert.That(hold.Commit()).IsFalse();
        await Assert.That(fence.TryAdmit()).IsNull();

        hold.Close();
        await Assert.That(fence.TryAdmit()).IsNotNull();
    }

    [Test]
    public async Task A_process_starting_inside_a_live_marker_starts_fenced() {
        var committing = NewFence();
        Acquire(committing).Commit();

        _time.Advance(TimeSpan.FromSeconds(30));
        var replacement = NewFence();

        await Assert.That(replacement.IsFenced).IsTrue();
        await Assert.That(replacement.TryAdmit()).IsNull();

        _time.Advance(AdmissionFence.CommitLease);
        await Assert.That(replacement.TryAdmit()).IsNotNull();
        await Assert.That(File.Exists(MarkerPath)).IsFalse();
    }

    [Test]
    public async Task A_process_starting_after_the_lease_starts_open_and_deletes_the_marker() {
        Acquire(NewFence()).Commit();

        _time.Advance(AdmissionFence.CommitLease);
        var replacement = NewFence();

        await Assert.That(replacement.IsFenced).IsFalse();
        await Assert.That(File.Exists(MarkerPath)).IsFalse();
    }

    /// <summary>An unreadable marker may still be a real commit, so it counts from the file's
    /// modification time — recent here, so the daemon must start fenced.</summary>
    [Test]
    public async Task An_unreadable_marker_starts_the_daemon_fenced() {
        Tmp.CreateFile("state/retiring.json", "{not json");
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);

        var fence = new AdmissionFence(MarkerPath, "instance-1", fake, NullLogger<AdmissionFence>.Instance);

        await Assert.That(fence.IsFenced).IsTrue();
    }

    /// <summary>A CLI that stalls with its connection open must not hold the daemon closed past the lease.</summary>
    [Test]
    public async Task The_lease_ends_a_commit_whose_connection_is_still_open() {
        var fence = NewFence();
        var hold = Acquire(fence);
        hold.Commit();

        _time.Advance(AdmissionFence.CommitLease);

        await Assert.That(fence.TryAdmit()).IsNotNull();
        await Assert.That(File.Exists(MarkerPath)).IsFalse();
        await Assert.That(hold.Commit()).IsFalse();
    }

    /// <summary>A marker left on disk would fence the next process, so an abort that cannot delete it
    /// fails and the commit stands.</summary>
    [Test]
    [ExcludeOn(TUnit.Core.Enums.OS.Windows)] // directory write permission
    [UnsupportedOSPlatform("windows")]
    public async Task An_abort_that_cannot_delete_the_marker_fails_and_stays_committed() {
        var fence = NewFence();
        var hold = Acquire(fence);
        hold.Commit();
        var stateDir = Tmp.PathTo("state");
        File.SetUnixFileMode(stateDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try {
            await Assert.That(hold.Abort()).IsFalse();
            await Assert.That(File.Exists(MarkerPath)).IsTrue();
            await Assert.That(fence.TryAdmit()).IsNull();
        } finally {
            File.SetUnixFileMode(stateDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Test]
    public async Task A_repeated_commit_does_not_extend_the_lease() {
        var fence = NewFence();
        var hold = Acquire(fence);
        hold.Commit();

        _time.Advance(AdmissionFence.CommitLease - TimeSpan.FromSeconds(1));
        await Assert.That(hold.Commit()).IsTrue();
        _time.Advance(TimeSpan.FromSeconds(1));

        await Assert.That(fence.TryAdmit()).IsNotNull();
    }
}
