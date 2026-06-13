using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.SignalR;

namespace Capacitor.Cli.Tests.Unit;

public class ConnectionRetryTests {
    static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(1);

    [Test]
    public async Task Recovers_after_transient_disconnect_once_ready() {
        var invokeCalls = 0;
        var pollCount   = 0;
        // Not ready for the first two readiness polls, then ready.
        Func<bool> isReady = () => ++pollCount > 2;
        var retries = new List<int>();

        Func<Task<string>> invoke = () => {
            invokeCalls++;
            if (invokeCalls == 1) throw new TaskCanceledException();
            return Task.FromResult("decision");
        };

        var result = await ConnectionRetry.InvokeWithConnectionRetryAsync(
            invoke, isReady, FastPoll, retries.Add, CancellationToken.None);

        await Assert.That(result).IsEqualTo("decision");
        await Assert.That(invokeCalls).IsEqualTo(2);
        await Assert.That(retries).IsEquivalentTo(new[] { 1 });
    }

    [Test]
    public async Task Cancellation_during_wait_propagates() {
        using var cts = new CancellationTokenSource();
        var retries = 0;

        Func<Task<string>> invoke = () => throw new TaskCanceledException();
        Func<bool>         isReady = () => false; // would otherwise wait forever
        Action<int>        onRetry = _ => { retries++; cts.Cancel(); };

        await Assert.That(async () => await ConnectionRetry.InvokeWithConnectionRetryAsync(
                invoke, isReady, FastPoll, onRetry, cts.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(retries).IsEqualTo(1);
    }

    [Test]
    public async Task HubException_is_not_retried() {
        var retries = 0;

        Func<Task<string>> invoke = () => throw new HubException("server rejected");

        await Assert.That(async () => await ConnectionRetry.InvokeWithConnectionRetryAsync(
                invoke, () => true, FastPoll, _ => retries++, CancellationToken.None))
            .Throws<HubException>();

        await Assert.That(retries).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidOperationException_while_not_ready_is_retried() {
        var invokeCalls = 0;
        var ready       = false;
        var retries     = 0;

        Func<Task<string>> invoke = () => {
            invokeCalls++;
            if (invokeCalls == 1) throw new InvalidOperationException("connection is not active");
            return Task.FromResult("decision");
        };
        Func<bool>  isReady = () => ready;
        Action<int> onRetry = _ => { retries++; ready = true; };

        var result = await ConnectionRetry.InvokeWithConnectionRetryAsync(
            invoke, isReady, FastPoll, onRetry, CancellationToken.None);

        await Assert.That(result).IsEqualTo("decision");
        await Assert.That(invokeCalls).IsEqualTo(2);
        await Assert.That(retries).IsEqualTo(1);
    }

    [Test]
    public async Task InvalidOperationException_while_ready_is_final() {
        var retries = 0;

        Func<Task<string>> invoke = () => throw new InvalidOperationException("boom");

        await Assert.That(async () => await ConnectionRetry.InvokeWithConnectionRetryAsync(
                invoke, () => true, FastPoll, _ => retries++, CancellationToken.None))
            .Throws<InvalidOperationException>();

        await Assert.That(retries).IsEqualTo(0);
    }

    [Test]
    public async Task Transient_failure_while_already_ready_retries_without_hanging() {
        var invokeCalls = 0;

        Func<Task<string>> invoke = () => {
            invokeCalls++;
            if (invokeCalls == 1) throw new TaskCanceledException();
            return Task.FromResult("decision");
        };

        var result = await ConnectionRetry.InvokeWithConnectionRetryAsync(
            invoke, () => true, FastPoll, _ => { }, CancellationToken.None);

        await Assert.That(result).IsEqualTo("decision");
        await Assert.That(invokeCalls).IsEqualTo(2);
    }
}
