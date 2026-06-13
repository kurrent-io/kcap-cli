using System.Collections.Concurrent;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// AI-842: the hosted-agent terminal mirror must reach the server in PTY order
/// and survive a flapping SignalR connection without dropping bytes. These tests
/// pin the two guarantees the single-consumer drain loop provides — strict
/// ordering and hold-and-retry across transient send failures — without standing
/// up a real hub.
/// </summary>
public class TerminalOutputSenderTests {
    static readonly TimeSpan FastRetry = TimeSpan.FromMilliseconds(5);

    [Test]
    public async Task Chunks_are_delivered_in_enqueue_order() {
        var delivered = new ConcurrentQueue<string>();

        var sender = new TerminalOutputSender(
            (_, base64, _) => {
                delivered.Enqueue(base64);

                return Task.CompletedTask;
            },
            isConnected: () => true,
            NullLogger.Instance,
            retryDelay: FastRetry
        );

        var run = sender.RunAsync(CancellationToken.None);

        for (var i = 0; i < 50; i++) {
            sender.Enqueue("agent-1", $"chunk-{i}");
        }

        sender.Complete();
        await run;

        // Order-sensitive, by index: IsEquivalentTo is permutation-tolerant and
        // would pass for any order, defeating the point of an ordering test.
        var arr = delivered.ToArray();
        await Assert.That(arr.Length).IsEqualTo(50);

        for (var i = 0; i < 50; i++) {
            await Assert.That(arr[i]).IsEqualTo($"chunk-{i}");
        }
    }

    [Test]
    public async Task Failed_send_is_retried_with_the_same_chunk_so_order_is_preserved_and_nothing_is_lost() {
        var delivered  = new ConcurrentQueue<string>();
        var failsLeft  = 5; // first chunk fails 5x (transport "down") before it lands

        var sender = new TerminalOutputSender(
            (_, base64, _) => {
                if (base64 == "first" && Interlocked.Decrement(ref failsLeft) >= 0) {
                    throw new InvalidOperationException("connection is not active");
                }

                delivered.Enqueue(base64);

                return Task.CompletedTask;
            },
            isConnected: () => false, // transport down — failures must be held and retried, not dropped
            NullLogger.Instance,
            retryDelay: FastRetry
        );

        var run = sender.RunAsync(CancellationToken.None);

        sender.Enqueue("agent-1", "first");
        sender.Enqueue("agent-1", "second");
        sender.Enqueue("agent-1", "third");

        sender.Complete();
        await run;

        // "first" must still arrive before "second"/"third" even though it failed
        // repeatedly: the drain loop holds the head chunk and retries it in place.
        // Asserted by index — IsEquivalentTo would pass for any order.
        var arr = delivered.ToArray();
        await Assert.That(arr.Length).IsEqualTo(3);
        await Assert.That(arr[0]).IsEqualTo("first");
        await Assert.That(arr[1]).IsEqualTo("second");
        await Assert.That(arr[2]).IsEqualTo("third");
    }

    [Test]
    public async Task Send_failure_while_connected_drops_the_chunk_and_keeps_delivering_later_chunks() {
        var delivered = new ConcurrentQueue<string>();

        // The hub reports Connected, but the send for "poison" throws. A blind
        // retry would spin forever and wedge the single shared loop; the sender
        // must instead drop that chunk and keep delivering the rest.
        var sender = new TerminalOutputSender(
            (_, base64, _) => {
                if (base64 == "poison") throw new InvalidOperationException("boom");

                delivered.Enqueue(base64);

                return Task.CompletedTask;
            },
            isConnected: () => true,
            NullLogger.Instance,
            retryDelay: FastRetry
        );

        var run = sender.RunAsync(CancellationToken.None);

        sender.Enqueue("agent-1", "poison");
        sender.Enqueue("agent-1", "after-1");
        sender.Enqueue("agent-1", "after-2");

        sender.Complete();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        var arr = delivered.ToArray();
        await Assert.That(arr.Length).IsEqualTo(2);
        await Assert.That(arr[0]).IsEqualTo("after-1");
        await Assert.That(arr[1]).IsEqualTo("after-2");
    }

    [Test]
    public async Task Cancellation_stops_the_loop_even_while_holding_a_failing_chunk() {
        using var cts = new CancellationTokenSource();

        var sender = new TerminalOutputSender(
            (_, _, _) => throw new InvalidOperationException("connection is not active"),
            isConnected: () => false, // held (not dropped), so the loop is genuinely stuck until cancelled
            NullLogger.Instance,
            retryDelay: FastRetry
        );

        var run = sender.RunAsync(cts.Token);

        sender.Enqueue("agent-1", "stuck");
        await Task.Delay(50);
        await cts.CancelAsync();

        // RunAsync must return promptly rather than retrying the failing chunk forever.
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
