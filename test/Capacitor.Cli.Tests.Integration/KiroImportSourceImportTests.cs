using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Harness.Kiro;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Kiro has no subagent handling at all on its routed <c>AlreadyLoaded</c> path —
/// <c>SentChildContent</c> stays at its safe default <c>false</c> via the implicit
/// <see cref="ImportOutcome"/> conversion. A real Kiro AlreadyLoaded replay must be recognized
/// by <see cref="ImportCommand.IsLifecycleOnlyRoutedReplay"/> (vendor-neutral) and suppressed
/// so it doesn't double-count on top of the classify-time AlreadyLoaded bucket.
/// </summary>
public class KiroImportSourceImportTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();
    readonly TempDir        _tmp    = new();
    readonly string         _tempDir;

    public KiroImportSourceImportTests() => _tempDir = _tmp.Path;

    const string DashedSid = "11111111-2222-3333-4444-555555555555";

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }

    string WriteSession() {
        var path = Path.Combine(_tempDir, DashedSid + ".jsonl");
        File.WriteAllLines(path, new[] {
            """{"version":"v1","kind":"Prompt","data":{"message_id":"m1","content":[{"kind":"text","data":"hi"}]}}""",
            """{"version":"v1","kind":"AssistantMessage","data":{"message_id":"m2","content":[{"kind":"text","data":"hello back"}]}}"""
        });
        File.WriteAllText(Path.Combine(_tempDir, DashedSid + ".json"), """{"cwd":"/work/a","title":"t","created_at":"2026-06-10T20:23:49.371Z","updated_at":"2026-06-10T20:23:50.000Z"}""");
        return _tempDir;
    }

    [Test]
    public async Task ImportSession_AlreadyLoaded_replay_is_a_no_op_suppressed_by_the_vendor_neutral_gate() {
        var root = WriteSession();

        // Server already covers every importable line → AlreadyLoaded.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":1}"""));
        foreach (var route in new[] { "/hooks/session-start/kiro", "/hooks/set-title", "/hooks/session-end/kiro" }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }

        using var client = new HttpClient();
        var source = new KiroImportSource(Config.Root,
            root,
            new KiroCrewPaths(root, null), new GitProviderRouter(), TimeProvider.System);

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        await Assert.That(discovered.Count).IsEqualTo(1);

        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, Home: Home),
            CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);

        var result = await source.ImportSessionAsync(
            classified[0],
            new ImportContext(client, _server.Url!, ForcePrivate: false),
            CancellationToken.None);

        // Kiro never touches a child/subagent stream — SentChildContent stays false.
        await Assert.That(result.SentChildContent).IsFalse();

        var isSuppressed = ImportCommand.IsLifecycleOnlyRoutedReplay(
            classified[0].Status, result.Outcome, result.SentChildContent);
        await Assert.That(isSuppressed).IsTrue();

        var resolved = ImportCommand.ResolveRoutedOutcomeForCounting(
            classified[0].Status, result.Outcome, result.SentChildContent);
        await Assert.That(resolved).IsNull();
    }

    /// <summary>The server takes a bounded batch of children per session-start, so a Crew chat with more
    /// sub-agents than that names every one across repeat session-starts rather than dropping the rest.</summary>
    [Test]
    public async Task ImportSession_names_every_crew_child_across_batches() {
        var root = WriteSession();
        var crew = new KiroCrewPaths(root, null);

        _tmp.CreateFile(["crew", "session_map.json"], $$$"""{"dashboard:chat-1": {"sid": "{{{DashedSid}}}"}}""");

        var spawnedAt = new DateTimeOffset(2026, 6, 10, 21, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var children  = Enumerable.Range(0, KiroCrewParentResolver.MaxChildrenPerStart + 1).Select(_ => Guid.NewGuid().ToString("D")).ToList();

        foreach (var (child, i) in children.Select((c, i) => (c, i))) {
            _tmp.CreateFile(["crew", "subagents", $"s{i:D3}", "state.json"],
                $$"""{"session_id": "{{child}}", "parent_session": "dashboard:chat-1", "started": {{spawnedAt}}}""");
        }

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":1}"""));
        foreach (var route in new[] { "/hooks/session-start/kiro", "/hooks/set-title", "/hooks/session-end/kiro" }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }

        using var client = new HttpClient();
        var source = new KiroImportSource(Config.Root, root, crew, new GitProviderRouter(), TimeProvider.System);

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered, new ClassifyContext(client, _server.Url!, MinLines: 0, Home: Home), CancellationToken.None);
        await source.ImportSessionAsync(classified[0], new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);

        var starts = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/kiro").UsingPost());
        var named  = starts.SelectMany(e => System.Text.Json.Nodes.JsonNode.Parse(e.RequestMessage.Body!)!["subagent_session_ids"]!.AsArray())
            .Select(n => n!.GetValue<string>())
            .ToList();

        await Assert.That(starts.Count).IsEqualTo(2);
        await Assert.That(named).IsEquivalentTo(children);
    }
}
