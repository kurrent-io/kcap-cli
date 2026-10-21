using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class LastReadsTests {
    [Test]
    [Arguments(true, "new")]
    [Arguments(false, null)]
    [Arguments(null, "old")]
    public async Task The_verdict_keeps_drops_or_leaves_the_last_read(bool? remember, string? kept) {
        var reads = new LastReads<string>();
        reads.Set("s", "old", reads.Generation);

        await reads.RecordAsync("s", () => Task.FromResult("new"), _ => remember);

        await Assert.That(reads.Get("s")).IsEqualTo(kept);
    }

    /// A read that began before the credential changed belongs to the previous identity.
    [Test]
    public async Task A_read_in_flight_across_a_clear_is_not_kept() {
        var reads = new LastReads<string>();
        var read = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recording = reads.RecordAsync("s", () => read.Task, _ => true);

        reads.Clear();
        read.SetResult("before the change");
        await recording;

        await Assert.That(reads.Get("s")).IsNull();
    }

    [Test]
    public async Task Past_capacity_the_least_recently_kept_goes() {
        var reads = new LastReads<string>(capacity: 2);
        reads.Set("a", "1", reads.Generation);
        reads.Set("b", "2", reads.Generation);
        reads.Set("a", "1 again", reads.Generation);
        reads.Set("c", "3", reads.Generation);

        await Assert.That(reads.Get("b")).IsNull();
        await Assert.That(reads.Get("a")).IsEqualTo("1 again");
    }
}
