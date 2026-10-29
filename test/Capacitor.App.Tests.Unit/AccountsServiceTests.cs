using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Capacitor.App.Services;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class AccountsServiceTests {
    static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    static AccountsSnapshotDto Snap(long revision) => new("host-1", revision, [], []);

    static Task<bool> Bounded(Task<bool> task) => task.WaitAsync(TimeSpan.FromSeconds(5));

    sealed class FakeAccountsStream {
        readonly Channel<AccountsStreamEvent?> _channel = Channel.CreateUnbounded<AccountsStreamEvent?>();
        int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);

        public async IAsyncEnumerable<AccountsStreamEvent> RunAsync([EnumeratorCancellation] CancellationToken ct) {
            Interlocked.Increment(ref _attempts);
            await foreach (var evt in _channel.Reader.ReadAllAsync(ct)) {
                if (evt is null) yield break;
                yield return evt;
            }
        }

        public void EmitSubscribed() => _channel.Writer.TryWrite(new AccountsStreamEvent.Subscribed());
        public void EmitSnapshot(AccountsSnapshotDto snapshot) => _channel.Writer.TryWrite(new AccountsStreamEvent.Snapshot(snapshot));
        public void EndAttempt() => _channel.Writer.TryWrite(null);
    }

    sealed class Harness : IDisposable {
        public readonly FakeDaemonClientService Daemon = new();
        public readonly FakeAccountsStream Stream = new();
        public readonly FakeTimeProvider Clock = new();
        public readonly TimerCountingTimeProvider Time;
        public readonly AccountsService Service;

        readonly IDisposable _snapshotSub;
        readonly IDisposable _supportedSub;
        readonly Lock _lock = new();
        readonly List<long?> _published = [];
        AccountsSnapshotDto? _held;
        bool _supported;

        public Harness() {
            Time = new TimerCountingTimeProvider(Clock);
            Service = new AccountsService(Daemon, Stream.RunAsync, Time, CancellationToken.None);
            _snapshotSub = Service.Snapshot.Subscribe(s => {
                lock (_lock) _published.Add(s?.Revision);
                Volatile.Write(ref _held, s);
            });
            _supportedSub = Service.Supported.Subscribe(v => Volatile.Write(ref _supported, v));
        }

        public AccountsSnapshotDto? Held => Volatile.Read(ref _held);
        public bool Supported => Volatile.Read(ref _supported);

        /// Every revision Snapshot published, in order and comma-joined, "null" for a cleared snapshot.
        public string Published() { lock (_lock) return string.Join(",", _published.Select(r => r is { } v ? $"{v}" : "null")); }

        public void Connect(params string[] caps) => Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, caps));

        public void Disconnect() => Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));

        public async Task StartAsync() {
            Connect("status/1", "accounts/1");
            await WaitUntilAsync(() => Stream.Attempts == 1, what: "the subscribe attempt");
            Stream.EmitSubscribed();
        }

        public async Task EmitAsync(long revision) {
            Stream.EmitSnapshot(Snap(revision));
            await WaitUntilAsync(() => Held?.Revision == revision, what: $"revision {revision} published");
        }

        public void Dispose() {
            Service.Dispose();
            _snapshotSub.Dispose();
            _supportedSub.Dispose();
        }
    }

    [Test]
    public async Task No_subscription_without_the_capability() {
        using var h = new Harness();
        h.Connect("status/1");
        await Task.Delay(50);

        await Assert.That(h.Stream.Attempts).IsEqualTo(0);
        await Assert.That(h.Supported).IsFalse();
        await Assert.That(h.Held).IsNull();
    }

    [Test]
    public async Task A_snapshot_is_published_once_the_daemon_sends_it() {
        using var h = new Harness();
        h.Connect("status/1", "accounts/1");
        await WaitUntilAsync(() => h.Stream.Attempts == 1, what: "the subscribe attempt");
        await WaitUntilAsync(() => h.Supported, what: "supported");
        h.Stream.EmitSubscribed();
        await Assert.That(h.Held).IsNull();

        await h.EmitAsync(3);

        await Assert.That(h.Held!.Revision).IsEqualTo(3);
        await Assert.That(h.Held.HostId).IsEqualTo("host-1");
    }

    [Test]
    public async Task Losing_the_connection_publishes_null_and_false() {
        using var h = new Harness();
        await h.StartAsync();
        await h.EmitAsync(3);
        await WaitUntilAsync(() => h.Supported, what: "supported");

        h.Disconnect();
        await WaitUntilAsync(() => h.Held is null && !h.Supported, what: "null and false after the connection went");

        h.Connect("accounts/1");
        await WaitUntilAsync(() => h.Stream.Attempts == 2 && h.Supported, what: "resubscribe");
        await h.EmitAsync(4);

        h.Connect("status/1");
        await WaitUntilAsync(() => h.Held is null && !h.Supported, what: "null and false after the capability went");
    }

    [Test]
    public async Task An_attempt_that_ends_is_retried_after_a_second_and_keeps_the_snapshot_meanwhile() {
        using var h = new Harness();
        await h.StartAsync();
        await h.EmitAsync(3);

        h.Stream.EndAttempt();
        await WaitUntilAsync(() => h.Time.TimersCreated == 1, what: "the 1s retry delay to be armed");
        await Assert.That(h.Stream.Attempts).IsEqualTo(1);
        await Assert.That(h.Held!.Revision).IsEqualTo(3);

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => h.Stream.Attempts == 2, what: "the resubscribe");
        h.Stream.EmitSubscribed();
        await h.EmitAsync(4);

        await Assert.That(h.Published()).IsEqualTo("null,3,4");
    }

    [Test]
    public async Task ReachedAsync_answers_true_for_a_later_revision_false_on_timeout_and_false_when_the_snapshot_drops() {
        using var h = new Harness();
        await h.StartAsync();
        await h.EmitAsync(3);

        await Assert.That(await Bounded(h.Service.ReachedAsync(3, Generous, CancellationToken.None))).IsTrue();
        await Assert.That(await Bounded(h.Service.ReachedAsync(2, Generous, CancellationToken.None))).IsTrue();

        var later = h.Service.ReachedAsync(5, Generous, CancellationToken.None);
        await Assert.That(later.IsCompleted).IsFalse();
        await h.EmitAsync(7);
        await Assert.That(await Bounded(later)).IsTrue();

        var timedOut = h.Service.ReachedAsync(9, TimeSpan.FromSeconds(5), CancellationToken.None);
        await Assert.That(timedOut.IsCompleted).IsFalse();
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        await Assert.That(await Bounded(timedOut)).IsFalse();

        var dropped = h.Service.ReachedAsync(9, Generous, CancellationToken.None);
        await Assert.That(dropped.IsCompleted).IsFalse();
        h.Disconnect();
        await Assert.That(await Bounded(dropped)).IsFalse();
        await Assert.That(await Bounded(h.Service.ReachedAsync(9, Generous, CancellationToken.None))).IsFalse();
    }

    [Test]
    public async Task ReachedAsync_waits_for_the_first_snapshot_of_a_live_subscription() {
        using var h = new Harness();
        await h.StartAsync();

        var first = h.Service.ReachedAsync(1, Generous, CancellationToken.None);
        await Assert.That(first.IsCompleted).IsFalse();
        await h.EmitAsync(1);

        await Assert.That(await Bounded(first)).IsTrue();
    }

    [Test]
    public async Task ReachedAsync_ends_with_the_callers_cancellation_and_with_disposal() {
        using var h = new Harness();
        await h.StartAsync();
        await h.EmitAsync(1);
        using var cancellation = new CancellationTokenSource();

        var cancelled = h.Service.ReachedAsync(5, Generous, cancellation.Token);
        var disposed = h.Service.ReachedAsync(5, Generous, CancellationToken.None);
        cancellation.Cancel();
        await Assert.That(async () => await Bounded(cancelled)).Throws<OperationCanceledException>();

        h.Service.Dispose();
        await Assert.That(await Bounded(disposed)).IsFalse();
        await Assert.That(await Bounded(h.Service.ReachedAsync(5, Generous, CancellationToken.None))).IsFalse();
    }
}
