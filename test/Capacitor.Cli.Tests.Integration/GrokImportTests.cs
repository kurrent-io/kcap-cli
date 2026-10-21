using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
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

    static readonly Func<string, Task<RepositoryPayload?>> NoRepository = _ => Task.FromResult<RepositoryPayload?>(null);

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
        var source = new GrokImportSource(new GrokPaths(Home, null), NoRepository, TimeProvider.System);

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

    /// <summary>Grok's own session-start names no repository, so import sends the one detected for the
    /// session's cwd on a transcript batch of its own, ahead of the content, which is how the server
    /// attributes the session to it.</summary>
    [Test]
    public async Task An_imported_session_names_its_repository_before_its_content() {
        WriteFixture();
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));
        foreach (var route in new[] { "/hooks/session-start/grok", "/hooks/transcript", "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-end/grok" })
            _server.Given(Request.Create().WithPath(route).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));

        var detectedFor = new List<string>();
        Func<string, Task<RepositoryPayload?>> detect = cwd => {
            detectedFor.Add(cwd);
            return Task.FromResult<RepositoryPayload?>(new RepositoryPayload { Owner = "kurrent-io", RepoName = "demo", Branch = "main", RemoteUrl = "https://github.com/kurrent-io/demo.git" });
        };

        using var client = new HttpClient();
        var source     = new GrokImportSource(new GrokPaths(Home, null), detect, TimeProvider.System);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered, new ClassifyContext(client, _server.Url!, MinLines: 0, Home: Home), CancellationToken.None);
        await source.ImportSessionAsync(classified.Single(), new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);

        var batches = _server.LogEntries.Where(e => e.RequestMessage.Path == "/hooks/transcript")
            .Select(e => JsonDocument.Parse(e.RequestMessage.Body!).RootElement).ToList();

        await Assert.That(detectedFor).IsEquivalentTo(new[] { "/repo/demo" });
        await Assert.That(batches[0].GetProperty("lines").GetArrayLength()).IsEqualTo(0);
        await Assert.That(batches[0].GetProperty("vendor").GetString()).IsEqualTo("grok");
        await Assert.That(batches[0].GetProperty("repository").GetProperty("owner").GetString()).IsEqualTo("kurrent-io");
        await Assert.That(batches[0].GetProperty("repository").GetProperty("repo_name").GetString()).IsEqualTo("demo");
        await Assert.That(batches[1].GetProperty("lines").GetArrayLength()).IsGreaterThan(0);
    }

    /// <summary>The live watcher records only the parent, so re-importing a fully watched session is
    /// what attaches its subagents. The parent's last mapped line is its first: the subagent lines
    /// after it map to nothing, and must not keep the session from reading as already loaded.</summary>
    [Test]
    public async Task An_already_loaded_parent_still_imports_its_subagents() {
        WriteFixture();
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").WithParam("agentId").UsingGet()).AtPriority(1)
               .RespondWith(Response.Create().WithStatusCode(404));
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet()).AtPriority(2)
               .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":0}"""));
        foreach (var route in new[] { "/hooks/session-start/grok", "/hooks/transcript", "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-end/grok" })
            _server.Given(Request.Create().WithPath(route).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();
        var source = new GrokImportSource(new GrokPaths(Home, null), NoRepository, TimeProvider.System);

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = (await source.ClassifyAsync(discovered, new ClassifyContext(client, _server.Url!, MinLines: 0, Home: Home), CancellationToken.None)).Single();
        await Assert.That(classified.Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);

        var result = await source.ImportSessionAsync(classified, new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Skipped);
        await Assert.That(result.SentChildContent).IsTrue();

        var batches = _server.LogEntries.Where(e => e.RequestMessage.Path == "/hooks/transcript")
            .Select(e => JsonDocument.Parse(e.RequestMessage.Body!).RootElement).ToList();
        await Assert.That(batches.Single().GetProperty("agent_id").GetString()).IsEqualTo(Child.Replace("-", ""));
    }

    /// <summary>Each trailing line is one the server parses but maps to no event, so it never moves the
    /// watermark past the first line.</summary>
    [Test]
    [Arguments("agent_message_chunk", "\"content\":{\"type\":\"text\",\"text\":\"\"}", "session/update")]
    [Arguments("tool_call", "\"title\":\"run\"", "session/update")]
    [Arguments("tool_call_update", "\"toolCallId\":\"c1\",\"status\":\"in_progress\"", "session/update")]
    [Arguments("turn_completed", "\"usage\":{}", "_x.ai/session/update")]
    [Arguments("turn_completed", "\"usage\":{\"modelUsage\":{\"grok-4.5-build\":{\"inputTokens\":0,\"outputTokens\":0}}}", "_x.ai/session/update")]
    public async Task A_trailing_line_the_server_maps_to_nothing_leaves_the_session_loaded(string kind, string extra, string method) {
        var dir = Path.Combine(Home.Path, ".grok", "sessions", "%2Frepo%2Fdemo", Parent);
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "updates.jsonl"), [
            Line(Parent, "user_message_chunk", "\"content\":{\"type\":\"text\",\"text\":\"hi\"}"),
            Line(Parent, kind, extra, method),
        ]);
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":0}"""));

        using var client = new HttpClient();
        var source = new GrokImportSource(new GrokPaths(Home, null), NoRepository, TimeProvider.System);

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered, new ClassifyContext(client, _server.Url!, MinLines: 0, Home: Home), CancellationToken.None);

        await Assert.That(classified.Single().Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);
    }

    /// <summary>A later batch landing past a refused one would move the server's watermark beyond the
    /// lines it never took, and a re-run would resume after them.</summary>
    [Test]
    public async Task A_refused_parent_batch_fails_the_session() {
        var dir = Path.Combine(Home.Path, ".grok", "sessions", "%2Frepo%2Fdemo", Parent);
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "updates.jsonl"), [Line(Parent, "user_message_chunk", "\"content\":{\"type\":\"text\",\"text\":\"hi\"}")]);
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost()).RespondWith(Response.Create().WithStatusCode(400));
        foreach (var route in new[] { "/hooks/session-start/grok", "/hooks/session-end/grok" })
            _server.Given(Request.Create().WithPath(route).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();
        var source = new GrokImportSource(new GrokPaths(Home, null), NoRepository, TimeProvider.System);

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered, new ClassifyContext(client, _server.Url!, MinLines: 0, Home: Home), CancellationToken.None);
        var result     = await source.ImportSessionAsync(classified.Single(), new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Failed);
    }
}
