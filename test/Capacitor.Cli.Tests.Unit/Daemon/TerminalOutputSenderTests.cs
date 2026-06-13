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
            NullLogger.Instance,
            retryDelay: FastRetry
        );

        var run = sender.RunAsync(CancellationToken.None);

        for (var i = 0; i < 50; i++) {
            sender.Enqueue("agent-1", $"chunk-{i}");
        }

        sender.Complete();
        await run;

        await Assert.That(delivered).HasCount().EqualTo(50);
        await Assert.That(delivered.ToArray()).IsEquivalentTo(Enumerable.Range(0, 50).Select(i => $"chunk-{i}").ToArray());
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
        await Assert.That(delivered.ToArray()).IsEquivalentTo(new[] { "first", "second", "third" });
    }

    [Test]
    public async Task Cancellation_stops_the_loop_even_while_holding_a_failing_chunk() {
        using var cts = new CancellationTokenSource();

        var sender = new TerminalOutputSender(
            (_, _, _) => throw new InvalidOperationException("connection is not active"),
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
