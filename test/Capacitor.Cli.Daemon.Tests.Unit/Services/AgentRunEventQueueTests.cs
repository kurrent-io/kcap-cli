using System.Net;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class AgentRunEventQueueTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    [Arguments(HttpStatusCode.BadRequest)]
    [Arguments(HttpStatusCode.NotFound)]
    [Arguments(HttpStatusCode.RequestEntityTooLarge)]
    public async Task A_refused_head_event_is_dropped_once_and_the_events_behind_it_are_delivered(HttpStatusCode refusal) {
        var server = new ScriptedServer((reason, _) => reason == "head" ? refusal : HttpStatusCode.OK);

        await using var run = Start(server, "head", "next");
        await run.PumpUntilAsync(() => server.Delivered.Count == 1);

        await Assert.That(server.Delivered).IsEquivalentTo(["next"]);
        await Assert.That(server.Attempts("head")).IsEqualTo(1);
        await Assert.That(run.Logger.Warnings.Any(w => w.Contains("AgentRunStopped") && w.Contains($"{(int)refusal}"))).IsTrue();
    }

    [Test]
    public async Task A_503_is_retried_and_delivered_in_order_once_the_server_recovers() {
        var server = new ScriptedServer((reason, attempt) =>
            reason == "head" && attempt <= 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);

        await using var run = Start(server, "head", "next");
        await run.PumpUntilAsync(() => server.Delivered.Count == 2);

        await Assert.That(server.Delivered).IsEquivalentTo(["head", "next"], CollectionOrdering.Matching);
        await Assert.That(server.Attempts("head")).IsEqualTo(4);
    }

    [Test]
    public async Task A_head_event_that_keeps_failing_with_500_is_dropped_after_the_bound() {
        var server = new ScriptedServer((reason, _) =>
            reason == "head" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);

        await using var run = Start(server, "head", "next");
        await run.PumpUntilAsync(() => server.Delivered.Count == 1);

        await Assert.That(server.Delivered).IsEquivalentTo(["next"]);
        await Assert.That(server.Attempts("head")).IsEqualTo(AgentRunEventQueue.MaxRetryableResponses);
    }

    /// <summary>Pins that an unreachable server does not spend the bound: the head outlasts more
    /// transport failures than the bound allows and is still delivered.</summary>
    [Test]
    public async Task Transport_failures_do_not_count_toward_the_bound() {
        var failures = AgentRunEventQueue.MaxRetryableResponses + 5;
        var server = new ScriptedServer((reason, attempt) =>
            reason == "head" && attempt <= failures
                ? throw new HttpRequestException("connection refused")
                : HttpStatusCode.OK);

        await using var run = Start(server, "head", "next");
        await run.PumpUntilAsync(() => server.Delivered.Count == 2);

        await Assert.That(server.Delivered).IsEquivalentTo(["head", "next"], CollectionOrdering.Matching);
    }

    Run Start(ScriptedServer server, params string[] reasons) {
        var time   = new FakeTimeProvider();
        var logger = new CapturingLogger();
        var queue = new AgentRunEventQueue(
            new DaemonConfig { Name = "queue-test", ServerUrl = "http://server.test", Profiles = Resolutions.None(Config.Root) },
            AuthFixtures.NewTokenStore(Config.Root), time, logger, new HttpClient(server));

        foreach (var reason in reasons) queue.Enqueue("agent-1", new AgentRunStopped(reason, 0));

        var cts = new CancellationTokenSource();

        return new(queue, time, logger, cts, queue.RunAsync(cts.Token));
    }

    sealed record Run(AgentRunEventQueue Queue, FakeTimeProvider Time, CapturingLogger Logger, CancellationTokenSource Cts, Task Drain)
        : IAsyncDisposable {
        public async Task PumpUntilAsync(Func<bool> done) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

            while (!done()) {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"the queue did not reach the expected state; last warning: {string.Join(" | ", Logger.Warnings)}");

                Time.Advance(TimeSpan.FromSeconds(30));
                await Task.Delay(5);
            }
        }

        public async ValueTask DisposeAsync() {
            await Cts.CancelAsync();
            await Drain;
            Queue.Dispose();
            Cts.Dispose();
        }
    }

    /// <summary>Answers each post by the event's reason and its 1-based attempt number, and records
    /// the reasons it accepted in arrival order.</summary>
    sealed class ScriptedServer(Func<string, int, HttpStatusCode> answer) : HttpMessageHandler {
        readonly Dictionary<string, int> _attempts  = [];
        readonly List<string>            _delivered = [];

        public IReadOnlyList<string> Delivered { get { lock (_delivered) return [.. _delivered]; } }

        public int Attempts(string reason) { lock (_attempts) return _attempts.GetValueOrDefault(reason); }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var reason = body.RootElement.GetProperty("data").GetProperty("reason").GetString()!;

            int attempt;
            lock (_attempts) attempt = _attempts[reason] = _attempts.GetValueOrDefault(reason) + 1;

            var status = answer(reason, attempt);

            if ((int)status < 300) lock (_delivered) _delivered.Add(reason);

            return new(status);
        }
    }
}
