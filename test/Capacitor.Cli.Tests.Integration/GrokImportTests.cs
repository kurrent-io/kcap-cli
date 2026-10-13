using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.Grok;
using Capacitor.Cli.Harness.Grok;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// <c>kcap import --grok</c> against a stub server: a parent session and the subagent its transcript
/// names, laid out as Grok writes them, sibling session directories under the encoded cwd.
/// </summary>
public class GrokImportTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    const string Parent = "01a1359d-bed2-7ce1-bad9-a99e91d9994e";
    const string Child  = "01a135a0-64af-7661-94e6-282ec3ef065c";

    public void Dispose() => _server.Stop();

    static string Line(string sid, string kind, string extra, string method = "session/update") =>
        "{\"timestamp\":1,\"method\":\"" + method + "\",\"params\":{\"sessionId\":\"" + sid
      + "\",\"update\":{\"sessionUpdate\":\"" + kind + "\"," + extra + "},\"_meta\":{\"eventId\":\"" + sid + "-" + kind + "\"}}}";

    void WriteFixture() {
        var cwdDir = Path.Combine(Home.Path, ".grok", "sessions", "%2Frepo%2Fdemo");

        Directory.CreateDirectory(Path.Combine(cwdDir, Parent));
        File.WriteAllLines(Path.Combine(cwdDir, Parent, "updates.jsonl"), [
            Line(Parent, "user_message_chunk", "\"content\":{\"type\":\"text\",\"text\":\"check the tests\"}"),
            Line(Parent, "subagent_spawned", $"\"child_session_id\":\"{Child}\",\"subagent_type\":\"general-purpose\"", "_x.ai/session/update"),
            Line(Parent, "subagent_finished", $"\"child_session_id\":\"{Child}\",\"status\":\"completed\"", "_x.ai/session/update"),
        ]);
        File.WriteAllText(Path.Combine(cwdDir, Parent, "summary.json"), """{"info":{"cwd":"/repo/demo"},"created_at":"2026-10-13T14:15:41Z"}""");

        Directory.CreateDirectory(Path.Combine(cwdDir, Child));
        File.WriteAllLines(Path.Combine(cwdDir, Child, "updates.jsonl"), [
            Line(Child, "agent_message_chunk", "\"content\":{\"type\":\"text\",\"text\":\"three test projects\"}"),
        ]);
        File.WriteAllText(Path.Combine(cwdDir, Child, "summary.json"), """{"info":{"cwd":"/repo/demo"},"session_kind":"subagent"}""");
    }

    [Test]
    public async Task A_session_imports_with_its_subagent_under_the_grok_routes() {
        WriteFixture();
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));
        foreach (var route in new[] { "/hooks/session-start/grok", "/hooks/transcript", "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-end/grok" })
            _server.Given(Request.Create().WithPath(route).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();
        var source = new GrokImportSource(new GrokPaths(Home, null), TimeProvider.System);

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        await Assert.That(discovered.Select(d => d.SessionId)).IsEquivalentTo(new[] { Parent.Replace("-", "") });

        var classified = await source.ClassifyAsync(discovered, new ClassifyContext(client, _server.Url!, MinLines: 0, Home: Home), CancellationToken.None);
        var result     = await source.ImportSessionAsync(classified.Single(), new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Loaded);
        await Assert.That(result.SentChildContent).IsTrue();

        var posts = _server.LogEntries.Where(e => e.RequestMessage.Method == "POST").Select(e => e.RequestMessage.Path).ToList();
        await Assert.That(posts).IsEquivalentTo(new[] {
            "/hooks/session-start/grok", "/hooks/transcript", "/hooks/subagent-start",
            "/hooks/transcript", "/hooks/subagent-stop", "/hooks/session-end/grok"
        }, TUnit.Assertions.Enums.CollectionOrdering.Matching);

        using var start = JsonDocument.Parse(_server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/session-start/grok").RequestMessage.Body!);
        await Assert.That(start.RootElement.GetProperty("origin").GetString()).IsEqualTo(ImportOrigins.Historical);

        using var sub = JsonDocument.Parse(_server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/subagent-start").RequestMessage.Body!);
        await Assert.That(sub.RootElement.GetProperty("agent_id").GetString()).IsEqualTo(Child.Replace("-", ""));
        await Assert.That(sub.RootElement.GetProperty("agent_type").GetString()).IsEqualTo("general-purpose");

        var batches = _server.LogEntries.Where(e => e.RequestMessage.Path == "/hooks/transcript")
            .Select(e => JsonDocument.Parse(e.RequestMessage.Body!).RootElement).ToList();
        await Assert.That(batches.All(b => b.GetProperty("vendor").GetString() == "grok")).IsTrue();
        await Assert.That(batches[1].GetProperty("agent_id").GetString()).IsEqualTo(Child.Replace("-", ""));
    }
}
