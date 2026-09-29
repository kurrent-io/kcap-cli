using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Harness.Titles;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Harness.Titles;

// Every test here spins a real WireMockServer and polls it on a real-time loop while a
// FakeTimeProvider drives production backoff delays; three of these running concurrently
// contend for the same process-wide thread pool and can starve each other's timers.
[NotInParallel(nameof(ImportHarnessTitleTests))]
public class ImportHarnessTitleTests {
    sealed class CollectingProgress : IProgress<ImportProgress> {
        public List<ImportProgress> Reported { get; } = [];
        public void Report(ImportProgress value) => Reported.Add(value);
    }

    // Generous, not tuned to a fast machine: the point of this bound is only to fail loudly instead
    // of hanging if a request never lands at all, never to race a loaded CI/dev box.
    static readonly TimeSpan RequestWait = TimeSpan.FromSeconds(60);

    // One nudge, not the whole backoff in a single jump — see AdvanceUntilRequestAsync.
    static readonly TimeSpan ClockStep = TimeSpan.FromMilliseconds(50);

    /// <summary>Blocks on REAL wall-clock time (never the <see cref="FakeTimeProvider"/> under test) until
    /// WireMock has logged <paramref name="expectedCount"/> requests to <paramref name="path"/>, or throws.
    /// Used for the first request, which an attempt already in flight produces with no clock help at
    /// all.</summary>
    static async Task WaitForRequestCountAsync(WireMockServer server, string path, int expectedCount, TimeSpan realTimeout) {
        var deadline = DateTime.UtcNow + realTimeout;

        while (server.LogEntries.Count(e => e.RequestMessage.Path == path) < expectedCount) {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {expectedCount} request(s) to {path}.");

            await Task.Delay(5);
        }
    }

    /// <summary>Advances the fake BACKOFF clock in small nudges, real-sleeping between them, until the next
    /// attempt's request lands — rather than jumping the FULL backoff in one <c>Advance</c> call.
    ///
    /// <para>A single big jump can land BEFORE <c>ImportHarnessTitle.PostAsync</c>'s continuation actually
    /// reaches its <c>Task.Delay(delay, backoffTime, ct)</c> call: the request landing at the server
    /// (observed here) and that continuation resuming are on different threads. When that race is lost, the
    /// eventual timer's due time becomes <c>(already-advanced now) + delay</c> — a full backoff further out
    /// than intended — and nothing ever advances the clock that far again, hanging for the rest of this
    /// method's real-time budget. Repeated small nudges close that window: whichever nudge lands after the
    /// timer exists is enough to cross its due time, however many nudges that takes.</para>
    ///
    /// <para>This clock only ever drives <c>PostAsync</c>'s backoff <c>Task.Delay</c> — the tests pass a
    /// SEPARATE, real <see cref="TimeProvider"/> for the per-attempt HTTP timeout in
    /// <c>HarnessTitleClient</c>/<c>SendWithRetryAsync</c>, so nudging this one can never cut off an
    /// in-flight request the way a single shared clock could.</para></summary>
    static async Task AdvanceUntilRequestAsync(WireMockServer server, string path, FakeTimeProvider time, int expectedCount) {
        var deadline = DateTime.UtcNow + RequestWait;

        while (server.LogEntries.Count(e => e.RequestMessage.Path == path) < expectedCount) {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {expectedCount} request(s) to {path}.");

            await Task.Delay(5);
            time.Advance(ClockStep);
        }
    }

    /// <summary>Drives the fake clock through exactly <paramref name="totalAttempts"/> attempts, one
    /// request at a time.</summary>
    static async Task DriveRetriesAsync(WireMockServer server, string path, FakeTimeProvider time, int totalAttempts) {
        await WaitForRequestCountAsync(server, path, 1, RequestWait);

        for (var i = 1; i < totalAttempts; i++) await AdvanceUntilRequestAsync(server, path, time, i + 1);
    }

    /// <summary>A freshly started WireMock server's very first request can be slow; paying that cost
    /// before the timed run keeps <see cref="WaitForRequestCountAsync"/>'s bound tight.</summary>
    static async Task WarmUpAsync(WireMockServer server, HttpClient client) {
        try { using var _ = await client.GetAsync($"{server.Url}/__warmup"); } catch { /* status irrelevant */ }
    }

    [Test]
    public async Task Retries_session_not_found_until_the_projection_catches_up() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost()).InScenario("lag").WillSetStateTo("ready")
              .RespondWith(Response.Create().WithStatusCode(404).WithBody("""{"error":"session_not_found"}"""));
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost()).InScenario("lag").WhenStateIs("ready")
              .RespondWith(Response.Create().WithStatusCode(200));
        var time = new FakeTimeProvider();
        using var client = new HttpClient();
        await WarmUpAsync(server, client);

        var task = ImportHarnessTitle.PostAsync(client, time, TimeProvider.System, server.Url!, "s", new("T", HarnessTitleKind.Auto, null), progress: null, default);
        await DriveRetriesAsync(server, "/hooks/harness-title", time, totalAttempts: 2);
        await task;

        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path == "/hooks/harness-title")).IsEqualTo(2);
    }

    [Test]
    public async Task Gives_up_after_about_a_minute_and_warns() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(404).WithBody("""{"error":"session_not_found"}"""));
        var time = new FakeTimeProvider();
        using var client = new HttpClient();
        await WarmUpAsync(server, client);
        var progress = new CollectingProgress();

        var task = ImportHarnessTitle.PostAsync(client, time, TimeProvider.System, server.Url!, "s", new("T", HarnessTitleKind.Auto, null), progress, default);
        await DriveRetriesAsync(server, "/hooks/harness-title", time, totalAttempts: 8);
        await task;

        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path == "/hooks/harness-title")).IsEqualTo(8);
        await Assert.That(progress.Reported.OfType<ImportTitleNotRecorded>().Count()).IsEqualTo(1);
        await Assert.That(progress.Reported.OfType<ImportTitleNotRecorded>().Single().SessionId).IsEqualTo("s");
    }

    [Test]
    public async Task Older_server_falls_back_to_set_title() {
        using var server = WireMockServer.Start();
        // /hooks/harness-title is deliberately left unstubbed — WireMock answers a bare 404.
        server.Given(Request.Create().WithPath("/hooks/set-title").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200));
        var time = new FakeTimeProvider();
        using var client = new HttpClient();
        await WarmUpAsync(server, client);
        var progress = new CollectingProgress();

        await ImportHarnessTitle.PostAsync(client, time, server.Url!, "s", new("T", HarnessTitleKind.Auto, null), progress, default);

        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path == "/hooks/set-title")).IsEqualTo(1);
        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path == "/hooks/harness-title")).IsEqualTo(1);
        await Assert.That(progress.Reported.OfType<ImportTitleNotRecorded>()).IsEmpty();
    }

    [Test]
    public async Task A_transient_status_is_retried_rather_than_ending_the_attempt() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost()).InScenario("blip").WillSetStateTo("ok")
              .RespondWith(Response.Create().WithStatusCode(503));
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost()).InScenario("blip").WhenStateIs("ok")
              .RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();
        await WarmUpAsync(server, client);
        var progress = new CollectingProgress();

        await ImportHarnessTitle.PostAsync(client, TimeProvider.System, server.Url!, "s", new("T", HarnessTitleKind.Auto, null), progress, default);

        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path == "/hooks/harness-title")).IsEqualTo(2);
        await Assert.That(progress.Reported.OfType<ImportTitleNotRecorded>()).IsEmpty();
    }
}
