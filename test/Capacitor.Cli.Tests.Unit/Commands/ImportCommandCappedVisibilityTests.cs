using Capacitor.Cli.Commands;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

[ParallelLimiter<SubprocessLimit>]
public class ImportCommandCappedVisibilityTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();
    readonly TempDir        _tmp    = new();

    public void Dispose() { _server.Dispose(); _tmp.Dispose(); }

    static readonly string[] SessionIds = ["capvis-old", "capvis-mid", "capvis-new"];

    /// Three Claude sessions with ascending timestamps, each one the server already holds part of.
    TempDirHandle Projects() {
        var projects = _tmp.CreateDir("projects");
        var dir = projects.CreateDir("-tmp-capvis-proj");
        for (var i = 0; i < SessionIds.Length; i++)
            dir.CreateFile($"{SessionIds[i]}.jsonl",
                [.. Enumerable.Range(0, 20).Select(n =>
                    $$$"""{"type":"user","timestamp":"2026-03-{{{(i + 1):00}}}T10:00:00Z","cwd":"/tmp/capvis-proj","message":{"content":"line {{{n}}}"}}""")]);
        return projects;
    }

    void StubServer() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":2}"""));
        foreach (var path in new[] { "/hooks/transcript", "/hooks/session-start*", "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-title", "/hooks/set-title" })
            _server.Given(Request.Create().WithPath(path).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));
    }

    async Task<string[]> RunAndCollectVisibilityWrites(int? maxSessions, bool forcePrivate, bool shareWithOrg) {
        StubServer();
        var projects = Projects();

        await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, TestHarnesses.Under(Home),
                new FixedCapacitorHttpClient(), router: new GitProviderRouter(), time: TimeProvider.System)
            .HandleImport(
                filterCwd: null, minLines: 1,
                sources: [new ClaudeImportSource(Config.Root, projects.Path, router: new GitProviderRouter(), time: TimeProvider.System)],
                scope: new ImportScope.All(), skipConfirmation: true, skipTitle: true,
                forcePrivate: forcePrivate, shareWithOrg: shareWithOrg,
                maxSessions: maxSessions);

        return [
            .. _server.LogEntries
                .Where(e => e.RequestMessage.Method == "PUT" && e.RequestMessage.Path.EndsWith("/visibility", StringComparison.Ordinal))
                .Select(e => e.RequestMessage.Path["/api/sessions/".Length..^"/visibility".Length])
                .Distinct(StringComparer.Ordinal)
        ];
    }

    [Test]
    public async Task Private_preflight_under_a_cap_touches_only_the_selected_session() {
        var written = await RunAndCollectVisibilityWrites(maxSessions: 1, forcePrivate: true, shareWithOrg: false);

        await Assert.That(written).IsEquivalentTo(new[] { "capvis-new" });
    }

    [Test]
    public async Task Share_write_under_a_cap_touches_only_the_selected_session() {
        var written = await RunAndCollectVisibilityWrites(maxSessions: 1, forcePrivate: false, shareWithOrg: true);

        await Assert.That(written).IsEquivalentTo(new[] { "capvis-new" });
    }

    [Test]
    public async Task Without_a_cap_every_existing_session_is_made_private() {
        var written = await RunAndCollectVisibilityWrites(maxSessions: null, forcePrivate: true, shareWithOrg: false);

        await Assert.That(written).IsEquivalentTo(SessionIds);
    }
}
