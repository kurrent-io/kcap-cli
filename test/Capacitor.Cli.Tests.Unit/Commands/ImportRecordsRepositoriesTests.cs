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

    [TempDir] public required TempDir Tmp { get; init; }

    static readonly DateTimeOffset LastWrite = new(2026, 3, 20, 10, 0, 0, TimeSpan.Zero);

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task import_records_the_repository_root_of_each_imported_session() {
        using var repo   = GitRepo.Create();
        var       subdir = Directory.CreateDirectory(Path.Combine(repo.Path, "src", "app")).FullName;
        var       plain  = Tmp.CreateDir("not-a-repo").Path;
        // Inside the repository but deleted: walking up would name the repository, which is not
        // where that session ran.
        var       gone   = Path.Combine(repo.Path, "removed");
        StubHooks(lastLine: null);

        var projects = Tmp.CreateDir("projects");
        WriteTranscript(projects, "in-repo", subdir);
        WriteTranscript(projects, "plain", plain);
        WriteTranscript(projects, "gone", gone);

        await Assert.That(await RunImport(projects)).IsEqualTo(0);

        await AssertRecorded(repo);
    }

    [Test]
    public async Task rerunning_import_over_already_loaded_sessions_restores_their_repository() {
        using var repo = GitRepo.Create();
        StubHooks(lastLine: 4);

        var projects = Tmp.CreateDir("projects");
        WriteTranscript(projects, "loaded", repo.Path);

        await Assert.That(await RunImport(projects)).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Any(e => e.RequestMessage.Path == "/hooks/transcript"))
                    .IsFalse()
                    .Because("the session must classify as already loaded, or this proves only a fresh import");

        await AssertRecorded(repo);
    }

    Task<int> RunImport(TempDirHandle projects) =>
        new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, TestHarnesses.Under(Home),
                          new FixedCapacitorHttpClient(), router: new GitProviderRouter(), time: TimeProvider.System)
            .HandleImport(
                filterCwd:        null,
                minLines:         1,
                sources:          [new ClaudeImportSource(Config.Root, projects.Path, router: new GitProviderRouter(), time: TimeProvider.System)],
                scope:            new ImportScope.All(),
                skipConfirmation: true,
                skipTitle:        true);

    async Task AssertRecorded(GitRepo repo) {
        var entries = await new RepoPathStore(Config.Root, TimeProvider.System).LoadAsync();
        await Assert.That(entries.Select(e => e.Path)).IsEquivalentTo([Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo.Path))]);
        await Assert.That(entries[0].LastUsed).IsEqualTo(LastWrite);
    }

    static void WriteTranscript(TempDirHandle projects, string name, string cwd) {
        var cwdJson = JsonSerializer.Serialize(cwd);
        var file = projects.CreateDir($"-{name}").CreateFile(
            $"{name}-session.jsonl",
            [.. Enumerable.Range(0, 5).Select(i =>
                $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":{{{cwdJson}}},"message":{"content":"step {{{i}}}"}}""")]);
        File.SetLastWriteTimeUtc(file, LastWrite.UtcDateTime);
    }

    /// <param name="lastLine">The server's watermark for every session; null when it holds none.</param>
    void StubHooks(int? lastLine) {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(lastLine is { } n
                ? Response.Create().WithStatusCode(200).WithBody($$"""{"last_line_number":{{n}}}""")
                : Response.Create().WithStatusCode(404));
        foreach (var path in new[] { "/hooks/transcript", "/hooks/session-start*", "/hooks/subagent-start", "/hooks/subagent-stop" })
            _server.Given(Request.Create().WithPath(path).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));

        _server.Given(Request.Create().WithPath("/hooks/session-end*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
    }
}
