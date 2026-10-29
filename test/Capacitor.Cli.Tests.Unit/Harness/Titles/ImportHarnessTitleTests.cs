using System.Net;
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

    // HTTP responses are synchronous in these retry tests. Only backoff needs a timer;
    // advance repeatedly so a continuation registering the next delay cannot miss the clock.
    static async Task CompleteRetriesAsync(Task task, FakeTimeProvider time) {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!task.IsCompleted) {
            time.Advance(TimeSpan.FromSeconds(15));
            await Task.Delay(1, guard.Token);
        }
        await task;
    }

    [Test]
    public async Task Retries_session_not_found_until_the_projection_catches_up() {
        using var script = new SequencedHttpScript(
            SequencedHttpScript.Reply(HttpStatusCode.NotFound, """{"error":"session_not_found"}"""),
            SequencedHttpScript.Reply(HttpStatusCode.OK));
        var time = new FakeTimeProvider();
        using var client = new HttpClient(script);

        var task = ImportHarnessTitle.PostAsync(client, time, TimeProvider.System, "https://stub.example", "s", new("T", HarnessTitleKind.Auto, null), progress: null, default);
        await CompleteRetriesAsync(task, time);

        await Assert.That(script.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Gives_up_after_about_a_minute_and_warns() {
        using var script = new SequencedHttpScript(
            SequencedHttpScript.Reply(HttpStatusCode.NotFound, """{"error":"session_not_found"}"""));
        var time = new FakeTimeProvider();
        using var client = new HttpClient(script);
        var progress = new CollectingProgress();

        var task = ImportHarnessTitle.PostAsync(client, time, TimeProvider.System, "https://stub.example", "s", new("T", HarnessTitleKind.Auto, null), progress, default);
        await CompleteRetriesAsync(task, time);

        await Assert.That(script.Count).IsEqualTo(8);
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
        var progress = new CollectingProgress();

        await ImportHarnessTitle.PostAsync(client, TimeProvider.System, server.Url!, "s", new("T", HarnessTitleKind.Auto, null), progress, default);

        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path == "/hooks/harness-title")).IsEqualTo(2);
        await Assert.That(progress.Reported.OfType<ImportTitleNotRecorded>()).IsEmpty();
    }
}
