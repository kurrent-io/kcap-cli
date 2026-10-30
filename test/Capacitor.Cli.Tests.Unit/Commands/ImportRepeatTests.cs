using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Re-running <c>kcap import</c> over a Claude session the server already holds. The stub answers the
/// watermark a real server keeps: the last line that produced a stored event, which a server restart
/// leaves where it was. Claude ends every turn with <c>system</c> records the server stores nothing for,
/// so a complete transcript always runs past that watermark.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class ImportRepeatTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    const string SessionId     = "repeat-session";
    const int    LastEventLine = 13;

    string[] Conversation(int turns, int from = 0) {
        var cwd = JsonSerializer.Serialize(Tmp.Path);

        return [
            .. Enumerable.Range(from, turns).Select(i => i % 2 == 0
                ? $$$"""{"type":"user","uuid":"{{{Uuid(i)}}}","timestamp":"2026-03-15T10:00:{{{i:00}}}Z","cwd":{{{cwd}}},"message":{"role":"user","content":"step {{{i}}}"}}"""
                : $$$"""{"type":"assistant","uuid":"{{{Uuid(i)}}}","timestamp":"2026-03-15T10:00:{{{i:00}}}Z","cwd":{{{cwd}}},"message":{"role":"assistant","content":[{"type":"text","text":"done {{{i}}}"}]}}""")
        ];
    }

    static string Uuid(int i) => $"00000000-0000-0000-0000-{i:000000000000}";

    static readonly string[] TurnEnd = [
        """{"type":"system","subtype":"stop_hook_summary","timestamp":"2026-03-15T10:01:00Z"}""",
        """{"type":"system","subtype":"turn_duration","durationMs":1200,"timestamp":"2026-03-15T10:01:01Z"}""",
    ];

    TempDirHandle Projects(params string[][] parts) {
        var projects = Tmp.CreateDir("projects");
        projects.CreateDir("-repeat-proj").CreateFile($"{SessionId}.jsonl", [.. parts.SelectMany(p => p)]);

        return projects;
    }

    static void StubServer(WireMockServer server, int status, string? body = null) {
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(body is null
                ? Response.Create().WithStatusCode(status)
                : Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

        foreach (var path in new[] { "/hooks/transcript", "/hooks/session-start*", "/hooks/subagent-start", "/hooks/subagent-stop" })
            server.Given(Request.Create().WithPath(path).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));

        server.Given(Request.Create().WithPath("/hooks/session-end*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
    }

    async Task<(int Handled, ImportCommand.ImportRunOutcome Outcome, TestImportTerminal Terminal)> RunImport(WireMockServer server, TempDirHandle projects) {
        var                             terminal = new TestImportTerminal();
        ImportCommand.ImportRunOutcome? outcome  = null;

        var handled = await new ImportCommand(Config.Root, Resolutions.At(server.Url!, Config.Root), Home, TestHarnesses.Under(Home),
                                              new FixedCapacitorHttpClient(), router: new GitProviderRouter(), time: TimeProvider.System,
                                              console: terminal.Console)
            .HandleImport(
                filterCwd:        null,
                minLines:         1,
                sources:          [new ClaudeImportSource(Config.Root, projects.Path, router: new GitProviderRouter(), time: TimeProvider.System)],
                scope:            new ImportScope.All(),
                skipConfirmation: true,
                skipTitle:        true,
                onFinished:       o => outcome = o);

        return (handled, outcome!, terminal);
    }

    static List<string> Posts(WireMockServer server, string pathPrefix) =>
        [.. server.LogEntries
            .Where(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path.StartsWith(pathPrefix, StringComparison.Ordinal))
            .Select(e => e.RequestMessage.Body ?? "")];

    static List<int> PostedLineNumbers(WireMockServer server) =>
        [.. Posts(server, "/hooks/transcript")
            .SelectMany(body => JsonDocument.Parse(body).RootElement.GetProperty("line_numbers").EnumerateArray().Select(n => n.GetInt32()))];

    /// <summary>Each run is a repeat over the same watermark, as every run after a restart is: none may
    /// re-send the turn-end records or re-finalize the session, and both count it as already loaded.</summary>
    [Test]
    public async Task An_unchanged_complete_transcript_is_already_loaded_on_every_repeat() {
        using var server   = WireMockServer.Start();
        var       projects = Projects(Conversation(LastEventLine + 1), TurnEnd);
        StubServer(server, 200, $$"""{"last_line_number":{{LastEventLine}}}""");

        for (var run = 0; run < 2; run++) {
            var (_, outcome, _) = await RunImport(server, projects);

            await Assert.That(outcome.Counts.AlreadyLoaded).IsEqualTo(1);
            await Assert.That(outcome.Counts.Imported).IsEqualTo(0);
        }

        await Assert.That(PostedLineNumbers(server)).IsEmpty();
        await Assert.That(Posts(server, "/hooks/session-end")).IsEmpty();
        await Assert.That(Posts(server, "/hooks/session-start")).IsEmpty();
    }

    /// <summary>The resume starts on the line after the last one the server stored, so the turn-end records
    /// between it and the new turn go once more and nothing the server stored goes again.</summary>
    [Test]
    public async Task Appended_lines_resume_from_the_line_after_the_stored_content() {
        using var server   = WireMockServer.Start();
        var       projects = Projects(Conversation(LastEventLine + 1), TurnEnd, Conversation(2, from: LastEventLine + 1 + TurnEnd.Length));
        StubServer(server, 200, $$"""{"last_line_number":{{LastEventLine}}}""");

        var (_, outcome, _) = await RunImport(server, projects);

        await Assert.That(outcome.Counts.Resumed).IsEqualTo(1);
        await Assert.That(outcome.Counts.AlreadyLoaded).IsEqualTo(0);
        await Assert.That(PostedLineNumbers(server)).IsEquivalentTo([14, 15, 16, 17]);
        await Assert.That(Posts(server, "/hooks/session-end")).Count().IsEqualTo(1);
    }

    [Test]
    public async Task A_session_deleted_on_the_server_is_counted_deleted_and_sent_nothing() {
        using var server   = WireMockServer.Start();
        var       projects = Projects(Conversation(LastEventLine + 1), TurnEnd);
        StubServer(server, 410, """{"error":"session_deleted"}""");

        var (handled, outcome, terminal) = await RunImport(server, projects);

        await Assert.That(outcome.Counts.Deleted).IsEqualTo(1);
        await Assert.That(outcome.Counts.Skipped).IsEqualTo(1);
        await Assert.That(outcome.Counts.Failed).IsEqualTo(0);
        await Assert.That(outcome.Counts.Imported).IsEqualTo(0);
        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Method == "POST")).IsEqualTo(0);
        await Assert.That(ImportExitCode.For(handled, outcome)).IsEqualTo(0);
        await Assert.That(terminal.Plain.ToString()).Contains("Deleted");
    }
}
