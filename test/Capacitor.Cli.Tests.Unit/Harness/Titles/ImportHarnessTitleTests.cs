using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Harness.Titles;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Harness.Titles;

public class ImportHarnessTitleTests {
    sealed class CollectingProgress : IProgress<ImportProgress> {
        public List<ImportProgress> Reported { get; } = [];
        public void Report(ImportProgress value) => Reported.Add(value);
    }

    /// <summary>Drives a <see cref="FakeTimeProvider"/> forward on a real background timer instead of a
    /// busy-spin loop. A tight <c>while (!task.IsCompleted) { time.Advance(...); await Task.Yield(); }</c>
    /// can race a real in-flight HTTP call: the per-attempt timeout in
    /// <c>HttpClientExtensions.SendWithRetryAsync</c> is built from the SAME provider, and a spin loop can
    /// advance it past that timeout in real microseconds — long before a genuine (if merely slow-to-warm)
    /// localhost round trip returns. A periodic real timer leaves the thread pool free to actually run that
    /// I/O between ticks.</summary>
    static IDisposable PumpFakeClock(FakeTimeProvider time) =>
        new Timer(_ => time.Advance(TimeSpan.FromSeconds(1)), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(20));

    /// <summary>A freshly started WireMock server's very first request can be slow relative to the
    /// per-attempt timeout the pumped clock above races against; paying that cost outside the timed
    /// run keeps it off the fake clock.</summary>
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

        var task = ImportHarnessTitle.PostAsync(client, time, server.Url!, "s", new("T", HarnessTitleKind.Auto, null), progress: null, default);
        using (PumpFakeClock(time)) await task;

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

        var task = ImportHarnessTitle.PostAsync(client, time, server.Url!, "s", new("T", HarnessTitleKind.Auto, null), progress, default);
        using (PumpFakeClock(time)) await task;

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
}
