using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>The forwarder answers which user messages the server has stored, so the hosted judge declares only
/// turns its evidence read can find: a message is stored once an ack's persisted seq reaches the seq the
/// forwarder gave it.</summary>
public class AcpPersistenceProbeTests {
    static readonly TimeSpan Ample = TimeSpan.FromSeconds(5);

    static AcpEventEnvelope User(string itemId) => new() { Kind = AcpEventKind.UserMessage, Text = "go", ItemId = itemId };

    static AcpTranscriptForwarder Forwarder(Channel<AcpEventEnvelope> channel, Func<AcpEventEnvelope[], AcpBatchAck> ack) =>
        new((batch, _) => Task.FromResult(ack(batch)), new AcpEventEnvelope { Kind = AcpEventKind.SessionStarted }, channel.Reader,
            NullLogger.Instance, TimeProvider.System, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5));

    [Test]
    public async Task A_user_message_is_persisted_once_an_ack_reaches_its_seq() {
        var channel   = Channel.CreateUnbounded<AcpEventEnvelope>();
        var forwarder = Forwarder(channel, batch => new AcpBatchAck(batch[^1].Seq, batch[^1].Seq));
        using var cts = new CancellationTokenSource();
        var run = forwarder.RunAsync(cts.Token);

        channel.Writer.TryWrite(User("m1"));
        var persisted = await forwarder.AwaitPersistedAsync(["m1"], Ample, CancellationToken.None);

        await Assert.That(persisted.Keys).IsEquivalentTo(new[] { "m1" });
        await cts.CancelAsync();
        await run;
    }

    /// Accepted is not stored: the evidence read finds only what the server persisted.
    [Test]
    public async Task A_message_accepted_but_not_persisted_by_the_deadline_is_left_out() {
        var channel   = Channel.CreateUnbounded<AcpEventEnvelope>();
        var forwarder = Forwarder(channel, batch => new AcpBatchAck(batch[^1].Seq, 0));
        using var cts = new CancellationTokenSource();
        var run = forwarder.RunAsync(cts.Token);

        channel.Writer.TryWrite(User("m1"));
        var persisted = await forwarder.AwaitPersistedAsync(["m1"], TimeSpan.FromMilliseconds(300), CancellationToken.None);

        await Assert.That(persisted).IsEmpty();
        await cts.CancelAsync();
        await run;
    }

    [Test]
    public async Task An_item_the_forwarder_never_saw_is_left_out() {
        var channel   = Channel.CreateUnbounded<AcpEventEnvelope>();
        var forwarder = Forwarder(channel, batch => new AcpBatchAck(batch[^1].Seq, batch[^1].Seq));
        using var cts = new CancellationTokenSource();
        var run = forwarder.RunAsync(cts.Token);

        channel.Writer.TryWrite(User("m1"));
        var persisted = await forwarder.AwaitPersistedAsync(["m1", "unknown"], TimeSpan.FromMilliseconds(300), CancellationToken.None);

        await Assert.That(persisted.Keys).IsEquivalentTo(new[] { "m1" });
        await cts.CancelAsync();
        await run;
    }

    /// A forwarder that stopped stores nothing more, so a waiter returns at once rather than spending its budget.
    [Test]
    public async Task A_waiter_returns_at_once_when_the_forwarder_stops() {
        var channel   = Channel.CreateUnbounded<AcpEventEnvelope>();
        var forwarder = Forwarder(channel, batch => new AcpBatchAck(batch[^1].Seq, 0));
        var run = forwarder.RunAsync(CancellationToken.None);

        channel.Writer.TryWrite(User("m1"));
        var waiting = forwarder.AwaitPersistedAsync(["m1"], TimeSpan.FromMinutes(1), CancellationToken.None);
        channel.Writer.Complete();
        await run;

        await Assert.That(await waiting.WaitAsync(TimeSpan.FromSeconds(5))).IsEmpty();
    }

    [Test]
    public async Task A_rejected_binding_persists_nothing() {
        var channel   = Channel.CreateUnbounded<AcpEventEnvelope>();
        var forwarder = Forwarder(channel, _ => new AcpBatchAck(-1, -1, null, Rejected: true));
        var run = forwarder.RunAsync(CancellationToken.None);

        channel.Writer.TryWrite(User("m1"));
        await run;
        var persisted = await forwarder.AwaitPersistedAsync(["m1"], TimeSpan.FromMilliseconds(300), CancellationToken.None);

        await Assert.That(persisted).IsEmpty();
    }
}
