using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>An import records the repositories its sessions ran in to <c>repos.json</c>, the list the
/// desktop launcher and the daemon offer.</summary>
[ParallelLimiter<SubprocessLimit>]
public class ImportRecordsRepositoriesTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();
    readonly TempDir        _tmp    = new();

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }

    [Test]
    public async Task import_records_the_repository_root_of_each_imported_session() {
        using var repo   = GitRepo.Create();
        var       subdir = Directory.CreateDirectory(Path.Combine(repo.Path, "src", "app")).FullName;
        var       plain  = _tmp.CreateDir("not-a-repo").Path;
        // Inside the repository but deleted: walking up would name the repository, which is not
        // where that session ran.
        var       gone   = Path.Combine(repo.Path, "removed");
        StubHooks();

        var projects = _tmp.CreateDir("projects");
        WriteTranscript(projects, "in-repo", subdir, "2026-03-15T10:00:00Z");
        WriteTranscript(projects, "plain", plain, "2026-03-16T10:00:00Z");
        WriteTranscript(projects, "gone", gone, "2026-03-17T10:00:00Z");

        var exit = await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, TestHarnesses.Under(Home),
                                           new FixedCapacitorHttpClient(), router: new GitProviderRouter(), time: TimeProvider.System)
            .HandleImport(
                filterCwd:        null,
                minLines:         1,
                sources:          [new ClaudeImportSource(Config.Root, projects.Path, router: new GitProviderRouter(), time: TimeProvider.System)],
                scope:            new ImportScope.All(),
                skipConfirmation: true,
                skipTitle:        true);

        await Assert.That(exit).IsEqualTo(0);

        var entries = await new RepoPathStore(Config.Root, TimeProvider.System).LoadAsync();
        await Assert.That(entries.Select(e => e.Path)).IsEquivalentTo([Path.GetFullPath(repo.Path).TrimEnd(Path.DirectorySeparatorChar)]);
        await Assert.That(entries[0].LastUsed).IsEqualTo(new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero));
    }

    static void WriteTranscript(TempDirHandle projects, string name, string cwd, string timestamp) {
        var cwdJson = JsonSerializer.Serialize(cwd);
        projects.CreateDir($"-{name}").CreateFile(
            $"{name}-session.jsonl",
            [.. Enumerable.Range(0, 5).Select(i =>
                $$$"""{"type":"user","timestamp":"{{{timestamp}}}","cwd":{{{cwdJson}}},"message":{"content":"step {{{i}}}"}}""")]);
    }

    void StubHooks() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        foreach (var path in new[] { "/hooks/transcript", "/hooks/session-start*", "/hooks/subagent-start", "/hooks/subagent-stop" })
            _server.Given(Request.Create().WithPath(path).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));

        _server.Given(Request.Create().WithPath("/hooks/session-end*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
    }
}
