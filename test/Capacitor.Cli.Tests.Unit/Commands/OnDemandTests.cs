using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class OnDemandTests {
    sealed class Value;

    [Test]
    public async Task Concurrent_first_callers_share_one_creation() {
        var created = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var onDemand = new OnDemand<Value>(async () => {
            Interlocked.Increment(ref created);
            await release.Task;
            return new Value();
        });

        var first  = onDemand.GetAsync();
        var second = onDemand.GetAsync();
        release.SetResult();

        await Assert.That(await first).IsSameReferenceAs(await second);
        await Assert.That(await onDemand.GetAsync()).IsSameReferenceAs(await first);
        await Assert.That(created).IsEqualTo(1);
    }

    [Test]
    public async Task A_failed_creation_is_retried_by_the_next_caller() {
        var attempts = 0;
        using var onDemand = new OnDemand<Value>(() =>
            ++attempts == 1 ? Task.FromException<Value>(new IOException("transient")) : Task.FromResult(new Value()));

        await Assert.That(async () => await onDemand.GetAsync()).Throws<IOException>();
        await Assert.That(await onDemand.GetAsync()).IsNotNull();
        await Assert.That(attempts).IsEqualTo(2);
    }

    [Test]
    public async Task Disposing_disposes_the_created_value() {
        var stream = new MemoryStream();
        var onDemand = new OnDemand<MemoryStream>(() => Task.FromResult(stream));
        await onDemand.GetAsync();

        onDemand.Dispose();

        await Assert.That(stream.CanRead).IsFalse();
    }
}
