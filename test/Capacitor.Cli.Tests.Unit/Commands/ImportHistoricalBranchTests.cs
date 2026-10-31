using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// An imported Claude session carries the branch its transcript recorded. The repository is checked out
/// on another branch while the import runs, which is the branch the session must not take.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class ImportHistoricalBranchTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    const string Recorded = "feature/SHOP-101-rate-limit";

    static GitRepo RepoOnMain() {
        var repo = GitRepo.CreateWithCommit();
        repo.Do("checkout", "-q", "-B", "main");
        repo.AddRemote("https://github.com/acme/shop.git");

        return repo;
    }

    TempDirHandle Projects(string cwd, string? gitBranch) {
        var cwdJson    = JsonSerializer.Serialize(cwd);
        var branchJson = gitBranch is null ? "" : $",\"gitBranch\":{JsonSerializer.Serialize(gitBranch)}";
        var projects   = Tmp.CreateDir("projects");

        projects.CreateDir("-shop").CreateFile("branch-session.jsonl", [
            .. Enumerable.Range(0, 6).Select(i =>
                "{\"type\":\"user\",\"timestamp\":\"2026-03-15T10:00:0" + i + "Z\",\"cwd\":" + cwdJson + branchJson
              + ",\"message\":{\"content\":\"step " + i + "\"}}")
        ]);

        return projects;
    }

    static void StubServer(WireMockServer server, string lastLine) {
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(lastLine == "404"
                ? Response.Create().WithStatusCode(404)
                : Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(lastLine));

        foreach (var path in new[] { "/hooks/transcript", "/hooks/session-start*" })
            server.Given(Request.Create().WithPath(path).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));

        server.Given(Request.Create().WithPath("/hooks/session-end*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
    }

    Task<int> RunImport(WireMockServer server, TempDirHandle projects) =>
        new ImportCommand(Config.Root, Resolutions.At(server.Url!, Config.Root), Home, TestHarnesses.Under(Home),
                          new FixedCapacitorHttpClient(), router: new GitProviderRouter(), time: TimeProvider.System,
                          console: new TestImportTerminal().Console)
            .HandleImport(
                filterCwd:        null,
                minLines:         1,
                sources:          [new ClaudeImportSource(Config.Root, projects.Path, router: new GitProviderRouter(), time: TimeProvider.System)],
                scope:            new ImportScope.All(),
                skipConfirmation: true,
                skipTitle:        true);

    static List<JsonObject> SessionStarts(WireMockServer server) =>
        [.. server.LogEntries
            .Where(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path.StartsWith("/hooks/session-start", StringComparison.Ordinal))
            .Select(e => JsonNode.Parse(e.RequestMessage.Body!)!.AsObject())];

    [Test]
    public async Task A_new_session_takes_the_branch_its_transcript_recorded_not_the_checkout() {
        using var repo   = RepoOnMain();
        using var server = WireMockServer.Start();
        StubServer(server, "404");

        await Assert.That(await RunImport(server, Projects(repo.Path, Recorded))).IsEqualTo(0);

        var start = SessionStarts(server).Single();
        await Assert.That(repo.CurrentBranch).IsEqualTo("main");
        await Assert.That(start["repository"]?["owner"]?.GetValue<string>()).IsEqualTo("acme")
                    .Because("the repository must still be detected, or the branch assertion below proves nothing");
        await Assert.That(start["repository"]?["branch"]?.GetValue<string>()).IsEqualTo(Recorded);
    }

    [Test]
    public async Task A_transcript_that_recorded_no_branch_leaves_it_unknown() {
        using var repo   = RepoOnMain();
        using var server = WireMockServer.Start();
        StubServer(server, "404");

        await Assert.That(await RunImport(server, Projects(repo.Path, gitBranch: null))).IsEqualTo(0);

        var repository = SessionStarts(server).Single()["repository"]!.AsObject();
        await Assert.That(repository["owner"]?.GetValue<string>()).IsEqualTo("acme");
        await Assert.That(repository.ContainsKey("branch")).IsFalse();
    }

    /// <summary>The session-start is what carries a branch, so a re-import that sends one could overwrite
    /// the recorded branch with the checkout's.</summary>
    [Test]
    public async Task A_repeated_import_sends_no_session_start() {
        using var repo   = RepoOnMain();
        using var server = WireMockServer.Start();
        StubServer(server, """{"last_line_number":5}""");

        await Assert.That(await RunImport(server, Projects(repo.Path, Recorded))).IsEqualTo(0);

        await Assert.That(SessionStarts(server)).IsEmpty();
    }

    [Test]
    public async Task The_first_branch_a_transcript_records_is_the_session_branch_and_HEAD_is_none() {
        var path = Tmp.CreateFile("meta.jsonl", [
            """{"type":"user","cwd":"/w","gitBranch":"HEAD","message":{"content":"a"}}""",
            """{"type":"user","cwd":"/w","gitBranch":"","message":{"content":"b"}}""",
            """{"type":"user","cwd":"/w","gitBranch":"fix/webhook-flake","message":{"content":"c"}}""",
            """{"type":"user","cwd":"/w","gitBranch":"main","message":{"content":"d"}}""",
        ]);

        await Assert.That(ImportCommand.ExtractSessionMetadata(path).GitBranch).IsEqualTo("fix/webhook-flake");
    }

    [Test]
    [Arguments(HarnessId.Claude)]
    [Arguments(HarnessId.Codex)]
    public async Task A_vendor_whose_transcript_records_the_branch_never_falls_back_to_the_checkout(HarnessId vendor) {
        await Assert.That(ImportCommand.HistoricalBranch(vendor, recorded: null, checkout: "main")).IsNull();
        await Assert.That(ImportCommand.HistoricalBranch(vendor, recorded: "spike/charts", checkout: "main")).IsEqualTo("spike/charts");
    }
}
