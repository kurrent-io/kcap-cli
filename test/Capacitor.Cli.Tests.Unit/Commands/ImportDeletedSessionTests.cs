using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Importing a Claude session its owner deleted on the server, which answers its probe with 410.</summary>
[ParallelLimiter<SubprocessLimit>]
public class ImportDeletedSessionTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    const string SessionId = "deleted-session";

    TempDirHandle Projects() {
        var cwd      = JsonSerializer.Serialize(Tmp.Path);
        var projects = Tmp.CreateDir("projects");

        projects.CreateDir("-deleted-proj").CreateFile($"{SessionId}.jsonl", [
            .. Enumerable.Range(0, 4).Select(i =>
                $$$"""{"type":"user","timestamp":"2026-03-15T10:00:{{{i:00}}}Z","cwd":{{{cwd}}},"message":{"role":"user","content":"step {{{i}}}"}}""")
        ]);

        return projects;
    }

    [Test]
    public async Task A_session_deleted_on_the_server_is_counted_deleted_and_sent_nothing() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(410).WithHeader("Content-Type", "application/json")
                             .WithBody("""{"error":"session_deleted"}"""));

        var                             terminal = new TestImportTerminal();
        ImportCommand.ImportRunOutcome? outcome  = null;

        var handled = await new ImportCommand(Config.Root, Resolutions.At(server.Url!, Config.Root), Home, TestHarnesses.Under(Home),
                                              new FixedCapacitorHttpClient(), router: new GitProviderRouter(), time: TimeProvider.System,
                                              console: terminal.Console)
            .HandleImport(
                filterCwd:        null,
                minLines:         1,
                sources:          [new ClaudeImportSource(Config.Root, Projects().Path, router: new GitProviderRouter(), time: TimeProvider.System)],
                scope:            new ImportScope.All(),
                skipConfirmation: true,
                skipTitle:        true,
                onFinished:       o => outcome = o);

        await Assert.That(handled).IsEqualTo(0);
        await Assert.That(outcome!.Counts.Deleted).IsEqualTo(1);
        await Assert.That(outcome.Counts.Skipped).IsEqualTo(1);
        await Assert.That(outcome.Counts.Failed).IsEqualTo(0);
        await Assert.That(outcome.Counts.Imported).IsEqualTo(0);
        await Assert.That(outcome.ExitCode).IsEqualTo(0);
        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Method == "POST")).IsEqualTo(0);
        await Assert.That(terminal.Plain.ToString()).Contains("Deleted");
    }
}
