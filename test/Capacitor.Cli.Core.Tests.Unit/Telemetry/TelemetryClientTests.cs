using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

public class TelemetryClientTests {
    sealed class StubHandler(HttpStatusCode status, Exception? throws = null) : HttpMessageHandler {
        public int    Calls    { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken ct) {
            Calls++;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            if (throws is not null) throw throws;

            return new HttpResponseMessage(status);
        }
    }

    static TelemetryEvent Event(string name) =>
        new(name, new JsonObject { ["source"] = "cli" }, DateTimeOffset.UnixEpoch);

    static TelemetryClient Client(StubHandler handler, string spoolPath, out TelemetrySpool spool) {
        spool = new TelemetrySpool(spoolPath);
        return new TelemetryClient(handler, spool, "phc_test", "https://phog.example", TimeProvider.System);
    }

    // Deterministic stand-in for real elapsed time: FlushAsync calls GetTimestamp() once at
    // entry and once more (via GetElapsedTime) after the drain/serialize phase, so two calls
    // apart by a fixed step simulates "that phase alone took `step`" without racing real
    // wall-clock timing or needing an artificially slow spool.
    sealed class FakeTimeProvider(TimeSpan step) : TimeProvider {
        long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() {
            var t = _timestamp;
            _timestamp += step.Ticks;
            return t;
        }
    }

    [Test]
    public async Task Flush_with_empty_queue_makes_no_request() {
        var handler = new StubHandler(HttpStatusCode.OK);
        using var tmp = TempDir.WithPathTo("spool.jsonl", out var spoolPath);
        var client  = Client(handler, spoolPath, out _);

        await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        await Assert.That(handler.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Flush_posts_queued_events() {
        var handler = new StubHandler(HttpStatusCode.OK);
        using var tmp = TempDir.WithPathTo("spool.jsonl", out var spoolPath);
        var client  = Client(handler, spoolPath, out _);
        client.Enqueue(Event("cli_command"));

        var ok = await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        await Assert.That(ok).IsTrue();
        await Assert.That(handler.Calls).IsEqualTo(1);
        await Assert.That(handler.LastBody!.Contains("cli_command")).IsTrue();
        await Assert.That(handler.LastBody!.Contains("device-1")).IsTrue();
    }

    [Test]
    public async Task Successful_flush_empties_the_queue() {
        var handler = new StubHandler(HttpStatusCode.OK);
        using var tmp = TempDir.WithPathTo("spool.jsonl", out var spoolPath);
        var client  = Client(handler, spoolPath, out _);
        client.Enqueue(Event("cli_command"));

        await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));
        await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Failed_flush_spills_to_the_spool() {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable);
        using var tmp = TempDir.WithPathTo("spool.jsonl", out var spoolPath);
        var client  = Client(handler, spoolPath, out var spool);
        client.Enqueue(Event("cli_command"));

        var ok = await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        await Assert.That(ok).IsFalse();
        await Assert.That(spool.DrainAll().Count).IsEqualTo(1);
    }

    [Test]
    public async Task Network_exception_spills_rather_than_propagating() {
        var handler = new StubHandler(HttpStatusCode.OK, new HttpRequestException("offline"));
        using var tmp = TempDir.WithPathTo("spool.jsonl", out var spoolPath);
        var client  = Client(handler, spoolPath, out var spool);
        client.Enqueue(Event("cli_command"));

        var ok = await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        await Assert.That(ok).IsFalse();
        await Assert.That(spool.DrainAll().Count).IsEqualTo(1);
    }

    [Test]
    public async Task Spooled_events_are_replayed_on_the_next_flush() {
        var failing = new StubHandler(HttpStatusCode.ServiceUnavailable);
        using var tmp = TempDir.WithPathTo("spool.jsonl", out var spoolPath);
        var spool   = new TelemetrySpool(spoolPath);

        var first = new TelemetryClient(failing, spool, "phc_test", "https://phog.example", TimeProvider.System);
        first.Enqueue(Event("offline_event"));
        await first.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        var ok      = new StubHandler(HttpStatusCode.OK);
        var second  = new TelemetryClient(ok, spool, "phc_test", "https://phog.example", TimeProvider.System);
        second.Enqueue(Event("fresh_event"));
        var flushed = await second.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        await Assert.That(flushed).IsTrue();
        await Assert.That(ok.LastBody!.Contains("offline_event")).IsTrue();
        await Assert.That(ok.LastBody!.Contains("fresh_event")).IsTrue();
        // Ordering: spooled events (offline) come before queued events (fresh)
        await Assert.That(ok.LastBody!.IndexOf("offline_event", StringComparison.Ordinal) < ok.LastBody!.IndexOf("fresh_event", StringComparison.Ordinal)).IsTrue();
        await Assert.That(spool.DrainAll().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Repeated_failures_do_not_duplicate_spooled_events() {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable);
        using var tmp = TempDir.WithPathTo("spool.jsonl", out var spoolPath);
        var client  = Client(handler, spoolPath, out var spool);
        client.Enqueue(Event("cli_command"));

        await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));
        await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        await Assert.That(spool.DrainAll().Count).IsEqualTo(1);
    }

    // Each clock read advances one second: the check before the drain sees 1s of a 1.5s budget and goes on,
    // the check after the build sees 2s and must spill rather than post.
    [Test]
    public async Task Budget_already_exhausted_by_drain_and_build_spills_without_a_request() {
        var handler = new StubHandler(HttpStatusCode.OK);
        using var tmp = TempDir.WithPathTo("spool.jsonl", out var spoolPath);
        var spool   = new TelemetrySpool(spoolPath);
        var clock   = new FakeTimeProvider(TimeSpan.FromSeconds(1));
        var client  = new TelemetryClient(handler, spool, "phc_test", "https://phog.example", clock);
        client.Enqueue(Event("cli_command"));

        var ok = await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(1.5));

        await Assert.That(ok).IsFalse();
        await Assert.That(handler.Calls).IsEqualTo(0);
        await Assert.That(spool.DrainAll().Count).IsEqualTo(1);
    }

    // Sanity check for the same fake clock: when the simulated drain/serialize phase leaves
    // budget to spare, the HTTP phase still proceeds normally and can succeed. Without this, a
    // bug that made the "exhausted" branch fire unconditionally would still pass the test above.
    [Test]
    public async Task Budget_with_time_remaining_after_drain_and_build_still_posts() {
        var handler = new StubHandler(HttpStatusCode.OK);
        using var tmp = TempDir.WithPathTo("spool.jsonl", out var spoolPath);
        var spool   = new TelemetrySpool(spoolPath);
        var clock   = new FakeTimeProvider(TimeSpan.FromMilliseconds(1));
        var client  = new TelemetryClient(handler, spool, "phc_test", "https://phog.example", clock);
        client.Enqueue(Event("cli_command"));

        var ok = await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        await Assert.That(ok).IsTrue();
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    /// The first request is held until released and succeeds; every later one fails.
    sealed class HeldThenFailingHandler : HttpMessageHandler {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _calls;

        public int Requests => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            if (Interlocked.Increment(ref _calls) > 1) return new HttpResponseMessage(HttpStatusCode.InternalServerError);

            await Release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Test]
    public async Task An_overlapping_flush_never_clears_events_another_flush_spilled() {
        var handler = new HeldThenFailingHandler();
        using var tmp = TempDir.WithPathTo("spool.jsonl", out var spoolPath);
        var spool  = new TelemetrySpool(spoolPath);
        var client = new TelemetryClient(handler, spool, "phc_test", "https://phog.example", TimeProvider.System);

        client.Enqueue(Event("held"));
        var held = client.FlushAsync("device-1", null, TimeSpan.FromSeconds(10));
        client.Enqueue(Event("spilled"));
        var failing = client.FlushAsync("device-1", null, TimeSpan.FromSeconds(10));
        handler.Release.SetResult();

        await Assert.That(await held).IsTrue();
        await Assert.That(await failing).IsFalse();
        await Assert.That(spool.DrainAll().Select(e => e.Name)).IsEquivalentTo(["spilled"]);
    }

    [Test]
    public async Task A_flush_that_spends_its_budget_waiting_spills_its_queue_without_a_request() {
        var handler = new HeldThenFailingHandler();
        using var tmp = TempDir.WithPathTo("spool.jsonl", out var spoolPath);
        var spool  = new TelemetrySpool(spoolPath);
        var client = new TelemetryClient(handler, spool, "phc_test", "https://phog.example", TimeProvider.System);

        client.Enqueue(Event("held"));
        var held = client.FlushAsync("device-1", null, TimeSpan.FromSeconds(10));
        client.Enqueue(Event("waited"));
        var waited = client.FlushAsync("device-1", null, TimeSpan.FromMilliseconds(20));
        await Task.Delay(100);
        handler.Release.SetResult();

        await Assert.That(await held).IsTrue();
        await Assert.That(await waited).IsFalse();
        await Assert.That(handler.Requests).IsEqualTo(1);
        await Assert.That(spool.DrainAll().Select(e => e.Name)).IsEquivalentTo(["waited"]);
    }

    [Test]
    public async Task Org_group_reaches_the_payload() {
        var handler = new StubHandler(HttpStatusCode.OK);
        using var tmp = TempDir.WithPathTo("spool.jsonl", out var spoolPath);
        var client  = Client(handler, spoolPath, out _);
        client.Enqueue(Event("cli_command"));

        await client.FlushAsync("device-1", "acme", TimeSpan.FromSeconds(2));

        await Assert.That(handler.LastBody!.Contains("organization")).IsTrue();
        await Assert.That(handler.LastBody!.Contains("acme")).IsTrue();
    }

    /// <summary>An opt-out leaves nothing for a later flush to send: neither what was queued nor what an
    /// earlier failure parked on disk. A stop that is only this process's own drops its queue but leaves the
    /// spool other processes share.</summary>
    [Test]
    [Arguments(true, 0)]
    [Arguments(false, 1)]
    public async Task After_a_stop_a_flush_sends_nothing_and_only_an_opt_out_empties_the_spool(bool optOut, int parked) {
        var handler = new StubHandler(HttpStatusCode.OK);
        using var tmp = TempDir.WithPathTo("spool.jsonl", out var spoolPath);
        var client  = Client(handler, spoolPath, out var spool);
        spool.Append([Event("parked_event")]);
        client.Enqueue(Event("queued_event"));

        if (optOut) client.Discard();
        else client.Abandon();
        await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        await Assert.That(handler.Calls).IsEqualTo(0);
        await Assert.That(spool.DrainAll().Count).IsEqualTo(parked);
    }

    /// <summary>A request already on the wire cannot be recalled, but if it fails it must not park its
    /// events for a later run to send.</summary>
    [Test]
    public async Task A_flush_in_flight_at_the_discard_does_not_spool_its_events_on_failure() {
        var handler = new GatedHandler(HttpStatusCode.ServiceUnavailable);
        using var tmp = TempDir.WithPathTo("spool.jsonl", out var spoolPath);
        var spool  = new TelemetrySpool(spoolPath);
        var client = new TelemetryClient(handler, spool, "phc_test", "https://phog.example", TimeProvider.System);
        client.Enqueue(Event("in_flight_event"));

        var flush = client.FlushAsync("device-1", null, TimeSpan.FromSeconds(10));
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        client.Discard();
        handler.Release.SetResult();
        await flush;

        await Assert.That(spool.DrainAll().Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_discard_cancels_the_send_in_flight() {
        var handler = new GatedHandler(HttpStatusCode.OK);
        using var tmp = TempDir.WithPathTo("spool.jsonl", out var spoolPath);
        var spool  = new TelemetrySpool(spoolPath);
        var client = new TelemetryClient(handler, spool, "phc_test", "https://phog.example", TimeProvider.System);
        client.Enqueue(Event("in_flight_event"));

        var flush = client.FlushAsync("device-1", null, TimeSpan.FromSeconds(30));
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        client.Discard();

        await Assert.That(await flush.WaitAsync(TimeSpan.FromSeconds(10))).IsFalse();
        await Assert.That(spool.DrainAll().Count).IsEqualTo(0);
    }

    /// <summary>An opt-out saved by another process between the capture and the send stops the send and drops
    /// what it would have carried.</summary>
    [Test]
    public async Task A_send_whose_consent_was_withdrawn_does_not_start() {
        var handler = new StubHandler(HttpStatusCode.OK);
        using var tmp = TempDir.WithPathTo("spool.jsonl", out var spoolPath);
        var spool   = new TelemetrySpool(spoolPath);
        var consent = true;
        var client  = new TelemetryClient(handler, spool, "phc_test", "https://phog.example", TimeProvider.System, () => consent);
        client.Enqueue(Event("captured_with_consent"));

        consent = false;

        await Assert.That(await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(10))).IsFalse();
        await Assert.That(handler.Calls).IsEqualTo(0);
        await Assert.That(spool.DrainAll().Count).IsEqualTo(0);

        // Withdrawn, not shut: once the choice is back on, what is captured after goes out.
        consent = true;
        client.Enqueue(Event("captured_after_reconsent"));
        await Assert.That(await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(10))).IsTrue();
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    /// <summary>A consent withdrawn after a failed send keeps its events off the disk too.</summary>
    [Test]
    public async Task A_failed_send_does_not_spill_events_whose_consent_was_withdrawn() {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable);
        using var tmp = TempDir.WithPathTo("spool.jsonl", out var spoolPath);
        var spool   = new TelemetrySpool(spoolPath);
        var asked   = 0;
        var client  = new TelemetryClient(handler, spool, "phc_test", "https://phog.example", TimeProvider.System, () => ++asked == 1);
        client.Enqueue(Event("captured_with_consent"));

        await Assert.That(await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(10))).IsFalse();
        await Assert.That(handler.Calls).IsEqualTo(1);
        await Assert.That(spool.DrainAll().Count).IsEqualTo(0);
    }

    sealed class GatedHandler(HttpStatusCode status) : HttpMessageHandler {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);

            return new HttpResponseMessage(status);
        }
    }
}
